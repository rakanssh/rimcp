use serde::de::{MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::{json, Map, Value};
use std::collections::HashSet;
use std::env;
use std::fmt;
use std::io::{self, BufRead, Read, Write};
use std::time::Duration;

// The same manifest exported from Shared/ToolManifest.cs as the Go trial.
const MANIFEST: &str = include_str!("../../go/manifest.json");

struct Envelope {
    fields: Map<String, Value>,
    duplicate: bool,
}

impl<'de> Deserialize<'de> for Envelope {
    fn deserialize<D: Deserializer<'de>>(deserializer: D) -> Result<Self, D::Error> {
        struct EnvelopeVisitor;
        impl<'de> Visitor<'de> for EnvelopeVisitor {
            type Value = Envelope;
            fn expecting(&self, formatter: &mut fmt::Formatter) -> fmt::Result {
                formatter.write_str("a JSON-RPC object")
            }
            fn visit_map<A: MapAccess<'de>>(self, mut map: A) -> Result<Envelope, A::Error> {
                let mut fields = Map::new();
                let mut duplicate = false;
                while let Some((key, value)) = map.next_entry::<String, Value>()? {
                    duplicate |= fields.insert(key, value).is_some();
                }
                Ok(Envelope { fields, duplicate })
            }
        }
        deserializer.deserialize_map(EnvelopeVisitor)
    }
}

struct Proxy {
    manifest: Value,
    base_url: String,
    token: String,
    agent: ureq::Agent,
}

fn main() -> Result<(), Box<dyn std::error::Error>> {
    let proxy = Proxy {
        manifest: serde_json::from_str(MANIFEST)?,
        base_url: env::var("RIMCP_URL").unwrap_or_else(|_| "http://127.0.0.1:39571".into()),
        token: env::var("RIMCP_TOKEN").unwrap_or_default(),
        agent: ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(100)))
            .http_status_as_error(false)
            .build()
            .into(),
    };
    let mut output = io::BufWriter::new(io::stdout().lock());
    for line in io::stdin().lock().lines() {
        let line = line?;
        if line.trim().is_empty() {
            continue;
        }
        if let Some(response) = proxy.handle(&line) {
            serde_json::to_writer(&mut output, &response)?;
            output.write_all(b"\n")?;
            output.flush()?;
        }
    }
    Ok(())
}

fn failure(id: &Value, code: i32, message: &str) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "error": {"code": code, "message": message}})
}

fn result(id: &Value, value: Value) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "result": value})
}

fn tool_error(message: &str) -> Value {
    json!({"content": [{"type": "text", "text": message}], "isError": true})
}

impl Proxy {
    fn handle(&self, raw: &str) -> Option<Value> {
        let parsed: Value = match serde_json::from_str(raw) {
            Ok(value) => value,
            Err(_) => return Some(failure(&Value::Null, -32700, "Parse error: invalid JSON.")),
        };
        if !parsed.is_object() {
            return Some(failure(&Value::Null, -32600, "Request must be an object."));
        }
        let envelope: Envelope = serde_json::from_str(raw).expect("validated object");
        if envelope.duplicate {
            return Some(failure(
                &Value::Null,
                -32600,
                "Request contains duplicate property names.",
            ));
        }
        let fields = envelope.fields;
        let id = fields.get("id").unwrap_or(&Value::Null);
        if !id.is_null() && !id.is_string() && !id.is_number() {
            return Some(failure(
                &Value::Null,
                -32600,
                "Request id must be a string, number, or null.",
            ));
        }
        if fields.get("jsonrpc").and_then(Value::as_str) != Some("2.0") {
            return Some(failure(id, -32600, "Request must use JSON-RPC 2.0."));
        }
        let method = fields
            .get("method")
            .and_then(Value::as_str)
            .unwrap_or_default();
        if method.trim().is_empty() {
            return Some(failure(id, -32600, "Method must be a non-empty string."));
        }
        if !fields.contains_key("id") {
            return None;
        }
        match method {
            "initialize" => Some(result(
                id,
                json!({
                    "protocolVersion": "2025-11-25", "capabilities": {"tools": {}},
                    "serverInfo": {"name": "rimcp", "version": self.manifest["version"]}
                }),
            )),
            "notifications/initialized" => None,
            "tools/list" => {
                let tools: Vec<Value> = self.manifest["tools"].as_array().unwrap().iter().map(|tool| {
                    let mut entry = json!({"name": tool["name"], "description": tool["description"],
                        "inputSchema": tool["inputSchema"]});
                    if tool["annotations"].as_object().is_some_and(|map| !map.is_empty()) {
                        entry["annotations"] = tool["annotations"].clone();
                    }
                    entry
                }).collect();
                Some(result(id, json!({"tools": tools})))
            }
            "tools/call" => Some(self.call(id, fields.get("params").unwrap_or(&Value::Null))),
            _ if !id.is_null() => Some(failure(id, -32601, &format!("Method not found: {method}"))),
            _ => None,
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
        let Some(tool) = self.manifest["tools"]
            .as_array()
            .unwrap()
            .iter()
            .find(|tool| tool["name"] == name)
        else {
            return failure(id, -32602, &format!("Unknown tool: {name}"));
        };
        let args = params.get("arguments").unwrap_or(&Value::Null);
        let path = match build_path(tool, args) {
            Ok(Some(path)) => path,
            Ok(None) => return result(id, tool_error("Missing required argument.")),
            Err(error) => return failure(id, -32603, error),
        };
        let mut body = Map::new();
        for key in tool["bodyKeys"].as_array().unwrap() {
            let key = key.as_str().unwrap();
            if let Some(value) = args.get(key).filter(|value| !value.is_null()) {
                body.insert(key.into(), value.clone());
            }
        }
        let has_body = !tool["bodyKeys"].as_array().unwrap().is_empty();
        let data = if has_body {
            serde_json::to_vec(&body).unwrap()
        } else {
            Vec::new()
        };
        let mut request = ureq::http::Request::builder()
            .method(tool["method"].as_str().unwrap())
            .uri(format!("{}/{}", self.base_url.trim_end_matches('/'), path));
        if has_body {
            request = request.header("Content-Type", "application/json; charset=utf-8");
        }
        if !self.token.trim().is_empty() {
            request = request.header("Authorization", format!("Bearer {}", self.token));
        }
        let received = (|| -> Result<(bool, String), Box<dyn std::error::Error>> {
            let mut response = self.agent.run(request.body(data)?)?;
            let success = response.status().is_success();
            let mut text = String::new();
            response.body_mut().as_reader().read_to_string(&mut text)?;
            Ok((success, text))
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
            use std::fmt::Write;
            write!(output, "%{byte:02X}").unwrap();
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

fn build_path(tool: &Value, args: &Value) -> Result<Option<String>, &'static str> {
    let mut segments = Vec::new();
    for segment in tool["pathTemplate"].as_str().unwrap().split('/') {
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
    for key in tool["queryKeys"].as_array().unwrap() {
        let key = key.as_str().unwrap();
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
