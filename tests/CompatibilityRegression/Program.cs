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
        Test("advance retries are recognised across recent runs and need matching arguments", CheckAdvanceReceipts);
        Test("get_advance answers for the requested run, and a deferred reply stays bound to it", CheckAdvanceWaitBinding);
        Test("a waiting request returns when its run ends, with that run's result", CheckDeferredDispatch);
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
            var row = (Dictionary<string, object>)Invoke("RiMCP.Read.ProductionReadService", "SerializeBill", record, Detail(detail));
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

    private static void ExpectCommandError(int status, Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            if (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            Check(e.GetType().FullName == "RiMCP.Command.CommandException", "expected a controlled command error, got " + e.GetType().Name + ": " + e.Message);
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
    private static Dictionary<string, object> Call(string service, string method, string path, string key = null, string value = null)
    {
        object response = Invoke("RiMCP.Read." + service, method, Request(path), Route(key, value));
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

// A modded-style def subclass: its defs are stored in DefDatabase<WorkTypeDef>.
internal sealed class ProbeWorkTypeDef : WorkTypeDef
{
}
