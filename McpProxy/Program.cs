using System.Collections;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RiMCP.Shared;

ToolManifest.Validate(null);

var bridgeUrl = Environment.GetEnvironmentVariable("RIMCP_URL") ?? "http://127.0.0.1:39571";
var token = Environment.GetEnvironmentVariable("RIMCP_TOKEN") ?? "";

using var http = new HttpClient { BaseAddress = new Uri(bridgeUrl.TrimEnd('/') + "/") };
if (!string.IsNullOrWhiteSpace(token))
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

var tools = ToolManifest.McpTools.ToArray();

string? line;
while ((line = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonNode? request;
    try
    {
        request = JsonNode.Parse(line);
        if (request is JsonObject parsed)
        {
            // JsonObject detects duplicate property names when its dictionary is first accessed.
            _ = parsed.Count;
        }
    }
    catch (JsonException ex)
    {
        WriteError(null, -32700, "Parse error: " + ex.Message);
        continue;
    }
    catch (ArgumentException)
    {
        WriteError(null, -32600, "Request contains duplicate property names.");
        continue;
    }

    if (request is not JsonObject message)
    {
        WriteError(null, -32600, "Request must be an object.");
        continue;
    }

    var id = message["id"];
    if (id != null && id.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number))
    {
        WriteError(null, -32600, "Request id must be a string, number, or null.");
        continue;
    }
    if (message["jsonrpc"] is not JsonValue version ||
        !version.TryGetValue<string>(out var jsonrpc) || jsonrpc != "2.0")
    {
        WriteError(id, -32600, "Request must use JSON-RPC 2.0.");
        continue;
    }
    if (message["method"] is not JsonValue methodValue ||
        !methodValue.TryGetValue<string>(out var method) || string.IsNullOrWhiteSpace(method))
    {
        WriteError(id, -32600, "Method must be a non-empty string.");
        continue;
    }

    // Notifications have no request id and must not receive a response.
    if (!message.ContainsKey("id"))
    {
        continue;
    }

    try
    {
        switch (method)
        {
            case "initialize":
                WriteResult(id, new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25",
                    ["capabilities"] = new JsonObject
                    {
                        ["tools"] = new JsonObject()
                    },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = "rimcp",
                        ["version"] = "0.4.0"
                    }
                });
                break;

            case "notifications/initialized":
                break;

            case "tools/list":
                WriteResult(id, new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(ToolToJson).ToArray())
                });
                break;

            case "tools/call":
                await HandleToolCall(id, message["params"] as JsonObject);
                break;

            default:
                if (id != null)
                {
                    WriteError(id, -32601, "Method not found: " + method);
                }
                break;
        }
    }
    catch (Exception ex)
    {
        WriteError(id, -32603, ex.GetType().Name + ": " + ex.Message);
    }
}

async Task HandleToolCall(JsonNode? id, JsonObject? parameters)
{
    var name = parameters?["name"]?.GetValue<string>();
    if (string.IsNullOrWhiteSpace(name))
    {
        WriteError(id, -32602, "Missing tool name.");
        return;
    }

    var tool = tools.FirstOrDefault(t => t.Name == name);
    if (tool == null)
    {
        WriteError(id, -32602, "Unknown tool: " + name);
        return;
    }

    var arguments = parameters?["arguments"] as JsonObject;
    var path = BuildPath(tool, arguments);
    if (path == null)
    {
        WriteResult(id, ToolError("Missing required argument."));
        return;
    }

    HttpResponseMessage response;
    string text;
    try
    {
        using var request = new HttpRequestMessage(new HttpMethod(tool.Method), path);
        var body = BuildBody(tool, arguments);
        if (body != null)
        {
            request.Content = new StringContent(body.ToJsonString(new JsonSerializerOptions { WriteIndented = false }), Encoding.UTF8, "application/json");
        }
        response = await http.SendAsync(request);
        text = await response.Content.ReadAsStringAsync();
    }
    catch (Exception ex)
    {
        WriteResult(id, ToolError("Unable to reach RiMCP at " + bridgeUrl + ": " + ex.Message));
        return;
    }

    JsonNode? structured = null;
    try
    {
        structured = JsonNode.Parse(text);
    }
    catch
    {
    }

    var content = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "text",
            ["text"] = SummarizeToolResult(tool.Name, structured, response.IsSuccessStatusCode)
        }
    };

    if (!string.IsNullOrWhiteSpace(text))
    {
        content.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = text
        });
    }

    var result = new JsonObject
    {
        ["content"] = content,
        ["isError"] = !response.IsSuccessStatusCode
    };

    if (structured != null)
    {
        result["structuredContent"] = structured;
    }

    WriteResult(id, result);
    response.Dispose();
}

static JsonObject ToolToJson(ToolSpec tool)
{
    var json = new JsonObject
    {
        ["name"] = tool.Name,
        ["description"] = tool.Description,
        ["inputSchema"] = ToJsonNode(tool.InputSchema)
    };
    if (tool.Annotations.Count > 0)
    {
        json["annotations"] = ToJsonNode(tool.Annotations);
    }
    return json;
}

static string? BuildPath(ToolSpec tool, JsonObject? args)
{
    var path = ExpandPath(tool.PathTemplate, args);
    if (path == null)
    {
        return null;
    }

    var query = new List<string>();
    foreach (var key in tool.QueryKeys.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (args == null || !args.TryGetPropertyValue(key, out var value) || value == null)
        {
            continue;
        }
        var encoded = EncodeValue(value);
        if (!string.IsNullOrEmpty(encoded))
        {
            query.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(encoded));
        }
    }

    var relativePath = path.TrimStart('/');
    return query.Count == 0 ? relativePath : relativePath + "?" + string.Join("&", query);
}

static string? ExpandPath(string pathTemplate, JsonObject? args)
{
    var segments = pathTemplate.Split('/');
    for (int i = 0; i < segments.Length; i++)
    {
        var segment = segments[i];
        if (segment.Length <= 2 || segment[0] != '{' || segment[^1] != '}')
        {
            continue;
        }

        var key = segment[1..^1];
        var value = args?[key]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        segments[i] = Uri.EscapeDataString(value);
    }
    return string.Join("/", segments);
}

static JsonObject? BuildBody(ToolSpec tool, JsonObject? args)
{
    if (tool.BodyKeys.Length == 0)
    {
        return null;
    }

    var body = new JsonObject();
    if (args == null)
    {
        return body;
    }

    foreach (var key in tool.BodyKeys)
    {
        if (args.TryGetPropertyValue(key, out var value) && value != null)
        {
            body[key] = value.DeepClone();
        }
    }
    return body;
}

static string EncodeValue(JsonNode value)
{
    if (value is JsonArray array)
    {
        return string.Join(",", array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)));
    }
    return value.GetValue<object>()?.ToString() ?? "";
}

static JsonNode? ToJsonNode(object? value)
{
    if (value == null)
    {
        return null;
    }
    if (value is string text)
    {
        return JsonValue.Create(text);
    }
    if (value is bool boolean)
    {
        return JsonValue.Create(boolean);
    }
    if (value is int integer)
    {
        return JsonValue.Create(integer);
    }
    if (value is long longInteger)
    {
        return JsonValue.Create(longInteger);
    }
    if (value is float floatValue)
    {
        return JsonValue.Create(floatValue);
    }
    if (value is double doubleValue)
    {
        return JsonValue.Create(doubleValue);
    }
    if (value is decimal decimalValue)
    {
        return JsonValue.Create(decimalValue);
    }
    if (value is IDictionary dictionary)
    {
        var json = new JsonObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key == null)
            {
                continue;
            }
            json[entry.Key.ToString()!] = ToJsonNode(entry.Value);
        }
        return json;
    }
    if (value is IEnumerable enumerable)
    {
        var json = new JsonArray();
        foreach (var item in enumerable)
        {
            json.Add(ToJsonNode(item));
        }
        return json;
    }
    return JsonValue.Create(value.ToString());
}

static string SummarizeToolResult(string toolName, JsonNode? structured, bool success)
{
    if (!success)
    {
        return structured?["error"]?.GetValue<string>() ?? "RiMCP request failed.";
    }

    var tick = structured?["tick"]?.GetValue<int?>();
    var changed = structured?["changed"]?.GetValue<bool?>();
    if (changed.HasValue)
    {
        return tick.HasValue
            ? toolName + ": command completed at tick " + tick.Value + " changed=" + changed.Value.ToString().ToLowerInvariant() + "."
            : toolName + ": command completed changed=" + changed.Value.ToString().ToLowerInvariant() + ".";
    }

    var truncated = structured?["truncated"]?.GetValue<bool?>() == true ? " truncated" : "";
    return tick.HasValue
        ? toolName + ": returned structured data at tick " + tick.Value + truncated + "."
        : toolName + ": returned structured data.";
}

static JsonObject ToolError(string message)
{
    return new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = message
            }
        },
        ["isError"] = true
    };
}

static void WriteResult(JsonNode? id, JsonNode result)
{
    WriteMessage(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = result
    });
}

static void WriteError(JsonNode? id, int code, string message)
{
    WriteMessage(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    });
}

static void WriteMessage(JsonObject message)
{
    Console.Out.WriteLine(message.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    Console.Out.Flush();
}
