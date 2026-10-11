use serde::de::{IgnoredAny, MapAccess, Visitor};
use serde::{Deserialize, Deserializer};
use serde_json::{json, Map, Value};
use std::fmt;

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

pub fn parse(raw: &[u8]) -> Result<Map<String, Value>, Value> {
    let parse_error = || failure(&Value::Null, -32700, "Parse error: invalid JSON.");
    if raw.iter().find(|byte| !byte.is_ascii_whitespace()) != Some(&b'{') {
        // Consume non-object values too, so malformed JSON still gets a parse error.
        serde_json::from_slice::<IgnoredAny>(raw).map_err(|_| parse_error())?;
        return Err(failure(&Value::Null, -32600, "Request must be an object."));
    }
    let envelope: Envelope = serde_json::from_slice(raw).map_err(|_| parse_error())?;
    if envelope.duplicate {
        return Err(failure(
            &Value::Null,
            -32600,
            "Request contains duplicate property names.",
        ));
    }
    Ok(envelope.fields)
}

pub fn failure(id: &Value, code: i32, message: &str) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "error": {"code": code, "message": message}})
}

pub fn result(id: &Value, value: Value) -> Value {
    json!({"jsonrpc": "2.0", "id": id, "result": value})
}

pub fn tool_error(message: &str) -> Value {
    json!({"content": [{"type": "text", "text": message}], "isError": true})
}
