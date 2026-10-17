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
    // Sets up a new game through the game's own screens, visibly. Supported path: Crashlanded-style
    // scenarios whose only extra screen is the colonist screen, a storyteller preset, world
    // generation, site search and choice, a preset or classic ideoligion, and colonist rerolls.
    // Other screens are handed to the player. Commands that change the setup take the revision from
    // get_setup, and nothing acts on a setup someone else has changed since.
    internal static class SetupCommandService
    {
        private const int MaxRerollAttempts = 500;
        private static readonly int[] Coverages = { 30, 50, 100 };
        private static int confirmations;

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
            return SessionCommandService.Envelope(false, State());
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

        public static BridgeResponse SetStoryteller(BridgeRequest request, RouteMatch route)
        {
            StorytellerBody body = CommandUtil.ReadBody<StorytellerBody>(request);
            Page_SelectStoryteller page = SetupController.Require<Page_SelectStoryteller>(SetupController.Storyteller);
            SetupController.RequireRevision(body.ExpectedRevision);
            StorytellerDef storyteller = DefDatabase<StorytellerDef>.AllDefs.FirstOrDefault(def => def.listVisible && string.Equals(def.defName, (body.Storyteller ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (storyteller == null)
            {
                throw new CommandException(400, "Field 'storyteller' must be one of: " + string.Join(", ", DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible).Select(def => def.defName).ToArray()) + ".");
            }
            DifficultyDef difficulty = DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => string.Equals(def.defName, (body.Difficulty ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (difficulty == null || difficulty.isCustom)
            {
                throw new CommandException(400, "Field 'difficulty' must be a preset: " + string.Join(", ", DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom).Select(def => def.defName).ToArray()) + ". Custom difficulty is set in game.");
            }
            if (!body.CommitmentMode.HasValue)
            {
                throw new CommandException(400, "Field 'commitmentMode' is required, as the screen asks: true for commitment mode, false to reload any time.");
            }
            SetupController.Set(page, "storyteller", storyteller);
            SetupController.Set(page, "difficulty", difficulty);
            SetupController.Set(page, "difficultyValues", new Difficulty(difficulty));
            Find.GameInitData.permadeathChosen = true;
            Find.GameInitData.permadeath = body.CommitmentMode.Value;
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new CommandException(409, "The storyteller screen did not accept these choices.");
            }
            SetupController.DoNext(page);
            SetupController.Changed();
            return SessionCommandService.Envelope(true, State());
        }

        public static BridgeResponse GenerateWorld(BridgeRequest request, RouteMatch route)
        {
            WorldBody body = CommandUtil.ReadBody<WorldBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "generate_world", requestId, () =>
            {
                Page_CreateWorldParams page = SetupController.Require<Page_CreateWorldParams>(SetupController.World);
                SetupController.RequireRevision(body.ExpectedRevision);
                if (body.Coverage.HasValue && !Coverages.Contains(body.Coverage.Value))
                {
                    throw new CommandException(400, "Field 'coverage' must be 30, 50, or 100 (percent of the planet).");
                }
                if (body.Pollution.HasValue && (!ModsConfig.BiotechActive || body.Pollution.Value < 0 || body.Pollution.Value > 100))
                {
                    throw new CommandException(400, ModsConfig.BiotechActive ? "Field 'pollution' must be 0 to 100 (percent)." : "Pollution needs Biotech.");
                }
                object rainfall = ParseEnum<OverallRainfall>(body.Rainfall, "rainfall");
                object temperature = ParseEnum<OverallTemperature>(body.Temperature, "temperature");
                object population = ParseEnum<OverallPopulation>(body.Population, "population");
                object landmarks = ParseEnum<LandmarkDensity>(body.LandmarkDensity, "landmarkDensity");
                int own = SetupController.Revision;
                return operation =>
                {
                    Action apply = () =>
                    {
                        if (!string.IsNullOrWhiteSpace(body.Seed))
                        {
                            SetupController.Set(page, "seedString", body.Seed.Trim());
                        }
                        if (body.Coverage.HasValue)
                        {
                            SetupController.Set(page, "planetCoverage", body.Coverage.Value / 100f);
                        }
                        if (rainfall != null)
                        {
                            SetupController.Set(page, "rainfall", rainfall);
                        }
                        if (temperature != null)
                        {
                            SetupController.Set(page, "temperature", temperature);
                        }
                        if (population != null)
                        {
                            SetupController.Set(page, "population", population);
                        }
                        if (landmarks != null)
                        {
                            SetupController.Set(page, "landmarkDensity", landmarks);
                        }
                        if (body.Pollution.HasValue)
                        {
                            page.pollution = body.Pollution.Value / 100f;
                        }
                    };
                    return Generate(operation, page, own, apply);
                };
            }, body.Seed, body.Coverage, body.Rainfall, body.Temperature, body.Population, body.LandmarkDensity, body.Pollution, body.ExpectedRevision);
        }

        // Choices are written once the operation runs, so a refused request changes nothing. The world
        // page starts generating from its own can-proceed check, then opens the next screen.
        private static IEnumerable<string> Generate(Operation operation, Page_CreateWorldParams page, int own, Action apply)
        {
            SetupController.Continue(operation, own);
            apply();
            SetupController.Changed();
            SetupController.CanNext(page);
            if (!LongEventHandler.AnyEventNowOrWaiting)
            {
                throw new OperationEnded(Operation.Failed, "The world screen did not start generating.");
            }
            operation.InNativeStep = true;
            operation.ReplacingGame = true;
            while (LongEventHandler.AnyEventNowOrWaiting)
            {
                yield return "generatingWorld";
            }
            operation.InNativeStep = false;
            operation.ReplacingGame = false;
            // When generation finishes, the game opens the next screen and closes this one.
            for (int frame = 0; frame < 60 && SetupController.OpenPage == page; frame++)
            {
                yield return "generatingWorld";
            }
            if (SetupController.OpenPage == page || SetupController.OpenPage == null || Find.World == null)
            {
                throw new OperationEnded(Operation.Failed, "World generation did not finish; get_game_log may say why.");
            }
            SetupController.Changed();
            operation.Result = Dto.Obj(
                Dto.Field("seed", Find.World.info.seedString),
                Dto.Field("coverage", Math.Round(Find.World.info.planetCoverage * 100)),
                Dto.Field("tiles", Find.WorldGrid.TilesCount),
                Dto.Field("setup", State()));
        }

        public static BridgeResponse FindStartingSites(BridgeRequest request, RouteMatch route)
        {
            SitesBody body = CommandUtil.ReadBody<SitesBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "find_starting_sites", ToolGroups.Observe, requestId, () =>
            {
                if (Find.World == null || Current.ProgramState != ProgramState.Entry || !(SetupController.OpenPage is Page_SelectStartingSite))
                {
                    throw new CommandException(409, "Site search needs the starting site screen of a new game: generate the world first.");
                }
                SiteCriteria criteria = new SiteCriteria
                {
                    Biomes = Defs<BiomeDef>(body.Biomes, "biomes"),
                    Hilliness = body.Hilliness == null ? null : new HashSet<string>(body.Hilliness.Select(name => RequiredEnum<Hilliness>(name, "hilliness").ToString())),
                    River = body.River,
                    Road = body.Road,
                    Coastal = body.Coastal,
                    MinTemperature = body.MinTemperature,
                    MaxTemperature = body.MaxTemperature,
                    MinRainfall = body.MinRainfall,
                    MaxRainfall = body.MaxRainfall,
                    MinGrowingDays = body.MinGrowingDays,
                    Limit = Math.Max(1, Math.Min(50, body.Limit ?? 10))
                };
                string worldId = SiteSearch.WorldId();
                return operation => SiteSearch.Run(operation, criteria, worldId);
            }, Join(body.Biomes), Join(body.Hilliness), body.River, body.Road, body.Coastal, body.MinTemperature, body.MaxTemperature, body.MinRainfall, body.MaxRainfall, body.MinGrowingDays, body.Limit, SiteSearch.WorldId());
        }

        public static BridgeResponse ChooseStartingSite(BridgeRequest request, RouteMatch route)
        {
            SiteBody body = CommandUtil.ReadBody<SiteBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "choose_starting_site", requestId, () =>
            {
                Page_SelectStartingSite page = SetupController.Require<Page_SelectStartingSite>(SetupController.Site);
                SetupController.RequireRevision(body.ExpectedRevision);
                if (page.next == null)
                {
                    throw new CommandException(409, "This scenario has no screens after the starting site, so choosing it starts the colony; choose the site in game.");
                }
                if (!body.TileId.HasValue || body.TileId.Value < 0 || body.TileId.Value >= Find.WorldGrid.TilesCount)
                {
                    throw new CommandException(400, "Field 'tileId' must be a tile of this world, 0 to " + (Find.WorldGrid.TilesCount - 1) + ".");
                }
                PlanetTile tile = new PlanetTile(body.TileId.Value, Find.WorldGrid.Surface);
                System.Text.StringBuilder reason = new System.Text.StringBuilder();
                if (!TileFinder.IsValidTileForNewSettlement(tile, reason))
                {
                    throw new CommandException(409, "The game does not allow settling tile " + body.TileId.Value + ": " + reason.ToString().StripTags());
                }
                if (body.MapSize.HasValue && !Dialog_AdvancedGameConfig.MapSizes.Contains(body.MapSize.Value))
                {
                    throw new CommandException(400, "Field 'mapSize' must be one of: " + string.Join(", ", Dialog_AdvancedGameConfig.MapSizes.Select(size => size.ToString()).ToArray()) + ".");
                }
                object season = ParseEnum<Season>(body.StartingSeason, "startingSeason");
                int own = SetupController.Revision;
                return operation =>
                {
                    Action apply = () =>
                    {
                        Find.WorldInterface.SelectedTile = tile;
                        if (body.MapSize.HasValue)
                        {
                            Find.GameInitData.mapSize = body.MapSize.Value;
                        }
                        if (season != null)
                        {
                            Find.GameInitData.startingSeason = (Season)season;
                        }
                    };
                    return Settle(operation, page, own, apply);
                };
            }, body.TileId, body.MapSize, body.StartingSeason, body.ExpectedRevision);
        }

        private static IEnumerable<string> Settle(Operation operation, Page_SelectStartingSite page, int own, Action apply)
        {
            SetupController.Continue(operation, own);
            apply();
            SetupController.Changed();
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The starting site screen did not accept the tile.");
            }
            List<Window> open = Find.WindowStack.Windows.ToList();
            SetupController.DoNext(page);
            if (SetupController.OpenPage == page)
            {
                // Settling near a faction asks first, as it does for a click.
                List<Window> asking = Find.WindowStack.Windows.Where(window => !open.Contains(window) && !(window is Page)).ToList();
                if (asking.Count == 0)
                {
                    throw new OperationEnded(Operation.Failed, "The starting site screen did not move on.");
                }
                foreach (string step in AwaitQuestion(operation, asking, "settleNearFaction", () => SetupController.OpenPage != page))
                {
                    yield return step;
                }
                if (SetupController.OpenPage == page)
                {
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Declined, "Settling there was not confirmed.");
                }
            }
            if (SetupController.OpenPage == null)
            {
                throw new OperationEnded(Operation.Failed, "The starting site screen did not open the next setup screen.");
            }
            SetupController.Changed();
            operation.Result = State();
        }

        public static BridgeResponse SetIdeoligion(BridgeRequest request, RouteMatch route)
        {
            IdeoligionBody body = CommandUtil.ReadBody<IdeoligionBody>(request);
            Page_ChooseIdeoPreset page = SetupController.Require<Page_ChooseIdeoPreset>(SetupController.Ideoligion);
            SetupController.RequireRevision(body.ExpectedRevision);
            string mode = (body.Mode ?? "").Trim().ToLowerInvariant();
            Type selection = typeof(Page_ChooseIdeoPreset).GetNestedType("PresetSelection", System.Reflection.BindingFlags.NonPublic);
            if (selection == null)
            {
                throw new CommandException(501, "This game version's ideoligion screen cannot be driven; choose in game.");
            }
            if (mode == "classic")
            {
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, "Classic"));
            }
            else if (mode == "preset")
            {
                IdeoPresetDef preset = Presets().FirstOrDefault(def => string.Equals(def.defName, (body.Preset ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    throw new CommandException(400, "Field 'preset' must be one of the screen's presets; get_setup lists them.");
                }
                MemeDef structure = null;
                if (!string.IsNullOrWhiteSpace(body.Structure))
                {
                    structure = Structures().FirstOrDefault(def => string.Equals(def.defName, body.Structure.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (structure == null)
                    {
                        throw new CommandException(400, "Field 'structure' must be a structure meme the player faction may use; get_setup lists them.");
                    }
                }
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, "Preset"));
                SetupController.Set(page, "selectedIdeo", preset);
                SetupController.Set(page, "selectedStructure", structure);
            }
            else
            {
                throw new CommandException(400, "Field 'mode' must be classic or preset. Custom and saved ideoligions are chosen in game for now.");
            }
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new CommandException(409, "The ideoligion screen did not accept this choice.");
            }
            SetupController.DoNext(page);
            SetupController.Changed();
            return SessionCommandService.Envelope(true, State());
        }

        public static BridgeResponse SetStartingPawns(BridgeRequest request, RouteMatch route)
        {
            PawnsBody body = CommandUtil.ReadBody<PawnsBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "set_starting_pawns", requestId, () =>
            {
                SetupController.Require<Page_ConfigureStartingPawns>(SetupController.Pawns);
                SetupController.RequireRevision(body.ExpectedRevision);
                int count = Find.GameInitData.startingAndOptionalPawns.Count;
                List<Reroll> rerolls = new List<Reroll>();
                foreach (RerollEntry entry in body.Rerolls ?? new RerollEntry[0])
                {
                    if (entry == null || !entry.Slot.HasValue || entry.Slot.Value < 0 || entry.Slot.Value >= count)
                    {
                        throw new CommandException(400, "Each reroll needs a slot from 0 to " + (count - 1) + ".");
                    }
                    rerolls.Add(new Reroll
                    {
                        Slot = entry.Slot.Value,
                        Criteria = Criteria(entry),
                        MaxAttempts = Math.Max(1, Math.Min(MaxRerollAttempts, entry.MaxAttempts ?? 100))
                    });
                }
                foreach (NameEntry name in body.Names ?? new NameEntry[0])
                {
                    if (name == null || !name.Slot.HasValue || name.Slot.Value < 0 || name.Slot.Value >= count || string.IsNullOrWhiteSpace(name.First) || string.IsNullOrWhiteSpace(name.Last))
                    {
                        throw new CommandException(400, "Each name needs a slot from 0 to " + (count - 1) + ", first, and last.");
                    }
                }
                if (rerolls.Count == 0 && (body.Names == null || body.Names.Length == 0))
                {
                    throw new CommandException(400, "Give rerolls, names, or both.");
                }
                int own = SetupController.Revision;
                NameEntry[] names = body.Names ?? new NameEntry[0];
                return operation => Rerolls(operation, rerolls, names, own);
            }, Fingerprint(body));
        }

        private sealed class Reroll
        {
            public int Slot;
            public PawnCriteria Criteria;
            public int MaxAttempts;
            public int Attempts;
            public bool Matched;
            public List<string> Unmet = new List<string>();
        }

        // One native reroll per frame: a single reroll can generate several pawns, until the colony's
        // required work types are covered. Each step first checks nobody else changed the setup.
        private static IEnumerable<string> Rerolls(Operation operation, List<Reroll> rerolls, NameEntry[] names, int own)
        {
            foreach (Reroll reroll in rerolls)
            {
                while (true)
                {
                    SetupController.Continue(operation, own);
                    Pawn pawn = Find.GameInitData.startingAndOptionalPawns[reroll.Slot];
                    reroll.Unmet = reroll.Criteria.Unmet(pawn);
                    if (reroll.Unmet.Count == 0)
                    {
                        reroll.Matched = true;
                        break;
                    }
                    if (reroll.Attempts >= reroll.MaxAttempts)
                    {
                        break;
                    }
                    StartingPawnUtility.RandomizePawn(reroll.Slot);
                    reroll.Attempts++;
                    own = SetupController.Changed();
                    operation.Result = RerollResult(rerolls);
                    yield return "rerolling";
                }
            }
            SetupController.Continue(operation, own);
            foreach (NameEntry entry in names)
            {
                Pawn pawn = Find.GameInitData.startingAndOptionalPawns[entry.Slot.Value];
                pawn.Name = new NameTriple(entry.First.Trim(), string.IsNullOrWhiteSpace(entry.Nick) ? entry.First.Trim() : entry.Nick.Trim(), entry.Last.Trim());
            }
            if (names.Length > 0)
            {
                SetupController.Changed();
            }
            operation.Result = RerollResult(rerolls);
        }

        private static object RerollResult(List<Reroll> rerolls)
        {
            return Dto.Obj(
                Dto.Field("rerolls", rerolls.Select(reroll => (object)Dto.Obj(
                    Dto.Field("slot", reroll.Slot),
                    Dto.Field("attempts", reroll.Attempts),
                    Dto.Field("matched", reroll.Matched),
                    Dto.Field("unmet", reroll.Unmet.Count == 0 ? null : reroll.Unmet.ToArray()))).ToArray()),
                Dto.Field("roster", Roster()));
        }

        public static BridgeResponse StartGame(BridgeRequest request, RouteMatch route)
        {
            RevisionBody body = CommandUtil.ReadBody<RevisionBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "start_game", requestId, () =>
            {
                Page page = SetupController.OpenPage;
                if (page == null || page.next != null || page.nextAct == null || SetupController.StageOf(page) == SetupController.Unsupported)
                {
                    throw new CommandException(409, page == null ? "No new game is being set up." : "start_game is for the last setup screen; setup is at the " + SetupController.StageOf(page) + " screen.");
                }
                SetupController.RequireRevision(body.ExpectedRevision);
                int own = SetupController.Revision;
                return operation => Begin(operation, page, own);
            }, body.ExpectedRevision);
        }

        private static IEnumerable<string> Begin(Operation operation, Page page, int own)
        {
            SetupController.Continue(operation, own);
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The " + SetupController.StageOf(page) + " screen is not ready to start; see the message in game.");
            }
            if (!BridgeRuntime.UnattendedSetup)
            {
                // The player approves this exact setup. Any change while asking voids the approval.
                bool approved = false;
                Dialog_MessageBox approval = new Dialog_MessageBox(Summary(), "Start colony", () => approved = true, "Not yet", null, "RimMCP is ready to start this colony");
                Find.WindowStack.Add(approval);
                operation.State = Operation.AwaitingConfirmation;
                operation.Confirmation = Dto.Obj(
                    Dto.Field("kind", "startColony"),
                    Dto.Field("message", "The player is asked to approve starting the colony as set up."),
                    Dto.Field("revision", own),
                    Dto.Field("answeredBy", "player"));
                while (Find.WindowStack.IsOpen(approval))
                {
                    if (SetupStateChanged(own))
                    {
                        approval.Close(false);
                        throw new OperationEnded(Operation.Interrupted, "The setup changed while the player was asked, so the approval was withdrawn.");
                    }
                    yield return "awaitingApproval";
                }
                operation.State = Operation.Running;
                operation.Confirmation = null;
                if (!approved)
                {
                    throw new OperationEnded(Operation.Declined, "The player chose not to start the colony yet.");
                }
                SetupController.Continue(operation, own);
            }
            List<Window> open = Find.WindowStack.Windows.ToList();
            SetupController.DoNext(page);
            if (!LongEventHandler.AnyEventNowOrWaiting)
            {
                List<Window> asking = Find.WindowStack.Windows.Where(window => !open.Contains(window) && !(window is Page)).ToList();
                if (asking.Count == 0)
                {
                    throw new OperationEnded(Operation.Failed, "The game did not start the colony.");
                }
                foreach (string step in AwaitQuestion(operation, asking, "requiredWorkDisabled", () => LongEventHandler.AnyEventNowOrWaiting))
                {
                    yield return step;
                }
                if (!LongEventHandler.AnyEventNowOrWaiting)
                {
                    throw new OperationEnded(Operation.Declined, "Starting without someone for every required job was not confirmed.");
                }
            }
            foreach (string step in SessionCommandService.AwaitTransition(operation, "startingColony", () => Current.ProgramState == ProgramState.Playing))
            {
                yield return step;
            }
            SetupController.Changed();
            operation.Result = Dto.Obj(
                Dto.Field("loadId", GameContext.LoadId),
                Dto.Field("tick", Find.TickManager.TicksGame),
                Dto.Field("colonists", Find.CurrentMap == null ? null : Find.CurrentMap.mapPawns.FreeColonists.Select(pawn => ReadUtil.PawnName(pawn)).ToArray()));
        }

        private static bool SetupStateChanged(int own)
        {
            SetupController.Observe();
            return SetupController.Revision != own;
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

        // Waits while the game asks something. In unattended runs an agent may answer a message box
        // through answer_confirmation, by the confirmation's id; it presses the button a click would.
        private static IEnumerable<string> AwaitQuestion(Operation operation, List<Window> asking, string kind, Func<bool> proceeded)
        {
            string id = "confirm-" + (++confirmations);
            int asked = SetupController.Revision;
            Dialog_MessageBox box = asking.OfType<Dialog_MessageBox>().FirstOrDefault();
            operation.State = Operation.AwaitingConfirmation;
            operation.ConfirmationId = id;
            operation.Confirmation = Dto.Obj(
                Dto.Field("confirmationId", id),
                Dto.Field("kind", kind),
                Dto.Field("message", box == null ? "The game is asking before it goes on." : box.text.ToString().StripTags()),
                Dto.Field("answeredBy", BridgeRuntime.UnattendedSetup ? "agent" : "player"),
                Dto.Field("dialogs", asking.Select(window => window.GetType().Name).ToArray()));
            operation.Answer = box == null ? (Func<bool, string>)null : accept =>
            {
                SetupController.Observe();
                if (SetupController.Revision != asked)
                {
                    return "The setup changed after this was asked, so the answer no longer applies.";
                }
                // As the button does: its action, then the box closes.
                Action action = accept ? box.buttonAAction : box.buttonBAction;
                if (action != null)
                {
                    action();
                }
                box.Close();
                return null;
            };
            while (asking.Any(Find.WindowStack.IsOpen) && !proceeded())
            {
                yield return "awaitingConfirmation";
            }
            operation.State = Operation.Running;
            operation.Confirmation = null;
            operation.ConfirmationId = null;
            operation.Answer = null;
        }

        // The setup as get_setup reports it: the screen, revision, choices so far, and the options for
        // the screen that is open.
        private static object State()
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
                state["screen"] = Screen(page, stage);
            }
            catch (CommandException ex)
            {
                state["handoff"] = ex.Message;
            }
            if (stage == SetupController.CustomIdeoligion || stage == SetupController.Unsupported)
            {
                state["handoff"] = "RimMCP does not drive the " + page.GetType().Name + " screen yet; continue it in game. get_setup follows once the next supported screen opens.";
            }
            return state;
        }

        private static object Screen(Page page, string stage)
        {
            switch (stage)
            {
                case SetupController.Storyteller:
                    return Dto.Obj(
                        Dto.Field("storyteller", Def(SetupController.Get(page, "storyteller"))),
                        Dto.Field("difficulty", Def(SetupController.Get(page, "difficulty"))),
                        Dto.Field("commitmentMode", Find.GameInitData.permadeathChosen ? (object)Find.GameInitData.permadeath : null),
                        Dto.Field("storytellers", DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible).OrderBy(def => def.listOrder).Select(def => def.defName).ToArray()),
                        Dto.Field("difficulties", DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom).Select(def => def.defName).ToArray()));
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
                        Dto.Field("modes", new[] { "classic", "preset" }));
                case SetupController.Pawns:
                    return Dto.Obj(
                        Dto.Field("startingCount", Find.GameInitData.startingPawnCount),
                        Dto.Field("roster", Roster()));
                default:
                    return null;
            }
        }

        private static object[] Roster()
        {
            GameInitData init = Find.GameInitData;
            return init.startingAndOptionalPawns.Select((pawn, slot) => (object)Dto.Obj(
                Dto.Field("slot", slot),
                Dto.Field("starting", slot < init.startingPawnCount),
                Dto.Field("id", pawn.ThingID),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("age", pawn.ageTracker.AgeBiologicalYears),
                Dto.Field("traits", pawn.story == null ? null : pawn.story.traits.allTraits.Select(trait => trait.def.defName).ToArray()),
                Dto.Field("skills", pawn.skills == null ? null : pawn.skills.skills.Select(skill => (object)Dto.Obj(
                    Dto.Field("skill", skill.def.defName),
                    Dto.Field("level", skill.TotallyDisabled ? null : (object)skill.Level),
                    Dto.Field("passion", skill.passion == Passion.None ? null : skill.passion.ToString()))).ToArray()),
                Dto.Field("cannotDo", DefDatabase<WorkTypeDef>.AllDefs.Where(pawn.WorkTypeIsDisabled).Select(work => work.defName).ToArray()))).ToArray();
        }

        private static string Summary()
        {
            GameInitData init = Find.GameInitData;
            List<string> lines = new List<string>
            {
                "Scenario: " + (Find.Scenario == null ? "?" : Find.Scenario.name),
                "Storyteller: " + (Find.Storyteller == null ? "?" : Find.Storyteller.def.LabelCap + ", " + Find.Storyteller.difficultyDef.LabelCap) + (init.permadeath ? " (commitment mode)" : ""),
                "Site: tile " + init.startingTile.tileId + (init.startingTile.Valid ? ", " + Find.WorldGrid[init.startingTile].PrimaryBiome.LabelCap : "") + ", map " + init.mapSize,
                "Colonists: " + string.Join(", ", init.startingAndOptionalPawns.Take(init.startingPawnCount).Select(pawn => ReadUtil.PawnName(pawn)).ToArray())
            };
            return string.Join("\n", lines.ToArray());
        }

        // The presets the ideoligion screen lists: every category but classic, fluid, and custom,
        // which are the screen's own buttons.
        private static IEnumerable<IdeoPresetDef> Presets()
        {
            return DefDatabase<IdeoPresetDef>.AllDefs.Where(def => def.categoryDef != null
                && def.categoryDef != IdeoPresetCategoryDefOf.Classic
                && def.categoryDef != IdeoPresetCategoryDefOf.Custom
                && def.categoryDef != IdeoPresetCategoryDefOf.Fluid);
        }

        private static IEnumerable<MemeDef> Structures()
        {
            FactionDef player = Find.FactionManager.OfPlayer.def;
            return DefDatabase<MemeDef>.AllDefs.Where(meme => meme.category == MemeCategory.Structure && IdeoUtility.IsMemeAllowedFor(meme, player));
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

        private static PawnCriteria Criteria(RerollEntry entry)
        {
            PawnCriteria criteria = new PawnCriteria { MinAge = entry.MinAge, MaxAge = entry.MaxAge };
            foreach (SkillEntry skill in entry.Skills ?? new SkillEntry[0])
            {
                SkillDef def = skill == null ? null : DefDatabase<SkillDef>.GetNamedSilentFail((skill.Skill ?? "").Trim());
                if (def == null || !skill.Min.HasValue || skill.Min.Value < 0 || skill.Min.Value > 20)
                {
                    throw new CommandException(400, "Each skill needs a SkillDef name, such as Cooking, and min from 0 to 20.");
                }
                criteria.Skills.Add(new PawnCriteria.SkillNeed { Skill = def, Min = skill.Min.Value, Passion = (Passion)ParseEnum<Passion>(skill.Passion, "passion", Passion.None) });
            }
            criteria.Traits = DefList<TraitDef>(entry.Traits, "traits");
            criteria.ExcludeTraits = DefList<TraitDef>(entry.ExcludeTraits, "excludeTraits");
            criteria.CanDo = DefList<WorkTypeDef>(entry.CanDo, "canDo");
            if (entry.MinAge.HasValue && entry.MaxAge.HasValue && entry.MinAge.Value > entry.MaxAge.Value)
            {
                throw new CommandException(400, "Field 'minAge' is above 'maxAge'.");
            }
            if (criteria.Empty)
            {
                throw new CommandException(400, "A reroll needs at least one criterion: skills, traits, excludeTraits, canDo, minAge, or maxAge.");
            }
            return criteria;
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

        private static string Fingerprint(PawnsBody body)
        {
            string rerolls = string.Join(";", (body.Rerolls ?? new RerollEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + Join(entry.Traits) + ":" + Join(entry.ExcludeTraits) + ":" + Join(entry.CanDo) + ":" + entry.MinAge + "-" + entry.MaxAge + ":" + entry.MaxAttempts + ":" + string.Join(",", (entry.Skills ?? new SkillEntry[0]).Select(skill => skill == null ? "-" : skill.Skill + skill.Min + skill.Passion).ToArray())).ToArray());
            string names = string.Join(";", (body.Names ?? new NameEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + entry.First + "/" + entry.Nick + "/" + entry.Last).ToArray());
            return rerolls + "|" + names + "|" + body.ExpectedRevision;
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
        private sealed class StorytellerBody
        {
            [DataMember(Name = "storyteller")] public string Storyteller { get; set; }
            [DataMember(Name = "difficulty")] public string Difficulty { get; set; }
            [DataMember(Name = "commitmentMode")] public bool? CommitmentMode { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class WorldBody
        {
            [DataMember(Name = "seed")] public string Seed { get; set; }
            [DataMember(Name = "coverage")] public int? Coverage { get; set; }
            [DataMember(Name = "rainfall")] public string Rainfall { get; set; }
            [DataMember(Name = "temperature")] public string Temperature { get; set; }
            [DataMember(Name = "population")] public string Population { get; set; }
            [DataMember(Name = "landmarkDensity")] public string LandmarkDensity { get; set; }
            [DataMember(Name = "pollution")] public float? Pollution { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SitesBody
        {
            [DataMember(Name = "biomes")] public string[] Biomes { get; set; }
            [DataMember(Name = "hilliness")] public string[] Hilliness { get; set; }
            [DataMember(Name = "river")] public bool? River { get; set; }
            [DataMember(Name = "road")] public bool? Road { get; set; }
            [DataMember(Name = "coastal")] public bool? Coastal { get; set; }
            [DataMember(Name = "minTemperature")] public float? MinTemperature { get; set; }
            [DataMember(Name = "maxTemperature")] public float? MaxTemperature { get; set; }
            [DataMember(Name = "minRainfall")] public float? MinRainfall { get; set; }
            [DataMember(Name = "maxRainfall")] public float? MaxRainfall { get; set; }
            [DataMember(Name = "minGrowingDays")] public int? MinGrowingDays { get; set; }
            [DataMember(Name = "limit")] public int? Limit { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SiteBody
        {
            [DataMember(Name = "tileId")] public int? TileId { get; set; }
            [DataMember(Name = "mapSize")] public int? MapSize { get; set; }
            [DataMember(Name = "startingSeason")] public string StartingSeason { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class IdeoligionBody
        {
            [DataMember(Name = "mode")] public string Mode { get; set; }
            [DataMember(Name = "preset")] public string Preset { get; set; }
            [DataMember(Name = "structure")] public string Structure { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class PawnsBody
        {
            [DataMember(Name = "rerolls")] public RerollEntry[] Rerolls { get; set; }
            [DataMember(Name = "names")] public NameEntry[] Names { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class RerollEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "skills")] public SkillEntry[] Skills { get; set; }
            [DataMember(Name = "traits")] public string[] Traits { get; set; }
            [DataMember(Name = "excludeTraits")] public string[] ExcludeTraits { get; set; }
            [DataMember(Name = "canDo")] public string[] CanDo { get; set; }
            [DataMember(Name = "minAge")] public int? MinAge { get; set; }
            [DataMember(Name = "maxAge")] public int? MaxAge { get; set; }
            [DataMember(Name = "maxAttempts")] public int? MaxAttempts { get; set; }
        }

        [DataContract]
        private sealed class SkillEntry
        {
            [DataMember(Name = "skill")] public string Skill { get; set; }
            [DataMember(Name = "min")] public int? Min { get; set; }
            [DataMember(Name = "passion")] public string Passion { get; set; }
        }

        [DataContract]
        private sealed class NameEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "first")] public string First { get; set; }
            [DataMember(Name = "nick")] public string Nick { get; set; }
            [DataMember(Name = "last")] public string Last { get; set; }
        }

        [DataContract]
        private sealed class AnswerBody
        {
            [DataMember(Name = "confirmationId")] public string ConfirmationId { get; set; }
            [DataMember(Name = "accept")] public bool? Accept { get; set; }
        }
    }
}
