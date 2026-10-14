mod manifest;
mod protocol;

use manifest::{Manifest, Tool};
use protocol::{failure, result, tool_error};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};
use std::env;
use std::error::Error;
use std::io::{self, BufRead, Read, Write};
use std::sync::{mpsc, Arc, Mutex};
use std::thread;
use std::time::Duration;
use ureq::http::{HeaderValue, Uri};

const MAX_REQUEST_BYTES: u64 = 1024 * 1024;
const MAX_RESPONSE_BYTES: u64 = 16 * 1024 * 1024;
const WORKERS: usize = 8;

type ProxyError = Box<dyn Error>;

struct Proxy {
    manifest: Manifest,
    base_url: String,
    authorization: Option<HeaderValue>,
    agent: ureq::Agent,
    // Ids of tool calls in progress, and whether the client cancelled each one.
    in_flight: Mutex<HashMap<String, bool>>,
}

enum Dispatch {
    Reply(Value),
    Silent,
    Call(Value, Value),
}

fn main() {
    if let Err(error) = run() {
        if error
            .downcast_ref::<io::Error>()
            .is_some_and(|error| error.kind() == io::ErrorKind::BrokenPipe)
        {
            return;
        }
        eprintln!("RimMCP proxy: {error}");
        std::process::exit(1);
    }
}

fn run() -> Result<(), ProxyError> {
    serve(Arc::new(Proxy::new()?), io::stdin().lock(), io::stdout())
}

/// Reads requests in order but runs tool calls on worker threads, so a slow call does not hold up
/// pings or other calls. Responses carry their request ids and may arrive in any order.
fn serve<W: Write + Send + 'static>(
    proxy: Arc<Proxy>,
    mut input: impl BufRead,
    output: W,
) -> Result<(), ProxyError> {
    let output = Arc::new(Mutex::new(io::BufWriter::new(output)));
    let (calls, queue) = mpsc::channel::<(Value, Value)>();
    let queue = Arc::new(Mutex::new(queue));
    let workers: Vec<_> = (0..WORKERS)
        .map(|_| {
            let (proxy, output, queue) = (proxy.clone(), output.clone(), queue.clone());
            thread::spawn(move || loop {
                let next = match queue.lock() {
                    Ok(queue) => queue.recv(),
                    Err(_) => break,
                };
                let Ok((id, params)) = next else { break };
                // A call cancelled while it waited in the queue never reaches the game.
                if proxy.withdraw_if_cancelled(&id) {
                    continue;
                }
                let response = proxy.call(&id, &params);
                if proxy.finish(&id) && write_line(&output, &response).is_err() {
                    break;
                }
            })
        })
        .collect();
    read_requests(&proxy, &mut input, &output, &calls)?;
    // Input closed: let calls already in progress answer before exiting.
    drop(calls);
    for worker in workers {
        let _ = worker.join();
    }
    Ok(())
}

fn read_requests<W: Write>(
    proxy: &Proxy,
    input: &mut impl BufRead,
    output: &Mutex<io::BufWriter<W>>,
    calls: &mpsc::Sender<(Value, Value)>,
) -> Result<(), ProxyError> {
    let mut line = Vec::new();
    loop {
        line.clear();
        if (&mut *input)
            .take(MAX_REQUEST_BYTES + 2)
            .read_until(b'\n', &mut line)?
            == 0
        {
            return Ok(());
        }
        let terminated = line.last() == Some(&b'\n');
        if terminated {
            line.pop();
            if line.last() == Some(&b'\r') {
                line.pop();
            }
        }
        let dispatch = if line.len() as u64 > MAX_REQUEST_BYTES {
            // Drain the rest without retaining it, then resume with the next request.
            if !terminated {
                input.skip_until(b'\n')?;
            }
            Dispatch::Reply(failure(&Value::Null, -32600, "Request exceeds 1 MiB."))
        } else if line.iter().all(u8::is_ascii_whitespace) {
            Dispatch::Silent
        } else {
            proxy.dispatch(&line)
        };
        match dispatch {
            Dispatch::Reply(response) => write_line(output, &response)?,
            Dispatch::Silent => {}
            Dispatch::Call(id, params) => {
                proxy.begin(&id);
                calls.send((id, params))?;
            }
        }
    }
}

fn write_line<W: Write>(output: &Mutex<io::BufWriter<W>>, value: &Value) -> io::Result<()> {
    let mut output = output
        .lock()
        .map_err(|_| io::Error::other("output lock poisoned"))?;
    writeln!(output, "{value}")?;
    output.flush()
}

impl Proxy {
    fn new() -> Result<Self, ProxyError> {
        let base_url = env::var("RIMCP_URL").unwrap_or_else(|_| "http://127.0.0.1:39571".into());
        let uri: Uri = base_url.parse()?;
        if !matches!(uri.scheme_str(), Some("http" | "https"))
            || uri.host().is_none()
            || uri.query().is_some()
            || base_url.contains('#')
        {
            return Err(
                "RIMCP_URL must be an HTTP(S) base URL without a query or fragment.".into(),
            );
        }
        let token = env::var("RIMCP_TOKEN").unwrap_or_default();
        let authorization = if token.trim().is_empty() {
            None
        } else {
            let mut header = HeaderValue::from_str(&format!("Bearer {token}"))?;
            header.set_sensitive(true);
            Some(header)
        };
        Ok(Self {
            manifest: Manifest::load()?,
            base_url,
            authorization,
            agent: ureq::Agent::config_builder()
                .timeout_global(Some(Duration::from_secs(100)))
                .http_status_as_error(false)
                .build()
                .into(),
            in_flight: Mutex::new(HashMap::new()),
        })
    }

    #[cfg(test)]
    fn handle(&self, raw: &[u8]) -> Option<Value> {
        match self.dispatch(raw) {
            Dispatch::Reply(response) => Some(response),
            Dispatch::Silent => None,
            Dispatch::Call(id, params) => Some(self.call(&id, &params)),
        }
    }

    fn begin(&self, id: &Value) {
        if let Ok(mut in_flight) = self.in_flight.lock() {
            in_flight.insert(id.to_string(), false);
        }
    }

    fn cancel(&self, id: &Value) {
        if let Ok(mut in_flight) = self.in_flight.lock() {
            if let Some(cancelled) = in_flight.get_mut(&id.to_string()) {
                *cancelled = true;
            }
        }
    }

    /// Stops tracking a call the client has cancelled; returns whether it was cancelled.
    fn withdraw_if_cancelled(&self, id: &Value) -> bool {
        let Ok(mut in_flight) = self.in_flight.lock() else {
            return false;
        };
        let key = id.to_string();
        if in_flight.get(&key) == Some(&true) {
            in_flight.remove(&key);
            return true;
        }
        false
    }

    /// Ends tracking for a call and reports whether its response should be sent.
    fn finish(&self, id: &Value) -> bool {
        self.in_flight
            .lock()
            .map(|mut in_flight| in_flight.remove(&id.to_string()) != Some(true))
            .unwrap_or(true)
    }

    fn dispatch(&self, raw: &[u8]) -> Dispatch {
        let fields = match protocol::parse(raw) {
            Ok(fields) => fields,
            Err(error) => return Dispatch::Reply(error),
        };
        let id = fields.get("id").unwrap_or(&Value::Null);
        if !id.is_null() && !id.is_string() && !id.is_number() {
            return Dispatch::Reply(failure(
                &Value::Null,
                -32600,
                "Request id must be a string, number, or null.",
            ));
        }
        if fields.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
            return Dispatch::Reply(failure(id, -32600, "Request must use JSON-RPC 2.0."));
        }
        let method = fields
            .get("method")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if method.trim().is_empty() {
            return Dispatch::Reply(failure(id, -32600, "Method must be a non-empty string."));
        }
        if !fields.contains_key("id") {
            if method == "notifications/cancelled" {
                if let Some(request) = fields
                    .get("params")
                    .and_then(|params| params.get("requestId"))
                {
                    self.cancel(request);
                }
            }
            return Dispatch::Silent;
        }
        match method {
            "ping" => Dispatch::Reply(result(id, json!({}))),
            "initialize" => Dispatch::Reply(result(
                id,
                json!({
                    "protocolVersion": "2025-11-25", "capabilities": {"tools": {}},
                    "serverInfo": {"name": "rimcp", "version": self.manifest.version}
                }),
            )),
            "notifications/initialized" => Dispatch::Silent,
            "tools/list" => Dispatch::Reply(result(id, json!({"tools": self.manifest.tools}))),
            "tools/call" => Dispatch::Call(
                id.clone(),
                fields.get("params").cloned().unwrap_or(Value::Null),
            ),
            _ if !id.is_null() => {
                Dispatch::Reply(failure(id, -32601, &format!("Method not found: {method}")))
            }
            _ => Dispatch::Silent,
        }
    }

    fn call(&self, id: &Value, params: &Value) -> Value {
        let name = params
            .get("name")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if name.trim().is_empty() {
            return failure(id, -32602, "Missing tool name.");
        }
        let Some(tool) = self.manifest.tools.iter().find(|tool| tool.name == name) else {
            return failure(id, -32602, &format!("Unknown tool: {name}"));
        };
        let args = params.get("arguments").unwrap_or(&Value::Null);
        let path = match build_path(tool, args) {
            Ok(Some(path)) => path,
            Ok(None) => return result(id, tool_error("Missing required argument.")),
            Err(error) => return failure(id, -32603, error),
        };
        let mut body = Map::new();
        for key in &tool.body_keys {
            if let Some(value) = args.get(key).filter(|value| !value.is_null()) {
                body.insert(key.into(), value.clone());
            }
        }
        let has_body = !tool.body_keys.is_empty();
        let data = if has_body {
            Value::Object(body).to_string().into_bytes()
        } else {
            Vec::new()
        };
        let mut request = ureq::http::Request::builder()
            .method(tool.method.as_str())
            .uri(format!("{}/{}", self.base_url.trim_end_matches('/'), path));
        if has_body {
            request = request.header("Content-Type", "application/json; charset=utf-8");
        }
        if let Some(token) = &self.authorization {
            request = request.header("Authorization", token);
        }
        let received = (|| -> Result<(bool, String), ProxyError> {
            let mut response = self.agent.run(request.body(data)?)?;
            let success = response.status().is_success();
            let mut bytes = Vec::new();
            response
                .body_mut()
                .as_reader()
                .take(MAX_RESPONSE_BYTES + 1)
                .read_to_end(&mut bytes)?;
            if bytes.len() as u64 > MAX_RESPONSE_BYTES {
                return Err(
                    "Bridge response exceeds 16 MiB; use a smaller page or less detail.".into(),
                );
            }
            Ok((success, String::from_utf8(bytes)?))
        })();
        match received {
            Ok((success, text)) => {
                let structured: Value = serde_json::from_str(&text).unwrap_or(Value::Null);
                let mut content =
                    vec![json!({"type": "text", "text": summarize(name, &structured, success)})];
                if !text.trim().is_empty() {
                    content.push(json!({"type": "text", "text": text}));
                }
                let mut value = json!({"content": content, "isError": !success});
                if !structured.is_null() {
                    value["structuredContent"] = structured;
                }
                result(id, value)
            }
            Err(error) => result(
                id,
                tool_error(&format!(
                    "Unable to reach RimMCP at {}: {error}",
                    self.base_url
                )),
            ),
        }
    }
}

fn escape(value: &str) -> String {
    let mut output = String::new();
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || b"-._~".contains(&byte) {
            output.push(byte as char);
        } else {
            const HEX: &[u8; 16] = b"0123456789ABCDEF";
            output.push('%');
            output.push(HEX[(byte >> 4) as usize] as char);
            output.push(HEX[(byte & 15) as usize] as char);
        }
    }
    output
}

fn query_value(value: &Value) -> Result<String, &'static str> {
    match value {
        Value::String(value) => Ok(value.clone()),
        Value::Number(value) => Ok(value.to_string()),
        Value::Bool(value) => Ok(if *value { "True" } else { "False" }.into()),
        Value::Array(values) => {
            let mut items = Vec::new();
            for value in values.iter().filter(|value| !value.is_null()) {
                let text = value.as_str().ok_or("Array values must be strings.")?;
                if !text.trim().is_empty() {
                    items.push(text);
                }
            }
            Ok(items.join(","))
        }
        _ => Err("Query values must be scalar values or arrays of strings."),
    }
}

fn build_path(tool: &Tool, args: &Value) -> Result<Option<String>, &'static str> {
    let mut segments = Vec::new();
    for segment in tool.path_template.split('/') {
        if segment.starts_with('{') && segment.ends_with('}') && segment.len() > 2 {
            let Some(value) = args
                .get(&segment[1..segment.len() - 1])
                .and_then(Value::as_str)
                .filter(|text| !text.trim().is_empty())
            else {
                return Ok(None);
            };
            segments.push(escape(value));
        } else {
            segments.push(segment.into());
        }
    }
    let mut path = segments.join("/").trim_start_matches('/').to_string();
    let mut query = Vec::new();
    let mut seen = HashSet::new();
    for key in &tool.query_keys {
        if !seen.insert(key.to_lowercase()) {
            continue;
        }
        if let Some(value) = args.get(key).filter(|value| !value.is_null()) {
            let encoded = query_value(value)?;
            if !encoded.is_empty() {
                query.push(format!("{}={}", escape(key), escape(&encoded)));
            }
        }
    }
    if !query.is_empty() {
        path.push('?');
        path.push_str(&query.join("&"));
    }
    Ok(Some(path))
}

fn summarize(name: &str, value: &Value, success: bool) -> String {
    if !success {
        return value
            .get("error")
            .and_then(Value::as_str)
            .unwrap_or("RimMCP request failed.")
            .into();
    }
    let tick = value
        .get("tick")
        .filter(|tick| tick.is_number())
        .map(|tick| format!(" at tick {tick}"))
        .unwrap_or_default();
    if let Some(changed) = value.get("changed").and_then(Value::as_bool) {
        return format!("{name}: command completed{tick} changed={changed}.");
    }
    if !tick.is_empty() {
        let truncated = if value.get("truncated").and_then(Value::as_bool) == Some(true) {
            " truncated"
        } else {
            ""
        };
        return format!("{name}: returned structured data{tick}{truncated}.");
    }
    format!("{name}: returned structured data.")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn disconnected_proxy() -> Proxy {
        Proxy {
            manifest: Manifest {
                version: "test".into(),
                tools: Vec::new(),
            },
            // An invalid URL makes accidental bridge access fail without opening a socket.
            base_url: String::new(),
            authorization: None,
            agent: ureq::Agent::config_builder().build().into(),
            in_flight: Mutex::new(HashMap::new()),
        }
    }

    #[test]
    fn ping_preserves_request_ids_without_a_bridge() {
        let proxy = disconnected_proxy();
        for id in [
            Value::Null,
            json!("health-check"),
            json!(42),
            json!(9007199254740993u64),
        ] {
            let request = json!({"jsonrpc": "2.0", "id": id, "method": "ping"});
            assert_eq!(
                proxy.handle(request.to_string().as_bytes()),
                Some(json!({"jsonrpc": "2.0", "id": id, "result": {}})),
                "ping must echo request id {id} and return an empty result"
            );
        }
    }

    #[test]
    fn ping_notification_does_not_produce_a_response() {
        let proxy = disconnected_proxy();
        assert_eq!(proxy.handle(br#"{"jsonrpc":"2.0","method":"ping"}"#), None);
    }

    #[test]
    fn ping_still_validates_the_request_envelope() {
        let proxy = disconnected_proxy();
        assert_eq!(
            proxy.handle(br#"{"jsonrpc":"1.0","id":7,"method":"ping"}"#),
            Some(failure(&json!(7), -32600, "Request must use JSON-RPC 2.0."))
        );
        assert_eq!(
            proxy.handle(br#"{"jsonrpc":"2.0","id":true,"method":"ping"}"#),
            Some(failure(
                &Value::Null,
                -32600,
                "Request id must be a string, number, or null."
            ))
        );
    }

    type Received = Arc<Mutex<Vec<String>>>;

    /// A stand-in bridge: `/v1/slow` answers after 400 ms, everything else at once. It records
    /// each request line it receives.
    fn fake_bridge() -> (String, Received) {
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        let received = Received::default();
        let log = received.clone();
        thread::spawn(move || {
            for stream in listener.incoming().flatten() {
                let log = log.clone();
                thread::spawn(move || {
                    let mut reader = io::BufReader::new(stream.try_clone().unwrap());
                    let mut request_line = String::new();
                    reader.read_line(&mut request_line).unwrap();
                    log.lock().unwrap().push(request_line.trim().to_string());
                    let mut header = String::new();
                    while reader.read_line(&mut header).unwrap() > 2 {
                        header.clear();
                    }
                    if request_line.contains("/v1/slow") {
                        thread::sleep(Duration::from_millis(400));
                    }
                    let body = r#"{"tick":1}"#;
                    write!(
                        &stream,
                        "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
                        body.len()
                    )
                    .unwrap();
                });
            }
        });
        (format!("http://{address}"), received)
    }

    fn bridge_proxy() -> (Arc<Proxy>, Received) {
        let tool = |name: &str| Tool {
            name: name.into(),
            description: String::new(),
            input_schema: Map::new(),
            annotations: Map::new(),
            method: manifest::Method::Get,
            path_template: format!("/v1/{name}"),
            query_keys: Vec::new(),
            body_keys: Vec::new(),
        };
        let (base_url, received) = fake_bridge();
        let proxy = Arc::new(Proxy {
            manifest: Manifest {
                version: "test".into(),
                tools: vec![tool("slow"), tool("fast"), tool("speed")],
            },
            base_url,
            authorization: None,
            agent: ureq::Agent::config_builder()
                .http_status_as_error(false)
                .build()
                .into(),
            in_flight: Mutex::new(HashMap::new()),
        });
        (proxy, received)
    }

    #[derive(Clone, Default)]
    struct Captured(Arc<Mutex<Vec<u8>>>);

    impl Write for Captured {
        fn write(&mut self, bytes: &[u8]) -> io::Result<usize> {
            self.0.lock().unwrap().extend_from_slice(bytes);
            Ok(bytes.len())
        }

        fn flush(&mut self) -> io::Result<()> {
            Ok(())
        }
    }

    /// Serves the requests and returns the ids answered, in order, and the bridge requests made.
    fn serve_requests(requests: &[Value]) -> (Vec<Value>, Vec<String>) {
        let input = requests
            .iter()
            .map(Value::to_string)
            .collect::<Vec<_>>()
            .join("\n");
        let output = Captured::default();
        let (proxy, received) = bridge_proxy();
        serve(proxy, io::Cursor::new(input), output.clone()).unwrap();
        let text = String::from_utf8(output.0.lock().unwrap().clone()).unwrap();
        let ids = text
            .lines()
            .map(|line| serde_json::from_str::<Value>(line).unwrap()["id"].clone())
            .collect();
        let received = received.lock().unwrap().clone();
        (ids, received)
    }

    fn served_ids(requests: &[Value]) -> Vec<Value> {
        serve_requests(requests).0
    }

    fn tool_call(id: u64, name: &str) -> Value {
        json!({"jsonrpc": "2.0", "id": id, "method": "tools/call", "params": {"name": name, "arguments": {}}})
    }

    #[test]
    fn slow_tool_calls_do_not_hold_up_later_requests() {
        let ids = served_ids(&[
            tool_call(1, "slow"),
            tool_call(2, "fast"),
            json!({"jsonrpc": "2.0", "id": 3, "method": "ping"}),
        ]);
        assert_eq!(ids.len(), 3);
        assert_eq!(
            ids.last(),
            Some(&json!(1)),
            "the slow call must answer last: {ids:?}"
        );
    }

    #[test]
    fn cancelled_tool_calls_get_no_response() {
        let ids = served_ids(&[
            tool_call(1, "slow"),
            json!({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 1}}),
            tool_call(2, "fast"),
        ]);
        assert_eq!(ids, [json!(2)]);
    }

    #[test]
    fn calls_cancelled_while_queued_never_reach_the_bridge() {
        // Occupy every worker, queue a speed change behind them, then cancel it.
        let mut requests: Vec<Value> = (1..=WORKERS as u64)
            .map(|id| tool_call(id, "slow"))
            .collect();
        requests.push(tool_call(99, "speed"));
        requests.push(json!({"jsonrpc": "2.0", "method": "notifications/cancelled", "params": {"requestId": 99}}));
        let (ids, received) = serve_requests(&requests);
        assert_eq!(ids.len(), WORKERS, "only the slow calls answer: {ids:?}");
        assert_eq!(
            received
                .iter()
                .filter(|line| line.contains("/v1/slow"))
                .count(),
            WORKERS,
            "every slow call must reach the bridge: {received:?}"
        );
        assert!(
            !received.iter().any(|line| line.contains("/v1/speed")),
            "the cancelled call reached the bridge: {received:?}"
        );
    }
}
