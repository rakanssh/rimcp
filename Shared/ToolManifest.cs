#nullable disable
using System;
using System.Collections.Generic;
using System.Linq;

namespace RiMCP.Shared
{
    internal sealed class ToolSpec
    {
        public readonly string HandlerId;
        // Access group from ToolGroups; null for bridge routes that are always reachable.
        public readonly string Group;
        public readonly string Name;
        public readonly string Description;
        public readonly string Method;
        public readonly string PathTemplate;
        public readonly string[] QueryKeys;
        public readonly string[] BodyKeys;
        public readonly Dictionary<string, object> InputSchema;
        public readonly Dictionary<string, object> Annotations;
        public readonly bool ExposeAsMcp;

        public ToolSpec(string handlerId, string group, string name, string description, string method, string pathTemplate, string[] queryKeys, string[] bodyKeys, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, bool exposeAsMcp)
        {
            HandlerId = handlerId;
            Group = group;
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
                F("group", Group),
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

    // Players enable tools by group. A tool added later inherits its group's setting.
    internal static class ToolGroups
    {
        public const string Observe = "observe";
        public const string Diagnostics = "diagnostics";
        public const string Time = "time";
        public const string Administration = "administration";
        public const string Orders = "orders";
        public const string PawnControl = "pawnControl";
        public const string GameSettings = "gameSettings";
        public const string GameSetup = "gameSetup";

        public static readonly string[] All = { Observe, Diagnostics, Time, Administration, Orders, PawnControl, GameSettings, GameSetup };

        public static string Label(string group)
        {
            switch (group)
            {
                case Observe: return "Observe";
                case Diagnostics: return "Diagnostics";
                case Time: return "Time";
                case Administration: return "Administration";
                case Orders: return "Orders";
                case PawnControl: return "Pawn control";
                case GameSettings: return "Game settings";
                case GameSetup: return "Game setup";
                default: return group;
            }
        }

        public static string Description(string group)
        {
            switch (group)
            {
                case Observe: return "Read the colony, pawns, map, defs, and time status.";
                case Diagnostics: return "Read loaded mods, the game log, and game options.";
                case Time: return "Pause, set the speed, and advance time.";
                case Administration: return "Change work priorities, assignments, policies, bills, research, and animal training.";
                case Orders: return "Map orders: forbid, haul, cut, harvest, mine, deconstruct, and cancel; animal designations; and blueprints.";
                case PawnControl: return "Draft pawns and give them direct orders: equip, wear, move, attack, rescue, and tend.";
                case GameSettings: return "Change the storyteller, difficulty, and global game options.";
                case GameSetup: return "Save, load, and leave games, and set up new ones. It can abandon a colony or overwrite a save, so only Full control includes it.";
                default: return "";
            }
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
                if (!ToolGroups.All.Contains(spec.Group))
                {
                    errors.Add("Unknown access group '" + spec.Group + "' for " + spec.Name);
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
                Get(ToolGroups.Observe, "game-context", "get_game_context", "Get game-wide context: current map, time, paused state, selected game speed, storyteller/difficulty, loaded mods, and map ids.", "/v1/game-context", Input()),
                Get(ToolGroups.Diagnostics, "mods", "list_mods", "List loaded mods in runtime order, or scope=installed for known installed mods including disabled ones. Includes game version, pending configuration changes, and RimWorld's warnings about the configured mod list. Load positions are zero-based.", "/v1/mods", Input(Prop("scope", EnumString("Mod inventory to inspect. Defaults to loaded.", "loaded", "installed")), Prop("query", Str("Optional search text for mod name or package id."))), "scope", "query"),
                Get(ToolGroups.Diagnostics, "mod", "get_mod", "Inspect one mod's metadata, dependencies, declared compatibility, loaded assemblies, and available settings panel names. Mod settings values are not exposed or editable.", "/v1/mods/{packageId}", RequiredOnly(Prop("packageId", Str("Exact package id from list_mods, preserving any suffix distinguishing local and Workshop copies.")))),
                Get(ToolGroups.Diagnostics, "game-log", "get_game_log", "Read retained in-game log entries, newest first. This is not the complete Player.log or startup history. Use include=[\"stackTrace\"] or detail=full for stack traces. The cursor is an offset into current history, not a durable polling cursor.", "/v1/debug/log", Input(Prop("level", EnumString("Severity filter. Defaults to all.", "all", "message", "warning", "error")), Prop("query", Str("Optional case-insensitive search in messages and stack traces."))), "level", "query"),
                Get(ToolGroups.Diagnostics, "game-settings", "get_game_settings", "Read supported global game options and the current colony's storyteller/difficulty, with available choices. Works without a colony for global options.", "/v1/settings", RequiredOnly()),
                Put(ToolGroups.GameSettings, "set-game-settings", "set_game_settings", "Change supported global options or the current colony's storyteller/difficulty preset. Only supplied fields change. Global options are saved; colony settings persist with the normal game save. Changing only the storyteller preserves custom difficulty.", "/v1/settings", GameSettingsInput(), IdempotentMutation(), Body("storytellerDefName", "difficultyDefName", "runInBackground", "pauseOnLoad", "automaticPauseMode", "autosaveIntervalDays", "volumeMaster", "volumeMusic")),
                Get(ToolGroups.Observe, "colony-status", "get_colony_status", "Get a compact dashboard for the active colony with top risks and drill-down hints. food gives stored nutrition as meals and raw food, how much the colonists eat per day, days left, and the change per day over up to the last three days (sampled hourly since the game was loaded). Only food in storage counts, so hauling food into storage shows as a rise.", "/v1/colony-status", Input()),
                Get(ToolGroups.Observe, "pawns", "list_pawns", "List pawns by validated filter. Use include=[\"assignments\"] for Assign-style pawn settings and include=[\"assignmentOptions\"] for valid policy, area, medical, hostility, medicine, and interaction options.", "/v1/pawns", Input(Prop("filter", PawnFilter())), "filter"),
                Get(ToolGroups.Observe, "pawn", "get_pawn", "Get full details for one pawn by ThingID or load id. This endpoint always returns the full pawn record.", "/v1/pawns/{id}", Required(Prop("id", Str("Pawn ThingID or load id.")))),
                Get(ToolGroups.Observe, "resources", "list_resources", "List grouped map resources with compact food, medicine, stack, forbidden, roof, and rot context. Items under fog are hidden, as in game, and not counted.", "/v1/resources", Input()),
                Get(ToolGroups.Observe, "things", "find_things", "Find items on a map: anything haulable, such as resources, food, medicine, weapons, apparel, chunks, corpses, and minified buildings. Each row gives id, def, label, x, z, count, forbidden, inStorage, and the storage id; storages labels those ids. Filter by def, category (subcategories included), excludeCategory (such as [\"Chunks\", \"Corpses\"]), label text, forbidden, inStorage, or distance from x and z; results are sorted by distance when x and z are given. Unexplored areas are never searched.", "/v1/things", Input(Prop("def", Str("Exact ThingDef defName, such as Steel or MealSimple.")), Prop("category", Str("ThingCategoryDef defName, such as Foods, Medicine, Weapons, or ResourcesRaw.")), Prop("excludeCategory", J(F("type", "array"), F("items", J(F("type", "string"))), F("description", "ThingCategoryDef defNames to leave out, subcategories included."))), Prop("query", Str("Text to find in the item label or defName.")), Prop("forbidden", Bool("Only forbidden (true) or only allowed (false) items.")), Prop("inStorage", Bool("Only items in storage that accepts them (true), or only items outside it (false).")), Prop("x", Int("Search centre x.", 0, 1000)), Prop("z", Int("Search centre z.", 0, 1000)), Prop("radius", Int("Search radius in cells around x and z.", 1, 500))), "def", "category", "excludeCategory", "query", "forbidden", "inStorage", "x", "z", "radius"),
                Get(ToolGroups.Observe, "map-view", "get_map_view", "Get a character picture of part of a map, one character per cell. rows[0] is the north edge (z = origin.z + height - 1); each row runs west to east from x = origin.x. Each cell shows its most important feature: a pawn, then a building or blueprint, tree, item, zone, water, or soil fertility; unexplored cells show nothing. Capital letters are colonists, and the legend gives each one's name, id, and job; legend lists only the symbols shown. Defaults to 32x32 centred on the colonists, at most 40x40. layers adds grids over the same cells, each under layers with its own legend: roof, or designations (orders such as mine, cut, or deconstruct). A layer reports markedCells and leaves out its rows when it marks none.", "/v1/map/view", InputWithOptionalMap(true, new string[0], Prop("x", Int("West edge of the view.", 0, 1000)), Prop("z", Int("South edge of the view.", 0, 1000)), Prop("width", Int("Columns, 1 to 40. Defaults to 32.", 1, 40)), Prop("height", Int("Rows, 1 to 40. Defaults to 32.", 1, 40)), Prop("layers", J(F("type", "array"), F("items", EnumString("Extra layer.", "roof", "designations")), F("description", "Extra grids to include.")))), "x", "z", "width", "height", "layers"),
                Get(ToolGroups.Observe, "check-placement", "check_placement", "Check whether a building fits at a position, or find where it fits among up to 20x20 positions, with the game's build checks. x and z are the building's position cell; footprintOffset gives the cells it covers from there, and interactionCellOffset where a worker stands. Returns fits (nearest the middle of the area first), rejected positions grouped by reason, the cost, and blockers such as missingMaterial or noConstructor. Changes nothing. Uses the map shown in game.", "/v1/placement", InputWithOptionalMap(true, new[] { "def", "x", "z" }, Prop("def", Str("Building defName, such as Campfire, FueledStove, ButcherSpot, or Wall.")), Prop("stuff", Str("Material defName for buildings made from one, such as WoodLog or Steel. Defaults to the game's choice.")), Prop("rotation", EnumString("Facing. Defaults to the building's usual facing.", "north", "east", "south", "west")), Prop("x", Int("Position x, or the west edge of the positions to check.", 0, 1000)), Prop("z", Int("Position z, or the south edge of the positions to check.", 0, 1000)), Prop("width", Int("Positions to check west to east, 1 to 20. Defaults to 1.", 1, 20)), Prop("height", Int("Positions to check south to north, 1 to 20. Defaults to 1.", 1, 20))), "def", "stuff", "rotation", "x", "z", "width", "height"),
                Get(ToolGroups.Observe, "work", "list_work", "List work priorities, current jobs, draft state, schedules, and allowed-area context for core pawns.", "/v1/work", Input()),
                Get(ToolGroups.Observe, "production", "list_production", "List production bills across colony bill givers. Each bill has a state (active, suspended, done, paused, or targetReached) and, when active, blockers that stop it: missingIngredients, noWorker (counting the bill's skill range and pawn restriction), needsFuel, or workbenchUnusable. detail=normal adds the usable ingredients each needs and has, following the game's rules for mixing kinds. Ingredients other jobs have reserved still count, so a bill can look ready a moment before a colonist finds them taken.", "/v1/production", Input()),
                Get(ToolGroups.Observe, "bill", "get_bill", "Get full details for one production bill by bill id, with its state, blockers, and the usable ingredients it needs and has: allowed, not forbidden, within its search radius, and reachable from the workbench. Reserved ingredients still count.", "/v1/bills/{id}", Required(Prop("id", Str("Bill id returned by list_production.")))),
                Put(ToolGroups.Administration, "set-bill", "set_bill", "Update or delete an existing production bill by bill id. Use mode=update to change fields, or mode=delete to remove it. Use search_defs for bill mode def names and list_zones for SpecificStockpile targets.", "/v1/bills/{id}", SetBillInput(), IdempotentDestructiveMutation(), Body("mapId", "mode", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName", "storeZoneId")),
                Get(ToolGroups.Observe, "workshops", "list_workshops", "List colony workshops/workbenches that can hold production bills, with current bill counts and available recipe counts. Use include=[\"recipes\"] or detail=full to include addable recipe summaries.", "/v1/workshops", Input()),
                Get(ToolGroups.Observe, "workshop", "get_workshop", "Get one workshop by ThingID or load id, including current bills and available recipes that can be added at that bench.", "/v1/workshops/{id}", Required(Prop("id", Str("Workshop ThingID or load id from list_workshops.")))),
                Post(ToolGroups.Administration, "add-bill-to-workshop", "add_bill_to_workshop", "Add a new bill to a workshop for a recipe. This is intentionally additive and can create duplicate recipe bills. Like the game, a new bill does the recipe once unless repeatModeDefName says otherwise: Forever, or TargetCount with targetCount. Use search_defs for bill mode def names and list_zones for SpecificStockpile targets.", "/v1/workshops/{workshopId}/bills", AddBillToWorkshopInput(), AdditiveMutation(), Body("mapId", "recipeDefName", "suspended", "repeatModeDefName", "repeatCount", "targetCount", "pauseWhenSatisfied", "unpauseWhenYouHave", "ingredientSearchRadius", "allowedSkillMin", "allowedSkillMax", "storeModeDefName", "storeZoneId", "requestId")),
                Get(ToolGroups.Observe, "zones", "list_zones", "List stockpiles, growing zones, and allowed areas. Stockpiles show their priority and accepts: the top-level categories they take fully (all), partly (some), or not at all (none), counting special filters that are off (specialFiltersOff), and the hit point and quality ranges they take when narrowed.", "/v1/zones", Input()),
                Get(ToolGroups.Observe, "zone", "get_zone", "Get full details for one zone or area by id.", "/v1/zones/{id}", Required(Prop("id", Str("Zone id returned by list_zones.")))),
                Get(ToolGroups.Observe, "environment", "get_environment", "Get weather, season, game conditions, hazards, and rooms: each room closed off from the map edge, with its role (such as Bedroom or Barracks), size, unroofed cells, temperature, and whether it uses the outdoor temperature. A room that is not closed in is part of the outdoors and is not listed, and neither is one still under fog.", "/v1/environment", Input()),
                Get(ToolGroups.Observe, "power", "get_power", "Get power grid, stored energy, generation/consumption, and powered component context.", "/v1/power", Input()),
                Get(ToolGroups.Observe, "threats", "list_threats", "List active threats such as hostile pawns, manhunters, predators, and fires. As in game, anything under fog is hidden and not listed.", "/v1/threats", Input()),
                Get(ToolGroups.Observe, "research", "get_research", "Get current research, blockers that stop it progressing (such as noProject, missingResearchBench, or noResearcher), and paged research projects. availableOnly lists only projects that can be started now.", "/v1/research", Input(Prop("availableOnly", Bool("Only projects that can be started now. Needs a loaded colony."))), "availableOnly"),
                Get(ToolGroups.Observe, "ideoligions", "list_ideoligions", "List loaded Ideology ideoligions with summary identity, memes, culture, and mode flags. Returns active=false with an empty list when Ideology is inactive.", "/v1/ideoligions", Input()),
                Get(ToolGroups.Observe, "ideoligion", "get_ideoligion", "Get full details for one ideoligion by id returned from list_ideoligions, including description and precepts.", "/v1/ideoligions/{id}", Required(Prop("id", Str("Ideoligion id returned by list_ideoligions.")))),
                Get(ToolGroups.Observe, "quests", "list_quests", "List active quests and quest state exposed by RimWorld.", "/v1/quests", Input()),
                Get(ToolGroups.Observe, "buildings", "list_buildings", "List colony buildings. Default rows are compact; detail=normal adds size, passability, power, battery, fuel, and billGiver. Filter category by production, power, bed, storage, or any defName substring. Use include=[\"contents\"] for storage-slot contents, or get_building to inspect one building by id.", "/v1/buildings", Input(Prop("category", BuildingCategory())), "category"),
                Get(ToolGroups.Observe, "building", "get_building", "Get full details for one colony building by ThingID or load id. Returns size, passability, power, fuel, bill support, and contents={supported,items} for storage-slot buildings.", "/v1/buildings/{id}", Required(Prop("id", Str("Building ThingID or load id from list_buildings.")))),
                Get(ToolGroups.Observe, "world", "list_world", "List world-level context: factions and world objects.", "/v1/world", Input()),
                Get(ToolGroups.Observe, "defs-search", "search_defs", "Search loaded game defs by kind, query, and category. sourceMod identifies the defining mod, not later patch contributors. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", "/v1/defs/search", Input(Prop("kind", DefKind()), Prop("query", Str("Search text for defName, label, or description.")), Prop("category", Str("Optional category filter."))), "kind", "query", "category"),
                Get(ToolGroups.Observe, "def", "get_def", "Get full detail for a loaded def by defName and optional kind. sourceMod identifies the defining mod, not later patch contributors. kind accepts any loaded Def type name without the Def suffix, compatibility aliases such as research/designation, or all.", "/v1/defs/{defName}", Required(Prop("defName", Str("Def name to retrieve.")), Prop("kind", DefKind())), "kind"),
                Put(ToolGroups.PawnControl, "set-pawn-drafted", "set_pawn_drafted", "Draft or undraft one player-controlled pawn by ThingID or load id.", "/v1/pawns/{pawnId}/drafted", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("drafted", Bool("Whether the pawn should be drafted."))), IdempotentMutation(), Body("mapId", "drafted")),
                Put(ToolGroups.Administration, "set-pawn-work-priority", "set_pawn_work_priority", "Set one player-controlled pawn's work priority for a work type. Priority 0 disables the work type; 1 is highest and 4 is lowest.", "/v1/pawns/{pawnId}/work/{workTypeDefName}", RequiredWithMap(Prop("pawnId", Str("Pawn ThingID or load id.")), Prop("workTypeDefName", Str("WorkTypeDef defName.")), Prop("priority", Int("Priority from 0 to 4.", 0, 4))), IdempotentMutation(), Body("mapId", "priority")),
                Put(ToolGroups.Administration, "set-pawn-assignment", "set_pawn_assignment", "Set one Assign-style pawn setting: schedule, policy, medical care, self-tend, hostility response, allowed area, carried medicine, or prisoner interaction.", "/v1/pawns/{pawnId}/assignments/{assignmentKind}", PawnAssignmentInput(), IdempotentMutation(), Body("mapId", "assignments", "policyKind", "policyId", "care", "enabled", "mode", "areaId", "count", "medicineDefName", "interactionModeDefName")),
                Put(ToolGroups.Orders, "designate-animal", "designate_animal", "Set or clear one animal's hunt, tame, or slaughter designation.", "/v1/animals/{pawnId}/designation", RequiredWithMap(Prop("pawnId", Str("Animal pawn ThingID or load id.")), Prop("action", AnimalDesignationAction())), IdempotentDestructiveMutation(), Body("mapId", "action")),
                Put(ToolGroups.Administration, "set-animal-training", "set_animal_training", "Set desired training flags for one colony animal by TrainableDef defName.", "/v1/animals/{pawnId}/training", RequiredWithMap(Prop("pawnId", Str("Colony animal pawn ThingID or load id.")), Prop("assignments", TrainingAssignments())), IdempotentMutation(), Body("mapId", "assignments")),
                Put(ToolGroups.Administration, "set-research-current", "set_research_project", "Set the current research project by ResearchProjectDef defName. Returns blockers that would stop it progressing, such as a missing research bench.", "/v1/research/current", RequiredOnly(Prop("projectDefName", Str("ResearchProjectDef defName."))), IdempotentMutation(), Body("projectDefName")),
                Put(ToolGroups.Orders, "designate", "designate", "Forbid, unforbid, or mark items for hauling; cut plants, harvest crops, or chop trees for wood; mine rock, deconstruct buildings, or cancel orders, blueprints, and unfinished buildings. Target things by thingIds or by rectangles of cells (optionally only one defName). Uses the game's own checks and reports each target's outcome: applied, alreadySatisfied, or rejected with a reason. Unforbidden items are hauled to stockpiles that accept them; haul marks chunks and other things that are not hauled automatically. cut clears plants but leaves grown trees, which chopWood takes for wood; harvest gathers crops and wild plants that are ready, such as berries. Unexplored cells are never mined. Acts on the map shown in game.", "/v1/designations", DesignateInput(), Mutation(true, true), Body("mapId", "kind", "thingIds", "rects", "defName")),
                Post(ToolGroups.Orders, "place-blueprint", "place_blueprint", "Place blueprints for colonists to build: up to 50 placements, each a building def with optional stuff (material) and rotation at x and z. Uses the game's build tool, so research and placement rules apply. Each placement reports applied or alreadySatisfied, with the id and state (blueprint, frame, or built) of what is there, or rejected with the reason. blockers lists missing materials (counting unforbidden, explored stock, including what other blueprints will use) or builders. Find a spot with check_placement first, and pass requestId for safe retries. Acts on the map shown in game.", "/v1/blueprints", PlaceBlueprintInput(), IdempotentMutation(), Body("mapId", "placements", "requestId")),
                Put(ToolGroups.Administration, "set-zone", "set_zone", "Create, grow, shrink, rename, or delete a stockpile or growing zone. To create one, give kind and add rectangles, and pass requestId for safe retries; to change one, give zoneId. Cells are checked like the game's zone tools, and every rejected cell is listed with its reason. A zone only grows into cells touching it; after removing cells it keeps only its connected part. New stockpiles accept the usual items; use set_storage to change that. Acts on the map shown in game.", "/v1/zones", SetZoneInput(), Mutation(false, true), Body("mapId", "zoneId", "kind", "add", "remove", "delete", "label", "requestId")),
                Put(ToolGroups.Administration, "set-storage", "set_storage", "Set a stockpile's or storage building's priority and what it accepts. allow and disallow take ThingCategoryDef names (such as FoodMeals, Medicine, ResourcesRaw) or ThingDef names (such as MealSimple); allowAll or disallowAll apply first. Each entry reports applied, alreadySatisfied, or rejected, for example when a shelf cannot hold it. Special filters, such as rotten food or tainted apparel, keep their settings unless named in allow or disallow (such as AllowRotten); an allowed category or thing lists those still refusing some of it, in specialFiltersOff (allow them by name to take those too) and fixedSpecialFiltersOff (the building refuses them whatever its settings). accepts lists the top-level categories the storage now takes fully (all), partly (some), or not at all (none), counting the special filters that are off in its settings (specialFiltersOff) or refused by the building whatever its settings (fixedSpecialFiltersOff, such as a shelf refusing large corpses), and the hit point and quality ranges it takes when narrowed.", "/v1/storage", SetStorageInput(), IdempotentMutation(), Body("mapId", "zoneId", "buildingId", "priority", "allowAll", "disallowAll", "allow", "disallow")),
                Put(ToolGroups.Administration, "set-growing-zone", "set_growing_zone", "Set a growing zone's crop and whether colonists sow it and cut other plants in it. The crop must be one the game's crop menu offers for the zone, so research and map rules apply. Returns the zone's growing state: sowableCells (cells without the crop where it could be sown now), sown, harvestable, averageGrowth, notGrowing (sown plants not growing right now) with notGrowingReasons (resting: the nightly rest every plant takes from about 19:00 to 6:00, which is normal; tooDark; temperature; other, such as blight), and blockers such as noPlantableCells, outOfSeason, or noGrower. list_zones and get_zone report the same.", "/v1/growing", SetGrowingZoneInput(), IdempotentMutation(), Body("mapId", "zoneId", "plant", "allowSow", "allowCut")),
                Put(ToolGroups.Administration, "set-area", "set_area", "Paint rectangles into or out of the Home area or an allowed area, rename or delete an allowed area, or create one by omitting areaId (pass requestId for safe retries). areaId is \"home\" or an area id from list_zones.", "/v1/areas", SetAreaInput(), Mutation(false, true), Body("mapId", "areaId", "label", "add", "remove", "delete", "requestId")),
                Post(ToolGroups.PawnControl, "order-pawn", "order_pawn", "Order one player pawn, with the game's own right-click checks: equip a weapon or wear apparel (targetId of an item), move to a cell (x and z; the game picks a standable cell nearby, reported as destination), attack (targetId of a hostile, animal, or building; a pawn with a ranged weapon fires and is refused when out of range, so move it closer first, and others attack in melee), rescue a downed pawn to a bed, or tend a pawn (targetId of a pawn). move, attack, and tend need a drafted pawn: use set_pawn_drafted first. The order replaces what the pawn is doing; equip and wear can instead wait behind its current job and earlier orders with queue, like shift-click, and queuedJobs lists what is waiting. Returns jobIssued, alreadySatisfied, rejected (with the game's reason, such as no path), or awaitingInput when the game asks the player to confirm. Acts on the map shown in game.", "/v1/pawns/{pawnId}/orders", OrderPawnInput(), Mutation(true, true), Body("mapId", "order", "targetId", "x", "z", "queue", "requestId")),
                Put(ToolGroups.Time, "set-game-speed", "set_game_speed", "Pause the game or set its speed. Also ends any running advance_time.", "/v1/time/speed", RequiredOnly(Prop("speed", EnumString("Game speed. Ultrafast needs development mode.", "Paused", "Normal", "Fast", "Superfast", "Ultrafast"))), IdempotentMutation(), Body("speed")),
                Post(ToolGroups.Time, "advance-time", "advance_time", "Run the game for up to 24 in-game hours, then pause it. The run stops early, and pauses, when a stopOn condition happens; the result lists what happened and why it ended. One run at a time. Retrying with the same requestId and arguments returns the existing run (the last 32 runs of the loaded game are remembered); reusing a requestId with different arguments is rejected. Pass waitSeconds to wait for the result in this call, otherwise follow up with get_advance using the run id. Cancelling this call does not stop a run that has started; use set_game_speed.", "/v1/time/advance", AdvanceTimeInput(), Mutation(false, false), Body("hours", "speed", "stopOn", "requestId"), "waitSeconds"),
                Get(ToolGroups.Observe, "get-advance", "get_advance", "Get the game's tick and speed, and an advance_time run: its status, what happened, and why it ended. Defaults to the most recent run; pass runId for a specific one. Pass waitSeconds to wait for that run to end first.", "/v1/time/advance", InputWithOptionalMap(false, new string[0], Prop("runId", J(F("type", "integer"), F("minimum", 1), F("description", "Run id returned by advance_time. Defaults to the most recent run."))), Prop("waitSeconds", WaitSeconds())), "runId", "waitSeconds"),
                Get(ToolGroups.Observe, "get-operation", "get_operation", "Get an operation by operationId, such as a save, load, world generation, or site search: its state (queued, running, awaitingConfirmation, succeeded, failed, unverified, declined, cancelled, revoked, or interruptedByChange when someone else changed the setup), current step, any question the game is asking the player, and its result. Works while RimWorld is loading, and after the game the operation started in is gone. Every running operation and the last 32 finished ones are kept. Pass waitSeconds to wait for it to finish first.", "/v1/operations/{operationId}", InputWithOptionalMap(false, new[] { "operationId" }, Prop("operationId", Str("Operation id returned by the tool that started it.")), Prop("waitSeconds", OperationWaitSeconds())), "waitSeconds"),
                PostAnyGame(ToolGroups.Observe, "cancel-operation", "cancel_operation", "Stop an operation at its next safe step, keeping whatever it already did. A native step that cannot be interrupted, such as loading or returning to the main menu, finishes first. Stopping a Game setup operation needs the Game setup group. Works while RimWorld is loading.", "/v1/operations/{operationId}/cancel", RequiredOnly(Prop("operationId", Str("Operation id returned by the tool that started it."))), IdempotentMutation(), Body()),
                Get(ToolGroups.Observe, "list-saves", "list_saves", "List save files, newest first, with name, last write time, size, game version, whether the save's mods match the running ones (as the game checks before loading), and commitment mode. loadedColony says whether the loaded colony is a commitment-mode game. Works at the main menu.", "/v1/saves", Input()),
                Post(ToolGroups.GameSetup, "save-game", "save_game", "Save the loaded colony under a name, as an operation: it returns at once with an operationId to follow with get_operation. A save succeeds only once verified: the file was written and is complete, and the game logged no error while saving, because the game hides its own save errors. failed or unverified means do not rely on the file. Refuses to replace an existing save unless overwrite is true. Not available in commitment mode, as in game." + OperationRetries, "/v1/saves", SaveGameInput(), Mutation(false, true), Body("name", "overwrite", "requestId"), "waitSeconds"),
                Post(ToolGroups.GameSetup, "load-game", "load_game", "Load a save by name, as an operation to follow with get_operation. When a colony is loaded, pass saveFirst (a save name) to save it first, or discardUnsaved: true; one is always required, because changes made while paused cannot be detected. Saving first pauses the game and holds it unchanged until it is replaced, so nothing done after the save is lost; a failed save stops the load. When the save's version or mods differ, the game asks the player, and the operation waits (awaitingConfirmation) until they answer in game. A running advance stops. Not available while a commitment-mode game is loaded: leave it first." + OperationRetries, "/v1/session/load", ReplaceInput(true), Mutation(false, true), Body("name", "saveFirst", "discardUnsaved", "overwrite", "requestId"), "waitSeconds"),
                Post(ToolGroups.GameSetup, "leave-game", "leave_game", "Return to the main menu, as an operation to follow with get_operation. Pass saveFirst (a save name) to save first, or discardUnsaved: true; one is always required. Saving first pauses the game and leaves in the same moment, so nothing done after the save is lost; a failed save stops it. Commitment-mode games always save to their own slot first, as in game, and take neither. A running advance stops." + OperationRetries, "/v1/session/leave", ReplaceInput(false), Mutation(false, true), Body("saveFirst", "discardUnsaved", "overwrite", "requestId"), "waitSeconds"),
                Get(ToolGroups.Observe, "list-scenarios", "list_scenarios", "List the scenarios a new game can start from, as the scenario screen shows them: name, category, summary, and whether RimMCP can set it up (supported). Scenarios with other setup screens list them in manualScreens; start those in game. Works at the main menu.", "/v1/setup/scenarios", Input()),
                Get(ToolGroups.Observe, "get-setup", "get_setup", "Get the new game being set up: the screen it is at (stage: scenario, storyteller, world, site, ideoligion, pawns, customIdeoligion, or unsupported), its revision, the choices on that screen, and the options it offers. Commands that change the setup take this revision as expectedRevision, and are refused if anyone, such as the player, changed the setup since (lastChangeExternal). handoff says when a screen must be finished in game. At the customIdeoligion stage it shows the ideoligion: memes, each precept with its id and what its box offers (replaceWith, cannotRemove, canRegenerate), the meme catalog, and blocksNext; include preceptOptions also lists every Add option by section, allowed or with the reason. Setup is visible: the player sees each screen and can take over at any point.", "/v1/setup", Input()),
                PostAnyGame(ToolGroups.Observe, "find-starting-sites", "find_starting_sites", "Search the generated world for starting sites, as an operation to follow with get_operation; its result lists up to limit settleable tiles, longest growing period first. Filters: biomes (BiomeDef names), hilliness (Flat, SmallHills, LargeHills, Mountainous), river, road, coastal, yearly average temperature (min/maxTemperature, in C), rainfall (min/maxRainfall, mm a year), minGrowingDays (of the 60-day year), and distance in world tiles to the nearest settlement of another faction (min/maxSettlementDistance, counting only settlementRelations, such as Ally or Neutral, when given), and avoidEncroaching to leave out tiles where a faction would lose goodwill, as the game warns when settling. Each site names its nearest counted settlement and any settlement it encroaches on. Needs the starting site screen. The search runs a few milliseconds per frame, so the game stays responsive, and stops if the world is generated again." + OperationRetries, "/v1/setup/sites/search", SiteSearchInput(), ReadOnly(), Body("biomes", "hilliness", "river", "road", "coastal", "minTemperature", "maxTemperature", "minRainfall", "maxRainfall", "minGrowingDays", "minSettlementDistance", "maxSettlementDistance", "settlementRelations", "avoidEncroaching", "limit", "requestId"), "waitSeconds"),
                Post(ToolGroups.GameSetup, "start-setup", "start_setup", "Start setting up a new game from a scenario (list_scenarios), as an operation to follow with get_operation. It opens the game's own setup screens at the storyteller screen, as New colony does. When a colony is loaded, it leaves it first: pass saveFirst (a save name) or discardUnsaved: true, as for leave_game. Then use get_setup, and the setup commands in order: set_setup_storyteller, generate_world, choose_starting_site, set_setup_ideoligion (with Ideology), set_starting_pawns, and start_game." + OperationRetries, "/v1/setup", StartSetupInput(), Mutation(false, true), Body("scenario", "category", "saveFirst", "discardUnsaved", "overwrite", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "cancel-setup", "cancel_setup", "Abandon the new game being set up and return to the main menu, dropping its world and colonists, as an operation to follow with get_operation." + OperationRetries, "/v1/setup/cancel", Input(new string[0], Prop("requestId", RequestIdProperty()), Prop("waitSeconds", OperationWaitSeconds())), Mutation(false, true), Body("requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "setup-back", "setup_back", "Go back one setup screen, as its Back button does. Choices made on later screens may be lost, as in game.", "/v1/setup/back", RevisionInput(), Mutation(false, false), Body("expectedRevision")),
                PostAnyGame(ToolGroups.GameSetup, "set-setup-storyteller", "set_setup_storyteller", "Choose the storyteller, the difficulty, and commitment mode on the storyteller screen, then go on to the world screen. For the custom difficulty (customDifficulty in get_setup), custom sets the panel's controls: base is the preset to start from (Rough when custom is first chosen, as the screen does; the current values once it is custom), and settings sets controls by name, a slider's value in the slider's own range and steps or a checkbox's enabled, as the panel shows them. get_setup lists the storytellers, difficulties, and each custom setting with its range and whether the panel offers it now (some need a DLC, or another setting).", "/v1/setup/storyteller", StorytellerSetupInput(), Mutation(false, false), Body("storyteller", "difficulty", "commitmentMode", "custom", "expectedRevision")),
                PostAnyGame(ToolGroups.GameSetup, "generate-world", "generate_world", "Set the world screen's options and generate the world, as an operation to follow with get_operation; it ends at the starting site screen. Options left out keep the screen's values, which get_setup shows. Generation takes from seconds to a minute or two, and other requests wait until it is done." + OperationRetries, "/v1/setup/world", WorldSetupInput(), Mutation(false, false), Body("seed", "coverage", "rainfall", "temperature", "population", "landmarkDensity", "pollution", "expectedRevision", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "choose-starting-site", "choose_starting_site", "Choose the starting tile (find_starting_sites finds them), optionally the map size and starting season, and go on to the next screen, as an operation to follow with get_operation. The game's own checks apply. Settling near another faction's settlement makes the game ask first: the operation waits (awaitingConfirmation) for the player to answer, or, with unattended setup on, for answer_confirmation." + OperationRetries, "/v1/setup/site", SiteSetupInput(), Mutation(false, false), Body("tileId", "mapSize", "startingSeason", "expectedRevision", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "set-setup-ideoligion", "set_setup_ideoligion", "Choose the ideoligion (Ideology) on the ideoligion screen: classic; a preset, with an optional structure meme (otherwise the preset's own or a random one, as in game); a saved one by file name; or a custom one. classic and preset go on to the next screen. saved runs as an operation to follow with get_operation, because the game may ask about the file's version or mods first: the player answers, or with unattended setup on, answer_confirmation. customFixed and customFluid open the custom ideoligion screen and create it with structure and memes, through the meme dialogs and their rules (one structure; 1 to 4 memes, exactly 1 for a new fluid ideoligion; none excluding another); the game generates the rest, which edit_setup_ideoligion then changes. get_setup lists the presets, structures, and saved files.", "/v1/setup/ideoligion", IdeoligionSetupInput(), Mutation(false, false), Body("mode", "preset", "structure", "memes", "file", "expectedRevision", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "edit-setup-ideoligion", "edit_setup_ideoligion", "Edit the custom ideoligion on the custom ideoligion screen, with the screen's own rules. Parts apply in this order, each reported as applied, alreadySatisfied, or rejected with the game's reason: randomize (all, or precepts); structure and memes, checked as the meme dialog checks them and applied together (the game then regenerates the precepts, styles, and description, as it warns, so give memes before the rest; a rejection stops the rest); culture; styles (up to three, most dominant first); precepts, in order: add one of the options an Add button offers (precept, with the pattern, thing, weapons, or xenotype it lists), or by id replaceWith another option for its issue, regenerate, rename, or remove, as the precept's box allows (replacing and regenerating give a new id, which later edits in the request follow); name, adjective, memberName. done: true then runs the screen's Next, refused with its message while something blocks it (blocksNext in get_setup). Refused while a dialog is open on the screen. With no ideoligion yet, structure and memes create one.", "/v1/setup/ideoligion/edit", EditIdeoligionInput(), Mutation(false, true), Body("randomize", "structure", "memes", "culture", "styles", "precepts", "name", "adjective", "memberName", "done", "expectedRevision")),
                PostAnyGame(ToolGroups.GameSetup, "set-starting-pawns", "set_starting_pawns", "Arrange, reroll, and rename starting colonists on the colonist screen, as an operation to follow with get_operation. In order: order rearranges the slots as dragging does (the first startingCount start; the rest stay behind); preferences set a slot's age (Adult, Child, or Baby, where the scenario allows) and xenotype (a XenotypeDef, or anyNonArchite), as the colonist card's buttons do with Biotech, rerolling it; each reroll presses Randomize for a slot until the colonist meets its criteria (minimum skills and passions, traits wanted or excluded, work it can do, xenotype, age) or maxAttempts runs out; names apply at the end. Slots after order refer to the new order. The result says what was matched and what is still missing, with the roster as it now is. Stops if anyone else changes the setup meanwhile." + OperationRetries, "/v1/setup/pawns", PawnsSetupInput(), Mutation(false, true), Body("order", "preferences", "rerolls", "names", "expectedRevision", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "start-game", "start_game", "Start the colony from the last setup screen, as an operation to follow with get_operation; its result has the new game's loadId. The player is asked in game to approve this exact setup first (awaitingConfirmation), and any change while asking withdraws the request, unless unattended setup is on in RimMCP settings. If no colonist can do a required job, the game asks too." + OperationRetries, "/v1/setup/start", RevisionInput(true), Mutation(false, false), Body("expectedRevision", "requestId"), "waitSeconds"),
                PostAnyGame(ToolGroups.GameSetup, "answer-confirmation", "answer_confirmation", "Answer a question the game asked during an operation, such as settling near another faction or starting without someone for every required job, by its confirmationId from get_operation. Only with unattended setup on in RimMCP settings; otherwise the player answers in game. Refused if the setup changed since it was asked.", "/v1/operations/{operationId}/answer", AnswerInput(), Mutation(false, false), Body("confirmationId", "accept")),
                RouteOnly("tools", "GET", "/v1/tools"),
                RouteOnly("access", "GET", "/v1/access")
            };
        }

        // How long an operation's requestId is remembered: OperationRunner.MaxRequestRecords.
        private const string OperationRetries = " Retrying with the same requestId and arguments returns the original operation, even after a load; the last 1,000 requests in this RimWorld process are recognised.";

        // An operation outlives the game it started in, so acting on it does not depend on which game
        // is loaded: no expectedLoadId.
        // Setup commands, likewise, act on the setup at a revision rather than on a loaded game.
        private static ToolSpec PostAnyGame(string group, string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, string[] bodyKeys, params string[] queryKeys)
        {
            return new ToolSpec(handlerId, group, name, description, "POST", pathTemplate, queryKeys, bodyKeys, inputSchema, annotations, true);
        }

        private static Dictionary<string, object> RequestIdProperty()
        {
            return J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries."));
        }

        private static Dictionary<string, object> ExpectedRevision()
        {
            return J(F("type", "integer"), F("minimum", 0), F("description", "The setup revision from get_setup. Refused if the setup has changed since."));
        }

        private static Dictionary<string, object> StrArray(string description)
        {
            return J(F("type", "array"), F("items", J(F("type", "string"))), F("maxItems", 50), F("description", description));
        }

        private static Dictionary<string, object> RevisionInput(bool operation = false)
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["expectedRevision"] = ExpectedRevision();
            if (operation)
            {
                props["requestId"] = RequestIdProperty();
                props["waitSeconds"] = OperationWaitSeconds();
            }
            return ObjectSchema(props, "expectedRevision");
        }

        private static Dictionary<string, object> StartSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["scenario"] = Str("Scenario name, as list_scenarios shows it, such as Crashlanded.");
            props["category"] = Str("The scenario's category from list_scenarios, such as FromDef for a built-in one or CustomLocal for the player's own. Needed only when two share a name.");
            props["saveFirst"] = Str("When a colony is loaded: save it under this name first. Give this or discardUnsaved.");
            props["discardUnsaved"] = Bool("When a colony is loaded: drop any unsaved changes. Give this or saveFirst.");
            props["overwrite"] = Bool("Let saveFirst replace an existing save with that name.");
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props, "scenario");
        }

        private static Dictionary<string, object> StorytellerSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["storyteller"] = Str("StorytellerDef name, such as Cassandra, Phoebe, or Randy.");
            props["difficulty"] = Str("DifficultyDef name: a preset, such as Peaceful, Easy, Medium, Rough, Hard, or Extreme, or the custom difficulty.");
            props["custom"] = J(
                F("type", "object"),
                F("description", "With the custom difficulty: the panel's settings."),
                F("properties", J(
                    F("base", Str("A preset to start from, as the panel's Reset menu does.")),
                    F("settings", J(
                        F("type", "array"),
                        F("maxItems", 80),
                        F("items", J(
                            F("type", "object"),
                            F("properties", J(
                                F("setting", Str("The setting's name, as get_setup lists it, such as threatScale or allowMortars.")),
                                F("value", J(F("type", "number"), F("description", "A slider's value, in its range and steps."))),
                                F("enabled", Bool("A checkbox, as the panel shows it.")))),
                            F("required", A("setting")),
                            F("additionalProperties", false))))))),
                F("additionalProperties", false));
            props["commitmentMode"] = Bool("true for commitment mode (one save, kept as you play), false to save and reload any time.");
            props["expectedRevision"] = ExpectedRevision();
            return ObjectSchema(props, "storyteller", "difficulty", "commitmentMode", "expectedRevision");
        }

        private static Dictionary<string, object> WorldSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["seed"] = Str("World seed.");
            props["coverage"] = J(F("type", "integer"), F("enum", A(30, 50, 100)), F("description", "Percent of the planet to generate."));
            props["rainfall"] = Str("Overall rainfall, as get_setup lists the options, such as Normal.");
            props["temperature"] = Str("Overall temperature, as get_setup lists the options, such as Normal.");
            props["population"] = Str("Overall population, as get_setup lists the options, such as Normal.");
            props["landmarkDensity"] = Str("Landmark density (Odyssey), as get_setup lists the options.");
            props["pollution"] = J(F("type", "number"), F("minimum", 0), F("maximum", 100), F("description", "Percent of the world polluted (Biotech)."));
            props["expectedRevision"] = ExpectedRevision();
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props, "expectedRevision");
        }

        private static Dictionary<string, object> SiteSearchInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["biomes"] = StrArray("BiomeDef names to accept, such as TemperateForest or AridShrubland.");
            props["hilliness"] = StrArray("Hilliness to accept: Flat, SmallHills, LargeHills, or Mountainous.");
            props["river"] = Bool("true for tiles with a river, false for tiles without.");
            props["road"] = Bool("true for tiles with a road, false for tiles without.");
            props["coastal"] = Bool("true for coastal tiles, false for inland ones.");
            props["minTemperature"] = J(F("type", "number"), F("description", "Lowest yearly average temperature, in C."));
            props["maxTemperature"] = J(F("type", "number"), F("description", "Highest yearly average temperature, in C."));
            props["minRainfall"] = J(F("type", "number"), F("minimum", 0), F("description", "Least rainfall, in mm a year."));
            props["maxRainfall"] = J(F("type", "number"), F("minimum", 0), F("description", "Most rainfall, in mm a year."));
            props["minGrowingDays"] = Int("Shortest growing period, in days of the 60-day year (60 is year-round).", 0, 60);
            props["minSettlementDistance"] = J(F("type", "number"), F("minimum", 0), F("description", "Least distance, in world tiles, to the nearest counted settlement."));
            props["maxSettlementDistance"] = J(F("type", "number"), F("minimum", 0), F("description", "Most distance, in world tiles, to the nearest counted settlement; a site needs one within it."));
            props["settlementRelations"] = StrArray("Which settlements count for the distances, by their faction's relation to the player: Ally, Neutral, or Hostile. All, when left out.");
            props["avoidEncroaching"] = Bool("Leave out tiles near enough to a settlement that its faction would lose goodwill, as the game warns.");
            props["limit"] = Int("Most sites to return. Defaults to 10.", 1, 50);
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props);
        }

        private static Dictionary<string, object> SiteSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["tileId"] = J(F("type", "integer"), F("minimum", 0), F("description", "Surface tile id, as find_starting_sites gives it."));
            props["mapSize"] = J(F("type", "integer"), F("enum", A(200, 225, 250, 275, 300, 325)), F("description", "Map size in cells along each side. Defaults to the screen's choice."));
            props["startingSeason"] = Str("Starting season: Spring, Summer, Fall, Winter, or Undefined for the game's choice.");
            props["expectedRevision"] = ExpectedRevision();
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props, "tileId", "expectedRevision");
        }

        private static Dictionary<string, object> IdeoligionSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["mode"] = EnumString("classic plays without ideoligion mechanics; preset starts from a preset; saved loads a saved ideoligion; customFixed and customFluid create one from structure and memes.", "classic", "preset", "saved", "customFixed", "customFluid");
            props["preset"] = Str("With mode preset: the IdeoPresetDef name, as get_setup lists them.");
            props["structure"] = Str("With mode preset: a structure MemeDef name, as get_setup lists them; defaults to the preset's own, or a random one. With a custom mode: the structure meme, required.");
            props["memes"] = StrArray("With a custom mode: the normal MemeDef names, 1 to 4 (exactly 1 for customFluid).");
            props["file"] = Str("With mode saved: the saved ideoligion's file name, as get_setup lists them.");
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            props["expectedRevision"] = ExpectedRevision();
            return ObjectSchema(props, "mode", "expectedRevision");
        }

        private static Dictionary<string, object> EditIdeoligionInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["randomize"] = EnumString("Randomize all, or only the precepts, as the screen's buttons do; applied first.", "all", "precepts");
            props["structure"] = Str("The structure MemeDef name.");
            props["memes"] = StrArray("The normal MemeDef names, replacing the current ones: 1 to 4, or exactly 1 on a new fluid ideoligion.");
            props["culture"] = Str("CultureDef name, as get_setup lists them.");
            props["styles"] = StrArray("Up to three StyleCategoryDef names, the most dominant first, as get_setup lists them.");
            props["precepts"] = J(
                F("type", "array"),
                F("maxItems", 40),
                F("description", "Precept edits, in order. Each does one thing."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("add", J(
                            F("type", "object"),
                            F("description", "Add an option an Add button offers, as get_setup with include preceptOptions lists it."),
                            F("properties", J(
                                F("precept", Str("PreceptDef name.")),
                                F("pattern", Str("RitualPatternDef name, for a ritual listed with one.")),
                                F("thing", Str("ThingDef name, for a building, apparel, or other thing choice.")),
                                F("weapons", Str("WeaponClassPairDef name, for noble and despised weapons.")),
                                F("xenotype", Str("XenotypeDef name, for a preferred xenotype.")))),
                            F("required", A("precept")),
                            F("additionalProperties", false))),
                        F("id", J(F("type", "integer"), F("description", "An existing precept's id, from get_setup."))),
                        F("replaceWith", Str("With id: another PreceptDef for the precept's issue, from its replaceWith.")),
                        F("regenerate", Bool("With id: regenerate it, where its box offers that.")),
                        F("rename", Str("With id: a new name, up to 32 letters, digits, spaces, apostrophes, and hyphens.")),
                        F("remove", Bool("With id: remove it, where its box offers that.")))),
                    F("additionalProperties", false))));
            props["name"] = Str("The ideoligion's name, up to 40 letters, digits, spaces, apostrophes, and hyphens.");
            props["adjective"] = Str("Its adjective, likewise.");
            props["memberName"] = Str("What a member is called, likewise.");
            props["done"] = Bool("After the edits, go on to the next screen, as Next does.");
            props["expectedRevision"] = ExpectedRevision();
            return ObjectSchema(props, "expectedRevision");
        }

        private static Dictionary<string, object> PawnsSetupInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["order"] = J(F("type", "array"), F("items", J(F("type", "integer"), F("minimum", 0))), F("maxItems", 50), F("description", "Every slot, once, in the new order, as get_setup's roster numbers them."));
            props["preferences"] = J(
                F("type", "array"),
                F("maxItems", 20),
                F("description", "Generation preferences per slot (Biotech). Changing one rerolls the colonist, except xenotype anyNonArchite."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("slot", Int("Colonist slot, in the order after order.", 0, 50)),
                        F("stage", EnumString("Age stage.", "Adult", "Child", "Baby")),
                        F("xenotype", Str("XenotypeDef name, or anyNonArchite.")))),
                    F("required", A("slot")),
                    F("additionalProperties", false))));
            props["rerolls"] = J(
                F("type", "array"),
                F("maxItems", 20),
                F("description", "Colonists to reroll until they meet the criteria, in order."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("slot", Int("Colonist slot, 0 first, as get_setup's roster shows it.", 0, 50)),
                        F("skills", J(
                            F("type", "array"),
                            F("maxItems", 12),
                            F("description", "Minimum skills."),
                            F("items", J(
                                F("type", "object"),
                                F("properties", J(
                                    F("skill", Str("SkillDef name, such as Shooting, Melee, Construction, Mining, Cooking, Plants, Animals, Crafting, Artistic, Medicine, Social, or Intellectual.")),
                                    F("min", Int("Lowest level.", 0, 20)),
                                    F("passion", EnumString("Least passion. Defaults to none.", "None", "Minor", "Major")))),
                                F("required", A("skill", "min")),
                                F("additionalProperties", false))))),
                        F("traits", StrArray("TraitDef names the colonist must have, such as Industriousness or Tough.")),
                        F("excludeTraits", StrArray("TraitDef names the colonist must not have, such as Pyromaniac.")),
                        F("canDo", StrArray("WorkTypeDef names the colonist must be able to do, such as Doctor, Hunting, or Firefighter.")),
                        F("xenotypes", StrArray("XenotypeDef names the colonist may be (Biotech), such as Baseliner or Hussar.")),
                        F("minAge", Int("Youngest biological age.", 0, 200)),
                        F("maxAge", Int("Oldest biological age.", 0, 200)),
                        F("maxAttempts", Int("Most rerolls for this slot. Defaults to 100.", 1, 500)))),
                    F("required", A("slot")),
                    F("additionalProperties", false))));
            props["names"] = J(
                F("type", "array"),
                F("maxItems", 20),
                F("description", "Names to give colonists, after any rerolls."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("slot", Int("Colonist slot, 0 first.", 0, 50)),
                        F("first", Str("First name.")),
                        F("nick", Str("Nickname, shown in game. Defaults to the first name.")),
                        F("last", Str("Last name.")))),
                    F("required", A("slot", "first", "last")),
                    F("additionalProperties", false))));
            props["expectedRevision"] = ExpectedRevision();
            props["requestId"] = RequestIdProperty();
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props, "expectedRevision");
        }

        private static Dictionary<string, object> AnswerInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["operationId"] = Str("The operation waiting for an answer.");
            props["confirmationId"] = Str("The confirmationId get_operation shows for the question.");
            props["accept"] = Bool("true to go ahead, as the game's confirm button; false to go back.");
            return ObjectSchema(props, "operationId", "confirmationId", "accept");
        }

        private static Dictionary<string, object> OperationWaitSeconds()
        {
            return J(F("type", "number"), F("minimum", 0), F("maximum", 30), F("description", "Seconds to wait for the operation to finish before replying, up to 30. Defaults to 0."));
        }

        private static Dictionary<string, object> SaveGameInput()
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            props["name"] = Str("Save name: the file name without .rws, as list_saves shows it.");
            props["overwrite"] = Bool("Replace an existing save with this name.");
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries."));
            props["waitSeconds"] = OperationWaitSeconds();
            return ObjectSchema(props, "name");
        }

        private static Dictionary<string, object> ReplaceInput(bool load)
        {
            Dictionary<string, object> props = new Dictionary<string, object>();
            if (load)
            {
                props["name"] = Str("The save to load, as list_saves shows it.");
            }
            props["saveFirst"] = Str("Save the loaded colony under this name first. Give this or discardUnsaved when a colony is loaded.");
            props["discardUnsaved"] = Bool("Drop any unsaved changes to the loaded colony. Give this or saveFirst when a colony is loaded.");
            props["overwrite"] = Bool("Let saveFirst replace an existing save with that name.");
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries."));
            props["waitSeconds"] = OperationWaitSeconds();
            return load ? ObjectSchema(props, "name") : ObjectSchema(props);
        }

        private static ToolSpec RouteOnly(string handlerId, string method, string pathTemplate)
        {
            return new ToolSpec(handlerId, null, null, null, method, pathTemplate, new string[0], new string[0], null, null, false);
        }

        private static ToolSpec Get(string group, string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, params string[] extraQueryKeys)
        {
            return new ToolSpec(handlerId, group, name, description, "GET", pathTemplate, SharedReadKeys.Concat(extraQueryKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), new string[0], inputSchema, ReadOnly(), true);
        }

        private static ToolSpec Put(string group, string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, string[] bodyKeys)
        {
            return new ToolSpec(handlerId, group, name, description, "PUT", pathTemplate, new string[0], WithLoadGuard(inputSchema, bodyKeys), inputSchema, annotations, true);
        }

        private static ToolSpec Post(string group, string handlerId, string name, string description, string pathTemplate, Dictionary<string, object> inputSchema, Dictionary<string, object> annotations, string[] bodyKeys, params string[] queryKeys)
        {
            return new ToolSpec(handlerId, group, name, description, "POST", pathTemplate, queryKeys, WithLoadGuard(inputSchema, bodyKeys), inputSchema, annotations, true);
        }

        // Writes accept the loadId from an earlier response; the bridge refuses them after another load.
        private static string[] WithLoadGuard(Dictionary<string, object> inputSchema, string[] bodyKeys)
        {
            ((Dictionary<string, object>)inputSchema["properties"])["expectedLoadId"] = Str("Optional loadId from an earlier response. The command is refused if another save has been loaded since.");
            return bodyKeys.Concat(new[] { "expectedLoadId" }).ToArray();
        }

        private static Dictionary<string, object> CellRects(string description)
        {
            return J(
                F("type", "array"),
                F("maxItems", 20),
                F("description", description + " Each rectangle starts at its south-west corner (x, z) and extends east by width and north by height."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("x", Int("West edge.", 0, 1000)),
                        F("z", Int("South edge.", 0, 1000)),
                        F("width", Int("Columns, 1 to 60. Defaults to 1.", 1, 60)),
                        F("height", Int("Rows, 1 to 60. Defaults to 1.", 1, 60)))),
                    F("required", A("x", "z")),
                    F("additionalProperties", false))));
        }

        private static Dictionary<string, object> DesignateInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["kind"] = EnumString("Order to apply.", "forbid", "unforbid", "haul", "cut", "harvest", "chopWood", "mine", "deconstruct", "cancel");
            props["thingIds"] = J(F("type", "array"), F("items", J(F("type", "string"))), F("maxItems", 300), F("description", "Ids of the things to order, such as items from find_things. Give these or rects."));
            props["rects"] = CellRects("Cells whose items or plants get the order. Give these or thingIds.");
            props["defName"] = Str("With rects, only things of this ThingDef, such as Plant_Berry or Plant_TreeOak.");
            return ObjectSchema(props, "kind");
        }

        // How long a requestId is remembered: CommandReceipts.MaxReceipts in the loaded game.
        private const string RetryWindow = " The last 64 calls given a requestId in the loaded game are remembered.";

        private static Dictionary<string, object> SetZoneInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["zoneId"] = Str("Zone id from list_zones. Omit to create a zone.");
            props["kind"] = EnumString("Zone kind. Required when creating.", "stockpile", "growing");
            props["add"] = CellRects("Cells to add.");
            props["remove"] = CellRects("Cells to remove from the zone.");
            props["delete"] = Bool("Delete the whole zone given by zoneId.");
            props["label"] = Str("New name for the zone.");
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries when creating: a repeat returns the original result." + RetryWindow));
            return ObjectSchema(props);
        }

        private static Dictionary<string, object> SetStorageInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["zoneId"] = Str("Stockpile zone id from list_zones. Give this or buildingId.");
            props["buildingId"] = Str("Storage building id, such as a shelf, from list_buildings. Give this or zoneId.");
            props["priority"] = EnumString("Hauling priority.", "Low", "Normal", "Preferred", "Important", "Critical");
            props["allowAll"] = Bool("Allow everything the storage can hold, before allow and disallow.");
            props["disallowAll"] = Bool("Allow nothing, before allow and disallow.");
            props["allow"] = J(F("type", "array"), F("items", J(F("type", "string"))), F("description", "ThingCategoryDef, ThingDef, or SpecialThingFilterDef names to allow."));
            props["disallow"] = J(F("type", "array"), F("items", J(F("type", "string"))), F("description", "ThingCategoryDef, ThingDef, or SpecialThingFilterDef names to disallow."));
            return ObjectSchema(props);
        }

        private static Dictionary<string, object> SetGrowingZoneInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["zoneId"] = Str("Growing zone id from list_zones.");
            props["plant"] = Str("Plant defName to grow, such as Plant_Rice, Plant_Potato, Plant_Corn, or Plant_Cotton.");
            props["allowSow"] = Bool("Whether colonists sow the crop.");
            props["allowCut"] = Bool("Whether colonists cut other plants in the zone.");
            return ObjectSchema(props, "zoneId");
        }

        private static Dictionary<string, object> PlaceBlueprintInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["placements"] = J(
                F("type", "array"),
                F("minItems", 1),
                F("maxItems", 50),
                F("description", "Buildings to place, in order."),
                F("items", J(
                    F("type", "object"),
                    F("properties", J(
                        F("def", Str("Building defName, such as Campfire, FueledStove, ButcherSpot, or Wall.")),
                        F("stuff", Str("Material defName for buildings made from one, such as WoodLog, Steel, or BlocksGranite. Defaults to the game's choice.")),
                        F("x", Int("Position x.", 0, 1000)),
                        F("z", Int("Position z.", 0, 1000)),
                        F("rotation", EnumString("Facing. Defaults to the building's usual facing.", "north", "east", "south", "west")))),
                    F("required", A("def", "x", "z")),
                    F("additionalProperties", false))));
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries: a repeat returns the original result." + RetryWindow));
            return ObjectSchema(props, "placements");
        }

        private static Dictionary<string, object> SetAreaInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["areaId"] = Str("\"home\" or an area id from list_zones. Omit to create an allowed area.");
            props["label"] = Str("Name for an allowed area.");
            props["add"] = CellRects("Cells to add to the area.");
            props["remove"] = CellRects("Cells to remove from the area.");
            props["delete"] = Bool("Delete the allowed area given by areaId.");
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries when creating: a repeat returns the original result." + RetryWindow));
            return ObjectSchema(props);
        }

        private static Dictionary<string, object> OrderPawnInput()
        {
            Dictionary<string, object> props = MapProperty();
            props["pawnId"] = Str("Pawn ThingID or load id.");
            props["order"] = EnumString("Order to give.", "equip", "wear", "move", "attack", "rescue", "tend");
            props["targetId"] = Str("What the order acts on: an item id from find_things for equip and wear, a pawn or building id for attack, a pawn id for rescue and tend. Not used by move.");
            props["x"] = Int("Cell x for move.", 0, 1000);
            props["z"] = Int("Cell z for move.", 0, 1000);
            props["queue"] = Bool("For equip and wear: wait behind the pawn's current job and earlier orders instead of replacing them, like shift-click. An idle pawn starts at once.");
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries: a repeat returns the original result, whatever its outcome, without giving the order again, so it never overrides an order given in between." + RetryWindow));
            return ObjectSchema(props, "pawnId", "order");
        }

        private static Dictionary<string, object> AdvanceTimeInput()
        {
            Dictionary<string, object> schema = ObjectSchema(J(
                F("hours", J(F("type", "number"), F("minimum", 0.01), F("maximum", 24), F("description", "In-game hours to run, from 0.01 to 24. One hour is 2,500 ticks, about 42 seconds at Normal speed; 0.01 hours is 25 ticks, for following a fight closely."))),
                F("speed", EnumString("Speed while running. Defaults to Superfast. The game still drops to normal speed during danger, as it does for players. Ultrafast needs development mode.", "Normal", "Fast", "Superfast", "Ultrafast")),
                F("stopOn", J(
                    F("type", "array"),
                    F("items", EnumString("Condition.", "death", "downed", "injured", "mentalBreak", "hostiles", "lowFood", "threatLetter", "letter")),
                    F("description", "Conditions that end the run early and pause. Defaults to death, downed, injured (new injury or bleeding), mentalBreak, hostiles (a visible hostile threat appears), lowFood (stored food drops below the game's Low food threshold of 4 nutrition per colonist), and threatLetter. letter stops on any letter. An empty list runs to the end. Checked every frame; lowFood every 250 ticks."))),
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
            props["requestId"] = J(F("type", "string"), F("maxLength", 100), F("description", "Optional id for safe retries: a repeat returns the bill it added instead of adding another." + RetryWindow));
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
