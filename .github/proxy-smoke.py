import argparse
import hashlib
import io
import json
import os
from pathlib import Path
import platform
import queue
import socket
import statistics
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import quote
import zipfile


class Bridge(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    mode = "success"
    requests = []

    def setup(self):
        super().setup()
        self.connection.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

    def handle(self):
        try:
            super().handle()
        except (ConnectionResetError, BrokenPipeError):
            pass  # A proxy can close its idle keep-alive connection on exit.

    def log_message(self, *_):
        pass

    def do_GET(self):
        self.respond()

    def do_POST(self):
        self.respond()

    def do_PUT(self):
        self.respond()

    def respond(self):
        if self.mode == "oversized":
            size = 16 * 1024 * 1024 + 100
            self.send_response(200)
            self.send_header("Content-Length", str(size))
            self.end_headers()
            self.wfile.write(b"x" * size)
            return
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
            if self.mode == "large":
                value["data"]["padding"] = "🤖" * 24000
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
                    assert len(Bridge.requests) == before + 1, (mode, minimal, falsy, t["name"], result)
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
        Bridge.mode = "large"
        result = tool_call(client, "get_game_log", {"include": ["stackTrace"], "limit": 200})
        assert result["structuredContent"]["data"]["padding"] == "🤖" * 24000
        assert json.loads(result["content"][1]["text"]) == result["structuredContent"]
        Bridge.mode = "success"
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
    return {"tools": len(discovered), "http_calls": calls + 3, "equivalence_sha256": digest}


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


def edge_cases(binary, env):
    client = Client(binary, env)
    try:
        limit = 1024 * 1024
        request = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "initialize"})
        client.raw(request + " " * (limit - len(request)))
        assert "result" in client.receive()
        client.raw(request + " " * limit)
        rejected = client.receive()
        assert rejected["error"]["code"] == -32600 and "1 MiB" in rejected["error"]["message"]
        assert "result" in client.call("initialize")
        client.process.stdin.buffer.write(b'\xff\n')
        client.process.stdin.buffer.flush()
        assert client.receive()["error"]["code"] == -32700
        malformed = ['[', '[1,', '{"id":1,"id":2,', 'true trailing', '"unterminated']
        for raw in malformed:
            client.raw(raw)
            assert client.receive()["error"]["code"] == -32700, raw
        for raw in ['[]', 'null', 'true', '123', '1e999', '"hello"']:
            client.raw(raw)
            assert client.receive()["error"]["code"] == -32600, raw
        client.raw('{"jsonrpc":"2.0","id":1,"method":"initialize","extra":1,"extra":2}')
        assert client.receive()["error"]["code"] == -32600
        client.raw(json.dumps({"jsonrpc":"2.0", "id":"🤖", "method":"tools/call", "params":{
            "name":"search_defs", "arguments":{"kind":"thing", "query":"café 🤖 中文"}}}, ensure_ascii=False))
        response = client.receive()
        assert response["id"] == "🤖" and response["result"]["isError"] is False
        assert "caf%C3%A9%20%F0%9F%A4%96%20%E4%B8%AD%E6%96%87" in Bridge.requests[-1]["path"]
        Bridge.mode = "oversized"
        response = tool_call(client, "get_game_log", {})
        assert response["isError"] and "16 MiB" in response["content"][0]["text"]
        Bridge.mode = "success"
        assert not tool_call(client, "get_game_settings", {})["isError"]
    finally:
        client.close()
    request = b'{"jsonrpc":"2.0","id":1,"method":"initialize"}'
    completed = subprocess.run([str(binary)], input=request, capture_output=True, env=env, timeout=10)
    assert completed.returncode == 0 and not completed.stderr and "result" in json.loads(completed.stdout)
    process = subprocess.Popen([str(binary)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    process.stdout.close()
    process.stdin.write(request + b'\n')
    process.stdin.close()
    assert process.wait(timeout=10) == 0 and not process.stderr.read(), "Closed stdout must exit quietly"
    for url in ['ftp://localhost', 'http://localhost/?query=1', 'http://localhost/#fragment', '']:
        invalid = subprocess.run([str(binary)], input=b'', capture_output=True, env=dict(env, RIMCP_URL=url), timeout=10)
        assert invalid.returncode != 0 and invalid.stderr and not invalid.stdout, url
    return {"input_limit": "1 MiB", "response_limit": "16 MiB", "recovery": True,
            "utf8": True, "malformed_json": True, "quiet_broken_pipe": True, "eof_without_newline": True}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--binary', required=True, type=Path)
    parser.add_argument('--manifest', required=True, type=Path)
    parser.add_argument('--output', required=True, type=Path)
    args = parser.parse_args()
    binary = args.binary.resolve()
    if os.name != 'nt':
        binary.chmod(0o755)
    manifest = json.loads(args.manifest.read_text(encoding='utf-8-sig'))
    bridge = ThreadingHTTPServer(('127.0.0.1', 0), Bridge)
    threading.Thread(target=bridge.serve_forever, daemon=True).start()
    env = dict(os.environ, RIMCP_URL=f'http://127.0.0.1:{bridge.server_port}/bridge/', RIMCP_TOKEN='comparison-token',
               NO_PROXY='127.0.0.1,localhost', no_proxy='127.0.0.1,localhost')
    try:
        checks = {}
        for protocol in ('HTTP/1.1', 'HTTP/1.0'):
            Bridge.protocol_version = protocol
            checks[protocol] = verify(binary, env, manifest)
            assert checks[protocol]['equivalence_sha256'] == '28f6359bae905d24ec813a45acaa8243e8e7484383f7f5a6379f14e2bae23daa'
            print(protocol, checks[protocol], flush=True)
        Bridge.protocol_version = 'HTTP/1.1'
        checks['edge_cases'] = edge_cases(binary, env)
        checks['binary_bytes'] = binary.stat().st_size
        args.output.write_text(json.dumps(checks, indent=2), encoding='utf-8')
        print('Limits, malformed input, UTF-8, recovery and closed pipes passed.', flush=True)
    finally:
        bridge.shutdown()
        bridge.server_close()


if __name__ == '__main__':
    main()


