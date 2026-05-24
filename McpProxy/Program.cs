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

static string SummarizeToolResult(string toolName, JsonNode? structured, bool success)
{
    if (!success)
    {
        return structured?["error"]?.GetValue<string>() ?? "RiMCP request failed.";
    }

    var tick = structured?["tick"]?.GetValue<int?>();
    var truncated = structured?["truncated"]?.GetValue<bool?>() == true ? " truncated" : "";
    var notModified = structured?["notModified"]?.GetValue<bool?>() == true;
    if (notModified)
    {
        return toolName + ": no changes since requested tick.";
    }
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
    private static readonly string[] SharedKeys = { "mapId", "detail", "include", "limit", "cursor", "sinceTick", "idsOnly" };

    public static List<McpTool> Create()
    {
        return new List<McpTool>
        {
            Tool("get_game_context", "Get game-wide context: current map, time, storyteller/difficulty, loaded mods, and map ids.", Input(), args => QueryPath("v1/game-context", args)),
            Tool("get_colony_status", "Get a compact dashboard for the active colony with top risks and drill-down hints.", Input(), args => QueryPath("v1/colony-status", args)),
            Tool("list_pawns", "List pawns by validated filter.", Input(Prop("filter", PawnFilter())), args => QueryPath("v1/pawns", args, "filter")),
            Tool("get_pawn", "Get full details for one pawn by ThingID or load id. This endpoint always returns the full pawn record.", Required(Prop("id", Str("Pawn ThingID or load id."))), PathWithId("v1/pawns", "id")),
            Tool("list_resources", "List grouped map resources with compact food, medicine, stack, forbidden, roof, and rot context.", Input(), args => QueryPath("v1/resources", args)),
            Tool("list_work", "List work priorities, current jobs, draft state, schedules, and allowed-area context for core pawns.", Input(), args => QueryPath("v1/work", args)),
            Tool("list_production", "List production bills across colony bill givers.", Input(), args => QueryPath("v1/production", args)),
            Tool("get_bill", "Get full details for one production bill by bill id.", Required(Prop("id", Str("Bill id returned by list_production."))), PathWithId("v1/bills", "id")),
            Tool("list_zones", "List stockpiles, growing zones, and allowed areas.", Input(), args => QueryPath("v1/zones", args)),
            Tool("get_zone", "Get full details for one zone or area by id.", Required(Prop("id", Str("Zone id returned by list_zones."))), PathWithId("v1/zones", "id")),
            Tool("get_environment", "Get weather, season, game conditions, room temperature summaries, and hazards.", Input(), args => QueryPath("v1/environment", args)),
            Tool("get_power", "Get power grid, stored energy, generation/consumption, and powered component context.", Input(), args => QueryPath("v1/power", args)),
            Tool("list_threats", "List active threats such as hostile pawns, manhunters, predators, and fires.", Input(), args => QueryPath("v1/threats", args)),
            Tool("get_research", "Get current research and paged loaded research projects.", Input(), args => QueryPath("v1/research", args)),
            Tool("list_quests", "List active quests and quest state exposed by RimWorld.", Input(), args => QueryPath("v1/quests", args)),
            Tool("list_buildings", "List colony buildings. Default rows are compact; detail=normal adds size, passability, power, battery, fuel, and billGiver. Filter category by production, power, bed, storage, or any defName substring. Use include=[\"contents\"] for storage-slot contents, or get_building to inspect one building by id.", Input(Prop("category", BuildingCategory())), args => QueryPath("v1/buildings", args, "category")),
            Tool("get_building", "Get full details for one colony building by ThingID or load id. Returns size, passability, power, fuel, bill support, and contents={supported,items} for storage-slot buildings.", Required(Prop("id", Str("Building ThingID or load id from list_buildings."))), PathWithId("v1/buildings", "id")),
            Tool("list_world", "List world-level context: factions and world objects.", Input(), args => QueryPath("v1/world", args)),
            Tool("search_defs", "Search loaded game defs by kind, query, and category. Useful for mod-aware game knowledge.", Input(Prop("kind", DefKind()), Prop("query", Str("Search text for defName, label, or description.")), Prop("category", Str("Optional category filter."))), args => QueryPath("v1/defs/search", args, "kind", "query", "category")),
            Tool("get_def", "Get full detail for a loaded def by defName and optional kind.", Required(Prop("defName", Str("Def name to retrieve.")), Prop("kind", DefKind())), PathWithId("v1/defs", "defName", "kind"))
        };
    }

    private static McpTool Tool(string name, string description, JsonObject schema, Func<JsonObject?, string?> buildPath)
    {
        return new McpTool(name, description, schema, buildPath);
    }

    private static Func<JsonObject?, string?> PathWithId(string prefix, string idName, params string[] extraQuery)
    {
        return args =>
        {
            var id = args?[idName]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }
            return QueryPath(prefix + "/" + Uri.EscapeDataString(id), args, extraQuery.Where(key => key != idName).ToArray());
        };
    }

    private static string QueryPath(string path, JsonObject? args, params string[] extraKeys)
    {
        var keys = SharedKeys.Concat(extraKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var query = new List<string>();
        if (args != null)
        {
            foreach (var key in keys)
            {
                if (!args.TryGetPropertyValue(key, out var value) || value == null)
                {
                    continue;
                }
                var encoded = EncodeValue(value);
                if (!string.IsNullOrEmpty(encoded))
                {
                    query.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(encoded));
                }
            }
        }
        return query.Count == 0 ? path : path + "?" + string.Join("&", query);
    }

    private static string EncodeValue(JsonNode value)
    {
        if (value is JsonArray array)
        {
            return string.Join(",", array.Select(item => item?.GetValue<string>()).Where(item => !string.IsNullOrWhiteSpace(item)));
        }
        return value.GetValue<object>()?.ToString() ?? "";
    }

    private static JsonObject Input(params JsonObject[] properties)
    {
        return Input(null, properties);
    }

    private static JsonObject Required(JsonObject requiredProperty, params JsonObject[] optionalProperties)
    {
        return Input(new[] { requiredProperty["name"]!.GetValue<string>() }, new[] { requiredProperty }.Concat(optionalProperties).ToArray());
    }

    private static JsonObject Input(string[]? required, params JsonObject[] properties)
    {
        var props = SharedProperties();
        foreach (var property in properties)
        {
            var name = property["name"]!.GetValue<string>();
            props[name] = property["schema"]!.DeepClone();
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = required == null ? new JsonArray() : new JsonArray(required.Select(item => JsonValue.Create(item)).ToArray()),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject Prop(string name, JsonObject schema)
    {
        return new JsonObject
        {
            ["name"] = name,
            ["schema"] = schema
        };
    }

    private static JsonObject SharedProperties()
    {
        return new JsonObject
        {
            ["mapId"] = Str("Optional RimWorld map id. Defaults to current map."),
            ["detail"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray("summary", "normal", "full"),
                ["description"] = "Payload detail level. Defaults to summary. full also includes tool-specific expensive sections."
            },
            ["include"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Optional expensive sections to include without requesting full detail. Known values include needs, health, skills, work, gear, relations, and contents."
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 1,
                ["maximum"] = 200,
                ["description"] = "Maximum items to return for paged tools. Defaults to 50."
            },
            ["cursor"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 0,
                ["description"] = "Cursor returned by a previous paged response."
            },
            ["sinceTick"] = new JsonObject
            {
                ["type"] = "integer",
                ["minimum"] = 0,
                ["description"] = "Return notModified when the game tick has not advanced past this value."
            },
            ["idsOnly"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Return identifiers only for list tools when supported."
            }
        };
    }

    private static JsonObject Str(string description)
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = description
        };
    }

    private static JsonObject DefKind()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("thing", "recipe", "research", "workType", "stat", "terrain", "biome", "pawnKind", "designation", "job", "weather"),
            ["description"] = "Loaded def kind to search or retrieve."
        };
    }

    private static JsonObject BuildingCategory()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Optional building category or defName substring. Known categories: production, power, bed, storage.",
            ["examples"] = new JsonArray("production", "power", "bed", "storage", "Shelf")
        };
    }

    private static JsonObject PawnFilter()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("core", "colonist", "colonists", "slave", "slaves", "prisoner", "prisoners", "guest", "guests", "colonyAnimal", "colonyAnimals", "wildAnimal", "wildAnimals", "hostile", "hostiles", "animals", "wildlife", "threats", "other", "others", "all"),
            ["description"] = "Pawn filter to list. Defaults to core."
        };
    }
}
