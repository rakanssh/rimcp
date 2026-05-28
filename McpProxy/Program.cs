using System.Net.Http.Headers;
using System.Text;
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
                        ["version"] = "0.3.0"
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
    var path = tool.Endpoint.BuildPath(arguments);
    if (path == null)
    {
        WriteResult(id, ToolError("Missing required argument."));
        return;
    }

    HttpResponseMessage response;
    string text;
    try
    {
        using var request = new HttpRequestMessage(new HttpMethod(tool.Endpoint.Method), path);
        var body = tool.Endpoint.BuildBody(arguments);
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

internal sealed record McpTool(
    string Name,
    string Description,
    JsonObject InputSchema,
    ToolEndpoint Endpoint,
    JsonObject Annotations)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone()
        };
        if (Annotations.Count > 0)
        {
            json["annotations"] = Annotations.DeepClone();
        }
        return json;
    }
}

internal sealed record ToolEndpoint(
    string Method,
    Func<JsonObject?, string?> BuildPath,
    Func<JsonObject?, JsonObject?> BuildBody)
{
    public static ToolEndpoint Get(Func<JsonObject?, string?> buildPath)
    {
        return new ToolEndpoint("GET", buildPath, _ => null);
    }

    public static ToolEndpoint Put(Func<JsonObject?, string?> buildPath, Func<JsonObject?, JsonObject?> buildBody)
    {
        return new ToolEndpoint("PUT", buildPath, buildBody);
    }

    public static ToolEndpoint Put(string pathTemplate, Func<JsonObject?, JsonObject?> buildBody)
    {
        return Put(args => ExpandPath(pathTemplate, args), buildBody);
    }

    public static ToolEndpoint Post(Func<JsonObject?, string?> buildPath, Func<JsonObject?, JsonObject?> buildBody)
    {
        return new ToolEndpoint("POST", buildPath, buildBody);
    }

    public static ToolEndpoint Post(string pathTemplate, Func<JsonObject?, JsonObject?> buildBody)
    {
        return Post(args => ExpandPath(pathTemplate, args), buildBody);
    }

    private static string? ExpandPath(string pathTemplate, JsonObject? args)
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
}

internal static class ToolCatalog
{
    private static readonly string[] SharedKeys = { "mapId", "detail", "include", "limit", "cursor", "idsOnly" };

    public static List<McpTool> Create()
    {
        return new List<McpTool>
        {
            Tool("get_game_context", "Get game-wide context: current map, time, storyteller/difficulty, loaded mods, and map ids.", Input(), ToolEndpoint.Get(args => QueryPath("v1/game-context", args)), ReadOnly()),
            Tool("get_colony_status", "Get a compact dashboard for the active colony with top risks and drill-down hints.", Input(), ToolEndpoint.Get(args => QueryPath("v1/colony-status", args)), ReadOnly()),
            Tool("list_pawns", "List pawns by validated filter. Use include=[\"assignments\"] for Assign-style pawn settings and include=[\"assignmentOptions\"] for valid policy, area, medical, hostility, medicine, and interaction options.", Input(Prop("filter", PawnFilter())), ToolEndpoint.Get(args => QueryPath("v1/pawns", args, "filter")), ReadOnly()),
            Tool("get_pawn", "Get full details for one pawn by ThingID or load id. This endpoint always returns the full pawn record.", Required(Prop("id", Str("Pawn ThingID or load id."))), ToolEndpoint.Get(PathWithId("v1/pawns", "id")), ReadOnly()),
            Tool("list_resources", "List grouped map resources with compact food, medicine, stack, forbidden, roof, and rot context.", Input(), ToolEndpoint.Get(args => QueryPath("v1/resources", args)), ReadOnly()),
            Tool("list_work", "List work priorities, current jobs, draft state, schedules, and allowed-area context for core pawns.", Input(), ToolEndpoint.Get(args => QueryPath("v1/work", args)), ReadOnly()),
            Tool("list_production", "List production bills across colony bill givers.", Input(), ToolEndpoint.Get(args => QueryPath("v1/production", args)), ReadOnly()),
            Tool("get_bill", "Get full details for one production bill by bill id.", Required(Prop("id", Str("Bill id returned by list_production."))), ToolEndpoint.Get(PathWithId("v1/bills", "id")), ReadOnly()),
            Tool("set_bill", "Update or delete an existing production bill by bill id. Use mode=update to change fields, or mode=delete to remove it.", SetBillInput(), ToolEndpoint.Put("v1/bills/{id}", Body("mapId", "mode", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName")), IdempotentDestructiveMutation()),
            Tool("list_workshops", "List colony workshops/workbenches that can hold production bills, with current bill counts and available recipe counts. Use include=[\"recipes\"] or detail=full to include addable recipe summaries.", Input(), ToolEndpoint.Get(args => QueryPath("v1/workshops", args)), ReadOnly()),
            Tool("get_workshop", "Get one workshop by ThingID or load id, including current bills and available recipes that can be added at that bench.", Required(Prop("id", Str("Workshop ThingID or load id from list_workshops."))), ToolEndpoint.Get(PathWithId("v1/workshops", "id")), ReadOnly()),
            Tool("add_bill_to_workshop", "Add a new bill to a workshop for a recipe. This is intentionally additive and can create duplicate recipe bills.", AddBillToWorkshopInput(), ToolEndpoint.Post("v1/workshops/{workshopId}/bills", Body("mapId", "recipeDefName", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName")), AdditiveMutation()),
            Tool("list_zones", "List stockpiles, growing zones, and allowed areas.", Input(), ToolEndpoint.Get(args => QueryPath("v1/zones", args)), ReadOnly()),
            Tool("get_zone", "Get full details for one zone or area by id.", Required(Prop("id", Str("Zone id returned by list_zones."))), ToolEndpoint.Get(PathWithId("v1/zones", "id")), ReadOnly()),
            Tool("get_environment", "Get weather, season, game conditions, room temperature summaries, and hazards.", Input(), ToolEndpoint.Get(args => QueryPath("v1/environment", args)), ReadOnly()),
            Tool("get_power", "Get power grid, stored energy, generation/consumption, and powered component context.", Input(), ToolEndpoint.Get(args => QueryPath("v1/power", args)), ReadOnly()),
            Tool("list_threats", "List active threats such as hostile pawns, manhunters, predators, and fires.", Input(), ToolEndpoint.Get(args => QueryPath("v1/threats", args)), ReadOnly()),
            Tool("get_research", "Get current research and paged loaded research projects.", Input(), ToolEndpoint.Get(args => QueryPath("v1/research", args)), ReadOnly()),
            Tool("list_ideoligions", "List loaded Ideology ideoligions with summary identity, memes, culture, and mode flags. Returns active=false with an empty list when Ideology is inactive.", Input(), ToolEndpoint.Get(args => QueryPath("v1/ideoligions", args)), ReadOnly()),
            Tool("get_ideoligion", "Get full details for one ideoligion by id returned from list_ideoligions, including description and precepts.", Required(Prop("id", Str("Ideoligion id returned by list_ideoligions."))), ToolEndpoint.Get(PathWithId("v1/ideoligions", "id")), ReadOnly()),
            Tool("list_quests", "List active quests and quest state exposed by RimWorld.", Input(), ToolEndpoint.Get(args => QueryPath("v1/quests", args)), ReadOnly()),
            Tool("list_buildings", "List colony buildings. Default rows are compact; detail=normal adds size, passability, power, battery, fuel, and billGiver. Filter category by production, power, bed, storage, or any defName substring. Use include=[\"contents\"] for storage-slot contents, or get_building to inspect one building by id.", Input(Prop("category", BuildingCategory())), ToolEndpoint.Get(args => QueryPath("v1/buildings", args, "category")), ReadOnly()),
            Tool("get_building", "Get full details for one colony building by ThingID or load id. Returns size, passability, power, fuel, bill support, and contents={supported,items} for storage-slot buildings.", Required(Prop("id", Str("Building ThingID or load id from list_buildings."))), ToolEndpoint.Get(PathWithId("v1/buildings", "id")), ReadOnly()),
            Tool("list_world", "List world-level context: factions and world objects.", Input(), ToolEndpoint.Get(args => QueryPath("v1/world", args)), ReadOnly()),
            Tool("search_defs", "Search loaded game defs by kind, query, and category. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", Input(Prop("kind", DefKind()), Prop("query", Str("Search text for defName, label, or description.")), Prop("category", Str("Optional category filter."))), ToolEndpoint.Get(args => QueryPath("v1/defs/search", args, "kind", "query", "category")), ReadOnly()),
            Tool("get_def", "Get full detail for a loaded def by defName and optional kind. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", Required(Prop("defName", Str("Def name to retrieve.")), Prop("kind", DefKind())), ToolEndpoint.Get(PathWithId("v1/defs", "defName", "kind")), ReadOnly()),
            Tool("set_pawn_drafted", "Draft or undraft one player-controlled pawn by ThingID or load id.", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("drafted", Bool("Whether the pawn should be drafted."))), ToolEndpoint.Put("v1/pawns/{pawnId}/drafted", Body("mapId", "drafted")), IdempotentMutation()),
            Tool("set_pawn_work_priority", "Set one player-controlled pawn's work priority for a work type. Priority 0 disables the work type; 1 is highest and 4 is lowest.", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("workTypeDefName", Str("WorkTypeDef defName.")), Prop("priority", Int("Priority from 0 to 4.", 0, 4))), ToolEndpoint.Put("v1/pawns/{pawnId}/work/{workTypeDefName}", Body("mapId", "priority")), IdempotentMutation()),
            Tool("set_pawn_assignment", "Set one Assign-style pawn setting: schedule, policy, medical care, self-tend, hostility response, allowed area, carried medicine, or prisoner interaction.", PawnAssignmentInput(), ToolEndpoint.Put("v1/pawns/{pawnId}/assignments/{assignmentKind}", Body("mapId", "assignments", "policyKind", "policyId", "care", "enabled", "mode", "areaId", "count", "medicineDefName", "interactionModeDefName")), IdempotentMutation()),
            Tool("designate_animal", "Set or clear one animal's hunt, tame, or slaughter designation.", RequiredWithMap(Prop("pawnId", Str("Animal pawn ThingID or load id.")), Prop("action", AnimalDesignationAction())), ToolEndpoint.Put("v1/animals/{pawnId}/designation", Body("mapId", "action")), IdempotentDestructiveMutation()),
            Tool("set_animal_training", "Set desired training flags for one colony animal by TrainableDef defName.", RequiredWithMap(Prop("pawnId", Str("Colony animal pawn ThingID or load id.")), Prop("assignments", TrainingAssignments())), ToolEndpoint.Put("v1/animals/{pawnId}/training", Body("mapId", "assignments")), IdempotentMutation()),
            Tool("set_research_project", "Set the current research project by ResearchProjectDef defName.", RequiredOnly(Prop("projectDefName", Str("ResearchProjectDef defName."))), ToolEndpoint.Put(_ => "v1/research/current", Body("projectDefName")), IdempotentMutation())
        };
    }

    private static McpTool Tool(string name, string description, JsonObject schema, ToolEndpoint endpoint, JsonObject annotations)
    {
        return new McpTool(name, description, schema, endpoint, annotations);
    }

    private static JsonObject ReadOnly()
    {
        return new JsonObject
        {
            ["readOnlyHint"] = true,
            ["openWorldHint"] = false
        };
    }

    private static JsonObject IdempotentMutation()
    {
        return Mutation(idempotent: true, destructive: false);
    }

    private static JsonObject IdempotentDestructiveMutation()
    {
        return Mutation(idempotent: true, destructive: true);
    }

    private static JsonObject AdditiveMutation()
    {
        return Mutation(idempotent: false, destructive: false);
    }

    private static JsonObject Mutation(bool idempotent, bool destructive)
    {
        return new JsonObject
        {
            ["readOnlyHint"] = false,
            ["idempotentHint"] = idempotent,
            ["destructiveHint"] = destructive,
            ["openWorldHint"] = false
        };
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

    private static Func<JsonObject?, JsonObject?> Body(params string[] keys)
    {
        return args =>
        {
            var body = new JsonObject();
            if (args != null)
            {
                foreach (var key in keys)
                {
                    if (args.TryGetPropertyValue(key, out var value) && value != null)
                    {
                        body[key] = value.DeepClone();
                    }
                }
            }
            return body;
        };
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

    private static JsonObject RequiredWithMap(params JsonObject[] properties)
    {
        return InputWithOptionalMap(true, properties.Select(property => property["name"]!.GetValue<string>()).ToArray(), properties);
    }

    private static JsonObject RequiredOnly(params JsonObject[] properties)
    {
        return InputWithOptionalMap(false, properties.Select(property => property["name"]!.GetValue<string>()).ToArray(), properties);
    }

    private static JsonObject PawnAssignmentInput()
    {
        var props = MapProperty();
        props["pawnId"] = Str("Pawn ThingID or load id.");
        props["assignmentKind"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("schedule", "policy", "medicalCare", "selfTend", "hostilityResponse", "allowedArea", "carryMedicine", "prisonerInteraction"),
            ["description"] = "Assign-style setting to change."
        };
        props["assignments"] = ScheduleAssignments();
        props["policyKind"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("apparel", "food", "drug", "reading"),
            ["description"] = "Policy column to set when assignmentKind is policy."
        };
        props["policyId"] = Str("Policy id returned by list_pawns include=[\"assignmentOptions\"].");
        props["care"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("NoCare", "NoMeds", "HerbalOrWorse", "NormalOrWorse", "Best"),
            ["description"] = "Medical care category."
        };
        props["enabled"] = Bool("Whether self-tend should be enabled.");
        props["mode"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("Ignore", "Attack", "Flee"),
            ["description"] = "Hostility response mode."
        };
        props["areaId"] = Str("Allowed area id returned by assignmentOptions, or unrestricted to clear it.");
        props["count"] = Int("Medicine count to carry.", 0, 3);
        props["medicineDefName"] = Str("Optional medicine ThingDef defName from assignmentOptions.");
        props["interactionModeDefName"] = Str("PrisonerInteractionModeDef defName, such as AttemptRecruit, ReduceResistance, Convert, or Release.");

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray("pawnId", "assignmentKind"),
            ["oneOf"] = new JsonArray(
                AssignmentBranch("schedule", "assignments"),
                AssignmentBranch("policy", "policyKind", "policyId"),
                AssignmentBranch("medicalCare", "care"),
                AssignmentBranch("selfTend", "enabled"),
                AssignmentBranch("hostilityResponse", "mode"),
                AssignmentBranch("allowedArea", "areaId"),
                AssignmentBranch("carryMedicine", "count"),
                AssignmentBranch("prisonerInteraction", "interactionModeDefName")),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject SetBillInput()
    {
        JsonObject props = MapProperty();
        props["id"] = Str("Bill id returned by list_production or get_workshop.");
        props["mode"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("update", "delete"),
            ["description"] = "Whether to update fields on the bill or delete it."
        };
        AddBillSettingProperties(props);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray("id", "mode"),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject AddBillToWorkshopInput()
    {
        JsonObject props = MapProperty();
        props["workshopId"] = Str("Workshop ThingID or load id from list_workshops.");
        props["recipeDefName"] = Str("RecipeDef defName from list_workshops include=[\"recipes\"] or get_workshop.");
        AddBillSettingProperties(props);
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray("workshopId", "recipeDefName"),
            ["additionalProperties"] = false
        };
    }

    private static void AddBillSettingProperties(JsonObject props)
    {
        props["suspended"] = Bool("Whether the bill should be suspended.");
        props["repeatModeDefName"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("Forever", "RepeatCount", "TargetCount"),
            ["description"] = "BillRepeatModeDef defName."
        };
        props["repeatCount"] = Int("Repeat count for RepeatCount mode.", 1, 999999);
        props["targetCount"] = Int("Target count for TargetCount mode.", 1, 999999);
        props["pauseWhenSatisfied"] = Bool("Whether the bill pauses when its target is satisfied.");
        props["unpauseWhenYouHave"] = Int("Inventory count where a paused bill can unpause.", 1, 999999);
        props["ingredientSearchRadius"] = Number("Ingredient search radius from 0 to RimWorld's maximum.");
        props["allowedSkillMin"] = Int("Minimum allowed pawn skill.", 0, 20);
        props["allowedSkillMax"] = Int("Maximum allowed pawn skill.", 0, 20);
        props["storeModeDefName"] = new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("DropOnFloor", "BestStockpile", "SpecificStockpile"),
            ["description"] = "BillStoreModeDef defName. SpecificStockpile is rejected until stockpile targeting is supported."
        };
    }

    private static JsonObject AssignmentBranch(string assignmentKind, params string[] required)
    {
        return new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["assignmentKind"] = new JsonObject { ["const"] = assignmentKind }
            },
            ["required"] = new JsonArray(new[] { "assignmentKind" }.Concat(required).Select(item => JsonValue.Create(item)).ToArray())
        };
    }

    private static JsonObject InputWithOptionalMap(bool includeMapId, string[] required, params JsonObject[] properties)
    {
        var props = includeMapId ? MapProperty() : new JsonObject();
        foreach (var property in properties)
        {
            var name = property["name"]!.GetValue<string>();
            props[name] = property["schema"]!.DeepClone();
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["required"] = new JsonArray(required.Select(item => JsonValue.Create(item)).ToArray()),
            ["additionalProperties"] = false
        };
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
                ["description"] = "Optional expensive sections to include without requesting full detail. Known values include needs, health, skills, work, assignments, assignmentOptions, training, gear, relations, contents, and recipes."
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
            ["idsOnly"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Return identifiers only for list tools when supported."
            }
        };
    }

    private static JsonObject MapProperty()
    {
        return new JsonObject
        {
            ["mapId"] = Str("Optional RimWorld map id. Defaults to current map.")
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

    private static JsonObject Bool(string description)
    {
        return new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = description
        };
    }

    private static JsonObject Int(string description, int minimum, int maximum)
    {
        return new JsonObject
        {
            ["type"] = "integer",
            ["minimum"] = minimum,
            ["maximum"] = maximum,
            ["description"] = description
        };
    }

    private static JsonObject Number(string description)
    {
        return new JsonObject
        {
            ["type"] = "number",
            ["description"] = description
        };
    }

    private static JsonObject ScheduleAssignments()
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["description"] = "Schedule assignment ranges. Ranges are half-open, non-overlapping, and use hours 0 through 24.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["startHour"] = Int("Inclusive start hour from 0 to 23.", 0, 23),
                    ["endHour"] = Int("Exclusive end hour from 1 to 24.", 1, 24),
                    ["assignmentDefName"] = Str("TimeAssignmentDef defName, such as Anything, Work, Joy, Sleep, or Meditate.")
                },
                ["required"] = new JsonArray("startHour", "endHour", "assignmentDefName"),
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject TrainingAssignments()
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 1,
            ["description"] = "Training desired-state changes for one colony animal.",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["trainableDefName"] = Str("TrainableDef defName, such as Tameness, Obedience, or Release."),
                    ["wanted"] = Bool("Whether this trainable should be wanted.")
                },
                ["required"] = new JsonArray("trainableDefName", "wanted"),
                ["additionalProperties"] = false
            }
        };
    }

    private static JsonObject AnimalDesignationAction()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["enum"] = new JsonArray("hunt", "tame", "slaughter", "none"),
            ["description"] = "Animal designation action. none clears hunt, tame, and slaughter designations."
        };
    }

    private static JsonObject DefKind()
    {
        return new JsonObject
        {
            ["type"] = "string",
            ["description"] = "Loaded def kind to search or retrieve. Accepts any Def type name without the Def suffix, compatibility aliases such as research/designation, or all.",
            ["examples"] = new JsonArray("thing", "hediff", "ability", "trainable", "billStoreMode", "billRepeatMode", "timeAssignment", "all")
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
