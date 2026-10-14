#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RiMCP.Shared
{
    internal sealed class ToolSpec
    {
        public readonly string HandlerId;
        public readonly string Name;
        public readonly string Description;
        public readonly string Method;
        public readonly string PathTemplate;
        public readonly string[] QueryKeys;
        public readonly string[] BodyKeys;
        public readonly Dictionary<string, object> InputSchema;
        public readonly Dictionary<string, object> Annotations;
        public readonly bool ExposeAsMcp;

        public ToolSpec(string handlerId, string name, string description, string method, string pathTemplate, string[] queryKeys, string[] bodyKeys, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, bool exposeAsMcp)
        {
            HandlerId = handlerId;
            Name = name;
            Description = description;
            Method = method;
            PathTemplate = pathTemplate;
            QueryKeys = queryKeys ?? new string[0];
            BodyKeys = bodyKeys ?? new string[0];
            InputSchema = inputSchema ?? new Dictionary<string, object>();
            Annotations = annotations ?? new Dictionary<string, object>();
            ExposeAsMcp = exposeAsMcp;
        }

        public Dictionary<string, object> ToDto()
        {
            return J(
                F("handlerId", HandlerId),
                F("name", Name),
                F("description", Description),
                F("method", Method),
                F("pathTemplate", PathTemplate),
                F("queryKeys", QueryKeys),
                F("bodyKeys", BodyKeys),
                F("inputSchema", InputSchema),
                F("annotations", Annotations),
                F("exposeAsMcp", ExposeAsMcp));
        }

        private static KeyValuePair<string, object> F(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }

        private static Dictionary<string, object> J(params KeyValuePair<string, object>[] fields)
        {
            Dictionary<string, object> dto = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> field in fields)
            {
                dto[field.Key] = field.Value;
            }
            return dto;
        }
    }

    internal sealed class ToolProperty
    {
        public readonly string Name;
        public readonly Dictionary<string, object> Schema;

        public ToolProperty(string name, Dictionary<string, object> schema)
        {
            Name = name;
            Schema = schema;
        }
    }

    internal static class ToolManifest
    {
        public const string Version = "1";

        public static readonly string[] SharedReadKeys = { "mapId", "detail", "include", "limit", "cursor", "idsOnly" };

        private static readonly ToolSpec[] specs = Create();

        public static IEnumerable<ToolSpec> All
        {
            get { return specs; }
        }

        public static IEnumerable<ToolSpec> McpTools
        {
            get { return specs.Where(spec => spec.ExposeAsMcp); }
        }

        public static Dictionary<string, object> ToDto()
        {
            return J(
                F("manifestVersion", Version),
                F("tools", specs.Select(spec => spec.ToDto()).ToArray()));
        }

        public static void Validate(IEnumerable<string> knownHandlerIds)
        {
            List<string> errors = new List<string>();
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> handlers = knownHandlerIds == null
                ? null
                : new HashSet<string>(knownHandlerIds, StringComparer.Ordinal);

            foreach (ToolSpec spec in specs)
            {
                string routeKey = spec.Method + " " + spec.PathTemplate;
                if (!routes.Add(routeKey))
                {
                    errors.Add("Duplicate route " + routeKey);
                }
                if (handlers != null && !handlers.Contains(spec.HandlerId))
                {
                    errors.Add("Unknown handlerId " + spec.HandlerId + " for " + routeKey);
                }
                if (!spec.ExposeAsMcp)
                {
                    continue;
                }
                if (string.IsNullOrWhiteSpace(spec.Name))
                {
                    errors.Add("Missing MCP name for " + routeKey);
                }
                else if (!names.Add(spec.Name))
                {
                    errors.Add("Duplicate MCP tool " + spec.Name);
                }
                foreach (string capture in PathCaptures(spec.PathTemplate))
                {
                    if (!SchemaHasProperty(spec.InputSchema, capture))
                    {
                        errors.Add("Path capture {" + capture + "} is missing from input schema for " + spec.Name);
                    }
                }
            }

            if (errors.Count > 0)
            {
                throw new InvalidOperationException("Invalid RimMCP tool manifest: " + string.Join("; ", errors.ToArray()));
            }
        }

        private static ToolSpec[] Create()
        {
            return new[]
            {
                Get("game-context", "get_game_context", "Get game-wide context: current map, time, paused state, selected game speed, storyteller/difficulty, loaded mods, and map ids.", "/v1/game-context", Input()),
                Get("mods", "list_mods", "List loaded mods in runtime order, or scope=installed for known installed mods including disabled ones. Includes game version, pending configuration changes, and RimWorld's warnings about the configured mod list. Load positions are zero-based.", "/v1/mods", Input(Prop("scope", EnumString("Mod inventory to inspect. Defaults to loaded.", "loaded", "installed")), Prop("query", Str("Optional search text for mod name or package id."))), "scope", "query"),
                Get("mod", "get_mod", "Inspect one mod's metadata, dependencies, declared compatibility, loaded assemblies, and available settings panel names. Mod settings values are not exposed or editable.", "/v1/mods/{packageId}", RequiredOnly(Prop("packageId", Str("Exact package id from list_mods, preserving any suffix distinguishing local and Workshop copies.")))),
                Get("game-log", "get_game_log", "Read retained in-game log entries, newest first. This is not the complete Player.log or startup history. Use include=[\"stackTrace\"] or detail=full for stack traces. The cursor is an offset into current history, not a durable polling cursor.", "/v1/debug/log", Input(Prop("level", EnumString("Severity filter. Defaults to all.", "all", "message", "warning", "error")), Prop("query", Str("Optional case-insensitive search in messages and stack traces."))), "level", "query"),
                Get("game-settings", "get_game_settings", "Read supported global game options and the current colony's storyteller/difficulty, with available choices. Works without a colony for global options.", "/v1/settings", RequiredOnly()),
                Put("set-game-settings", "set_game_settings", "Change supported global options or the current colony's storyteller/difficulty preset. Only supplied fields change. Global options are saved; colony settings persist with the normal game save. Changing only the storyteller preserves custom difficulty. Requires Allow changes.", "/v1/settings", GameSettingsInput(), IdempotentMutation(), Body("storytellerDefName", "difficultyDefName", "runInBackground", "pauseOnLoad", "automaticPauseMode", "autosaveIntervalDays", "volumeMaster", "volumeMusic")),
                Get("colony-status", "get_colony_status", "Get a compact dashboard for the active colony with top risks and drill-down hints.", "/v1/colony-status", Input()),
                Get("pawns", "list_pawns", "List pawns by validated filter. Use include=[\"assignments\"] for Assign-style pawn settings and include=[\"assignmentOptions\"] for valid policy, area, medical, hostility, medicine, and interaction options.", "/v1/pawns", Input(Prop("filter", PawnFilter())), "filter"),
                Get("pawn", "get_pawn", "Get full details for one pawn by ThingID or load id. This endpoint always returns the full pawn record.", "/v1/pawns/{id}", Required(Prop("id", Str("Pawn ThingID or load id.")))),
                Get("resources", "list_resources", "List grouped map resources with compact food, medicine, stack, forbidden, roof, and rot context.", "/v1/resources", Input()),
                Get("work", "list_work", "List work priorities, current jobs, draft state, schedules, and allowed-area context for core pawns.", "/v1/work", Input()),
                Get("production", "list_production", "List production bills across colony bill givers.", "/v1/production", Input()),
                Get("bill", "get_bill", "Get full details for one production bill by bill id.", "/v1/bills/{id}", Required(Prop("id", Str("Bill id returned by list_production.")))),
                Put("set-bill", "set_bill", "Update or delete an existing production bill by bill id. Use mode=update to change fields, or mode=delete to remove it. Use search_defs for bill mode def names and list_zones for SpecificStockpile targets.", "/v1/bills/{id}", SetBillInput(), IdempotentDestructiveMutation(), Body("mapId", "mode", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName", "storeZoneId")),
                Get("workshops", "list_workshops", "List colony workshops/workbenches that can hold production bills, with current bill counts and available recipe counts. Use include=[\"recipes\"] or detail=full to include addable recipe summaries.", "/v1/workshops", Input()),
                Get("workshop", "get_workshop", "Get one workshop by ThingID or load id, including current bills and available recipes that can be added at that bench.", "/v1/workshops/{id}", Required(Prop("id", Str("Workshop ThingID or load id from list_workshops.")))),
                Post("add-bill-to-workshop", "add_bill_to_workshop", "Add a new bill to a workshop for a recipe. This is intentionally additive and can create duplicate recipe bills. Use search_defs for bill mode def names and list_zones for SpecificStockpile targets.", "/v1/workshops/{workshopId}/bills", AddBillToWorkshopInput(), AdditiveMutation(), Body("mapId", "recipeDefName", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName", "storeZoneId")),
                Get("zones", "list_zones", "List stockpiles, growing zones, and allowed areas.", "/v1/zones", Input()),
                Get("zone", "get_zone", "Get full details for one zone or area by id.", "/v1/zones/{id}", Required(Prop("id", Str("Zone id returned by list_zones.")))),
                Get("environment", "get_environment", "Get weather, season, game conditions, room temperature summaries, and hazards.", "/v1/environment", Input()),
                Get("power", "get_power", "Get power grid, stored energy, generation/consumption, and powered component context.", "/v1/power", Input()),
                Get("threats", "list_threats", "List active threats such as hostile pawns, manhunters, predators, and fires.", "/v1/threats", Input()),
                Get("research", "get_research", "Get current research and paged loaded research projects.", "/v1/research", Input()),
                Get("ideoligions", "list_ideoligions", "List loaded Ideology ideoligions with summary identity, memes, culture, and mode flags. Returns active=false with an empty list when Ideology is inactive.", "/v1/ideoligions", Input()),
                Get("ideoligion", "get_ideoligion", "Get full details for one ideoligion by id returned from list_ideoligions, including description and precepts.", "/v1/ideoligions/{id}", Required(Prop("id", Str("Ideoligion id returned by list_ideoligions.")))),
                Get("quests", "list_quests", "List active quests and quest state exposed by RimWorld.", "/v1/quests", Input()),
                Get("buildings", "list_buildings", "List colony buildings. Default rows are compact; detail=normal adds size, passability, power, battery, fuel, and billGiver. Filter category by production, power, bed, storage, or any defName substring. Use include=[\"contents\"] for storage-slot contents, or get_building to inspect one building by id.", "/v1/buildings", Input(Prop("category", BuildingCategory())), "category"),
                Get("building", "get_building", "Get full details for one colony building by ThingID or load id. Returns size, passability, power, fuel, bill support, and contents={supported,items} for storage-slot buildings.", "/v1/buildings/{id}", Required(Prop("id", Str("Building ThingID or load id from list_buildings.")))),
                Get("world", "list_world", "List world-level context: factions and world objects.", "/v1/world", Input()),
                Get("defs-search", "search_defs", "Search loaded game defs by kind, query, and category. sourceMod identifies the defining mod, not later patch contributors. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", "/v1/defs/search", Input(Prop("kind", DefKind()), Prop("query", Str("Search text for defName, label, or description.")), Prop("category", Str("Optional category filter."))), "kind", "query", "category"),
                Get("def", "get_def", "Get full detail for a loaded def by defName and optional kind. sourceMod identifies the defining mod, not later patch contributors. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", "/v1/defs/{defName}", Required(Prop("defName", Str("Def name to retrieve.")), Prop("kind", DefKind())), "kind"),
                Put("set-pawn-drafted", "set_pawn_drafted", "Draft or undraft one player-controlled pawn by ThingID or load id.", "/v1/pawns/{pawnId}/drafted", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("drafted", Bool("Whether the pawn should be drafted."))), IdempotentMutation(), Body("mapId", "drafted")),
                Put("set-pawn-work-priority", "set_pawn_work_priority", "Set one player-controlled pawn's work priority for a work type. Priority 0 disables the work type; 1 is highest and 4 is lowest.", "/v1/pawns/{pawnId}/work/{workTypeDefName}", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("workTypeDefName", Str("WorkTypeDef defName.")), Prop("priority", Int("Priority from 0 to 4.", 0, 4))), IdempotentMutation(), Body("mapId", "priority")),
                Put("set-pawn-assignment", "set_pawn_assignment", "Set one Assign-style pawn setting: schedule, policy, medical care, self-tend, hostility response, allowed area, carried medicine, or prisoner interaction.", "/v1/pawns/{pawnId}/assignments/{assignmentKind}", PawnAssignmentInput(), IdempotentMutation(), Body("mapId", "assignments", "policyKind", "policyId", "care", "enabled", "mode", "areaId", "count", "medicineDefName", "interactionModeDefName")),
                Put("designate-animal", "designate_animal", "Set or clear one animal's hunt, tame, or slaughter designation.", "/v1/animals/{pawnId}/designation", RequiredWithMap(Prop("pawnId", Str("Animal pawn ThingID or load id.")), Prop("action", AnimalDesignationAction())), IdempotentDestructiveMutation(), Body("mapId", "action")),
                Put("set-animal-training", "set_animal_training", "Set desired training flags for one colony animal by TrainableDef defName.", "/v1/animals/{pawnId}/training", RequiredWithMap(Prop("pawnId", Str("Colony animal pawn ThingID or load id.")), Prop("assignments", TrainingAssignments())), IdempotentMutation(), Body("mapId", "assignments")),
                Put("set-research-current", "set_research_project", "Set the current research project by ResearchProjectDef defName.", "/v1/research/current", RequiredOnly(Prop("projectDefName", Str("ResearchProjectDef defName."))), IdempotentMutation(), Body("projectDefName")),
                Put("set-game-speed", "set_game_speed", "Pause the game or set its speed. Also ends any running advance_time. Requires Allow changes.", "/v1/time/speed", RequiredOnly(Prop("speed", EnumString("Game speed. Ultrafast needs development mode.", "Paused", "Normal", "Fast", "Superfast", "Ultrafast"))), IdempotentMutation(), Body("speed")),
                Post("advance-time", "advance_time", "Run the game for up to 24 in-game hours, then pause it. The run stops early, and pauses, when a stopOn condition happens; the result lists what happened and why it ended. One run at a time. Retrying with the same requestId and arguments returns the existing run (the last 32 runs of the loaded game are remembered); reusing a requestId with different arguments is rejected. Pass waitSeconds to wait for the result in this call, otherwise follow up with get_advance using the run id. Cancelling this call does not stop a run that has started; use set_game_speed. Requires Allow changes.", "/v1/time/advance", AdvanceTimeInput(), Mutation(false, false), Body("hours", "speed", "stopOn", "requestId"), "waitSeconds"),
                Get("get-advance", "get_advance", "Get the game's tick and speed, and an advance_time run: its status, what happened, and why it ended. Defaults to the most recent run; pass runId for a specific one. Pass waitSeconds to wait for that run to end first.", "/v1/time/advance", InputWithOptionalMap(false, new string[0], Prop("runId", J(F("type", "integer"), F("minimum", 1), F("description", "Run id returned by advance_time. Defaults to the most recent run."))), Prop("waitSeconds", WaitSeconds())), "runId", "waitSeconds"),
                RouteOnly("tools", "GET", "/v1/tools")
            };
        }

        private static ToolSpec RouteOnly(string handlerId, string method, string pathTemplate)
        {
            return new ToolSpec(handlerId, null, null, method, pathTemplate, new string[0], new string[0], null, null, false);
        }

        private static ToolSpec Get(string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, params string[] extraQueryKeys)
        {
            return new ToolSpec(handlerId, name, description, "GET", pathTemplate, SharedReadKeys.Concat(extraQueryKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), new string[0], inputSchema, ReadOnly(), true);
        }

        private static ToolSpec Put(string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, string[] bodyKeys)
        {
            return new ToolSpec(handlerId, name, description, "PUT", pathTemplate, new string[0], WithLoadGuard(inputSchema, bodyKeys), inputSchema, annotations, true);
        }

        private static ToolSpec Post(string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, string[] bodyKeys, params string[] queryKeys)
        {
            return new ToolSpec(handlerId, name, description, "POST", pathTemplate, queryKeys, WithLoadGuard(inputSchema, bodyKeys), inputSchema, annotations, true);
        }

        // Writes accept the loadId from an earlier response; the bridge refuses them after another load.
        private static string[] WithLoadGuard(Dictionary<string, object> inputSchema, string[] bodyKeys)
        {
            ((Dictionary<string, object>)inputSchema["properties"])["expectedLoadId"] = Str("Optional loadId from an earlier response. The command is refused if another save has been loaded since.");
            return bodyKeys.Concat(new[] { "expectedLoadId" }).ToArray();
        }

        private static Dictionary<string, object> AdvanceTimeInput()
        {
            Dictionary<string, object> schema = ObjectSchema(J(
                F("hours", J(F("type", "number"), F("minimum", 0.1), F("maximum", 24), F("description", "In-game hours to run, from 0.1 to 24. One hour is 2,500 ticks."))),
                F("speed", EnumString("Speed while running. Defaults to Superfast. The game still drops to normal speed during danger, as it does for players. Ultrafast needs development mode.", "Normal", "Fast", "Superfast", "Ultrafast")),
                F("stopOn", J(
                    F("type", "array"),
                    F("items", EnumString("Condition.", "death", "downed", "injured", "mentalBreak", "hostiles", "threatLetter", "letter")),
                    F("description", "Conditions that end the run early and pause. Defaults to death, downed, injured (new injury or bleeding), mentalBreak, hostiles (a visible hostile threat appears), and threatLetter. letter stops on any letter. An empty list runs to the end. Checked every frame."))),
                F("requestId", J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries: a repeat with the same id returns the existing run."))),
                F("waitSeconds", WaitSeconds())),
                "hours");
            return schema;
        }

        private static Dictionary<string, object> WaitSeconds()
        {
            return J(F("type", "number"), F("minimum", 0), F("maximum", 30), F("description", "Seconds to wait for the run to end before replying, up to 30. Defaults to 0."));
        }

        private static Dictionary<string, object> GameSettingsInput()
        {
            Dictionary<string, object> schema = ObjectSchema(J(
                F("storytellerDefName", Str("Selectable StorytellerDef defName from get_game_settings. Requires a loaded game.")),
                F("difficultyDefName", Str("Non-custom DifficultyDef defName from get_game_settings. Replaces the current difficulty with that preset; requires a loaded game.")),
                F("runInBackground", Bool("Keep RimWorld running while unfocused.")),
                F("pauseOnLoad", Bool("Pause when loading a game.")),
                F("automaticPauseMode", EnumString("Which events automatically pause the game.", "Never", "MajorThreat", "AnyThreat", "AnyLetter")),
                F("autosaveIntervalDays", J(F("type", "number"), F("enum", new[] { 0.5, 1.0, 3.0, 7.0, 14.0 }), F("description", "Autosave interval in game days, using the normal game options."))),
                F("volumeMaster", J(F("type", "number"), F("minimum", 0), F("maximum", 1), F("description", "Master volume from 0 to 1."))),
                F("volumeMusic", J(F("type", "number"), F("minimum", 0), F("maximum", 1), F("description", "Music volume from 0 to 1.")))));
            schema["minProperties"] = 1;
            return schema;
        }

        private static string[] Body(params string[] keys)
        {
            return keys;
        }

        private static Dictionary<string, object> ReadOnly()
        {
            return J(F("readOnlyHint", true), F("openWorldHint", false));
        }

        private static Dictionary<string, object> IdempotentMutation()
        {
            return Mutation(true, false);
        }

        private static Dictionary<string, object> IdempotentDestructiveMutation()
        {
            return Mutation(true, true);
        }

        private static Dictionary<string, object> AdditiveMutation()
        {
            return Mutation(false, false);
        }

        private static Dictionary<string, object> Mutation(bool idempotent, bool destructive)
        {
            return J(
                F("readOnlyHint", false),
                F("idempotentHint", idempotent),
                F("destructiveHint", destructive),
                F("openWorldHint", false));
        }

        private static Dictionary<string, object> PawnAssignmentInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["pawnId"] = Str("Pawn ThingID or load id.");
            props["assignmentKind"] = EnumString("Assign-style setting to change.", "schedule", "policy", "medicalCare", "selfTend", "hostilityResponse", "allowedArea", "carryMedicine", "prisonerInteraction");
            props["assignments"] = ScheduleAssignments();
            props["policyKind"] = EnumString("Required when assignmentKind is policy. Policy column to set.", "apparel", "food", "drug", "reading");
            props["policyId"] = Str("Required when assignmentKind is policy. Policy id returned by list_pawns include=[\"assignmentOptions\"].");
            props["care"] = EnumString("Required when assignmentKind is medicalCare. Medical care category.", "NoCare", "NoMeds", "HerbalOrWorse", "NormalOrWorse", "Best");
            props["enabled"] = Bool("Required when assignmentKind is selfTend. Whether self-tend should be enabled.");
            props["mode"] = EnumString("Required when assignmentKind is hostilityResponse. Hostility response mode.", "Ignore", "Attack", "Flee");
            props["areaId"] = Str("Required when assignmentKind is allowedArea. Area id from assignmentOptions, or unrestricted to clear it.");
            props["count"] = Int("Required when assignmentKind is carryMedicine. Medicine count to carry.", 0, 3);
            props["medicineDefName"] = Str("Optional when assignmentKind is carryMedicine. Medicine ThingDef defName from assignmentOptions; omit to keep the current medicine.");
            props["interactionModeDefName"] = Str("Required when assignmentKind is prisonerInteraction. PrisonerInteractionModeDef defName, such as AttemptRecruit, ReduceResistance, Convert, or Release.");
            return ObjectSchema(props, "pawnId", "assignmentKind");
        }

        private static Dictionary<string, object> SetBillInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["id"] = Str("Bill id returned by list_production or get_workshop.");
            props["mode"] = EnumString("Whether to update fields on the bill or delete it.", "update", "delete");
            AddBillSettingProperties(props);
            return ObjectSchema(props, "id", "mode");
        }

        private static Dictionary<string, object> AddBillToWorkshopInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["workshopId"] = Str("Workshop ThingID or load id from list_workshops.");
            props["recipeDefName"] = Str("RecipeDef defName from list_workshops include=[\"recipes\"] or get_workshop.");
            AddBillSettingProperties(props);
            return ObjectSchema(props, "workshopId", "recipeDefName");
        }

        private static void AddBillSettingProperties(Dictionary<string, object> props)
        {
            props["suspended"] = Bool("Whether the bill should be suspended.");
            props["repeatModeDefName"] = J(F("type", "string"), F("description", "BillRepeatModeDef defName from search_defs kind=billRepeatMode."), F("examples", A("Forever", "RepeatCount", "TargetCount")));
            props["repeatCount"] = Int("Repeat count for RepeatCount mode.", 1, 999999);
            props["targetCount"] = Int("Target count for TargetCount mode.", 1, 999999);
            props["pauseWhenSatisfied"] = Bool("Whether the bill pauses when its target is satisfied.");
            props["unpauseWhenYouHave"] = Int("Inventory count where a paused bill can unpause. Must be less than targetCount.", 0, 999998);
            props["ingredientSearchRadius"] = J(F("type", "number"), F("minimum", 3), F("maximum", 999), F("description", "Ingredient search radius in cells. 100 or more means unlimited and is stored as 999."));
            props["allowedSkillMin"] = Int("Minimum allowed pawn skill.", 0, 20);
            props["allowedSkillMax"] = Int("Maximum allowed pawn skill.", 0, 20);
            props["storeModeDefName"] = J(F("type", "string"), F("description", "BillStoreModeDef defName from search_defs kind=billStoreMode. SpecificStockpile also requires storeZoneId."), F("examples", A("DropOnFloor", "BestStockpile", "SpecificStockpile")));
            props["storeZoneId"] = Str("Stockpile zone id from list_zones. Required only when storeModeDefName is SpecificStockpile.");
        }

        private static Dictionary<string, object> Input(params ToolProperty[] properties)
        {
            return Input(null, properties);
        }

        private static Dictionary<string, object> Required(ToolProperty requiredProperty, params ToolProperty[] optionalProperties)
        {
            return Input(new[] { requiredProperty.Name }, new[] { requiredProperty }.Concat(optionalProperties).ToArray());
        }

        private static Dictionary<string, object> RequiredWithMap(params ToolProperty[] properties)
        {
            return InputWithOptionalMap(true, properties.Select(property => property.Name).ToArray(), properties);
        }

        private static Dictionary<string, object> RequiredOnly(params ToolProperty[] properties)
        {
            return InputWithOptionalMap(false, properties.Select(property => property.Name).ToArray(), properties);
        }

        private static Dictionary<string, object> InputWithOptionalMap(bool includeMapId, string[] required, params ToolProperty[] properties)
        {
            Dictionary<string, object> props = includeMapId ? MapProperty() : new Dictionary<string, object>();
            foreach (ToolProperty property in properties)
            {
                props[property.Name] = property.Schema;
            }
            return ObjectSchema(props, required);
        }

        private static Dictionary<string, object> Input(string[] required, params ToolProperty[] properties)
        {
            Dictionary<string, object> props = SharedProperties();
            foreach (ToolProperty property in properties)
            {
                props[property.Name] = property.Schema;
            }
            return ObjectSchema(props, required ?? new string[0]);
        }

        private static Dictionary<string, object> ObjectSchema(Dictionary<string, object> properties, params string[] required)
        {
            return J(
                F("type", "object"),
                F("properties", properties),
                F("required", required),
                F("additionalProperties", false));
        }

        private static ToolProperty Prop(string name, Dictionary<string, object> schema)
        {
            return new ToolProperty(name, schema);
        }

        private static Dictionary<string, object> SharedProperties()
        {
            return J(
                F("mapId", Str("Optional RimWorld map id. Defaults to current map.")),
                F("detail", EnumString("Payload detail level. Defaults to summary. full also includes tool-specific expensive sections.", "summary", "normal", "full")),
                F("include", J(F("type", "array"), F("items", J(F("type", "string"))), F("description", "Optional expensive sections to include without requesting full detail. Known values include needs, health, skills, work, assignments, assignmentOptions, training, gear, relations, contents, recipes, and stackTrace."))),
                F("limit", Int("Maximum items to return for paged tools. Defaults to 50.", 1, 200)),
                F("cursor", J(F("type", "integer"), F("minimum", 0), F("description", "Cursor returned by a previous paged response."))),
                F("idsOnly", Bool("Return identifiers only for list tools when supported.")));
        }

        private static Dictionary<string, object> MapProperty()
        {
            return J(F("mapId", Str("Optional RimWorld map id. Defaults to current map.")));
        }

        private static Dictionary<string, object> Str(string description)
        {
            return J(F("type", "string"), F("description", description));
        }

        private static Dictionary<string, object> Bool(string description)
        {
            return J(F("type", "boolean"), F("description", description));
        }

        private static Dictionary<string, object> Int(string description, int minimum, int maximum)
        {
            return J(F("type", "integer"), F("minimum", minimum), F("maximum", maximum), F("description", description));
        }

        private static Dictionary<string, object> EnumString(string description, params string[] values)
        {
            return J(F("type", "string"), F("enum", values), F("description", description));
        }

        private static Dictionary<string, object> ScheduleAssignments()
        {
            return J(
                F("type", "array"),
                F("minItems", 1),
                F("description", "Required when assignmentKind is schedule. Ranges are half-open, non-overlapping, and use hours 0 through 24."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("startHour", Int("Inclusive start hour from 0 to 23.", 0, 23)),
                        F("endHour", Int("Exclusive end hour from 1 to 24.", 1, 24)),
                        F("assignmentDefName", Str("TimeAssignmentDef defName, such as Anything, Work, Joy, Sleep, or Meditate.")))),
                    F("required", A("startHour", "endHour", "assignmentDefName")),
                    F("additionalProperties", false))));
        }

        private static Dictionary<string, object> TrainingAssignments()
        {
            return J(
                F("type", "array"),
                F("minItems", 1),
                F("description", "Training desired-state changes for one colony animal."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("trainableDefName", Str("TrainableDef defName, such as Tameness, Obedience, or Release.")),
                        F("wanted", Bool("Whether this trainable should be wanted.")))),
                    F("required", A("trainableDefName", "wanted")),
                    F("additionalProperties", false))));
        }

        private static Dictionary<string, object> AnimalDesignationAction()
        {
            return EnumString("Animal designation action. none clears hunt, tame, and slaughter designations.", "hunt", "tame", "slaughter", "none");
        }

        private static Dictionary<string, object> DefKind()
        {
            return J(
                F("type", "string"),
                F("description", "Loaded def kind to search or retrieve. Accepts any Def type name without the Def suffix, compatibility aliases such as research/designation, or all."),
                F("examples", A("thing", "hediff", "ability", "trainable", "billStoreMode", "billRepeatMode", "timeAssignment", "all")));
        }

        private static Dictionary<string, object> BuildingCategory()
        {
            return J(
                F("type", "string"),
                F("description", "Optional building category or defName substring. Known categories: production, power, bed, storage."),
                F("examples", A("production", "power", "bed", "storage", "Shelf")));
        }

        private static Dictionary<string, object> PawnFilter()
        {
            return EnumString("Pawn filter to list. Defaults to core.", "core", "colonist", "colonists", "slave", "slaves", "prisoner", "prisoners", "guest", "guests", "colonyAnimal", "colonyAnimals", "wildAnimal", "wildAnimals", "hostile", "hostiles", "animals", "wildlife", "threats", "other", "others", "all");
        }

        private static IEnumerable<string> PathCaptures(string template)
        {
            foreach (string segment in template.Split('/'))
            {
                if (segment.Length > 2 && segment[0] == '{' && segment[segment.Length - 1] == '}')
                {
                    yield return segment.Substring(1, segment.Length - 2);
                }
            }
        }

        private static bool SchemaHasProperty(Dictionary<string, object> schema, string name)
        {
            object properties;
            if (schema == null || !schema.TryGetValue("properties", out properties))
            {
                return false;
            }
            Dictionary<string, object> typed = properties as Dictionary<string, object>;
            return typed != null && typed.ContainsKey(name);
        }

        private static object[] A(params object[] items)
        {
            return items;
        }

        private static KeyValuePair<string, object> F(string name, object value)
        {
            return new KeyValuePair<string, object>(name, value);
        }

        private static Dictionary<string, object> J(params KeyValuePair<string, object>[] fields)
        {
            Dictionary<string, object> json = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> field in fields)
            {
                json[field.Key] = field.Value;
            }
            return json;
        }
    }
}
