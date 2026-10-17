using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using RimWorld;
using Verse;

internal static class Program
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static Assembly mod;
    private static int passed;
    private static int failed;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: CompatibilityRegression.exe <RimWorld 1.6 managed directory> <RiMCP.dll>");
            return 2;
        }
        string managed = Path.GetFullPath(args[0]);
        AppDomain.CurrentDomain.AssemblyResolve += (sender, e) =>
        {
            string path = Path.Combine(managed, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };
        try
        {
            return Run(Path.GetFullPath(args[1]));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return 2;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Run(string modPath)
    {
        mod = Assembly.LoadFrom(modPath);
        Console.WriteLine("Testing " + mod.GetName().Version + " with RimWorld " + typeof(Game).Assembly.GetName().Version);
        // All state belongs to this test process. No game startup, networking,
        // player config, or native Unity initialization is performed.
        Current.Game = null;
        Current.CreatingWorld = null;
        Current.ProgramState = ProgramState.Entry;
        typeof(DefOfHelper).GetField("bindingNow", Static).SetValue(null, true);
        SetAccess("full", AllGroups());
        ResearchProjectDef alpha = Project("RegressionAlpha");
        Project("RegressionBeta");
        Project("RegressionGamma");
        foreach (string suffix in new[] { "Alpha", "Beta", "Gamma" })
            DefDatabase<WorkTypeDef>.Add(new WorkTypeDef { defName = "RegressionWork" + suffix, label = "Regression work " + suffix });

        Test("research listing at main menu retains definitions without colony progress", () =>
        {
            Dictionary<string, object> data = Data(Call("ResearchReadService", "GetResearch", "/v1/research?detail=full"));
            var rows = Rows(data["projects"]);
            Check(rows.Count == 3, "expected three seeded research definitions");
            foreach (Dictionary<string, object> row in rows) CheckResearchMetadata(row);
            Check(!data.ContainsKey("current"), "no selected colony project exists");
            Check(!data.ContainsKey("blockers"), "blockers describe a colony, so there are none at the main menu");
            ExpectBridgeError(409, () => Invoke("RiMCP.Read.ResearchReadService", "GetResearch", Request("/v1/research?availableOnly=true"), Route(null, null)));
        });
        Test("research definition lookup at main menu", () =>
        {
            var data = Data(Call("DefReadService", "GetDef", "/v1/defs/RegressionAlpha?kind=research", "defName", alpha.defName));
            CheckResearchMetadata((Dictionary<string, object>)data["research"]);
        });
        Test("research definition search at main menu", () =>
        {
            var data = Data(Call("DefReadService", "SearchDefs", "/v1/defs?kind=ResearchProject&query=Regression&limit=10"));
            var rows = Rows(data["defs"]);
            Check(rows.Count == 3, "research search must include every seeded match");
            foreach (Dictionary<string, object> row in rows) CheckResearchMetadata((Dictionary<string, object>)row["research"]);
        });
        Test("quest listing at main menu is empty", () =>
        {
            Check(Rows(Data(Call("QuestWorldReadService", "ListQuests", "/v1/quests"))["quests"]).Count == 0, "quests must be empty");
        });
        Test("ideology enumeration without a world is empty", () =>
        {
            // This is the exact enumerable used by both ideology endpoints.
            // Calling it directly avoids ModsConfig's native-engine startup.
            var ideos = (IEnumerable)Invoke("RiMCP.Read.IdeoligionReadService", "Ideos");
            Check(!ideos.Cast<object>().Any(), "ideologies must be empty");
        });
        Test("world listing at main menu is empty", () =>
        {
            var data = Data(Call("QuestWorldReadService", "ListWorld", "/v1/world"));
            Check(!data.ContainsKey("playerFaction"), "no player faction exists");
            Check(Rows(data["factions"]).Count == 0, "factions must be empty");
            Check(Rows(data["worldObjects"]).Count == 0, "world objects must be empty");
        });
        Test("research write at main menu rejects with 409", () =>
        {
            ExpectCommandError(409, () => Invoke("RiMCP.Command.ResearchCommandService", "SetCurrent",
                Request("/v1/research/current", "PUT", "{\"projectDefName\":\"RegressionAlpha\"}"), Route()));
        });
        Test("numeric cursor survives JSON and selects the next page", () =>
        {
            const string path = "/v1/defs?kind=workType&query=RegressionWork&limit=1";
            var first = Call("DefReadService", "SearchDefs", path);
            Check(first["nextCursor"] is int, "nextCursor must be a JSON number, not a string");
            Check((bool)first["truncated"], "first page must be truncated");
            int cursor = (int)first["nextCursor"];
            var second = Call("DefReadService", "SearchDefs", path + "&cursor=" + cursor);
            var firstRow = (Dictionary<string, object>)Rows(Data(first)["defs"])[0];
            var secondRow = (Dictionary<string, object>)Rows(Data(second)["defs"])[0];
            Check((string)firstRow["defName"] == "RegressionWorkAlpha" && (string)secondRow["defName"] == "RegressionWorkBeta", "cursor skipped or repeated a definition");
            var last = Call("DefReadService", "SearchDefs", path + "&cursor=" + (int)second["nextCursor"]);
            Check(!(bool)last["truncated"] && !last.ContainsKey("nextCursor"), "last page must finish pagination");
        });
        Test("def subclass kinds search their ancestor's database", () =>
        {
            // Subclass defs live in DefDatabase<WorkTypeDef>; DefDatabase<ProbeWorkTypeDef> stays empty.
            // Adding after earlier searches also checks that cached def indexes pick up new defs.
            DefDatabase<WorkTypeDef>.Add(new ProbeWorkTypeDef { defName = "SubclassProbe", label = "Subclass probe" });
            var rows = Rows(Data(Call("DefReadService", "SearchDefs", "/v1/defs?kind=probeWorkType&limit=10"))["defs"]);
            Check(rows.Count == 1 && (string)((Dictionary<string, object>)rows[0])["defName"] == "SubclassProbe", "subclass kind must find its defs");
            Check(Rows(Data(Call("DefReadService", "SearchDefs", "/v1/defs?kind=workType&query=SubclassProbe"))["defs"]).Count == 1, "parent kind must include subclass defs");
            Data(Call("DefReadService", "GetDef", "/v1/defs/SubclassProbe?kind=probeWorkType", "defName", "SubclassProbe"));
        });
        Test("dispatcher spreads a burst across frames within its time budget", CheckDispatcherBudget);
        Test("advance runs complete at the target and end on outside changes", () =>
        {
            object game = new object();
            object done = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast), null);
            Step(done, Observation(game, 150, TimeSpeed.Superfast));
            Check(Field<string>(done, "Status") == "running", "a run must keep going before its target");
            Step(done, Observation(game, 200, TimeSpeed.Superfast));
            Check(Field<string>(done, "Status") == "completed" && Field<int>(done, "EndTick") == 200, "a run must complete at its target tick");

            object paused = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast), null);
            Step(paused, Observation(game, 150, TimeSpeed.Paused));
            Check(Field<string>(paused, "Status") == "interrupted" && Field<string>(paused, "EndReason") == "paused", "an outside pause must interrupt the run");

            object reloaded = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast), null);
            Step(reloaded, Observation(new object(), 150, TimeSpeed.Superfast));
            Check(Field<string>(reloaded, "EndReason") == "gameUnloaded", "loading another game must interrupt the run");
        });
        Test("advance stop conditions fire on changes during the run, not on the starting state", () =>
        {
            object game = new object();
            object start = Observation(game, 100, TimeSpeed.Superfast, null,
                new[] { Colonist("A", bleeding: true), Colonist("B") }, new[] { Letter(1, "Old raid", true) });
            object run = AdvanceRun(game, start, null);
            Step(run, Observation(game, 110, TimeSpeed.Superfast, null,
                new[] { Colonist("A", bleeding: true), Colonist("B") }, new[] { Letter(1, "Old raid", true), Letter(2, "Trader", false) }));
            Check(Field<string>(run, "Status") == "running", "existing injuries, old letters, and unrequested conditions must not stop the run");
            Check(Events(run).Single() == "letter", "unrequested conditions must still be recorded");
            Step(run, Observation(game, 120, TimeSpeed.Superfast, null,
                new[] { Colonist("A", bleeding: true), Colonist("B", injuries: 1) }, new[] { Letter(1, "Old raid", true), Letter(2, "Trader", false) }));
            object stoppedBy = Field<object>(run, "StoppedBy");
            Check(Field<string>(run, "Status") == "stopped" && Field<string>(stoppedBy, "Kind") == "injured" && Field<string>(stoppedBy, "PawnId") == "B", "a new injury must stop the run");
        });
        Test("advance stops when stored food drops low during the run, not when it starts low", () =>
        {
            object game = new object();
            object lowAtStart = AdvanceRun(game, LowFood(Observation(game, 100, TimeSpeed.Superfast), "low"), null);
            Step(lowAtStart, LowFood(Observation(game, 110, TimeSpeed.Superfast), "low"));
            Check(Field<string>(lowAtStart, "Status") == "running", "food that was already low must not stop the run");

            object run = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast), null);
            Step(run, LowFood(Observation(game, 110, TimeSpeed.Superfast), "stored food is low"));
            object stoppedBy = Field<object>(run, "StoppedBy");
            Check(Field<string>(run, "Status") == "stopped" && Field<string>(stoppedBy, "Kind") == "lowFood" && Field<string>(stoppedBy, "Detail") == "stored food is low", "food dropping low must stop the run by default");
        });
        Test("advance retries are recognised across recent runs and need matching arguments", CheckAdvanceReceipts);
        Test("get_advance answers for the requested run, and a deferred reply stays bound to it", CheckAdvanceWaitBinding);
        Test("a waiting request returns when its run ends, with that run's result", CheckDeferredDispatch);
        Test("an observation failure ends the run and pauses, while an outside pause is left alone", CheckObserverFailure);
        Test("get_advance only defaults to a run from the loaded game", () =>
        {
            FieldInfo latest = mod.GetType("RiMCP.Command.AdvanceRunner", true).GetField("latest", Static);
            object game = new object();
            latest.SetValue(null, AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast)));
            try
            {
                Check(!Data(Call("TimeCommandService", "GetAdvance", "/v1/time/advance", null, null, "RiMCP.Command.")).ContainsKey("run"), "a run from another save must not be reported as current");
            }
            finally
            {
                latest.SetValue(null, null);
            }
        });
        Test("load ids change with every loaded game and guard commands", CheckLoadGuard);
        Test("every tool has an access group, and the groups match the plan", () =>
        {
            IEnumerable tools = (IEnumerable)mod.GetType("RiMCP.Shared.ToolManifest", true).GetProperty("McpTools", Static).GetValue(null);
            Dictionary<string, int> counts = tools.Cast<object>().GroupBy(tool => Field<string>(tool, "Group") ?? "(none)").ToDictionary(group => group.Key, group => group.Count());
            var expected = new Dictionary<string, int> { { "observe", 34 }, { "diagnostics", 4 }, { "time", 2 }, { "administration", 10 }, { "orders", 3 }, { "pawnControl", 2 }, { "gameSettings", 1 }, { "gameSetup", 14 } };
            int count;
            Check(counts.Count == expected.Count && expected.All(pair => counts.TryGetValue(pair.Key, out count) && count == pair.Value),
                "group sizes: " + string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value).ToArray()));
        });
        Test("presets choose groups, and settings saved before presets migrate", CheckAccessPresets);
        Test("operations run one at a time, a step per frame, and stop at a safe step when cancelled or revoked", CheckOperationRunner);
        Test("operation retries return the original operation or an expired answer, for the last 1,000 requests", CheckOperationRetries);
        Test("replacing a loaded colony needs a save or a discard, and commitment mode keeps the game's rules", CheckReplaceRefusal);
        Test("a save file passes only when written by the save, complete, and plain XML with its header", CheckSaveFileCheck);
        Test("the save log watch counts errors on the saving thread and sees its marker", CheckSaveLogWatch);
        Test("save headers give version, mods, and commitment mode, and mods match as the game checks", CheckSaveHeader);
        Test("while the game changes only operation status answers, and cancelling follows the target's group", CheckBusyRouterAndCancel);
        Test("starting sites are filtered on the tile's own facts, longest growing period first", CheckSiteFilters);
        Test("settlement distances bound the nearest counted settlement, and a maximum needs one", CheckSettlementDistance);
        Test("custom difficulty settings read and write as the panel's controls show them", CheckDifficultySettings);
        Test("setup commands need an open setup and its current revision", CheckSetupOutsideSetup);
        Test("only unattended setup lets agents answer the game, and only the question asked", CheckAnswerConfirmation);
        Test("ideoligion names follow the dialogs' rules, and precepts sort into the screen's sections", CheckIdeoligionRules);
        Test("disabled groups are refused, and the access endpoint stays reachable", CheckAccessEnforcement);
        Test("single tools turn on or off as exceptions to their group, and settle back into presets", CheckToolAccess);
        Test("turning off Time ends and pauses a running advance", CheckTimeRevocation);
        Test("cell rectangles are bounded, clipped to the map, and de-duplicated", CheckCellRects);
        Test("rejected cells are all counted but listed only up to a cap", () =>
        {
            object outcomes = Activator.CreateInstance(mod.GetType("RiMCP.Command.CellOutcomes", true), true);
            MethodInfo reject = outcomes.GetType().GetMethod("Reject");
            for (int i = 0; i < 250; i++) reject.Invoke(outcomes, new object[] { new IntVec3(i, 0, 0), "blocked" });
            var dto = (Dictionary<string, object>)outcomes.GetType().GetMethod("ToDto").Invoke(outcomes, null);
            Check((int)dto["rejectedCount"] == 250 && ((object[])dto["rejected"]).Length == 200 && (bool)dto["rejectedListTruncated"], "every rejection must be counted and the list capped");
        });
        Test("a retry returns the first result whatever its outcome, without running again, and refuses a reused id", CheckReceipts);
        Test("list query fields split on commas, and unknown defNames are refused", () =>
        {
            DefDatabase<ThingCategoryDef>.Add(Category("RegressionChunks"));
            MethodInfo fromUri = mod.GetType("RiMCP.Read.ReadRequest", true).GetMethod("FromUri", Static);
            Func<string, object> request = query => fromUri.Invoke(null, new object[] { new Uri("http://127.0.0.1/v1/things?" + query) });
            object known = request("excludeCategory=RegressionChunks,%20RegressionChunks&layers=");
            string[] items = (string[])known.GetType().GetMethod("List").Invoke(known, new object[] { "excludeCategory" });
            Check(items.Length == 2 && items.All(item => item == "RegressionChunks"), "list items must be split and trimmed");
            Check(((string[])known.GetType().GetMethod("List").Invoke(known, new object[] { "layers" })).Length == 0, "an empty list field has no items");
            MethodInfo defList = known.GetType().GetMethod("DefList").MakeGenericMethod(typeof(ThingCategoryDef));
            Check(((IList)defList.Invoke(known, new object[] { "excludeCategory" })).Count == 2, "known categories must resolve");
            object unknown = request("excludeCategory=RegressionChunks,NoSuchCategory");
            ExpectBridgeError(400, () => defList.Invoke(unknown, new object[] { "excludeCategory" }));
        });
        Test("storage accepts lists the top-level categories it takes fully, partly, or not at all", CheckStorageAccepts);
        Test("storage accepts names the building's own refusals and the ranges it takes", CheckStorageLimits);
        Test("growing zones read the game's own growth, rest, and light for each plant", () =>
        {
            Type growing = mod.GetType("RiMCP.Read.GrowingSummary", true);
            foreach (string name in new[] { "Resting", "HasEnoughLight", "GrowthPerTick" })
            {
                Delegate getter = (Delegate)growing.GetField(name, Static).GetValue(null);
                Check(getter.Method.DeclaringType == typeof(Plant), name + " must read the game's Plant member, not the fallback");
            }
        });
        Test("map layers leave out their rows when they mark nothing", CheckMapLayers);
        Test("order_pawn and designate offer exactly the kinds they handle, and queue reaches the handler", () =>
        {
            CheckOfferedKinds("order_pawn", "order", "RiMCP.Command.PawnOrderCommandService", "Orders");
            CheckOfferedKinds("designate", "kind", "RiMCP.Command.DesignationCommandService", "Kinds");
            object spec = Tool("order_pawn");
            Check(ToolProperties(spec).ContainsKey("queue") && Field<string[]>(spec, "BodyKeys").Contains("queue"), "queue must be in the schema and sent in the body");
            Check(new[] { "x", "z" }.All(Field<string[]>(spec, "BodyKeys").Contains), "move's cell must be sent in the body");
        });
        Test("orders that cannot be queued are refused before anything changes", () =>
        {
            foreach (string order in new[] { "move", "attack", "rescue", "tend" })
            {
                ExpectCommandError(400, () => Invoke("RiMCP.Command.PawnOrderCommandService", "OrderPawn",
                    Request("/v1/pawns/Human1/orders", "POST", "{\"order\":\"" + order + "\",\"queue\":true,\"x\":1,\"z\":1,\"targetId\":\"Human2\"}"), Route("pawnId", "Human1")));
            }
        });
        Test("placement rotations parse by name, and blockers keep kind first and drop empty fields", () =>
        {
            Type placement = mod.GetType("RiMCP.Command.BuildPlacement", true);
            MethodInfo parse = placement.GetMethod("ParseRotation", Static);
            Check((Rot4?)parse.Invoke(null, new object[] { " East " }) == Rot4.East, "rotation names are trimmed and case-insensitive");
            Check(parse.Invoke(null, new object[] { "up" }) == null, "an unknown rotation must not parse");
            Check((string)placement.GetMethod("RotationName", Static).Invoke(null, new object[] { Rot4.West }) == "west", "rotations are named as the tools take them");

            Type dto = mod.GetType("RiMCP.Read.Dto", true);
            MethodInfo field = dto.GetMethod("Field", Static);
            Array fields = Array.CreateInstance(field.ReturnType, 2);
            fields.SetValue(field.Invoke(null, new object[] { "defName", "Steel" }), 0);
            fields.SetValue(field.Invoke(null, new object[] { "forbidden", null }), 1);
            var blocker = (Dictionary<string, object>)mod.GetType("RiMCP.Read.Blockers", true).GetMethod("Of", Static).Invoke(null, new object[] { "missingMaterial", "Needs steel.", fields });
            Check(blocker.Keys.SequenceEqual(new[] { "kind", "defName", "message" }), "blocker fields: " + string.Join(", ", blocker.Keys));
        });
        Test("food trends need six hours of samples and use the last three days", CheckFoodTrend);
        Test("bill states follow ShouldDoNow without changing the bill", CheckBillStates);
        Test("storage edits keep special filters and report any change to the accepted items", CheckStorageEdits);
        Test("storage entries change a special filter only when named, and say which still refuse what they allow", CheckSpecialFilterEntries);
        Test("a single-kind ingredient needs one kind with enough, and a mixed one adds kinds up", CheckIngredientKinds);
        Test("advance reports the most severe stop when several happen in one frame", () =>
        {
            // The game pauses itself for threat letters, and the target can be reached in the same frame.
            object game = new object();
            object run = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast, null, new[] { Colonist("A") }, new object[0]), null);
            Step(run, Observation(game, 300, TimeSpeed.Paused, "Raider", new[] { Colonist("A", downed: true) }, new[] { Letter(7, "Raid", true) }));
            Check(Field<string>(run, "Status") == "stopped" && Field<string>(Field<object>(run, "StoppedBy"), "Kind") == "downed", "the downed colonist must be reported as the cause");
            Check(Events(run).SequenceEqual(new[] { "downed", "hostiles", "threatLetter" }), "every change must be recorded in severity order: " + string.Join(", ", Events(run)));
        });
        Test("bill repeatCount appears in summary and full detail", CheckBillRepeatCount);
        Test("bill settings reject values the bill dialog cannot produce", CheckBillSettingsValidation);
        Test("pawn work reads leave uninitialized work settings untouched", () =>
        {
            // Visitors and raiders have a tracker without priorities. Reading priorities
            // from it logs an error and initializes it, mutating the save.
            var pawn = new Pawn();
            pawn.workSettings = new Pawn_WorkSettings(pawn);
            Check(Invoke("RiMCP.Read.PawnReadService", "SerializePawnWork", pawn) == null, "get_pawn must omit work for pawns that never work");
            Check(((object[])Invoke("RiMCP.Read.WorkReadService", "CompactWork", pawn)).Length == 0, "list_work must omit work for pawns that never work");
            Check(!pawn.workSettings.EverWork, "reads must not initialize work settings");
        });
        Test("permanent game conditions report no duration", () =>
        {
            // GameCondition.Duration and TicksLeft log an error for permanent conditions.
            var condition = new GameCondition { def = new GameConditionDef { defName = "RegressionPermanent", label = "regression permanent" }, Permanent = true };
            var row = (Dictionary<string, object>)Invoke("RiMCP.Read.EnvironmentReadService", "SerializeGameCondition", condition, "world");
            Check((bool)row["permanent"] && (string)row["scope"] == "world", "permanent world condition must be labelled");
            Check(!row.ContainsKey("durationTicks") && !row.ContainsKey("ticksLeft"), "permanent conditions have no duration");
        });
        Test("medical care parsing accepts enum names only", () =>
        {
            MethodInfo parse = mod.GetType("RiMCP.Command.PawnAssignmentCommandService", true).GetMethod("ParseEnum", Static).MakeGenericMethod(typeof(MedicalCareCategory));
            Func<string, object> call = value => parse.Invoke(null, new object[] { value, "care", "Medical care" });
            Check((MedicalCareCategory)call("best") == MedicalCareCategory.Best, "names must parse case-insensitively");
            // Enum.TryParse would turn these into other, valid categories.
            foreach (string value in new[] { "3", "NoMeds,HerbalOrWorse" })
                ExpectCommandError(400, () => call(value));
        });
        Test("colony research still reports real finished and progress values", () =>
        {
            // Bypass the Game constructor, which boots native engine systems;
            // use the real manager and its actual progress dictionary.
            Game game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
            game.researchManager = new ResearchManager();
            var progress = (Dictionary<ResearchProjectDef, float>)typeof(ResearchManager)
                .GetField("progress", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(game.researchManager);
            progress[alpha] = 40f;
            Current.Game = game;
            try
            {
                var row = (Dictionary<string, object>)Invoke("RiMCP.Read.ResearchReadService", "SerializeProject", alpha, Detail("Summary"));
                Check((float)row["progress"] == 40f && !(bool)row["finished"], "partial progress must remain available");
                progress[alpha] = 100f;
                row = (Dictionary<string, object>)Invoke("RiMCP.Read.ResearchReadService", "SerializeProject", alpha, Detail("Full"));
                Check((float)row["progress"] == 100f && (bool)row["finished"], "completed research must remain finished");
            }
            finally { Current.Game = null; }
        });
        Console.WriteLine(passed + " passed; " + failed + " failed.");
        return failed == 0 ? 0 : 1;
    }

    private static ResearchProjectDef Project(string name)
    {
        var project = new ResearchProjectDef { defName = name, label = name, baseCost = 100f, description = "Regression research metadata" };
        DefDatabase<ResearchProjectDef>.Add(project);
        return project;
    }

    private static void CheckResearchMetadata(Dictionary<string, object> row)
    {
        Check(row.ContainsKey("defName") && row.ContainsKey("baseCost"), "definition metadata is missing");
        Check(!row.ContainsKey("finished") && !row.ContainsKey("progress"), "unavailable colony research state must be omitted");
    }

    private static void CheckBillRepeatCount()
    {
        BillRepeatModeDefOf.RepeatCount = new BillRepeatModeDef { defName = "RepeatCount" };
        var bill = new Bill_Production
        {
            recipe = new RecipeDef { defName = "RegressionRecipe", label = "Regression recipe" },
            repeatMode = BillRepeatModeDefOf.RepeatCount,
            repeatCount = 7,
            targetCount = 99
        };
        Type recordType = mod.GetType("RiMCP.Read.ProductionReadService+BillRecord", true);
        object record = Activator.CreateInstance(recordType, new object[] { null, null, bill });
        foreach (string detail in new[] { "Summary", "Full" })
        {
            var row = (Dictionary<string, object>)Invoke("RiMCP.Read.ProductionReadService", "SerializeBill", record, Detail(detail), null);
            Check(row.ContainsKey("repeatCount") && (int)row["repeatCount"] == 7, "repeatCount missing/incorrect in " + detail);
            Check((int)row["targetCount"] == 99, "repeatCount must not replace targetCount in " + detail);
        }
    }

    private static void CheckBillSettingsValidation()
    {
        const string service = "RiMCP.Command.ProductionCommandService";
        Type bodyType = mod.GetType(service + "+BillSettingsBody", true);
        var bill = new Bill_Production
        {
            recipe = new RecipeDef { defName = "RegressionSettingsRecipe", label = "Regression settings recipe" },
            targetCount = 10,
            unpauseWhenYouHave = 5
        };
        Func<string, object, object> resolve = (property, value) =>
        {
            object body = Activator.CreateInstance(bodyType, true);
            bodyType.GetProperty(property).SetValue(body, value);
            return Invoke(service, "ResolveBillSettings", bill, body, null);
        };

        // NaN passes a plain range check and makes WorkGiver_DoBill find no ingredients.
        foreach (float radius in new[] { float.NaN, float.PositiveInfinity, 0f, 2.5f, 1000f })
            ExpectCommandError(400, () => resolve("IngredientSearchRadius", (float?)radius));
        object unlimited = resolve("IngredientSearchRadius", (float?)150f);
        Check((float)unlimited.GetType().GetField("IngredientSearchRadius").GetValue(unlimited) == Bill.MaxIngredientSearchRadius, "a radius of 100 or more must be stored as unlimited");

        resolve("UnpauseWhenYouHave", (int?)0);
        ExpectCommandError(400, () => resolve("UnpauseWhenYouHave", (int?)10));

        // Like Dialog_BillConfig, changing the target moves the unpause threshold with it.
        Invoke(service, "ApplyBillSettings", bill, resolve("TargetCount", (int?)20));
        Check(bill.targetCount == 20 && bill.unpauseWhenYouHave == 15, "raising the target must keep the unpause gap");
        Invoke(service, "ApplyBillSettings", bill, resolve("TargetCount", (int?)3));
        Check(bill.targetCount == 3 && bill.unpauseWhenYouHave == 0, "lowering the target must keep unpause below it");
    }

    private static void CheckDispatcherBudget()
    {
        // Three handlers that each take longer than a 4 ms frame budget: every frame must run
        // exactly one of them and record how long it held the main thread.
        Type dispatcherType = mod.GetType("RiMCP.Bridge.MainThreadDispatcher", true);
        Type responseType = mod.GetType("RiMCP.Bridge.BridgeResponse", true);
        Delegate slow = Expression.Lambda(typeof(Func<>).MakeGenericType(responseType), Expression.Block(
            Expression.Call(typeof(Thread).GetMethod("Sleep", new[] { typeof(int) }), Expression.Constant(10)),
            Expression.Call(responseType.GetMethod("Json", Static), Expression.Constant(200), Expression.Constant("{}")))).Compile();
        object dispatcher = Activator.CreateInstance(dispatcherType, true);
        Func<int> queued = () => (int)dispatcherType.GetProperty("QueuedCount").GetValue(dispatcher);
        Task<object>[] callers = Enumerable.Range(0, 3)
            .Select(_ => Task.Run(() => dispatcherType.GetMethod("Invoke").Invoke(dispatcher, new object[] { slow, 10000 })))
            .ToArray();
        for (int wait = 0; queued() < 3 && wait < 500; wait++) Thread.Sleep(10);
        Thread.Sleep(50);
        Check(queued() == 3, "expected three queued requests");
        for (int expected = 2; expected >= 0; expected--)
        {
            dispatcherType.GetMethod("ProcessQueued").Invoke(dispatcher, new object[] { 16, 4.0 });
            Check(queued() == expected, "each frame must stop after the budget is spent; " + queued() + " left, expected " + expected);
        }
        Check(Task.WaitAll(callers, 5000), "callers must complete");
        foreach (Task<object> caller in callers)
        {
            object ms = responseType.GetField("MainThreadMs").GetValue(caller.Result);
            Check(ms != null && (double)ms >= 9, "response must record main-thread time");
        }
    }

    private static object AdvanceRun(object game, object baseline, string[] stopOn = null, string requestId = null, int id = 1)
    {
        Type type = mod.GetType("RiMCP.Command.AdvanceRun", true);
        stopOn = stopOn ?? (string[])type.GetField("DefaultStopOn").GetValue(null);
        int start = Field<int>(baseline, "Tick");
        return Activator.CreateInstance(type, new object[] { id, requestId, game, TimeSpeed.Superfast, start, start + 100, 1f, stopOn, baseline });
    }

    private static void CheckAdvanceReceipts()
    {
        // FindReceipt is what advance_time consults before starting a run.
        Type runType = mod.GetType("RiMCP.Command.AdvanceRun", true);
        MethodInfo find = mod.GetType("RiMCP.Command.AdvanceRunner", true).GetMethod("FindReceipt");
        string[] stopOn = (string[])runType.GetField("DefaultStopOn").GetValue(null);
        object game = new object();
        object first = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast), requestId: "first", id: 1);
        object second = AdvanceRun(game, Observation(game, 200, TimeSpeed.Superfast), requestId: "second", id: 2);
        Array runs = Array.CreateInstance(runType, 2);
        runs.SetValue(first, 0);
        runs.SetValue(second, 1);
        Func<object, string, float, object> receipt = (forGame, requestId, hours) =>
            find.Invoke(null, new object[] { runs, forGame, requestId, hours, TimeSpeed.Superfast, stopOn });

        Check(receipt(game, "first", 1f) == first, "a retry must find a run that is no longer the latest");
        Check(receipt(game, "third", 1f) == null && receipt(game, null, 1f) == null, "new or missing ids must start a run");
        Check(receipt(new object(), "first", 1f) == null, "runs from another loaded game must not match");
        ExpectCommandError(409, () => receipt(game, "first", 2f));
    }

    private static void CheckAdvanceWaitBinding()
    {
        // Two runs where the requested one is not the latest. Runs belong to the loaded game, which
        // is null in this harness.
        Type runner = mod.GetType("RiMCP.Command.AdvanceRunner", true);
        IList history = (IList)runner.GetField("history", Static).GetValue(null);
        FieldInfo latest = runner.GetField("latest", Static);
        object first = AdvanceRun(null, Observation(null, 100, TimeSpeed.Superfast), id: 1);
        object second = AdvanceRun(null, Observation(null, 200, TimeSpeed.Superfast), id: 2);
        history.Add(first);
        history.Add(second);
        latest.SetValue(null, second);
        Func<object, int> runId = response => (int)((Dictionary<string, object>)Data(Json.Deserialize<Dictionary<string, object>>(
            (string)response.GetType().GetField("Body").GetValue(response)))["run"])["id"];
        try
        {
            object response = Invoke("RiMCP.Command.TimeCommandService", "GetAdvance", Request("/v1/time/advance?runId=1&waitSeconds=5"), Route());
            Check(runId(response) == 1, "runId must select that run, not the latest");
            object deferred = response.GetType().GetField("Deferred").GetValue(response);
            Check(deferred != null, "waiting on a running run must defer the reply");
            Check(runId(((Delegate)deferred.GetType().GetField("Reply").GetValue(deferred)).DynamicInvoke()) == 1, "the deferred reply must describe the same run");
            object unknown = Invoke("RiMCP.Command.TimeCommandService", "GetAdvance", Request("/v1/time/advance?runId=9"), Route());
            Check((int)unknown.GetType().GetField("StatusCode").GetValue(unknown) == 404, "an unknown run must be reported");
        }
        finally
        {
            history.Clear();
            latest.SetValue(null, null);
        }
    }

    private static void CheckDeferredDispatch()
    {
        // The whole bridge path: the request waits off the main thread, returns as soon as its run
        // ends, and the reply is built for that run. The test thread plays the main thread.
        Type runner = mod.GetType("RiMCP.Command.AdvanceRunner", true);
        Type bridge = mod.GetType("RiMCP.Bridge.BridgeRuntime", true);
        IList history = (IList)runner.GetField("history", Static).GetValue(null);
        FieldInfo latest = runner.GetField("latest", Static);
        object first = AdvanceRun(null, Observation(null, 100, TimeSpeed.Superfast), id: 1);
        object second = AdvanceRun(null, Observation(null, 200, TimeSpeed.Superfast), id: 2);
        history.Add(first);
        history.Add(second);
        latest.SetValue(null, second);
        try
        {
            System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
            Task<object> call = Task.Run(() => bridge.GetMethod("Dispatch", Static).Invoke(null, new[] { Request("/v1/time/advance?runId=1&waitSeconds=10") }));
            MethodInfo pump = bridge.GetMethod("ProcessMainThreadQueue", Static);
            bool ended = false;
            while (!call.IsCompleted && elapsed.Elapsed.TotalSeconds < 8)
            {
                pump.Invoke(null, null);
                if (!ended && elapsed.Elapsed.TotalMilliseconds > 300)
                {
                    Field<System.Threading.ManualResetEventSlim>(first, "Ended").Set();
                    ended = true;
                }
                Thread.Sleep(5);
            }
            Check(call.IsCompleted && elapsed.Elapsed.TotalSeconds < 3, "the wait must end when its run ends, not at the timeout");
            object response = call.Result;
            string body = (string)response.GetType().GetField("Body").GetValue(response);
            Check((int)((Dictionary<string, object>)Data(Json.Deserialize<Dictionary<string, object>>(body))["run"])["id"] == 1, "the reply must describe the awaited run: " + body);
        }
        finally
        {
            history.Clear();
            latest.SetValue(null, null);
        }
    }

    private static void Step(object run, object observation)
    {
        run.GetType().GetMethod("Step").Invoke(run, new[] { observation });
    }

    private static object Observation(object game, int tick, TimeSpeed speed, string threat = null, object[] colonists = null, object[] letters = null)
    {
        object observation = New("AdvanceObservation", "Game", game, "Playing", true, "Tick", tick, "Speed", speed, "Threat", threat);
        IDictionary colonistsById = Field<IDictionary>(observation, "Colonists");
        foreach (object colonist in colonists ?? new object[0]) colonistsById[Field<string>(colonist, "Id")] = colonist;
        IDictionary lettersById = Field<IDictionary>(observation, "Letters");
        foreach (object letter in letters ?? new object[0]) lettersById[Field<int>(letter, "Id")] = letter;
        return observation;
    }

    private static object LowFood(object observation, string detail)
    {
        observation.GetType().GetField("LowFood").SetValue(observation, detail);
        return observation;
    }

    private static object Colonist(string id, bool downed = false, bool bleeding = false, int injuries = 0)
    {
        return New("ColonistState", "Id", id, "Name", id, "Downed", downed, "Bleeding", bleeding, "Injuries", injuries);
    }

    private static object Letter(int id, string label, bool threat)
    {
        return New("LetterState", "Id", id, "Label", label, "Threat", threat);
    }

    private static string[] Events(object run)
    {
        return Field<IEnumerable>(run, "Events").Cast<object>().Select(change => Field<string>(change, "Kind")).ToArray();
    }

    private static object New(string type, params object[] fields)
    {
        object value = Activator.CreateInstance(mod.GetType("RiMCP.Command." + type, true), true);
        for (int i = 0; i < fields.Length; i += 2) value.GetType().GetField((string)fields[i]).SetValue(value, fields[i + 1]);
        return value;
    }

    private static T Field<T>(object value, string name)
    {
        return (T)value.GetType().GetField(name).GetValue(value);
    }

    private static void CheckObserverFailure()
    {
        Type runner = mod.GetType("RiMCP.Command.AdvanceRunner", true);
        Type observationType = mod.GetType("RiMCP.Command.AdvanceObservation", true);
        FieldInfo observer = runner.GetField("Observer", Static);
        FieldInfo latest = runner.GetField("latest", Static);
        object original = observer.GetValue(null);
        object run = AdvanceRun(null, Observation(null, 100, TimeSpeed.Superfast));
        observer.SetValue(null, Expression.Lambda(typeof(Func<>).MakeGenericType(observationType),
            Expression.Throw(Expression.New(typeof(InvalidOperationException).GetConstructor(new[] { typeof(string) }), Expression.Constant("probe")), observationType)).Compile());
        latest.SetValue(null, run);
        try
        {
            runner.GetMethod("Update", Static).Invoke(null, null);
            Check(Field<string>(run, "Status") == "interrupted" && Field<string>(run, "EndReason").StartsWith("error: InvalidOperationException: probe"), "a failed observation must end the run with the error");
            Check((bool)run.GetType().GetProperty("PausesGameAtEnd").GetValue(run), "a failed run must pause the game it was running");
            Check(Field<System.Threading.ManualResetEventSlim>(run, "Ended").IsSet, "waiting requests must be released");
        }
        finally
        {
            observer.SetValue(null, original);
            latest.SetValue(null, null);
        }

        object game = new object();
        object paused = AdvanceRun(game, Observation(game, 100, TimeSpeed.Superfast));
        Step(paused, Observation(game, 150, TimeSpeed.Paused));
        Check(!(bool)paused.GetType().GetProperty("PausesGameAtEnd").GetValue(paused), "a pause by someone else must not trigger another pause");
    }

    private static void CheckLoadGuard()
    {
        Type context = mod.GetType("RiMCP.Bridge.GameContext", true);
        Func<string> loadId = () => (string)context.GetProperty("LoadId", Static).GetValue(null);
        MethodInfo handle = mod.GetType("RiMCP.Bridge.BridgeRouter", true).GetMethod("Handle", Static);
        Func<string, object> put = body => handle.Invoke(null, new[] { Request("/v1/time/speed", "PUT", body) });
        Func<object, string> bodyOf = response => (string)response.GetType().GetField("Body").GetValue(response);
        try
        {
            Check(loadId() == null, "no game means no load id");
            Current.Game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
            string first = loadId();
            Check(first != null && loadId() == first, "a load id must stay the same while the game stays loaded");
            Current.Game = (Game)FormatterServices.GetUninitializedObject(typeof(Game));
            string second = loadId();
            Check(second != null && second != first, "another load must get a new id");

            object stale = put("{\"speed\":\"Paused\",\"expectedLoadId\":\"" + first + "\"}");
            Check((int)stale.GetType().GetField("StatusCode").GetValue(stale) == 409 && bodyOf(stale).Contains("Another save was loaded"), "a command planned for an earlier load must be refused: " + bodyOf(stale));
            // The matching id passes the guard and reaches the handler, which needs a playing game.
            Check(!bodyOf(put("{\"speed\":\"Paused\",\"expectedLoadId\":\"" + second + "\"}")).Contains("Another save was loaded"), "the current load id must pass the guard");
            Check(!bodyOf(put("{\"speed\":\"Paused\"}")).Contains("Another save was loaded"), "the guard is optional");
        }
        finally
        {
            Current.Game = null;
        }
    }

    private static string[] AllGroups()
    {
        return (string[])mod.GetType("RiMCP.Shared.ToolGroups", true).GetField("All").GetValue(null);
    }

    private static void SetAccess(string preset, string[] groups)
    {
        mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetMethod("ApplyAccess", Static, null, new[] { typeof(string), typeof(IEnumerable<string>) }, null).Invoke(null, new object[] { preset, groups });
    }

    private static int AccessRevision()
    {
        object access = mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetProperty("Access", Static).GetValue(null);
        return Field<int>(access, "Revision");
    }

    // Operations: a step factory for tests, built as the internal Func<Operation, IEnumerable<string>>.
    private static Delegate Steps(Func<object, IEnumerable<string>> body)
    {
        Type operation = mod.GetType("RiMCP.Command.Operation", true);
        ParameterExpression parameter = Expression.Parameter(operation, "operation");
        Expression call = Expression.Invoke(Expression.Constant(body), Expression.Convert(parameter, typeof(object)));
        return Expression.Lambda(typeof(Func<,>).MakeGenericType(operation, typeof(IEnumerable<string>)), call, parameter).Compile();
    }

    private static Type Runner()
    {
        return mod.GetType("RiMCP.Command.OperationRunner", true);
    }

    private static object StartOperation(string tool, string group, string requestId, string fingerprint, Func<object, IEnumerable<string>> body)
    {
        try { return Runner().GetMethod("Start", Static).Invoke(null, new object[] { tool, group, requestId, fingerprint, Steps(body) }); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }

    private static void RunnerUpdate()
    {
        Runner().GetMethod("Update", Static).Invoke(null, null);
    }

    private static object OperationById(string id)
    {
        return Runner().GetMethod("Find", Static).Invoke(null, new object[] { id });
    }

    private static string OperationState(object operation)
    {
        return Field<string>(operation, "State");
    }

    private static string OperationId(object operation)
    {
        return Field<string>(operation, "Id");
    }

    private static void SetOperationField(object operation, string name, object value)
    {
        operation.GetType().GetField(name).SetValue(operation, value);
    }

    private static IEnumerable<string> TwoSteps(List<string> log)
    {
        log.Add("a");
        yield return "a";
        log.Add("b");
        yield return "b";
    }

    private static IEnumerable<string> Finish()
    {
        yield break;
    }

    private static void CheckOperationRunner()
    {
        Runner().GetMethod("Reset", Static).Invoke(null, null);
        try
        {
            List<string> log = new List<string>();
            object first = StartOperation("save_game", "gameSetup", null, null, _ => TwoSteps(log));
            Check(OperationState(first) == "queued" && log.Count == 0, "starting queues the operation; its steps run on later frames");
            ExpectCommandError(409, () => StartOperation("load_game", "gameSetup", null, null, _ => Finish()));
            RunnerUpdate();
            Check(OperationState(first) == "running" && Field<string>(first, "Step") == "a", "each frame runs one step");
            RunnerUpdate();
            RunnerUpdate();
            Check(OperationState(first) == "succeeded" && log.SequenceEqual(new[] { "a", "b" }) && (bool)first.GetType().GetProperty("Done").GetValue(first), "the operation succeeds after its last step");

            // A step can end the operation with another outcome.
            Type ended = mod.GetType("RiMCP.Command.OperationEnded", true);
            object failing = StartOperation("save_game", "gameSetup", null, null, _ => Throws((Exception)Activator.CreateInstance(ended, "unverified", "log hidden")));
            RunnerUpdate();
            Check(OperationState(failing) == "unverified" && Field<string>(failing, "Message") == "log hidden", "a step's OperationEnded sets the outcome");

            // Cancelled before it starts, it never runs; cancelling a finished one changes nothing.
            log.Clear();
            object queued = StartOperation("save_game", "gameSetup", null, null, _ => TwoSteps(log));
            MethodInfo cancel = Runner().GetMethod("Cancel", Static);
            Check((bool)cancel.Invoke(null, new object[] { queued, "stop" }) && OperationState(queued) == "cancelled" && log.Count == 0, "a queued operation cancels at once");
            Check(!(bool)cancel.Invoke(null, new object[] { first, "stop" }), "cancelling a finished operation reports that it had finished");

            // A native step cannot be interrupted: cancellation waits for it, then stops before the next.
            log.Clear();
            object native = StartOperation("leave_game", "gameSetup", null, null, operation => NativeThenMore(operation, log));
            RunnerUpdate();
            Check(Field<bool>(native, "InNativeStep"), "the operation is in its native step");
            cancel.Invoke(null, new object[] { native, "stop" });
            RunnerUpdate();
            Check(OperationState(native) == "running" && log.Contains("native done"), "the native step finishes despite the cancellation");
            RunnerUpdate();
            Check(OperationState(native) == "cancelled" && !log.Contains("after"), "the operation stops at the next step boundary");

            // Revocation affects only its group; a queued operation never starts.
            object observe = StartOperation("find_starting_sites", "observe", null, null, _ => TwoSteps(new List<string>()));
            MethodInfo revoke = Runner().GetMethod("Revoke", Static);
            revoke.Invoke(null, new object[] { "gameSetup", "access off" });
            Check(OperationState(observe) == "queued", "revoking Game setup leaves other groups' operations alone");
            revoke.Invoke(null, new object[] { "observe", "access off" });
            Check(OperationState(observe) == "revoked" && Field<string>(observe, "Message") == "access off", "a queued operation of the revoked group never starts");

            // Only finished operations are evicted, oldest first, past the limit.
            Runner().GetMethod("Reset", Static).Invoke(null, null);
            int max = (int)Runner().GetField("MaxCompleted", Static).GetValue(null);
            List<string> ids = new List<string>();
            for (int i = 0; i < max + 8; i++)
            {
                object operation = StartOperation("save_game", "gameSetup", null, null, _ => Finish());
                ids.Add(OperationId(operation));
                RunnerUpdate();
            }
            Check(ids.Take(8).All(id => OperationById(id) == null) && ids.Skip(8).All(id => OperationById(id) != null), "the last " + max + " finished operations are kept");
        }
        finally
        {
            Runner().GetMethod("Reset", Static).Invoke(null, null);
        }
    }

    private static IEnumerable<string> Throws(Exception exception)
    {
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static IEnumerable<string> NativeThenMore(object operation, List<string> log)
    {
        SetOperationField(operation, "InNativeStep", true);
        yield return "native";
        SetOperationField(operation, "InNativeStep", false);
        log.Add("native done");
        yield return "native done";
        log.Add("after");
        yield return "after";
    }

    private static void CheckOperationRetries()
    {
        Runner().GetMethod("Reset", Static).Invoke(null, null);
        MethodInfo recall = Runner().GetMethod("Recall", Static);
        Func<string, string, string, object[]> remember = (tool, requestId, fingerprint) =>
        {
            object[] args = { tool, requestId, fingerprint, null };
            try { return new[] { recall.Invoke(null, args), args[3] }; }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        };
        try
        {
            object op = StartOperation("load_game", "gameSetup", "r1", "fp1", _ => Finish());
            object[] again = remember("load_game", "r1", "fp1");
            Check(again[0] != null && ReferenceEquals(again[1], op), "a repeated requestId recalls its operation");
            ExpectCommandError(409, () => remember("load_game", "r1", "fp2"));
            ExpectCommandError(409, () => remember("save_game", "r1", "fp1"));
            Check(remember("load_game", "r2", "fp1")[0] == null, "a new requestId is not recalled");
            RunnerUpdate();

            // Once the detailed record is evicted, the compact record still answers, without the operation.
            int max = (int)Runner().GetField("MaxCompleted", Static).GetValue(null);
            for (int i = 0; i < max; i++)
            {
                StartOperation("save_game", "gameSetup", "churn" + i, "fp", _ => Finish());
                RunnerUpdate();
            }
            object[] expired = remember("load_game", "r1", "fp1");
            Check(expired[0] != null && expired[1] == null && Field<string>(expired[0], "Outcome").StartsWith("succeeded"), "an evicted operation's requestId gets its outcome, not a new run");

            // Past the request records' limit, the oldest request is no longer recognised.
            int records = (int)Runner().GetField("MaxRequestRecords", Static).GetValue(null);
            for (int i = max; i < records; i++)
            {
                StartOperation("save_game", "gameSetup", "churn" + i, "fp", _ => Finish());
                RunnerUpdate();
            }
            Check(remember("load_game", "r1", "fp1")[0] == null && remember("save_game", "churn" + (records - 1), "fp")[0] != null, "the last " + records + " requests are recognised, and no more");
        }
        finally
        {
            Runner().GetMethod("Reset", Static).Invoke(null, null);
        }
    }

    private static void CheckSiteFilters()
    {
        Type criteriaType = mod.GetType("RiMCP.Command.SiteCriteria", true);
        Type factsType = mod.GetType("RiMCP.Command.SiteFacts", true);
        Type search = mod.GetType("RiMCP.Command.SiteSearch", true);
        Func<object, string, object, object> set = (target, name, value) => { target.GetType().GetField(name).SetValue(target, value); return target; };
        Func<object> criteria = () => Activator.CreateInstance(criteriaType, true);
        Func<object> tile = () =>
        {
            object facts = Activator.CreateInstance(factsType, true);
            set(facts, "Biome", "TemperateForest");
            set(facts, "Hilliness", "SmallHills");
            set(facts, "Temperature", 14f);
            set(facts, "Rainfall", 1200f);
            set(facts, "River", true);
            return facts;
        };
        Func<object, object, bool> passes = (c, f) => (bool)search.GetMethod("PassesCheap", Static).Invoke(null, new[] { c, f });
        Func<object, float, int, double> score = (c, temperature, days) => (double)search.GetMethod("Score", Static).Invoke(null, new object[] { c, temperature, days });

        Check(passes(criteria(), tile()), "no filters accept any land tile");
        Check(!passes(criteria(), set(tile(), "WaterCovered", true)), "water is never a starting site");
        Check(passes(set(criteria(), "Biomes", new HashSet<string> { "TemperateForest", "BorealForest" }), tile()), "a listed biome passes");
        Check(!passes(set(criteria(), "Biomes", new HashSet<string> { "Desert" }), tile()), "an unlisted biome fails");
        Check(!passes(set(criteria(), "Hilliness", new HashSet<string> { "Flat" }), tile()), "hilliness filters");
        Check(passes(set(set(criteria(), "MinTemperature", (float?)14f), "MaxTemperature", (float?)14f), tile()), "temperature bounds include their ends");
        Check(!passes(set(criteria(), "MinTemperature", (float?)15f), tile()) && !passes(set(criteria(), "MaxRainfall", (float?)1000f), tile()), "temperature and rainfall bounds filter");
        Check(passes(set(criteria(), "River", (bool?)true), tile()) && !passes(set(criteria(), "River", (bool?)false), tile()) && !passes(set(criteria(), "Road", (bool?)true), tile()), "river and road can be wanted or refused");

        object any = criteria();
        Check(score(any, 30f, 60) > score(any, 20f, 55), "a longer growing period comes first");
        Check(score(any, 20f, 40) > score(any, 5f, 40), "with no temperature asked for, nearer 20 C comes first");
        object mild = set(set(criteria(), "MinTemperature", (float?)10f), "MaxTemperature", (float?)20f);
        Check(score(mild, 15f, 40) > score(mild, 19f, 40), "otherwise nearer the middle of the range asked for");
    }

    private static void CheckSettlementDistance()
    {
        Type criteriaType = mod.GetType("RiMCP.Command.SiteCriteria", true);
        MethodInfo passes = mod.GetType("RiMCP.Command.SiteSearch", true).GetMethod("PassesDistance", Static);
        Func<float?, float?, float?, bool> check = (min, max, nearest) =>
        {
            object criteria = Activator.CreateInstance(criteriaType, true);
            criteriaType.GetField("MinSettlementDistance").SetValue(criteria, min);
            criteriaType.GetField("MaxSettlementDistance").SetValue(criteria, max);
            return (bool)passes.Invoke(null, new object[] { criteria, nearest });
        };
        Check(check(null, null, null) && check(null, null, 3f), "no bounds accept any tile");
        Check(check(null, 10f, 10f) && !check(null, 10f, 10.5f), "the maximum includes its end");
        Check(!check(null, 10f, null), "a maximum needs a counted settlement within it");
        Check(check(5f, null, 5f) && !check(5f, null, 4.9f), "the minimum includes its end");
        Check(check(5f, null, null), "with no counted settlement, nothing is too near");
    }

    private static void CheckDifficultySettings()
    {
        Type settings = mod.GetType("RiMCP.Command.DifficultySettings", true);
        IList all = (IList)settings.GetProperty("All", Static).GetValue(null, null);
        Func<string, object> named = name => settings.GetMethod("Named", Static).Invoke(null, new object[] { name });
        Check(all.Count > 40 && named("threatScale") != null && named("THREATSCALE") != null && named("noSuchSetting") == null, "the panel's settings are listed and found by name");
        Func<object, string, Delegate> member = (setting, field) => (Delegate)setting.GetType().GetField(field).GetValue(setting);
        Action<string, Difficulty, float> write = (name, values, value) => member(named(name), "Write").DynamicInvoke(values, value);
        Func<string, Difficulty, float> read = (name, values) => (float)member(named(name), "Read").DynamicInvoke(values);
        Action<string, Difficulty, bool> toggle = (name, values, shown) => member(named(name), "WriteToggle").DynamicInvoke(values, shown);

        Difficulty difficulty = new Difficulty();
        write("threatScale", difficulty, 1.234f);
        Check(Math.Abs(difficulty.threatScale - 1.23f) < 0.0001f, "sliders round to their steps");
        write("colonistMoodOffset", difficulty, 5.6f);
        Check(difficulty.colonistMoodOffset == 6f, "whole-number sliders round to whole numbers");
        write("diseaseIntervalFactor", difficulty, 2f);
        Check(Math.Abs(difficulty.diseaseIntervalFactor - 0.5f) < 0.0001f && Math.Abs(read("diseaseIntervalFactor", difficulty) - 2f) < 0.0001f, "the disease slider shows the reciprocal of the stored interval, as the panel does");
        write("fixedWealthTimeFactor", difficulty, 6f);
        Check(Math.Abs(difficulty.fixedWealthTimeFactor - 2f) < 0.0001f && read("fixedWealthTimeFactor", difficulty) == 6f, "fixed wealth time is shown in twelfths");
        difficulty.peacefulTemples = false;
        toggle("peacefulTemples", difficulty, false);
        Check(difficulty.peacefulTemples, "an inverted checkbox stores the opposite of what it shows");
        Difficulty copy = (Difficulty)settings.GetMethod("Copy", Static).Invoke(null, new object[] { difficulty });
        write("threatScale", copy, 3f);
        Check(Math.Abs(difficulty.threatScale - 1.23f) < 0.0001f && copy.threatScale == 3f, "a request is checked on a copy, so a refusal leaves the screen's values alone");
    }

    private static Dictionary<string, object> SetupCall(string method, string path, string body)
    {
        object response = Invoke("RiMCP.Command.SetupCommandService", method, Request(path, "POST", body), Route());
        return Json.Deserialize<Dictionary<string, object>>((string)response.GetType().GetField("Body").GetValue(response));
    }

    private static void CheckSetupOutsideSetup()
    {
        Dictionary<string, object> state = Data(Call("SetupCommandService", "GetSetup", "/v1/setup", ns: "RiMCP.Command."));
        Check(!(bool)state["active"] && (string)state["mode"] == "visible", "at the main menu nothing is set up, and setup is visible unless the player chose otherwise");
        int revision = (int)state["revision"];
        ExpectCommandError(400, () => SetupCall("SetupBack", "/v1/setup/back", "{}"));
        ExpectCommandError(409, () => SetupCall("SetupBack", "/v1/setup/back", "{\"expectedRevision\":" + (revision + 5) + "}"));
        ExpectCommandError(409, () => SetupCall("SetupBack", "/v1/setup/back", "{\"expectedRevision\":" + revision + "}"));
        ExpectCommandError(409, () => SetupCall("SetStoryteller", "/v1/setup/storyteller", "{\"storyteller\":\"Cassandra\",\"difficulty\":\"Rough\",\"commitmentMode\":false,\"expectedRevision\":" + revision + "}"));
        ExpectCommandError(409, () => SetupCall("SetIdeoligion", "/v1/setup/ideoligion", "{\"mode\":\"classic\",\"expectedRevision\":" + revision + "}"));
        Check((int)Data(Call("SetupCommandService", "GetSetup", "/v1/setup", ns: "RiMCP.Command."))["revision"] == revision, "refused commands change nothing");
    }

    private static void CheckIdeoligionRules()
    {
        Type editor = mod.GetType("RiMCP.Command.IdeoEditor", true);
        Func<string, int, bool> valid = (text, max) => (bool)editor.GetMethod("ValidSymbol", Static).Invoke(null, new object[] { text, max });
        Check(valid("Rimmcp Testgrove", 40) && valid("Tree-folk's Way", 40) && valid("Árbol 2", 40), "letters (of any script), digits, spaces, apostrophes and hyphens are allowed");
        Check(!valid("Bad/Name", 40) && !valid("Semi;colon", 40) && !valid(null, 40), "other characters are refused, as the dialogs' text fields refuse them");
        Check(valid(new string('a', 40), 40) && !valid(new string('a', 41), 40) && !valid(new string('a', 33), 32), "names are limited to the dialogs' lengths");
        Func<Type, string> section = type => (string)editor.GetMethod("SectionOf", Static).Invoke(null, new object[] { new PreceptDef { defName = "Regression" + type.Name, preceptClass = type } });
        Check(section(typeof(Precept)) == "precepts" && section(typeof(Precept_Ritual)) == "rituals" && section(typeof(Precept_RoleMulti)) == "roles"
            && section(typeof(Precept_Building)) == "buildings" && section(typeof(Precept_RitualSeat)) == "buildings" && section(typeof(Precept_Relic)) == "relics"
            && section(typeof(Precept_Weapon)) == "weapons" && section(typeof(Precept_Animal)) == "animals" && section(typeof(Precept_Apparel)) == "apparel",
            "each precept class sorts into the section whose Add button offers it");
        Check(section(typeof(Precept_ThingStyle)) == null, "precepts the screen does not list under a section are left out");
    }

    private static void CheckAnswerConfirmation()
    {
        Runner().GetMethod("Reset", Static).Invoke(null, null);
        Type runtime = mod.GetType("RiMCP.Bridge.BridgeRuntime", true);
        object settings = Activator.CreateInstance(mod.GetType("RiMCP.Settings.RiMCPSettings", true));
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            List<string> log = new List<string>();
            object operation = StartOperation("choose_starting_site", "gameSetup", null, null, _ => TwoSteps(log));
            string id = OperationId(operation);
            Func<string, bool, object> answer = (confirmation, accept) => Invoke("RiMCP.Command.SetupCommandService", "AnswerConfirmation",
                Request("/v1/operations/" + id + "/answer", "POST", "{\"confirmationId\":\"" + confirmation + "\",\"accept\":" + (accept ? "true" : "false") + "}"), Route("operationId", id));
            bool? answered = null;
            operation.GetType().GetField("ConfirmationId", Any).SetValue(operation, "confirm-1");
            operation.GetType().GetField("Answer", Any).SetValue(operation, (Func<bool, string>)(accept => { answered = accept; return null; }));

            ExpectCommandError(404, () => Invoke("RiMCP.Command.SetupCommandService", "AnswerConfirmation", Request("/v1/operations/op-none/answer", "POST", "{}"), Route("operationId", "op-none")));
            ExpectCommandError(409, () => answer("confirm-1", true));
            Check(answered == null, "in visible setup the player answers, so an agent's answer does nothing");

            settings.GetType().GetField("UnattendedSetup").SetValue(settings, true);
            runtime.GetMethod("Configure", Static).Invoke(null, new[] { settings });
            ExpectCommandError(409, () => answer("confirm-2", true));
            Check(answered == null, "an answer to another question does nothing");
            answer("confirm-1", false);
            Check(answered == false, "in unattended setup the answer reaches the question asked");
            Check(OperationState(operation) == "running", "an answered operation goes on");
            ExpectCommandError(409, () => answer("confirm-1", true));
            Check(answered == false, "a question is answered once, so a repeat cannot press the button again");
        }
        finally
        {
            runtime.GetMethod("Configure", Static).Invoke(null, new object[] { null });
            Runner().GetMethod("Reset", Static).Invoke(null, null);
        }
    }

    private static void CheckReplaceRefusal()
    {
        MethodInfo refusal = mod.GetType("RiMCP.Command.SessionCommandService", true).GetMethod("ReplaceRefusal", Static);
        Func<bool, bool, bool, string, string, string> why = (colony, commitment, discard, saveFirst, action) => (string)refusal.Invoke(null, new object[] { colony, commitment, discard, saveFirst, action });
        Check(why(false, false, false, null, "load") == null, "nothing to replace at the main menu");
        Check((why(true, false, false, null, "leave") ?? "").Contains("saveFirst"), "a loaded colony needs saveFirst or discardUnsaved, even with no detectable changes");
        Check(why(true, false, true, null, "leave") == null && why(true, false, false, "Backup", "load") == null, "either choice is enough");
        Check((why(true, false, true, "Backup", "load") ?? "").Contains("not both"), "both at once is refused");
        Check(why(true, true, false, null, "leave") == null, "commitment mode leaves, saving to its own slot");
        Check(why(true, true, true, null, "leave") != null && why(true, true, false, "Other", "leave") != null, "commitment mode takes neither discardUnsaved nor saveFirst");
        Check((why(true, true, false, null, "load") ?? "").Contains("commitment"), "commitment mode refuses loading another save, as in game");
    }

    private static void CheckSaveFileCheck()
    {
        MethodInfo check = mod.GetType("RiMCP.Command.SaveVerifier", true).GetMethod("CheckFile", Static);
        Func<string, DateTime?, string> verdict = (path, before) => (string)check.Invoke(null, new object[] { path, before });
        string dir = Path.Combine(Path.GetTempPath(), "rimmcp-save-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string good = Path.Combine(dir, "good.rws");
            string xml = "\uFEFF<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<savegame>\n\t<meta>\n\t\t<gameVersion>1.6.4871 rev591</gameVersion>\n\t</meta>\n\t<game>\n\t\t<info />\n\t</game>\n</savegame>";
            File.WriteAllText(good, xml, new System.Text.UTF8Encoding(false));
            Check(verdict(good, null) == null, "a complete save with its header passes: " + verdict(good, null));
            Check((verdict(good, DateTime.UtcNow.AddHours(1)) ?? "").Contains("not updated"), "a file older than the save attempt was not written by it");
            Check((verdict(Path.Combine(dir, "missing.rws"), null) ?? "").Contains("not written"), "a missing file was not written");

            string cut = Path.Combine(dir, "cut.rws");
            File.WriteAllText(cut, xml.Substring(0, xml.Length - 14), new System.Text.UTF8Encoding(false));
            Check((verdict(cut, null) ?? "").Contains("incomplete"), "a file cut short is incomplete: " + verdict(cut, null));

            string headless = Path.Combine(dir, "headless.rws");
            File.WriteAllText(headless, "<savegame><game /></savegame>");
            Check((verdict(headless, null) ?? "").Contains("meta header"), "a save without its meta header is refused");

            string packed = Path.Combine(dir, "packed.rws");
            File.WriteAllBytes(packed, new byte[] { 0x04, 0x22, 0x4D, 0x18, 0x60, 0x40, 0x82 });
            Check((verdict(packed, null) ?? "").Contains("not plain XML"), "a file in another format cannot be checked, and says so");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static void CheckSaveLogWatch()
    {
        // Built without its constructor, which attaches to Unity's log; OnLog is what Unity calls.
        Type watchType = mod.GetType("RiMCP.Command.SaveVerifier+LogWatch", true);
        object watch = FormatterServices.GetUninitializedObject(watchType);
        BindingFlags any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        watchType.GetField("thread", any).SetValue(watch, Thread.CurrentThread.ManagedThreadId);
        watchType.GetField("Marker").SetValue(watch, "RimMCP: saving \"Test\" (1)");
        MethodInfo onLog = watchType.GetMethod("OnLog", any);
        Action<string, UnityEngine.LogType> log = (message, type) => onLog.Invoke(watch, new object[] { message, "", type });

        log("unrelated message", UnityEngine.LogType.Log);
        Check(!Field<bool>(watch, "MarkerSeen"), "only the marker shows the log is working");
        log("RimMCP: saving \"Test\" (1)", UnityEngine.LogType.Log);
        Check(Field<bool>(watch, "MarkerSeen") && Field<int>(watch, "Errors") == 0, "the marker is seen and is not an error");
        log("a warning", UnityEngine.LogType.Warning);
        log("Exception while saving Map: boom", UnityEngine.LogType.Error);
        log("second", UnityEngine.LogType.Exception);
        Check(Field<int>(watch, "Errors") == 2 && Field<string>(watch, "FirstError") == "Exception while saving Map: boom", "errors and exceptions count, warnings do not");
        Task.Run(() => log("another thread", UnityEngine.LogType.Error)).Wait();
        Check(Field<int>(watch, "Errors") == 2, "errors from other threads are not the save's");
    }

    private static void CheckSaveHeader()
    {
        Type header = mod.GetType("RiMCP.Command.SaveHeader", true);
        MethodInfo read = header.GetMethod("Read", Static);
        string dir = Path.Combine(Path.GetTempPath(), "rimmcp-save-header-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "commit.rws");
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><savegame><meta><gameVersion>1.6.4871 rev591</gameVersion><modIds><li>ludeon.rimworld</li><li>Rakanssh.RimCP</li></modIds><modNames><li>Core</li><li>RimMCP</li></modNames></meta><game><currentMapIndex>0</currentMapIndex><info><permadeathMode>True</permadeathMode><permadeathModeUniqueName>Colony</permadeathModeUniqueName></info><world>" + new string('x', 200) + "</world></game></savegame>");
            object parsed = read.Invoke(null, new object[] { path });
            Check(Field<string>(parsed, "GameVersion") == "1.6.4871 rev591" && Field<bool>(parsed, "CommitmentMode") && Field<string>(parsed, "Problem") == null, "version and commitment mode come from the header");
            Check(((IList)header.GetField("ModIds").GetValue(parsed)).Cast<string>().SequenceEqual(new[] { "ludeon.rimworld", "Rakanssh.RimCP" }), "mod ids come from meta/modIds only, not modNames");
            MethodInfo match = header.GetMethod("ModsMatch");
            Check((bool)match.Invoke(parsed, new object[] { new List<string> { "Ludeon.RimWorld", "rakanssh.rimcp" }, new List<string> { "Core", "RimMCP" } }), "mods match in order, ignoring case");
            Check(!(bool)match.Invoke(parsed, new object[] { new List<string> { "rakanssh.rimcp", "ludeon.rimworld" }, new List<string>() }), "a different order does not match, as the game checks");

            string plain = Path.Combine(dir, "plain.rws");
            File.WriteAllText(plain, "<savegame><meta><gameVersion>1.6</gameVersion></meta><game><info><realPlayTimeInteracting>10</realPlayTimeInteracting></info></game></savegame>");
            Check(!Field<bool>(read.Invoke(null, new object[] { plain }), "CommitmentMode"), "without permadeathMode it is not commitment mode");
            string broken = Path.Combine(dir, "broken.rws");
            File.WriteAllText(broken, "not xml");
            Check(Field<string>(read.Invoke(null, new object[] { broken }), "Problem") != null, "an unreadable header reports a problem");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private static IEnumerable<string> LoadingUntil(object operation, Func<bool> released)
    {
        SetOperationField(operation, "InNativeStep", true);
        SetOperationField(operation, "ReplacingGame", true);
        while (!released())
        {
            yield return "loading";
        }
        SetOperationField(operation, "InNativeStep", false);
        SetOperationField(operation, "ReplacingGame", false);
        yield return "loaded";
        yield return "after";
    }

    private static void CheckBusyRouterAndCancel()
    {
        MethodInfo handle = mod.GetType("RiMCP.Bridge.BridgeRouter", true).GetMethod("Handle", Static);
        Func<string, string, object> send = (method, path) => handle.Invoke(null, new[] { Request(path, method, method == "GET" ? null : "{}") });
        Func<object, int> status = response => (int)response.GetType().GetField("StatusCode").GetValue(response);
        Func<object, string> body = response => (string)response.GetType().GetField("Body").GetValue(response);
        Runner().GetMethod("Reset", Static).Invoke(null, null);
        try
        {
            SetAccess("full", AllGroups());
            bool released = false;
            object loading = StartOperation("load_game", "gameSetup", null, null, operation => LoadingUntil(operation, () => released));
            string id = OperationId(loading);
            RunnerUpdate();

            object read = send("GET", "/v1/research");
            Check(status(read) == 503 && body(read).Contains("changing games") && body(read).Contains(id), "while the game is replaced, game reads wait: " + body(read));
            object progress = send("GET", "/v1/operations/" + id);
            Dictionary<string, object> data = Data(Json.Deserialize<Dictionary<string, object>>(body(progress)));
            Check(status(progress) == 200 && (string)data["state"] == "running" && (string)data["step"] == "loading", "operation status still answers: " + body(progress));
            Check(status(send("GET", "/v1/operations/op-none")) == 404, "an unknown operation is not found");

            // Stopping a Game setup operation needs Game setup, although the tool itself is Observe.
            // Turning Game setup off also revokes the load, which waits for its native step.
            SetAccess("readOnly", new[] { "observe", "diagnostics" });
            object refused = send("POST", "/v1/operations/" + id + "/cancel");
            Check(status(refused) == 403 && body(refused).Contains("Game setup"), "an Observe-only agent cannot stop a load: " + body(refused));
            SetAccess("full", AllGroups());
            RunnerUpdate();
            Check(OperationState(loading) == "running", "revocation waits for the native step");
            released = true;
            RunnerUpdate();
            RunnerUpdate();
            Check(OperationState(loading) == "revoked" && Field<string>(loading, "Step") == "loaded", "revoked at the step after the native one: " + OperationState(loading) + " at " + Field<string>(loading, "Step"));
            Check(status(send("GET", "/v1/research")) == 200, "game reads answer again");

            // Cancelling during a native step waits for it too, then stops before the next step.
            bool done = false;
            object second = StartOperation("load_game", "gameSetup", null, null, operation => LoadingUntil(operation, () => done));
            RunnerUpdate();
            object stopping = send("POST", "/v1/operations/" + OperationId(second) + "/cancel");
            Check(status(stopping) == 200 && body(stopping).Contains("cannot be interrupted"), "cancelling during the native step says it waits for it: " + body(stopping));
            RunnerUpdate();
            Check(OperationState(second) == "running", "the native step keeps running");
            done = true;
            RunnerUpdate();
            RunnerUpdate();
            Check(OperationState(second) == "cancelled" && Field<string>(second, "Step") == "loaded", "cancelled at the step after the native one: " + OperationState(second) + " at " + Field<string>(second, "Step"));

            // An Observe operation can be stopped with Observe alone.
            object search = StartOperation("find_starting_sites", "observe", null, null, _ => TwoSteps(new List<string>()));
            SetAccess("readOnly", new[] { "observe", "diagnostics" });
            Check(status(send("POST", "/v1/operations/" + OperationId(search) + "/cancel")) == 200 && OperationState(search) == "cancelled", "an Observe-only agent can stop its own search");
        }
        finally
        {
            SetAccess("full", AllGroups());
            Runner().GetMethod("Reset", Static).Invoke(null, null);
        }
    }

    private static void CheckAccessPresets()
    {
        Type presets = mod.GetType("RiMCP.Settings.AccessPresets", true);
        Func<string, string[]> groups = preset => (string[])presets.GetMethod("Groups").Invoke(null, new object[] { preset });
        Func<string, bool, string> migrate = (saved, legacy) => (string)presets.GetMethod("Migrate").Invoke(null, new object[] { saved, legacy });
        Check(groups("full").SequenceEqual(AllGroups()), "Full control must enable every group");
        Check(groups("control").SequenceEqual(AllGroups().Where(group => group != "gameSettings" && group != "gameSetup")), "Control must leave out game settings and game setup");
        Check(groups("administration").SequenceEqual(new[] { "observe", "diagnostics", "time", "administration" }), "Administration must include time control");
        Check(groups("readOnly").SequenceEqual(new[] { "observe", "diagnostics" }), "Read only must allow reads only");
        Check(migrate(null, true) == "full" && migrate(null, false) == "readOnly", "Allow changes on or off must become Full control or Read only");
        Check(migrate("administration", false) == "administration", "a saved preset must win over the old setting");
        Check(migrate("bogus", true) == "readOnly", "an unrecognised preset must fail closed");

        // Changing one group switches to Custom, unless the result is a named preset.
        Type settingsType = mod.GetType("RiMCP.Settings.RiMCPSettings", true);
        object settings = Activator.CreateInstance(settingsType);
        settingsType.GetField("AccessPreset").SetValue(settings, "full");
        MethodInfo setGroup = settingsType.GetMethod("SetGroup", BindingFlags.Instance | BindingFlags.NonPublic);
        setGroup.Invoke(settings, new object[] { "gameSettings", false });
        setGroup.Invoke(settings, new object[] { "gameSetup", false });
        Check((string)settingsType.GetField("AccessPreset").GetValue(settings) == "control", "turning off game settings and game setup from Full control is the Control preset");
        setGroup.Invoke(settings, new object[] { "orders", false });
        string[] enabled = (string[])settingsType.GetProperty("EnabledGroups", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(settings);
        Check((string)settingsType.GetField("AccessPreset").GetValue(settings) == "custom" && !enabled.Contains("orders") && enabled.Contains("time"), "a hand-picked set must be Custom: " + string.Join(", ", enabled));
    }

    private static void CheckAccessEnforcement()
    {
        MethodInfo handle = mod.GetType("RiMCP.Bridge.BridgeRouter", true).GetMethod("Handle", Static);
        Func<string, string, object> send = (method, path) => handle.Invoke(null, new[] { Request(path, method, method == "GET" ? null : "{\"speed\":\"Paused\"}") });
        Func<object, int> status = response => (int)response.GetType().GetField("StatusCode").GetValue(response);
        Func<object, string> body = response => (string)response.GetType().GetField("Body").GetValue(response);
        try
        {
            int revision = AccessRevision();
            SetAccess("full", AllGroups());
            Check(AccessRevision() == revision, "re-applying the same access must keep the revision");

            SetAccess("readOnly", new[] { "observe", "diagnostics" });
            Check(AccessRevision() == revision + 1, "changing access must change the revision");
            object speed = send("PUT", "/v1/time/speed");
            Check(status(speed) == 403 && body(speed).Contains("set_game_speed is disabled: the Time group is off") && body(speed).Contains("Read only"), "a disabled tool must say which setting enables it: " + body(speed));
            Check(status(send("GET", "/v1/research")) == 200, "enabled groups must keep working");

            SetAccess("custom", new string[0]);
            Check(status(send("GET", "/v1/research")) == 403, "reads are gated too");
            object tools = send("GET", "/v1/tools");
            Check(status(tools) == 200, "the access endpoint must stay reachable with every group off");
            Dictionary<string, object> manifest = Json.Deserialize<Dictionary<string, object>>(body(tools));
            Dictionary<string, object> access = (Dictionary<string, object>)manifest["access"];
            Check((string)access["preset"] == "custom" && Rows(access["groups"]).Count == 0 && access.ContainsKey("instance"), "the access summary must describe the settings");
            Check(Rows(manifest["tools"]).Cast<Dictionary<string, object>>().Where(tool => tool.ContainsKey("group")).All(tool => !(bool)tool["enabled"]), "every grouped tool must report itself disabled");

            // Disabling RimMCP refuses what is still waiting for the main thread, whatever its
            // group, and enabling it again restores the saved access.
            SetAccess("full", AllGroups());
            revision = AccessRevision();
            mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetMethod("Disable", Static).Invoke(null, null);
            Check(AccessRevision() == revision + 1, "disabling must change the revision");
            foreach (object waiting in new[] { send("PUT", "/v1/time/speed"), send("GET", "/v1/research"), send("GET", "/v1/tools") })
            {
                Check(status(waiting) == 403 && body(waiting).Contains("RimMCP is disabled"), "a request reaching the router after disabling must be refused: " + body(waiting));
            }
            SetAccess("full", AllGroups());
            Check(status(send("GET", "/v1/research")) == 200 && AccessRevision() == revision + 2, "enabling again must restore access with a new revision");
        }
        finally
        {
            SetAccess("full", AllGroups());
        }
    }

    private static void CheckToolAccess()
    {
        const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        Type settingsType = mod.GetType("RiMCP.Settings.RiMCPSettings", true);
        object settings = Activator.CreateInstance(settingsType);
        settingsType.GetMethod("EnsureAccess", Members).Invoke(settings, null);
        IEnumerable tools = (IEnumerable)mod.GetType("RiMCP.Shared.ToolManifest", true).GetProperty("McpTools", Static).GetValue(null);
        Func<string, object> spec = name => tools.Cast<object>().First(tool => Field<string>(tool, "Name") == name);
        Action<string> preset = name => settingsType.GetMethod("SetPreset", Members).Invoke(settings, new object[] { name });
        Action<string, bool> group = (name, on) => settingsType.GetMethod("SetGroup", Members).Invoke(settings, new object[] { name, on });
        Action<string, bool> tool = (name, on) => settingsType.GetMethod("SetTool", Members).Invoke(settings, new object[] { spec(name), on });
        Func<string, bool> isOn = name => (bool)settingsType.GetMethod("IsToolOn", Members).Invoke(settings, new object[] { spec(name) });
        Func<string> current = () => (string)settingsType.GetField("AccessPreset").GetValue(settings);
        Func<string, IList> list = field => (IList)settingsType.GetField(field).GetValue(settings);

        preset("control");
        tool("designate", false);
        Check(current() == "custom" && !isOn("designate") && isOn("place_blueprint") && list("ToolsOff").Contains("designate"), "turning one tool off leaves the rest of its group on, under Custom");
        tool("designate", true);
        Check(current() == "control" && list("ToolsOff").Count == 0, "turning it back on returns to the preset, with no exceptions left");
        tool("save_game", true);
        Check(current() == "custom" && isOn("save_game") && !isOn("load_game"), "a tool can be on while its group is off");
        group("gameSetup", true);
        Check(isOn("load_game") && list("ToolsOn").Count == 0, "turning its group on drops the group's exceptions");
        group("gameSettings", true);
        Check(current() == "full", "choices that amount to a preset are that preset");
        foreach (string name in new[] { "designate", "designate_animal", "place_blueprint" })
        {
            tool(name, false);
        }
        IEnumerable<string> groups = ((IEnumerable)settingsType.GetProperty("EnabledGroups", Members).GetValue(settings, null)).Cast<string>();
        Check(!groups.Contains("orders") && list("ToolsOff").Count == 0, "a group whose every tool is off is the group off, not a list of exceptions");
        preset("readOnly");
        Check(list("ToolsOn").Count == 0 && list("ToolsOff").Count == 0 && !isOn("designate"), "choosing a preset clears every exception");

        MethodInfo apply = mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetMethod("ApplyAccess", Static, null, new[] { typeof(string), typeof(IEnumerable<string>), typeof(IEnumerable<string>), typeof(IEnumerable<string>) }, null);
        try
        {
            apply.Invoke(null, new object[] { "custom", AllGroups(), new string[0], new[] { "designate" } });
            object access = mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetProperty("Access", Static).GetValue(null);
            object response = Invoke("RiMCP.Read.ToolReadService", "GetAccess", access);
            var data = Json.Deserialize<Dictionary<string, object>>((string)response.GetType().GetField("Body").GetValue(response));
            var on = ((ArrayList)data["tools"]).Cast<string>().ToList();
            Check(!on.Contains("designate") && on.Contains("place_blueprint") && data.ContainsKey("revision") && data.ContainsKey("instance"), "the access endpoint lists only the tools that are on, with its version");
            object refused = Invoke("RiMCP.Bridge.BridgeRouter", "Handle", Request("/v1/designations", "PUT", "{\"kind\":\"forbid\"}"));
            Check((int)refused.GetType().GetField("StatusCode").GetValue(refused) == 403 && ((string)refused.GetType().GetField("Body").GetValue(refused)).Contains("designate is turned off"), "the router refuses a tool turned off on its own, by name");
        }
        finally
        {
            SetAccess("full", AllGroups());
        }
    }

    private static void CheckTimeRevocation()
    {
        FieldInfo latest = mod.GetType("RiMCP.Command.AdvanceRunner", true).GetField("latest", Static);
        object run = AdvanceRun(null, Observation(null, 100, TimeSpeed.Superfast));
        latest.SetValue(null, run);
        try
        {
            SetAccess("administration", new[] { "observe", "diagnostics", "time", "administration" });
            Check(Field<string>(run, "Status") == "running", "keeping Time must leave the run alone");
            SetAccess("readOnly", new[] { "observe", "diagnostics" });
            Check(Field<string>(run, "Status") == "interrupted" && Field<string>(run, "EndReason").Contains("Time access was turned off"), "removing Time must end the run with the reason");
            Check((bool)run.GetType().GetProperty("PausesGameAtEnd").GetValue(run), "the run owned time, so revoking it must pause");
            Check(Field<System.Threading.ManualResetEventSlim>(run, "Ended").IsSet, "waiting requests must be released");
        }
        finally
        {
            latest.SetValue(null, null);
            SetAccess("full", AllGroups());
        }
    }

    private static void CheckCellRects()
    {
        // A 10x10 map is enough for bounds checks; nothing else on the map is touched.
        Map map = (Map)FormatterServices.GetUninitializedObject(typeof(Map));
        map.info = new MapInfo { Size = new IntVec3(10, 1, 10) };
        Type rectType = mod.GetType("RiMCP.Command.CellRectBody", true);
        Func<int, int, int, int, object> rect = (x, z, width, height) =>
        {
            object body = Activator.CreateInstance(rectType, true);
            rectType.GetProperty("X").SetValue(body, (int?)x);
            rectType.GetProperty("Z").SetValue(body, (int?)z);
            rectType.GetProperty("Width").SetValue(body, (int?)width);
            rectType.GetProperty("Height").SetValue(body, (int?)height);
            return body;
        };
        MethodInfo fromRects = mod.GetType("RiMCP.Command.CommandCells", true).GetMethod("FromRects", Static);
        Func<object[], object[]> cells = rects =>
        {
            Array typed = Array.CreateInstance(rectType, rects.Length);
            for (int i = 0; i < rects.Length; i++) typed.SetValue(rects[i], i);
            object[] args = { typed, map, "add", 0 };
            IList result = (IList)fromRects.Invoke(null, args);
            return new object[] { result.Count, (int)args[3] };
        };

        object[] clipped = cells(new[] { rect(8, 8, 4, 4) });
        Check((int)clipped[0] == 4 && (int)clipped[1] == 12, "cells off the map must be counted, not returned: " + clipped[0] + " in, " + clipped[1] + " out");
        object[] overlapping = cells(new[] { rect(0, 0, 3, 3), rect(1, 1, 3, 3) });
        Check((int)overlapping[0] == 14, "overlapping rectangles must not repeat cells");
        ExpectCommandError(400, () => cells(new[] { rect(0, 0, 61, 1) }));
        ExpectCommandError(400, () => cells(Enumerable.Range(0, 2).Select(i => rect(0, 0, 60, 31)).ToArray()));
    }

    private static object Tool(string name)
    {
        return ((IEnumerable)mod.GetType("RiMCP.Shared.ToolManifest", true).GetProperty("McpTools", Static).GetValue(null)).Cast<object>().Single(tool => Field<string>(tool, "Name") == name);
    }

    private static Dictionary<string, object> ToolProperties(object spec)
    {
        return (Dictionary<string, object>)Field<Dictionary<string, object>>(spec, "InputSchema")["properties"];
    }

    // A tool's enum of kinds must match the table its handler dispatches on.
    private static void CheckOfferedKinds(string tool, string property, string handler, string table)
    {
        string[] offered = (string[])((Dictionary<string, object>)ToolProperties(Tool(tool))[property])["enum"];
        string[] handled = ((Array)mod.GetType(handler, true).GetField(table, Static).GetValue(null)).Cast<object>().Select(kind => Field<string>(kind, "Name")).ToArray();
        Check(offered.OrderBy(name => name, StringComparer.Ordinal).SequenceEqual(handled.OrderBy(name => name, StringComparer.Ordinal)), tool + " offers " + string.Join(", ", offered) + " but handles " + string.Join(", ", handled));
    }

    // A retry that finds a receipt must not act again: a player may have changed what the command
    // would now do, so acting again could undo the player. Every outcome is kept for that reason.
    private static void CheckReceipts()
    {
        Type resultType = mod.GetType("RiMCP.Command.CommandResult", true);
        MethodInfo once = mod.GetType("RiMCP.Command.CommandReceipts", true).GetMethod("Once", Static);
        MethodInfo keyFor = mod.GetType("RiMCP.Command.RequestKey", true).GetMethod("For", Static);
        object context = FormatterServices.GetUninitializedObject(mod.GetType("RiMCP.Command.CommandContext", true));
        Map first = (Map)FormatterServices.GetUninitializedObject(typeof(Map));
        Map second = (Map)FormatterServices.GetUninitializedObject(typeof(Map));
        first.uniqueID = 1;
        second.uniqueID = 2;
        Func<string, string, Map, object[], object> key = (tool, id, map, arguments) => keyFor.Invoke(null, new object[] { tool, id, map, arguments });
        int runs = 0;
        // Runs a command through Once that would answer with outcome now, or fail when it is null.
        Func<object, string, Dictionary<string, object>> run = (requestKey, outcome) =>
        {
            Func<object> command = () =>
            {
                runs++;
                if (outcome == null)
                {
                    throw (Exception)Activator.CreateInstance(mod.GetType("RiMCP.Command.CommandException", true), 409, "Refused.");
                }
                return Activator.CreateInstance(resultType, outcome == "jobIssued", new Dictionary<string, object> { { "outcome", outcome } });
            };
            Delegate typed = Expression.Lambda(typeof(Func<>).MakeGenericType(resultType), Expression.Convert(Expression.Invoke(Expression.Constant(command)), resultType)).Compile();
            object response = once.Invoke(null, new[] { context, requestKey, typed });
            return Json.Deserialize<Dictionary<string, object>>((string)response.GetType().GetField("Body").GetValue(response));
        };
        Func<Dictionary<string, object>, string> outcomeOf = envelope => (string)Data(envelope)["outcome"];

        // The pawn was already moving there; then the player sent it elsewhere, so the order would
        // now issue a job. The retry, its arguments normalised, must answer as before instead.
        run(key("order_pawn", "move-1", first, new object[] { "Human1", "move", 10, " Crops " }), "alreadySatisfied");
        Dictionary<string, object> retry = run(key("order_pawn", "move-1", first, new object[] { "Human1", "move", 10, "Crops" }), "jobIssued");
        Check(runs == 1 && outcomeOf(retry) == "alreadySatisfied" && !(bool)retry["changed"], "a retry must return the first outcome without acting again");
        run(key("order_pawn", "attack-1", first, new object[] { "Human1", "attack" }), "rejected");
        Check(outcomeOf(run(key("order_pawn", "attack-1", first, new object[] { "Human1", "attack" }), "jobIssued")) == "rejected" && runs == 2, "a rejected outcome is kept too");
        Check(outcomeOf(run(key("place_blueprint", "move-1", first, new object[0]), "applied")) == "applied" && runs == 3, "receipts are per tool");

        // A request that failed changed nothing and keeps nothing, so trying it again runs it.
        ExpectCommandError(409, () => run(key("order_pawn", "tend-1", first, new object[] { "Human1", "tend" }), null));
        Check(outcomeOf(run(key("order_pawn", "tend-1", first, new object[] { "Human1", "tend" }), "jobIssued")) == "jobIssued" && runs == 5, "a failed request must not be kept");
        Check(key("order_pawn", null, first, new object[0]) == null, "no requestId means no key");
        run(null, "jobIssued");
        run(null, "jobIssued");
        Check(runs == 7, "without a requestId the command runs every time");

        int kept = (int)mod.GetType("RiMCP.Command.CommandReceipts", true).GetField("MaxReceipts", Static).GetValue(null);
        string described = (string)((Dictionary<string, object>)ToolProperties(Tool("order_pawn"))["requestId"])["description"];
        Check(described.Contains("last " + kept + " calls"), "the requestId description must say how many results are kept: " + described);

        ExpectCommandError(409, () => run(key("order_pawn", "move-1", first, new object[] { "Human1", "move", 11, "Crops" }), "jobIssued"));
        ExpectCommandError(409, () => run(key("order_pawn", "move-1", second, new object[] { "Human1", "move", 10, "Crops" }), "jobIssued"));
        Check(runs == 7, "a reused requestId is refused before the command runs");
    }

    private static void CheckStorageLimits()
    {
        ThingCategoryDef root = Category("RegressionLimitRoot");
        ThingCategoryDef corpses = Category("RegressionCorpses"), rocks = Category("RegressionRocks");
        root.childCategories.AddRange(new[] { corpses, rocks });
        ThingDef corpse = PlainDef("RegressionCorpse"), rock = PlainDef("RegressionRock");
        corpse.thingCategories = new List<ThingCategoryDef> { corpses };
        rock.thingCategories = new List<ThingCategoryDef> { rocks };
        corpses.childThingDefs.Add(corpse);
        rocks.childThingDefs.Add(rock);
        Func<string, ThingCategoryDef, Type, SpecialThingFilterDef> special = (name, parent, worker) =>
        {
            SpecialThingFilterDef def = (SpecialThingFilterDef)FormatterServices.GetUninitializedObject(typeof(SpecialThingFilterDef));
            def.defName = name;
            def.parentCategory = parent;
            def.configurable = true;
            def.workerClass = worker;
            DefDatabase<SpecialThingFilterDef>.Add(def);
            return def;
        };
        // Large corpses are some corpses; every rock is a cracked rock.
        SpecialThingFilterDef large = special("RegressionLargeCorpses", corpses, typeof(RegressionMatchAll));
        SpecialThingFilterDef cracked = special("RegressionCrackedRocks", rocks, typeof(RegressionAlwaysMatches));
        SpecialThingFilterDef unheld = special("RegressionUnheld", Category("RegressionElsewhere"), typeof(RegressionMatchAll));

        // A shelf-like building: it holds both, refuses large corpses and the unheld filter, and
        // only takes Normal quality or better. Its settings allow both but turn cracked rocks off.
        ThingFilter limit = new ThingFilter();
        limit.SetAllow(corpse, true);
        limit.SetAllow(rock, true);
        limit.SetAllow(large, false);
        limit.SetAllow(unheld, false);
        limit.AllowedQualityLevels = new QualityRange(QualityCategory.Normal, QualityCategory.Legendary);
        ThingFilter filter = new ThingFilter();
        filter.SetAllow(corpse, true);
        filter.SetAllow(rock, true);
        filter.SetAllow(cracked, false);
        filter.AllowedHitPointsPercents = new FloatRange(0.5f, 1f);
        filter.AllowedQualityLevels = new QualityRange(QualityCategory.Awful, QualityCategory.Excellent);

        MethodInfo accepts = mod.GetType("RiMCP.Read.StorageSummary", true).GetMethod("Accepts", Static, null, new[] { typeof(ThingFilter), typeof(ThingFilter), typeof(ThingCategoryDef) }, null);
        var dto = (Dictionary<string, object>)accepts.Invoke(null, new object[] { filter, limit, root });
        Check(!dto.ContainsKey("all") && ((string[])dto["some"]).SequenceEqual(new[] { "RegressionCorpses" }) && ((string[])dto["none"]).SequenceEqual(new[] { "RegressionRocks" }),
            "a special filter that is off makes its category partly accepted, or not at all when it refuses every kind in it");
        Check(dto["fixedSpecialFiltersOff"] is string[] fixedOff && fixedOff.SequenceEqual(new[] { "RegressionLargeCorpses" }),
            "the building's refusal must be named, but not one that cannot apply to anything it holds");
        Check(dto["specialFiltersOff"] is string[] ownOff && ownOff.SequenceEqual(new[] { "RegressionCrackedRocks" }), "the storage's own refusal must be named");
        var hitPoints = (Dictionary<string, object>)dto["hitPoints"];
        Check(Convert.ToDouble(hitPoints["min"]) == 0.5 && Convert.ToDouble(hitPoints["max"]) == 1.0, "a narrowed hit point range must be shown");
        var quality = (Dictionary<string, object>)dto["quality"];
        Check((string)quality["min"] == "Normal" && (string)quality["max"] == "Excellent", "quality is where the settings and the building overlap");
    }

    private static void CheckStorageAccepts()
    {
        Func<string, ThingDef> def = name =>
        {
            ThingDef thing = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
            thing.defName = name;
            thing.virtualDefs = new List<ThingDef>();
            return thing;
        };
        ThingDef a1 = def("RegressionA1"), a2 = def("RegressionA2"), b1 = def("RegressionB1"), b2 = def("RegressionB2"), c1 = def("RegressionC1"), d1 = def("RegressionD1");
        ThingCategoryDef a = Category("RegressionA", a1);
        // A subcategory counts toward its top-level category, and a def listed twice counts once.
        a.childCategories.Add(Category("RegressionASub", a2, a1));
        ThingCategoryDef root = Category("RegressionRoot");
        root.childCategories.AddRange(new[] { a, Category("RegressionB", b1, b2), Category("RegressionC", c1), Category("RegressionD", d1) });
        // The storage can hold everything but C; it allows all of A, half of B, none of D.
        ThingFilter limit = new ThingFilter();
        foreach (ThingDef holdable in new[] { a1, a2, b1, b2, d1 }) limit.SetAllow(holdable, true);
        ThingFilter filter = new ThingFilter();
        foreach (ThingDef allowed in new[] { a1, a2, b1, c1 }) filter.SetAllow(allowed, true);

        MethodInfo accepts = mod.GetType("RiMCP.Read.StorageSummary", true).GetMethod("Accepts", Static, null, new[] { typeof(ThingFilter), typeof(ThingFilter), typeof(ThingCategoryDef) }, null);
        Func<ThingFilter, string> summary = bound =>
        {
            var dto = (Dictionary<string, object>)accepts.Invoke(null, new object[] { filter, bound, root });
            return string.Join(" ", new[] { "all", "some", "none" }.Where(dto.ContainsKey).Select(key => key + "=" + string.Join(",", (string[])dto[key])).ToArray());
        };
        string limited = summary(limit);
        Check(limited == "all=RegressionA some=RegressionB none=RegressionD", "categories the storage cannot hold must be left out: " + limited);
        string unlimited = summary(null);
        Check(unlimited == "all=RegressionA,RegressionC some=RegressionB none=RegressionD", "without a limit every category counts: " + unlimited);
    }

    // Constructing a category def loads its icon, which needs the game's content, so tests build
    // categories without running constructors.
    private static ThingCategoryDef Category(string name, params ThingDef[] defs)
    {
        ThingCategoryDef category = (ThingCategoryDef)FormatterServices.GetUninitializedObject(typeof(ThingCategoryDef));
        category.defName = name;
        category.childThingDefs = defs.ToList();
        category.childCategories = new List<ThingCategoryDef>();
        return category;
    }

    private static void CheckFoodTrend()
    {
        Type sampleType = mod.GetType("RiMCP.Read.FoodSample", true);
        Func<int, float, float, object> sample = (tick, meals, raw) =>
        {
            object made = Activator.CreateInstance(sampleType, true);
            sampleType.GetField("Tick").SetValue(made, tick);
            sampleType.GetField("Meals").SetValue(made, meals);
            sampleType.GetField("Raw").SetValue(made, raw);
            return made;
        };
        MethodInfo trend = mod.GetType("RiMCP.Read.FoodHistory", true).GetMethods(Static).Single(method => method.Name == "Trend" && method.GetParameters()[0].ParameterType != typeof(Map));
        Func<object[], object, Dictionary<string, object>> run = (history, now) =>
        {
            IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(sampleType));
            foreach (object item in history) list.Add(item);
            return (Dictionary<string, object>)trend.Invoke(null, new[] { list, now });
        };
        int hour = GenDate.TicksPerHour, day = GenDate.TicksPerDay;

        Check(run(new[] { sample(0, 10, 10) }, sample(5 * hour, 9, 10)) == null, "five hours of history is too little for a trend");
        // Samples older than three days are ignored: the trend runs from day 1 to day 4.
        var lastThreeDays = run(new[] { sample(0, 100, 100), sample(day, 30, 60), sample(2 * day, 25, 50) }, sample(4 * day, 15, 30));
        Check(Convert.ToInt32(lastThreeDays["overHours"]) == 72, "the trend must start at the oldest sample within three days");
        Check(Convert.ToDouble(lastThreeDays["mealsPerDay"]) == -5.0 && Convert.ToDouble(lastThreeDays["rawPerDay"]) == -10.0 && Convert.ToDouble(lastThreeDays["totalPerDay"]) == -15.0,
            "per-day change: meals " + lastThreeDays["mealsPerDay"] + ", raw " + lastThreeDays["rawPerDay"]);
    }

    private static void CheckBillStates()
    {
        FieldInfo[] modes = { typeof(BillRepeatModeDefOf).GetField("Forever"), typeof(BillRepeatModeDefOf).GetField("RepeatCount"), typeof(BillRepeatModeDefOf).GetField("TargetCount") };
        object[] saved = modes.Select(mode => mode.GetValue(null)).ToArray();
        try
        {
            foreach (FieldInfo mode in modes) mode.SetValue(null, FormatterServices.GetUninitializedObject(typeof(BillRepeatModeDef)));
            MethodInfo stateOf = mod.GetType("RiMCP.Read.BillStatus", true).GetMethod("StateOf", Static);
            Func<Bill_Production, string> state = bill =>
            {
                object[] args = { bill, null };
                return (string)stateOf.Invoke(null, args);
            };
            Bill_Production production = (Bill_Production)FormatterServices.GetUninitializedObject(typeof(Bill_Production));
            production.repeatMode = BillRepeatModeDefOf.RepeatCount;
            production.repeatCount = 2;
            Check(state(production) == "active", "a bill with repeats left is active");
            production.repeatCount = 0;
            Check(state(production) == "done", "a bill with no repeats left is done, not blocked");
            production.repeatMode = BillRepeatModeDefOf.Forever;
            production.suspended = true;
            Check(state(production) == "suspended", "suspension comes first");
            production.suspended = false;
            Check(state(production) == "active" && !production.paused, "a forever bill is active, and reading it changes nothing");
        }
        finally
        {
            for (int i = 0; i < modes.Length; i++) modes[i].SetValue(null, saved[i]);
        }
    }

    private static ThingDef PlainDef(string name)
    {
        ThingDef def = (ThingDef)FormatterServices.GetUninitializedObject(typeof(ThingDef));
        def.defName = name;
        def.virtualDefs = new List<ThingDef>();
        return def;
    }

    private static void CheckStorageEdits()
    {
        SpecialThingFilterDef rotten = (SpecialThingFilterDef)FormatterServices.GetUninitializedObject(typeof(SpecialThingFilterDef));
        rotten.defName = "RegressionAllowRotten";
        rotten.configurable = true;
        DefDatabase<SpecialThingFilterDef>.Add(rotten);
        ThingDef steel = PlainDef("RegressionSteel"), wood = PlainDef("RegressionWood");
        ThingFilter filter = new ThingFilter();
        filter.SetAllow(steel, true);
        filter.SetAllow(rotten, false);
        MethodInfo change = mod.GetType("RiMCP.Command.ZoneCommandService", true).GetMethod("ChangeItemsOnly", Static);
        Func<Action<ThingFilter>, bool> edit = action => (bool)change.Invoke(null, new object[] { filter, action });

        Check(edit(f => f.SetDisallowAll()) && !filter.Allows(steel), "disallowing everything changes the accepted items");
        Check(!filter.Allows(rotten), "the player's special filter must survive disallowAll, which the game's setter resets");
        Check(!edit(f => f.SetDisallowAll()), "nothing changed must report nothing changed");
        filter.SetAllow(steel, true);
        Check(edit(f => { f.SetAllow(steel, false); f.SetAllow(wood, true); }), "a different set of items with the same count is a change");
        Check(!filter.Allows(rotten), "special filters stay as they were after any edit");
    }

    private static void CheckSpecialFilterEntries()
    {
        ThingCategoryDef weapons = Category("RegressionEntryWeapons");
        DefDatabase<ThingCategoryDef>.Add(weapons);
        ThingDef knife = PlainDef("RegressionEntryKnife");
        knife.thingCategories = new List<ThingCategoryDef> { weapons };
        weapons.childThingDefs.Add(knife);
        DefDatabase<ThingDef>.Add(knife);
        Func<string, bool, SpecialThingFilterDef> special = (name, configurable) =>
        {
            SpecialThingFilterDef def = (SpecialThingFilterDef)FormatterServices.GetUninitializedObject(typeof(SpecialThingFilterDef));
            def.defName = name;
            def.parentCategory = weapons;
            def.configurable = configurable;
            def.workerClass = typeof(RegressionMatchAll);
            DefDatabase<SpecialThingFilterDef>.Add(def);
            return def;
        };
        SpecialThingFilterDef smeltable = special("RegressionEntrySmeltable", true);
        SpecialThingFilterDef biocoded = special("RegressionEntryBiocoded", true);
        SpecialThingFilterDef fixedByGame = special("RegressionEntryGameOnly", false);
        // The player unticked the category, which also turned its special filters off; the
        // building refuses biocoded weapons whatever its settings.
        ThingFilter filter = new ThingFilter();
        filter.SetAllow(smeltable, false);
        filter.SetAllow(biocoded, false);
        ThingFilter limit = new ThingFilter();
        limit.SetAllow(knife, true);
        limit.SetAllow(biocoded, false);

        MethodInfo apply = mod.GetType("RiMCP.Command.ZoneCommandService", true).GetMethod("ApplyEntry", Static);
        List<object> entries = new List<object>();
        Func<string, bool, Dictionary<string, object>> entry = (name, allow) =>
        {
            apply.Invoke(null, new object[] { filter, limit, name, allow, entries });
            return (Dictionary<string, object>)entries.Last();
        };
        Func<Dictionary<string, object>, string, string> names = (dto, key) => dto.ContainsKey(key) ? string.Join(",", (string[])dto[key]) : "";

        Dictionary<string, object> knifeEntry = entry("RegressionEntryKnife", true);
        Check((string)knifeEntry["outcome"] == "applied" && filter.Allows(knife), "allowing a thing applies");
        Check(!filter.Allows(smeltable) && names(knifeEntry, "specialFiltersOff") == "RegressionEntrySmeltable"
            && names(knifeEntry, "fixedSpecialFiltersOff") == "RegressionEntryBiocoded",
            "special filters stay off, and the entry says which still refuse it: those it can allow by name, and the building's");
        Dictionary<string, object> smeltableEntry = entry("RegressionEntrySmeltable", true);
        Check((string)smeltableEntry["kind"] == "specialFilter" && (string)smeltableEntry["outcome"] == "applied" && filter.Allows(smeltable), "a named special filter changes");
        Check((string)entry("RegressionEntryBiocoded", true)["outcome"] == "rejected" && !filter.Allows(biocoded), "a special filter the building refuses cannot be allowed");
        Check((string)entry("RegressionEntryGameOnly", false)["outcome"] == "rejected" && filter.Allows(fixedByGame), "a special filter the game sets cannot be changed");
        Dictionary<string, object> again = entry("RegressionEntryKnife", true);
        Check((string)again["outcome"] == "alreadySatisfied" && names(again, "specialFiltersOff") == "" && names(again, "fixedSpecialFiltersOff") == "RegressionEntryBiocoded",
            "only what still refuses it is listed");
        Check((string)entry("RegressionEntrySmeltable", true)["outcome"] == "alreadySatisfied", "an allowed special filter is already satisfied");
    }

    private static void CheckIngredientKinds()
    {
        ThingDef steel = PlainDef("RegressionKindSteel"), wood = PlainDef("RegressionKindWood");
        ThingFilter either = new ThingFilter();
        either.SetAllow(steel, true);
        either.SetAllow(wood, true);
        typeof(ThingFilter).GetField("customSummary", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(either, "steel or wood");
        IngredientCount ten = new IngredientCount { filter = either };
        ten.SetBaseCount(10);
        RecipeDef recipe = new RecipeDef { defName = "RegressionKinds", ingredients = new List<IngredientCount> { ten } };
        ThingFilter allowed = new ThingFilter();
        allowed.SetAllow(steel, true);
        allowed.SetAllow(wood, true);
        Bill_Production bill = (Bill_Production)FormatterServices.GetUninitializedObject(typeof(Bill_Production));
        bill.recipe = recipe;
        bill.ingredientFilter = allowed;
        Func<ThingDef, int, Thing> stack = (def, count) =>
        {
            Thing thing = (Thing)FormatterServices.GetUninitializedObject(typeof(Thing));
            thing.def = def;
            thing.stackCount = count;
            return thing;
        };
        List<Thing> usable = new List<Thing> { stack(steel, 6), stack(wood, 6) };
        Type status = mod.GetType("RiMCP.Read.BillStatus", true);
        Func<string, object[], object> need = (method, args) => ((IList)status.GetMethod(method, Static).Invoke(null, args))[0];

        object single = need("SingleKind", new object[] { bill, usable });
        Check(!Field<bool>(single, "Enough") && Field<float>(single, "Available") == 6f, "six of each kind is not ten of one kind");
        object mixed = need("Mixed", new object[] { bill, usable, true });
        Check(Field<bool>(mixed, "Enough") && Field<float>(mixed, "Available") == 12f, "mixing kinds adds them up");
        usable.Add(stack(steel, 5));
        Check(Field<bool>(need("SingleKind", new object[] { bill, usable }), "Enough"), "two stacks of one kind together are enough");
    }

    private static void CheckMapLayers()
    {
        Type service = mod.GetType("RiMCP.Read.MapViewReadService", true);
        object roof = ((Array)service.GetField("Layers", Static).GetValue(null)).GetValue(0);
        MethodInfo draw = service.GetNestedType("Grid", BindingFlags.NonPublic).GetMethod("Draw", Static);
        MethodInfo layerDto = service.GetMethod("LayerDto", Static);
        Func<Func<IntVec3, char>, Dictionary<string, object>> layer = symbol => (Dictionary<string, object>)layerDto.Invoke(null, new[] { roof, draw.Invoke(null, new object[] { 10, 20, 4, 3, symbol }) });

        Dictionary<string, object> open = layer(cell => cell.x == 10 ? '?' : '-');
        Check(open.Count == 1 && (int)open["markedCells"] == 0, "a layer that marks nothing must leave out its rows and legend");

        Dictionary<string, object> roofed = layer(cell => cell.x == 10 ? 'R' : cell.z == 22 ? '?' : '-');
        string[] rows = (string[])roofed["rows"];
        Check((int)roofed["markedCells"] == 3 && rows.Length == 3 && rows[0] == "R???" && rows[2] == "R---", "rows run north to south: " + string.Join("|", rows));
        string[] symbols = ((Dictionary<string, object>)roofed["legend"]).Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
        Check(symbols.SequenceEqual(new[] { "-", "?", "R" }), "the legend lists only the symbols shown: " + string.Join(",", symbols));
    }

    private static void ExpectCommandError(int status, Action action)
    {
        ExpectError("RiMCP.Command.CommandException", status, action);
    }

    private static void ExpectBridgeError(int status, Action action)
    {
        ExpectError("RiMCP.Bridge.BridgeException", status, action);
    }

    private static void ExpectError(string type, int status, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Check(e.GetType().FullName == type, "expected a controlled " + type + ", got " + e.GetType().Name + ": " + e.Message);
            Check((int)e.GetType().GetField("StatusCode").GetValue(e) == status, "expected status " + status + ", got " + e.GetType().GetField("StatusCode").GetValue(e));
            return;
        }
        throw new Exception("expected status " + status + " but the call succeeded");
    }

    private static object Detail(string name) => Enum.Parse(mod.GetType("RiMCP.Read.ReadDetail", true), name);
    private static object Request(string path, string method = "GET", string body = null) =>
        Activator.CreateInstance(mod.GetType("RiMCP.Bridge.BridgeRequest", true), new object[] { method, new Uri("http://127.0.0.1" + path), body });
    private static object Route(string key = null, string value = null)
    {
        var values = new Dictionary<string, string>();
        if (key != null) values[key] = value;
        return Activator.CreateInstance(mod.GetType("RiMCP.Bridge.RouteMatch", true), new object[] { values });
    }
    private static Dictionary<string, object> Call(string service, string method, string path, string key = null, string value = null, string ns = "RiMCP.Read.")
    {
        object response = Invoke(ns + service, method, Request(path), Route(key, value));
        Check((int)response.GetType().GetField("StatusCode").GetValue(response) == 200, "endpoint did not return status 200");
        return Json.Deserialize<Dictionary<string, object>>((string)response.GetType().GetField("Body").GetValue(response));
    }
    private static object Invoke(string type, string method, params object[] args)
    {
        try { return mod.GetType(type, true).GetMethod(method, Static).Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException ?? e; }
    }
    private static Dictionary<string, object> Data(Dictionary<string, object> envelope) => (Dictionary<string, object>)envelope["data"];
    private static ArrayList Rows(object value) => (ArrayList)value;
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
    private static void Test(string name, Action test)
    {
        try
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }
        catch (Exception e)
        {
            failed++;
            Console.WriteLine("FAIL " + name + ": " + e.GetType().Name + ": " + e.Message);
        }
    }
}

// A special filter that matches every thing, for storage tests.
public sealed class RegressionMatchAll : SpecialThingFilterWorker
{
    public override bool Matches(Thing t)
    {
        return true;
    }
}

// A special filter that refuses every thing of every kind it covers.
public sealed class RegressionAlwaysMatches : SpecialThingFilterWorker
{
    public override bool Matches(Thing t)
    {
        return true;
    }

    public override bool AlwaysMatches(ThingDef def)
    {
        return true;
    }
}

// A modded-style def subclass: its defs are stored in DefDatabase<WorkTypeDef>.
internal sealed class ProbeWorkTypeDef : WorkTypeDef
{
}
