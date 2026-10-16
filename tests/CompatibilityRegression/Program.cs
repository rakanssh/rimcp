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
            var expected = new Dictionary<string, int> { { "observe", 28 }, { "diagnostics", 4 }, { "time", 2 }, { "administration", 10 }, { "orders", 3 }, { "pawnControl", 2 }, { "gameSettings", 1 } };
            int count;
            Check(counts.Count == expected.Count && expected.All(pair => counts.TryGetValue(pair.Key, out count) && count == pair.Value),
                "group sizes: " + string.Join(", ", counts.Select(pair => pair.Key + "=" + pair.Value).ToArray()));
        });
        Test("presets choose groups, and settings saved before presets migrate", CheckAccessPresets);
        Test("disabled groups are refused, and the access endpoint stays reachable", CheckAccessEnforcement);
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
        mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetMethod("ApplyAccess", Static).Invoke(null, new object[] { preset, groups });
    }

    private static int AccessRevision()
    {
        object access = mod.GetType("RiMCP.Bridge.BridgeRuntime", true).GetProperty("Access", Static).GetValue(null);
        return Field<int>(access, "Revision");
    }

    private static void CheckAccessPresets()
    {
        Type presets = mod.GetType("RiMCP.Settings.AccessPresets", true);
        Func<string, string[]> groups = preset => (string[])presets.GetMethod("Groups").Invoke(null, new object[] { preset });
        Func<string, bool, string> migrate = (saved, legacy) => (string)presets.GetMethod("Migrate").Invoke(null, new object[] { saved, legacy });
        Check(groups("full").SequenceEqual(AllGroups()), "Full control must enable every group");
        Check(groups("control").SequenceEqual(AllGroups().Where(group => group != "gameSettings")), "Control must leave out only game settings");
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
        Check((string)settingsType.GetField("AccessPreset").GetValue(settings) == "control", "turning off game settings from Full control is the Control preset");
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
