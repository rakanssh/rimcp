using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Command
{
    // Sets up a new game through the game's own screens, visibly, for scenarios whose only extra
    // screen is the colonist screen; other screens are handed to the player. Commands that change
    // the setup take the revision from get_setup, and nothing acts on a setup someone else has
    // changed since. This file has what every screen shares: starting, going back, cancelling,
    // answering the game's questions, and reading the setup. Each screen's commands are in their own
    // file (Storyteller, World, Ideoligion, Pawns, Start).
    internal static partial class SetupCommandService
    {
        public static BridgeResponse ListScenarios(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            List<Scenario> scenarios = ScenarioLister.AllScenarios().Where(scenario => scenario.showInUI).ToList();
            Page<Scenario> page = new Page<Scenario>(scenarios, context.Request);
            object[] rows = page.Items.Select(scenario =>
            {
                string[] manual = UnsupportedScreens(scenario);
                return (object)Dto.Obj(
                    Dto.Field("name", scenario.name),
                    Dto.Field("category", scenario.Category.ToString()),
                    Dto.Field("summary", scenario.summary),
                    Dto.Field("supported", manual.Length == 0),
                    Dto.Field("manualScreens", manual.Length == 0 ? null : manual));
            }).ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(Dto.Field("scenarios", rows)), page.Truncated, page.NextCursor);
        }

        public static BridgeResponse GetSetup(BridgeRequest request, RouteMatch route)
        {
            SetupController.Observe();
            return SessionCommandService.Envelope(false, State(ReadContext.From(request).Request.Wants("preceptOptions")));
        }

        public static BridgeResponse StartSetup(BridgeRequest request, RouteMatch route)
        {
            StartSetupBody body = CommandUtil.ReadBody<StartSetupBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            bool discard = body.DiscardUnsaved == true;
            bool overwrite = body.Overwrite == true;
            return SessionCommandService.Begin(request, "start_setup", requestId, () =>
            {
                Scenario scenario = FindScenario(body.Scenario, body.Category);
                SessionCommandService.RequireSettled();
                if (SetupController.OpenPage != null)
                {
                    throw new CommandException(409, "A new game is already being set up (at the " + SetupController.StageOf(SetupController.OpenPage) + " screen). Use cancel_setup to abandon it first.");
                }
                bool colony = SessionCommandService.ColonyLoaded();
                bool commitment = colony && Current.Game.Info.permadeathMode;
                string saveFirst = body.SaveFirst == null ? null : SessionCommandService.ValidateSaveName(body.SaveFirst, "saveFirst");
                // Starting a new game leaves the loaded one, exactly as leave_game does.
                string refusal = SessionCommandService.ReplaceRefusal(colony, commitment, discard, saveFirst, "leave");
                if (refusal != null)
                {
                    throw new CommandException(409, refusal);
                }
                if (commitment)
                {
                    saveFirst = Current.Game.Info.permadeathModeUniqueName;
                    overwrite = true;
                }
                if (saveFirst != null)
                {
                    SessionCommandService.RequireSavable();
                    SessionCommandService.RequireFreeName(saveFirst, overwrite, "saveFirst");
                }
                string loadId = colony ? GameContext.LoadId : null;
                string save = saveFirst;
                bool replace = overwrite;
                return operation => Start(operation, scenario, colony, save, replace, loadId);
            }, body.Scenario, body.Category, discard, body.SaveFirst, overwrite);
        }

        private static IEnumerable<string> Start(Operation operation, Scenario scenario, bool colony, string saveFirst, bool overwrite, string loadId)
        {
            if (colony)
            {
                foreach (string step in SessionCommandService.Leave(operation, saveFirst, overwrite, loadId))
                {
                    yield return step;
                }
                yield return "openingSetup";
            }
            if (SetupController.OpenPage != null)
            {
                throw new OperationEnded(Operation.Failed, "A setup screen was opened in game meanwhile, so this one was not; get_setup shows it.");
            }
            // As the main menu's New colony button does, then the scenario screen's Next.
            Page_SelectScenario page = new Page_SelectScenario();
            Find.WindowStack.Add(page);
            yield return "openingSetup";
            SetupController.Set(page, "curScen", scenario);
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The scenario screen did not accept " + scenario.name + ".");
            }
            SetupController.DoNext(page);
            if (SetupController.OpenPage == null || SetupController.OpenPage is Page_SelectScenario)
            {
                throw new OperationEnded(Operation.Failed, "The scenario screen did not open the next setup screen.");
            }
            SetupController.Changed();
            operation.Result = State();
        }

        public static BridgeResponse SetupBack(BridgeRequest request, RouteMatch route)
        {
            RevisionBody body = CommandUtil.ReadBody<RevisionBody>(request);
            SetupController.RequireRevision(body.ExpectedRevision);
            Page page = SetupController.OpenPage;
            if (page == null)
            {
                throw new CommandException(409, "No new game is being set up.");
            }
            if (!SetupController.TryBack(page))
            {
                throw new CommandException(409, "The " + SetupController.StageOf(page) + " screen cannot go back now.");
            }
            SetupController.Changed();
            return SessionCommandService.Envelope(true, State());
        }

        public static BridgeResponse CancelSetup(BridgeRequest request, RouteMatch route)
        {
            RevisionBody body = CommandUtil.ReadBody<RevisionBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "cancel_setup", requestId, () =>
            {
                SessionCommandService.RequireSettled();
                bool setupGame = Current.ProgramState == ProgramState.Entry && Current.Game != null;
                if (SetupController.OpenPage == null && !setupGame)
                {
                    throw new CommandException(409, "No new game is being set up.");
                }
                return operation => Cancel(operation, setupGame);
            });
        }

        private static IEnumerable<string> Cancel(Operation operation, bool setupGame)
        {
            foreach (Page page in Find.WindowStack.Windows.OfType<Page>().ToList())
            {
                page.Close(false);
            }
            if (setupGame)
            {
                // The setup's game and world are dropped as the game's own main-menu return does.
                operation.InNativeStep = true;
                operation.ReplacingGame = true;
                GenScene.GoToMainMenu();
                foreach (string step in SessionCommandService.AwaitTransition(operation, "leaving", () => Current.ProgramState == ProgramState.Entry && Current.Game == null))
                {
                    yield return step;
                }
            }
            SetupController.Changed();
            operation.Result = Dto.Obj(Dto.Field("atMainMenu", true));
        }

        private static T DefNamed<T>(string name, string field) where T : Def
        {
            T def = name == null ? null : DefDatabase<T>.GetNamedSilentFail(name.Trim());
            if (def == null)
            {
                throw new CommandException(400, "Field '" + field + "' has an unknown " + typeof(T).Name + ": " + name + ".");
            }
            return def;
        }

        // Answers a pending confirmation in unattended runs. In visible runs the player answers it in
        // game. The answer is bound to that confirmation and the setup revision it was asked at.
        public static BridgeResponse AnswerConfirmation(BridgeRequest request, RouteMatch route)
        {
            AnswerBody body = CommandUtil.ReadBody<AnswerBody>(request);
            Operation operation = OperationRunner.Find(route["operationId"]);
            if (operation == null)
            {
                throw new CommandException(404, "No operation " + route["operationId"] + ".");
            }
            if (!BridgeRuntime.UnattendedSetup)
            {
                throw new CommandException(409, "In visible setup the player answers the game's questions on screen. Unattended setup is a RimMCP setting.");
            }
            if (operation.Done || operation.ConfirmationId == null || operation.ConfirmationId != body.ConfirmationId)
            {
                throw new CommandException(409, "Operation " + operation.Id + " is not waiting for confirmation " + body.ConfirmationId + "; get_operation shows what it is waiting for.");
            }
            if (!body.Accept.HasValue)
            {
                throw new CommandException(400, "Field 'accept' is required.");
            }
            string refusal = operation.Answer == null ? "This question can only be answered in game." : operation.Answer(body.Accept.Value);
            if (refusal != null)
            {
                throw new CommandException(409, refusal);
            }
            // The question is answered once: a repeat before the operation's next step finds nothing
            // to answer, and the reply shows the operation going on.
            operation.Answer = null;
            operation.ConfirmationId = null;
            operation.Confirmation = null;
            operation.State = Operation.Running;
            return SessionCommandService.Envelope(true, SessionCommandService.Serialize(operation));
        }

        // Waits while the game asks something during setup. The question belongs to the operation and
        // to the setup's current revision (Confirmations); in unattended runs an agent may answer it.
        private static IEnumerable<string> AwaitQuestion(Operation operation, List<Window> asking, string kind, Func<bool> proceeded)
        {
            return Confirmations.Await(operation, asking, kind, null, true, proceeded, Confirmations.SetupUnchanged(SetupController.Revision));
        }

        // The setup as get_setup reports it: the screen, revision, choices so far, and the options for
        // the screen that is open.
        private static object State(bool withOptions = false)
        {
            Page page = SetupController.OpenPage;
            if (page == null)
            {
                return Dto.Obj(
                    Dto.Field("active", false),
                    Dto.Field("revision", SetupController.Revision),
                    Dto.Field("mode", BridgeRuntime.UnattendedSetup ? "unattended" : "visible"));
            }
            string stage = SetupController.StageOf(page);
            Dictionary<string, object> state = Dto.Obj(
                Dto.Field("active", true),
                Dto.Field("stage", stage),
                Dto.Field("revision", SetupController.Revision),
                Dto.Field("lastChangeExternal", SetupController.LastChangeExternal ? (object)true : null),
                Dto.Field("mode", BridgeRuntime.UnattendedSetup ? "unattended" : "visible"),
                Dto.Field("title", page.PageTitle),
                Dto.Field("scenario", Find.Scenario == null ? null : Find.Scenario.name),
                Dto.Field("lastScreen", page.next == null && page.nextAct != null ? (object)true : null));
            try
            {
                state["screen"] = Screen(page, stage, withOptions);
            }
            catch (CommandException ex)
            {
                state["handoff"] = ex.Message;
            }
            if (stage == SetupController.Unsupported)
            {
                state["handoff"] = "RimMCP does not drive the " + page.GetType().Name + " screen yet; continue it in game. get_setup follows once the next supported screen opens.";
            }
            return state;
        }

        private static object Screen(Page page, string stage, bool withOptions)
        {
            switch (stage)
            {
                case SetupController.Storyteller:
                    return Dto.Obj(
                        Dto.Field("storyteller", Def(SetupController.Get(page, "storyteller"))),
                        Dto.Field("difficulty", Def(SetupController.Get(page, "difficulty"))),
                        Dto.Field("commitmentMode", Find.GameInitData.permadeathChosen ? (object)Find.GameInitData.permadeath : null),
                        Dto.Field("storytellers", DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible).OrderBy(def => def.listOrder).Select(def => def.defName).ToArray()),
                        Dto.Field("difficulties", DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom).Select(def => def.defName).ToArray()),
                        Dto.Field("customDifficulty", CustomDifficultyDef() == null ? null : CustomDifficultyDef().defName),
                        Dto.Field("customSettings", CustomSettingsShown(page)));
                case SetupController.World:
                    return Dto.Obj(
                        Dto.Field("seed", SetupController.Get(page, "seedString")),
                        Dto.Field("coverage", Math.Round((float)SetupController.Get(page, "planetCoverage") * 100)),
                        Dto.Field("rainfall", SetupController.Get(page, "rainfall").ToString()),
                        Dto.Field("temperature", SetupController.Get(page, "temperature").ToString()),
                        Dto.Field("population", SetupController.Get(page, "population").ToString()),
                        Dto.Field("landmarkDensity", SetupController.Get(page, "landmarkDensity").ToString()),
                        Dto.Field("pollution", ModsConfig.BiotechActive ? (object)Math.Round(((Page_CreateWorldParams)page).pollution * 100) : null),
                        Dto.Field("options", Dto.Obj(
                            Dto.Field("coverage", Coverages),
                            Dto.Field("rainfall", Enum.GetNames(typeof(OverallRainfall))),
                            Dto.Field("temperature", Enum.GetNames(typeof(OverallTemperature))),
                            Dto.Field("population", Enum.GetNames(typeof(OverallPopulation))),
                            Dto.Field("landmarkDensity", Enum.GetNames(typeof(LandmarkDensity))))));
                case SetupController.Site:
                    PlanetTile selected = Find.WorldInterface.SelectedTile;
                    return Dto.Obj(
                        Dto.Field("seed", Find.World.info.seedString),
                        Dto.Field("tiles", Find.WorldGrid.TilesCount),
                        Dto.Field("selectedTile", selected.Valid ? (object)selected.tileId : null),
                        Dto.Field("mapSize", Find.GameInitData.mapSize),
                        Dto.Field("startingSeason", Find.GameInitData.startingSeason.ToString()),
                        Dto.Field("options", Dto.Obj(
                            Dto.Field("mapSize", Dialog_AdvancedGameConfig.MapSizes),
                            Dto.Field("startingSeason", Enum.GetNames(typeof(Season))))));
                case SetupController.Ideoligion:
                    return Dto.Obj(
                        Dto.Field("selection", SetupController.Get(page, "presetSelection").ToString()),
                        Dto.Field("preset", Def(SetupController.Get(page, "selectedIdeo"))),
                        Dto.Field("structure", Def(SetupController.Get(page, "selectedStructure"))),
                        Dto.Field("presets", Presets().Select(def => (object)Dto.Obj(
                            Dto.Field("defName", def.defName),
                            Dto.Field("label", def.LabelCap.ToString()),
                            Dto.Field("category", def.categoryDef == null ? null : def.categoryDef.defName),
                            Dto.Field("memes", def.memes == null ? null : def.memes.Select(meme => meme.defName).ToArray()))).ToArray()),
                        Dto.Field("structures", Structures().Select(meme => meme.defName).ToArray()),
                        Dto.Field("saved", SavedIdeoligions().ToArray()),
                        Dto.Field("modes", new[] { "classic", "preset", "customFixed", "customFluid", "saved" }));
                case SetupController.CustomIdeoligion:
                    Ideo ideo = ((Page_ConfigureIdeo)page).ideo;
                    return Dto.Obj(
                        Dto.Field("fluid", page is Page_ConfigureFluidIdeo),
                        Dto.Field("ideoligion", ideo == null ? null : IdeoEditor.Describe(ideo, withOptions)),
                        Dto.Field("create", ideo == null ? "No ideoligion yet: edit_setup_ideoligion with structure and memes creates one." : null),
                        Dto.Field("memeCatalog", ideo == null ? IdeoEditor.MemeCatalog(IdeoUtility.MakeEmptyIdeo()) : null));
                case SetupController.Pawns:
                    return Dto.Obj(
                        Dto.Field("startingCount", Find.GameInitData.startingPawnCount),
                        Dto.Field("roster", Roster()),
                        Dto.Field("childSelection", ModsConfig.BiotechActive ? (object)ScenarioUtility.AllowsChildSelection(Find.Scenario) : null),
                        Dto.Field("xenotypes", ModsConfig.BiotechActive ? DefDatabase<XenotypeDef>.AllDefs.OrderBy(xenotype => -xenotype.displayPriority).Select(xenotype => xenotype.defName).ToArray() : null));
                default:
                    return null;
            }
        }

        private static object Def(object def)
        {
            Def typed = def as Def;
            return typed == null ? null : typed.defName;
        }

        // By name, and by category when a player's own scenario shares a built-in one's name.
        private static Scenario FindScenario(string name, string category)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandException(400, "Field 'scenario' must name a scenario; list_scenarios lists them.");
            }
            ScenarioCategory? wanted = string.IsNullOrWhiteSpace(category) ? (ScenarioCategory?)null : RequiredEnum<ScenarioCategory>(category, "category");
            List<Scenario> matches = ScenarioLister.AllScenarios().Where(scenario => scenario.showInUI
                && string.Equals(scenario.name, name.Trim(), StringComparison.OrdinalIgnoreCase)
                && (!wanted.HasValue || scenario.Category == wanted.Value)).ToList();
            if (matches.Count == 0)
            {
                throw new CommandException(404, "No scenario named '" + name.Trim() + "'" + (wanted.HasValue ? " in " + wanted.Value : "") + "; list_scenarios lists them.");
            }
            if (matches.Count > 1)
            {
                throw new CommandException(409, "More than one scenario is named '" + name.Trim() + "' (" + string.Join(", ", matches.Select(scenario => scenario.Category.ToString()).Distinct().ToArray()) + "); pass category to choose one" + (wanted.HasValue ? ", or rename one in game" : "") + ".");
            }
            string[] manual = UnsupportedScreens(matches[0]);
            if (manual.Length > 0)
            {
                throw new CommandException(409, matches[0].name + " has setup screens RimMCP does not drive yet (" + string.Join(", ", manual) + "); start it in game.");
            }
            return matches[0];
        }

        // Scenario screens besides the colonist screen are handed to the player.
        private static string[] UnsupportedScreens(Scenario scenario)
        {
            try
            {
                return scenario.AllParts.SelectMany(part => part.GetConfigPages() ?? Enumerable.Empty<Page>())
                    .Where(page => !(page is Page_ConfigureStartingPawns))
                    .Select(page => page.GetType().Name)
                    .Distinct()
                    .ToArray();
            }
            catch (Exception ex)
            {
                // A scenario part from a mod that cannot list its screens is left to the player.
                return new[] { "unknown (" + ex.GetType().Name + ")" };
            }
        }

        private static HashSet<string> Defs<T>(string[] names, string field) where T : Def
        {
            return names == null ? null : new HashSet<string>(DefList<T>(names, field).Select(def => def.defName));
        }

        private static List<T> DefList<T>(string[] names, string field) where T : Def
        {
            List<T> found = new List<T>();
            foreach (string name in names ?? new string[0])
            {
                T def = name == null ? null : DefDatabase<T>.GetNamedSilentFail(name.Trim());
                if (def == null)
                {
                    throw new CommandException(400, "Field '" + field + "' has an unknown " + typeof(T).Name + ": " + name + ".");
                }
                found.Add(def);
            }
            return found;
        }

        private static object ParseEnum<T>(string value, string field, object fallback = null) where T : struct
        {
            return string.IsNullOrWhiteSpace(value) ? fallback : RequiredEnum<T>(value, field);
        }

        private static T RequiredEnum<T>(string value, string field) where T : struct
        {
            T parsed;
            if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse(value.Trim(), true, out parsed) || int.TryParse(value.Trim(), out _))
            {
                throw new CommandException(400, "Field '" + field + "' must be one of: " + string.Join(", ", Enum.GetNames(typeof(T))) + ".");
            }
            return parsed;
        }

        private static string Join(string[] values)
        {
            return values == null ? null : string.Join(",", values.Select(value => (value ?? "").Trim()).OrderBy(value => value).ToArray());
        }

        [DataContract]
        private sealed class StartSetupBody
        {
            [DataMember(Name = "scenario")] public string Scenario { get; set; }
            [DataMember(Name = "category")] public string Category { get; set; }
            [DataMember(Name = "discardUnsaved")] public bool? DiscardUnsaved { get; set; }
            [DataMember(Name = "saveFirst")] public string SaveFirst { get; set; }
            [DataMember(Name = "overwrite")] public bool? Overwrite { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class RevisionBody
        {
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class AnswerBody
        {
            [DataMember(Name = "confirmationId")] public string ConfirmationId { get; set; }
            [DataMember(Name = "accept")] public bool? Accept { get; set; }
        }
    }
}
