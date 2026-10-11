use serde::{Deserialize, Serialize};
use serde_json::{Map, Value};

#[derive(Deserialize)]
pub struct Manifest {
    pub version: String,
    pub tools: Vec<Tool>,
}

impl Manifest {
    pub fn load() -> Result<Self, serde_json::Error> {
        // The exporter validates Shared/ToolManifest.cs before producing this file.
        serde_json::from_str(include_str!(concat!(env!("OUT_DIR"), "/manifest.json")))
    }
}

#[derive(Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct Tool {
    pub name: String,
    pub description: String,
    pub input_schema: Map<String, Value>,
    #[serde(default, skip_serializing_if = "Map::is_empty")]
    pub annotations: Map<String, Value>,
    #[serde(skip_serializing)]
    pub method: Method,
    #[serde(skip_serializing)]
    pub path_template: String,
    #[serde(skip_serializing)]
    pub query_keys: Vec<String>,
    #[serde(skip_serializing)]
    pub body_keys: Vec<String>,
}

#[derive(Deserialize)]
#[serde(rename_all = "UPPERCASE")]
pub enum Method {
    Get,
    Put,
    Post,
}

impl Method {
    pub fn as_str(&self) -> &'static str {
        match self {
            Self::Get => "GET",
            Self::Put => "PUT",
            Self::Post => "POST",
        }
    }
}
