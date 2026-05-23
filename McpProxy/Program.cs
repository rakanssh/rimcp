using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

var bridgeUrl = Environment.GetEnvironmentVariable("RIMCP_URL") ?? "http://127.0.0.1:39571";
var token = Environment.GetEnvironmentVariable("RIMCP_TOKEN") ?? "";

using var http = new HttpClient { BaseAddress = new Uri(bridgeUrl.TrimEnd('/') + "/") };
if (!string.IsNullOrWhiteSpace(token))
{
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
}

var tools = ToolCatalog.Create();

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
    }
    catch (Exception ex)
    {
        WriteError(null, -32700, "Parse error: " + ex.Message);
        continue;
    }

    var id = request?["id"]?.DeepClone();
    var method = request?["method"]?.GetValue<string>();
    if (method == null)
    {
        WriteError(id, -32600, "Missing method.");
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
                        ["version"] = "0.1.0"
                    }
                });
                break;

            case "notifications/initialized":
                break;

            case "tools/list":
                WriteResult(id, new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(t => t.ToJson()).ToArray())
                });
                break;

            case "tools/call":
                await HandleToolCall(id, request?["params"] as JsonObject);
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
    var path = tool.BuildPath(arguments);
    if (path == null)
    {
        WriteResult(id, ToolError("Missing required argument."));
        return;
    }

    HttpResponseMessage response;
    string text;
    try
    {
        response = await http.GetAsync(path);
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

    var result = new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        },
        ["isError"] = !response.IsSuccessStatusCode
    };

    if (structured != null)
    {
        result["structuredContent"] = structured;
    }

    WriteResult(id, result);
    response.Dispose();
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

internal sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    Func<JsonObject?, string?> BuildPath)
{
    public JsonObject ToJson()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone()
        };
    }
}

internal static class ToolCatalog
{
    public static List<McpTool> Create()
    {
        var empty = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false
        };
        var pawnId = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["id"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Pawn ThingID or load ID returned by a RiMCP tool."
                }
            },
            ["required"] = new JsonArray("id"),
            ["additionalProperties"] = false
        };

        return new List<McpTool>
        {
            new("get_colony_summary", "Get high-level colony status for the active map.", empty, _ => "v1/colony-summary"),
            new("list_core_pawns", "List colony core pawns on the active map: colonists, slaves, and prisoners.", empty, _ => "v1/core-pawns"),
            new("get_pawn", "Get detailed read-only information for one spawned pawn on the active map, including health.", pawnId, PathForPawn),
            new("list_inventory", "List stored colony resources as shown by RimWorld's resource readout.", empty, _ => "v1/inventory"),
            new("list_bills", "List production bills on colony workbenches.", empty, _ => "v1/bills"),
            new("list_alerts", "List recent visible letters and alerts available to RiMCP.", empty, _ => "v1/alerts"),
            new("list_animals", "List player-faction animals on the active map.", empty, _ => "v1/animals"),
            new("list_hostiles", "List spawned hostile threats on the active map.", empty, _ => "v1/hostiles")
        };
    }

    private static string? PathForPawn(JsonObject? args)
    {
        var id = args?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        return "v1/pawns/" + Uri.EscapeDataString(id);
    }
}
