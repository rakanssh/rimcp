package main

import (
	"bufio"
	"bytes"
	_ "embed"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"strings"
	"time"
)

// Generated from Shared/ToolManifest.cs before building.
//go:embed manifest.json
var manifestJSON []byte

type tool struct {
	Name         string         `json:"name"`
	Description  string         `json:"description"`
	Method       string         `json:"method"`
	PathTemplate string         `json:"pathTemplate"`
	QueryKeys    []string       `json:"queryKeys"`
	BodyKeys     []string       `json:"bodyKeys"`
	InputSchema  map[string]any `json:"inputSchema"`
	Annotations  map[string]any `json:"annotations"`
}

type manifest struct {
	Version string `json:"version"`
	Tools   []tool `json:"tools"`
}

type proxy struct {
	manifest manifest
	baseURL  string
	token    string
	client   *http.Client
	output   *json.Encoder
}

func main() {
	p := proxy{baseURL: env("RIMCP_URL", "http://127.0.0.1:39571"), token: os.Getenv("RIMCP_TOKEN"),
		client: &http.Client{Timeout: 100 * time.Second}, output: json.NewEncoder(os.Stdout)}
	if err := json.Unmarshal(manifestJSON, &p.manifest); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
	reader := bufio.NewReader(os.Stdin)
	for {
		line, err := reader.ReadString('\n')
		if strings.TrimSpace(line) != "" {
			p.handle([]byte(line))
		}
		if err != nil {
			if err != io.EOF {
				fmt.Fprintln(os.Stderr, err)
			}
			return
		}
	}
}

func env(key, fallback string) string {
	if value, ok := os.LookupEnv(key); ok {
		return value
	}
	return fallback
}

func decode(raw []byte) (any, error) {
	d := json.NewDecoder(bytes.NewReader(raw))
	d.UseNumber()
	var value any
	err := d.Decode(&value)
	return value, err
}

// Check the envelope separately because encoding/json otherwise accepts duplicate keys.
func envelope(raw []byte) (map[string]json.RawMessage, bool) {
	d := json.NewDecoder(bytes.NewReader(raw))
	d.Token()
	result := map[string]json.RawMessage{}
	for d.More() {
		key, _ := d.Token()
		name, _ := key.(string)
		if _, exists := result[name]; exists {
			return nil, true
		}
		var value json.RawMessage
		d.Decode(&value)
		result[name] = value
	}
	return result, false
}

func (p *proxy) handle(raw []byte) {
	if !json.Valid(raw) {
		p.failure(nil, -32700, "Parse error: invalid JSON.")
		return
	}
	if bytes.TrimSpace(raw)[0] != '{' {
		p.failure(nil, -32600, "Request must be an object.")
		return
	}
	message, duplicate := envelope(raw)
	if duplicate {
		p.failure(nil, -32600, "Request contains duplicate property names.")
		return
	}
	id, _ := decode(message["id"])
	if id != nil {
		switch id.(type) {
		case string, json.Number:
		default:
			p.failure(nil, -32600, "Request id must be a string, number, or null.")
			return
		}
	}
	version, _ := decode(message["jsonrpc"])
	if version != "2.0" {
		p.failure(id, -32600, "Request must use JSON-RPC 2.0.")
		return
	}
	value, _ := decode(message["method"])
	method, _ := value.(string)
	if strings.TrimSpace(method) == "" {
		p.failure(id, -32600, "Method must be a non-empty string.")
		return
	}
	if _, present := message["id"]; !present {
		return
	}
	switch method {
	case "initialize":
		p.result(id, map[string]any{"protocolVersion": "2025-11-25", "capabilities": map[string]any{"tools": map[string]any{}},
			"serverInfo": map[string]any{"name": "rimcp", "version": p.manifest.Version}})
	case "notifications/initialized":
	case "tools/list":
		list := make([]any, 0, len(p.manifest.Tools))
		for _, t := range p.manifest.Tools {
			entry := map[string]any{"name": t.Name, "description": t.Description, "inputSchema": t.InputSchema}
			if len(t.Annotations) > 0 {
				entry["annotations"] = t.Annotations
			}
			list = append(list, entry)
		}
		p.result(id, map[string]any{"tools": list})
	case "tools/call":
		parameters, _ := decode(message["params"])
		params, _ := parameters.(map[string]any)
		p.call(id, params)
	default:
		if id != nil {
			p.failure(id, -32601, "Method not found: "+method)
		}
	}
}

func escape(value string) string {
	return strings.ReplaceAll(url.QueryEscape(value), "+", "%20")
}

func queryValue(value any) (string, error) {
	switch v := value.(type) {
	case []any:
		items := []string{}
		for _, item := range v {
			if item == nil {
				continue
			}
			s, ok := item.(string)
			if !ok {
				return "", errors.New("Array values must be strings.")
			}
			if strings.TrimSpace(s) != "" {
				items = append(items, s)
			}
		}
		return strings.Join(items, ","), nil
	case string:
		return v, nil
	case json.Number:
		return v.String(), nil
	case bool:
		if v {
			return "True", nil
		}
		return "False", nil
	default:
		return "", errors.New("Query values must be scalar values or arrays of strings.")
	}
}

func buildPath(t tool, args map[string]any) (string, error) {
	segments := strings.Split(t.PathTemplate, "/")
	for i, segment := range segments {
		if len(segment) > 2 && strings.HasPrefix(segment, "{") && strings.HasSuffix(segment, "}") {
			value, ok := args[segment[1:len(segment)-1]].(string)
			if !ok || strings.TrimSpace(value) == "" {
				return "", nil
			}
			segments[i] = escape(value)
		}
	}
	path := strings.TrimLeft(strings.Join(segments, "/"), "/")
	query := []string{}
	seen := map[string]bool{}
	for _, key := range t.QueryKeys {
		if seen[strings.ToLower(key)] {
			continue
		}
		seen[strings.ToLower(key)] = true
		if value := args[key]; value != nil {
			encoded, err := queryValue(value)
			if err != nil {
				return "", err
			}
			if encoded != "" {
				query = append(query, escape(key)+"="+escape(encoded))
			}
		}
	}
	if len(query) > 0 {
		path += "?" + strings.Join(query, "&")
	}
	return path, nil
}

func (p *proxy) call(id any, params map[string]any) {
	name, _ := params["name"].(string)
	if strings.TrimSpace(name) == "" {
		p.failure(id, -32602, "Missing tool name.")
		return
	}
	var selected *tool
	for i := range p.manifest.Tools {
		if p.manifest.Tools[i].Name == name {
			selected = &p.manifest.Tools[i]
			break
		}
	}
	if selected == nil {
		p.failure(id, -32602, "Unknown tool: "+name)
		return
	}
	args, _ := params["arguments"].(map[string]any)
	path, err := buildPath(*selected, args)
	if err != nil {
		p.failure(id, -32603, err.Error())
		return
	}
	if path == "" {
		p.result(id, toolError("Missing required argument."))
		return
	}
	var body io.Reader
	if len(selected.BodyKeys) > 0 {
		values := map[string]any{}
		for _, key := range selected.BodyKeys {
			if value := args[key]; value != nil {
				values[key] = value
			}
		}
		data, _ := json.Marshal(values)
		body = bytes.NewReader(data)
	}
	request, err := http.NewRequest(selected.Method, strings.TrimRight(p.baseURL, "/")+"/"+path, body)
	if err == nil {
		if body != nil {
			request.Header.Set("Content-Type", "application/json; charset=utf-8")
		}
		if strings.TrimSpace(p.token) != "" {
			request.Header.Set("Authorization", "Bearer "+p.token)
		}
		var response *http.Response
		response, err = p.client.Do(request)
		if err == nil {
			defer response.Body.Close()
			data, readErr := io.ReadAll(response.Body)
			if readErr == nil {
				p.respond(id, name, data, response.StatusCode >= 200 && response.StatusCode < 300)
				return
			}
			err = readErr
		}
	}
	p.result(id, toolError("Unable to reach RimMCP at "+p.baseURL+": "+err.Error()))
}

func (p *proxy) respond(id any, name string, data []byte, success bool) {
	var structured any
	if json.Valid(data) {
		structured, _ = decode(data)
	}
	content := []any{map[string]any{"type": "text", "text": summarize(name, structured, success)}}
	if strings.TrimSpace(string(data)) != "" {
		content = append(content, map[string]any{"type": "text", "text": string(data)})
	}
	result := map[string]any{"content": content, "isError": !success}
	if structured != nil {
		result["structuredContent"] = structured
	}
	p.result(id, result)
}

func summarize(name string, structured any, success bool) string {
	value, _ := structured.(map[string]any)
	if !success {
		if message, ok := value["error"].(string); ok {
			return message
		}
		return "RimMCP request failed."
	}
	tick := ""
	if number, ok := value["tick"].(json.Number); ok {
		tick = " at tick " + number.String()
	}
	if changed, ok := value["changed"].(bool); ok {
		return fmt.Sprintf("%s: command completed%s changed=%t.", name, tick, changed)
	}
	truncated := ""
	if value["truncated"] == true {
		truncated = " truncated"
	}
	if tick != "" {
		return name + ": returned structured data" + tick + truncated + "."
	}
	return name + ": returned structured data."
}

func toolError(message string) map[string]any {
	return map[string]any{"content": []any{map[string]any{"type": "text", "text": message}}, "isError": true}
}

func (p *proxy) result(id, result any) {
	p.output.Encode(map[string]any{"jsonrpc": "2.0", "id": id, "result": result})
}

func (p *proxy) failure(id any, code int, message string) {
	p.output.Encode(map[string]any{"jsonrpc": "2.0", "id": id, "error": map[string]any{"code": code, "message": message}})
}
