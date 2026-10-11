import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import queue
import statistics
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import quote
import zipfile


class Bridge(BaseHTTPRequestHandler):
    mode = "success"
    requests = []

    def log_message(self, *_):
        pass

    def do_GET(self):
        self.respond()

    def do_POST(self):
        self.respond()

    def do_PUT(self):
        self.respond()

    def respond(self):
        raw = self.rfile.read(int(self.headers.get("Content-Length", "0")))
        body = json.loads(raw) if raw else None
        request = {"method": self.command, "path": self.path, "body": body,
                   "auth": self.headers.get("Authorization")}
        self.requests.append(request)
        if self.mode == "error":
            status, value = 403, {"error": "Expected smoke-test rejection", "tick": 123}
        elif self.mode == "plain":
            status, value = 200, None
        elif self.mode == "empty":
            status, value = 204, None
        else:
            status, value = 200, {"schemaVersion": "1", "tick": 123, "truncated": self.mode == "truncated",
                                  "data": {"request": request, "text": "café 🤖\n中文", "integer": 9007199254740993,
                                           "decimal": 1.25, "nullable": None, "enabled": False}}
            if self.command != "GET":
                value["changed"] = False
        if value is not None:
            data = json.dumps(value, ensure_ascii=False, sort_keys=True, separators=(",", ":")).encode()
        else:
            data = b"plain bridge response" if self.mode == "plain" else b""
        self.send_response(status)
        self.send_header("Content-Type", "application/json" if value else "text/plain")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)


class Client:
    def __init__(self, binary, env):
        self.process = subprocess.Popen([str(binary)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                        stderr=subprocess.PIPE, text=True, encoding="utf-8", env=env)
        self.lines = queue.Queue()
        self.counter = 0
        threading.Thread(target=self.read, daemon=True).start()

    def read(self):
        for line in self.process.stdout:
            self.lines.put(line)
        self.lines.put(None)

    def raw(self, text):
        self.process.stdin.write(text + "\n")
        self.process.stdin.flush()

    def receive(self):
        line = self.lines.get(timeout=30)
        assert line is not None, f"Proxy exited: {self.process.stderr.read()}"
        return json.loads(line)

    def call(self, method, params=None, id=None):
        self.counter += 1
        identifier = self.counter if id is None else id
        self.raw(json.dumps({"jsonrpc": "2.0", "id": identifier, "method": method,
                             "params": params or {}}, ensure_ascii=True))
        response = self.receive()
        assert response["jsonrpc"] == "2.0" and response["id"] == identifier, response
        return response

    def close(self):
        self.process.stdin.close()
        try:
            code = self.process.wait(timeout=10)
            stderr = self.process.stderr.read()
            assert code == 0 and not stderr.strip(), (code, stderr)
        finally:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait()


def sample(schema, minimal=False, falsy=False):
    if "enum" in schema:
        return schema["enum"][0]
    if "oneOf" in schema:
        return sample(schema["oneOf"][0], minimal, falsy)
    kind = schema.get("type", "string")
    if kind == "object":
        required = schema.get("required", [])
        return {k: sample(v, minimal, falsy) for k, v in schema.get("properties", {}).items()
                if not minimal or k in required}
    if kind == "array":
        return [sample(schema.get("items", {}), minimal, falsy)]
    if kind == "boolean":
        return not falsy
    if kind == "integer":
        return schema.get("minimum", 0) if falsy else schema.get("minimum", 0) + 1
    if kind == "number":
        return 0 if falsy else 0.75
    return "sample / café ?& 🤖"


def expected_request(tool, args, token="comparison-token"):
    path = tool["pathTemplate"]
    for key, value in args.items():
        path = path.replace("{" + key + "}", quote(str(value), safe=""))
    query = []
    seen = set()
    for key in tool["queryKeys"]:
        if key.lower() in seen:
            continue
        seen.add(key.lower())
        value = args.get(key)
        if value is None:
            continue
        if isinstance(value, list):
            value = ",".join(v for v in value if v and v.strip())
        else:
            value = str(value)
        if value:
            query.append(quote(key, safe="") + "=" + quote(value, safe=""))
    if query:
        path += "?" + "&".join(query)
    body = {key: args[key] for key in tool["bodyKeys"] if args.get(key) is not None} if tool["bodyKeys"] else None
    return {"method": tool["method"], "path": "/bridge" + path, "body": body,
            "auth": "Bearer " + token if token else None}


def tool_call(client, name, arguments):
    response = client.call("tools/call", {"name": name, "arguments": arguments})
    assert "result" in response, response
    return response["result"]


def verify(binary, env, manifest):
    client = Client(binary, env)
    calls = 0
    transcript = []
    try:
        initialized = client.call("initialize")["result"]
        assert initialized == {"protocolVersion": "2025-11-25", "capabilities": {"tools": {}},
                               "serverInfo": {"name": "rimcp", "version": manifest["version"]}}
        client.raw('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        discovered = client.call("tools/list")["result"]["tools"]
        expected = [{key: t[key] for key in ("name", "description", "inputSchema", "annotations")}
                    for t in manifest["tools"]]
        assert discovered == expected and len(discovered) == 37
        for mode in ("success", "truncated", "error", "plain", "empty"):
            Bridge.mode = mode
            for t in manifest["tools"]:
                for minimal, falsy in ((False, False), (True, False), (False, True)):
                    arguments = sample(t["inputSchema"], minimal, falsy)
                    arguments["unexpected"] = "ignored"
                    if not minimal and "include" in arguments:
                        arguments["include"] = ["stackTrace", "", "  ", None, "café"]
                    if minimal:
                        for key in t["queryKeys"] + t["bodyKeys"]:
                            if key not in arguments:
                                arguments[key] = None
                    if not minimal and t["name"] == "set_game_settings":
                        arguments["volumeMaster"] = 0
                        arguments["runInBackground"] = False
                    before = len(Bridge.requests)
                    result = tool_call(client, t["name"], arguments)
                    calls += 1
                    assert len(Bridge.requests) == before + 1, (t["name"], result)
                    assert Bridge.requests[-1] == expected_request(t, arguments), (t["name"], Bridge.requests[-1], expected_request(t, arguments))
                    assert result["isError"] == (mode == "error"), (t["name"], result)
                    if mode in ("success", "truncated", "error"):
                        structured = result["structuredContent"]
                        assert structured["tick"] == 123 and json.loads(result["content"][1]["text"]) == structured
                        if mode != "error":
                            assert structured["data"]["integer"] == 9007199254740993
                        else:
                            assert result["content"][0]["text"] == "Expected smoke-test rejection"
                    else:
                        assert "structuredContent" not in result
                        assert len(result["content"]) == (2 if mode == "plain" else 1)
                    transcript.append(result)

        Bridge.mode = "success"
        for t in manifest["tools"]:
            if "{" in t["pathTemplate"]:
                before = len(Bridge.requests)
                result = tool_call(client, t["name"], {})
                assert result == {"content": [{"type": "text", "text": "Missing required argument."}], "isError": True}
                assert len(Bridge.requests) == before
        # A line larger than Scanner's default 64 KiB and values larger than float64's integer precision.
        t = next(t for t in manifest["tools"] if t["name"] == "search_defs")
        arguments = {"kind": "thing", "query": "café", "limit": 50, "unexpected": "🤖" * 24000}
        result = tool_call(client, t["name"], arguments)
        assert Bridge.requests[-1] == expected_request(t, arguments)
        assert result["isError"] is False
        for identifier in ("string-id", 9007199254740993, 1.25, -3):
            assert "result" in client.call("tools/list", id=identifier)
        for raw, code in [('{', -32700), ('[]', -32600), ('null', -32600),
                          ('{"jsonrpc":"2.0","id":true,"method":"initialize"}', -32600),
                          ('{"jsonrpc":"2.0","id":1,"method":"initialize","method":"tools/list"}', -32600),
                          ('{"jsonrpc":"1.0","id":1,"method":"initialize"}', -32600),
                          ('{"jsonrpc":"2.0","id":1,"method":" "}', -32600)]:
            client.raw(raw)
            assert client.receive()["error"]["code"] == code
        assert client.call("unsupported")["error"]["code"] == -32601
        assert client.call("tools/call", {"name": "unknown"})["error"]["code"] == -32602
        assert client.call("tools/call")["error"]["code"] == -32602
        # Notifications must never execute a tool or write a response.
        before = len(Bridge.requests)
        client.raw('{"jsonrpc":"2.0","method":"tools/call","params":{"name":"get_game_settings"}}')
        client.raw('{"jsonrpc":"2.0","id":null,"method":"tools/list"}')
        assert client.receive()["id"] is None
        assert len(Bridge.requests) == before
    finally:
        client.close()

    # Empty token and unreachable bridge, separate from the authenticated fixture.
    no_token = dict(env, RIMCP_TOKEN="")
    client = Client(binary, no_token)
    try:
        result = tool_call(client, "get_game_settings", {})
        assert result["isError"] is False and Bridge.requests[-1]["auth"] is None
    finally:
        client.close()
    unreachable = dict(env, RIMCP_URL="http://127.0.0.1:1")
    client = Client(binary, unreachable)
    try:
        result = tool_call(client, "get_game_settings", {})
        assert result["isError"] and result["content"][0]["text"].startswith("Unable to reach RimMCP at http://127.0.0.1:1:")
    finally:
        client.close()
    digest = hashlib.sha256(json.dumps(transcript, sort_keys=True, ensure_ascii=False).encode()).hexdigest()
    return {"tools": len(discovered), "http_calls": calls + 2, "equivalence_sha256": digest}


def startup(binary, env):
    started = time.perf_counter_ns()
    client = Client(binary, env)
    try:
        assert "result" in client.call("initialize")
        assert len(client.call("tools/list")["result"]["tools"]) == 37
        elapsed = (time.perf_counter_ns() - started) / 1_000_000
    finally:
        client.close()
    return elapsed


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--iterations", type=int, default=30)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8-sig"))
    root = args.root.resolve()
    variants = ("baseline", "aot", "go")
    suffix = ".exe" if args.rid.startswith("win") else ""
    binaries = {name: root / name / args.rid / ("RiMCP.McpProxy" + suffix) for name in variants}
    bridge = ThreadingHTTPServer(("127.0.0.1", 0), Bridge)
    threading.Thread(target=bridge.serve_forever, daemon=True).start()
    env = dict(os.environ, RIMCP_URL=f"http://127.0.0.1:{bridge.server_port}/bridge/", RIMCP_TOKEN="comparison-token",
               DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root / "extract"), NO_PROXY="127.0.0.1,localhost", no_proxy="127.0.0.1,localhost")
    measurements = {}
    try:
        for name, binary in binaries.items():
            assert binary.is_file(), binary
            if not args.rid.startswith("win"):
                binary.chmod(0o755)
            first = startup(binary, dict(env, DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root / "first" / name)))
            checks = verify(binary, env, manifest)
            directory = binary.parent
            package_files = [binary, directory / "LICENSE.TXT", directory / "THIRD-PARTY-NOTICES.TXT"]
            archive = root / (name + "-" + args.rid + ".zip")
            with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as output:
                for path in package_files:
                    output.write(path, args.rid + "/" + path.name)
            measurements[name] = {"binary_bytes": binary.stat().st_size, "package_bytes": sum(p.stat().st_size for p in package_files),
                                  "zip_bytes": archive.stat().st_size, "first_start_ms": first, "checks": checks, "startup_ms": []}
            print(f"{name}: {checks['tools']} tools, {checks['http_calls']} HTTP checks, {binary.stat().st_size} bytes", flush=True)
        hashes = {m["checks"]["equivalence_sha256"] for m in measurements.values()}
        assert len(hashes) == 1, "Proxy tool results differ from baseline"
        for iteration in range(args.iterations):
            order = list(variants[iteration % 3:] + variants[:iteration % 3])
            for name in order:
                measurements[name]["startup_ms"].append(startup(binaries[name], env))
        for name, data in measurements.items():
            times = sorted(data["startup_ms"])
            data["startup_median_ms"] = statistics.median(times)
            data["startup_p95_ms"] = times[min(len(times) - 1, int(len(times) * 0.95))]
            print(f"{name}: median {data['startup_median_ms']:.1f} ms; p95 {data['startup_p95_ms']:.1f} ms", flush=True)
        result = {"rid": args.rid, "host": platform.platform(), "architecture": platform.machine(),
                  "iterations": args.iterations, "measurements": measurements}
        (root / ("results-" + args.rid + ".json")).write_text(json.dumps(result, indent=2), encoding="utf-8")
    finally:
        bridge.shutdown()
        bridge.server_close()


if __name__ == "__main__":
    main()
