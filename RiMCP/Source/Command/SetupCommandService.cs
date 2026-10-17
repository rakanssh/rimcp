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
            if (difficulty == null)
            {
                throw new CommandException(400, "Field 'difficulty' must be one of: " + string.Join(", ", DefDatabase<DifficultyDef>.AllDefs.Select(def => def.defName).ToArray()) + ".");
            }
            if (!body.CommitmentMode.HasValue)
            {
                throw new CommandException(400, "Field 'commitmentMode' is required, as the screen asks: true for commitment mode, false to reload any time.");
            }
            Difficulty values;
            if (difficulty.isCustom)
            {
                values = CustomDifficulty(page, body.Custom);
            }
            else if (body.Custom != null)
            {
                throw new CommandException(400, "Field 'custom' is for the custom difficulty (" + CustomDifficultyDef().defName + ").");
            }
            else
            {
                values = new Difficulty(difficulty);
            }
            SetupController.Set(page, "storyteller", storyteller);
            SetupController.Set(page, "difficulty", difficulty);
            SetupController.Set(page, "difficultyValues", values);
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

        // The custom difficulty panel: its values start from a preset (Rough when first chosen, as the
        // screen does, or the one given in base, as its Reset menu does), then each setting is set as
        // its control would set it. Checked in full before the screen changes.
        private static Difficulty CustomDifficulty(Page_SelectStoryteller page, CustomBody custom)
        {
            DifficultyDef current = SetupController.Get(page, "difficulty") as DifficultyDef;
            Difficulty values;
            if (custom != null && !string.IsNullOrWhiteSpace(custom.Base))
            {
                DifficultyDef preset = DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => !def.isCustom && string.Equals(def.defName, custom.Base.Trim(), StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    throw new CommandException(400, "Field 'custom.base' must be a preset: " + string.Join(", ", DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom).Select(def => def.defName).ToArray()) + ".");
                }
                values = new Difficulty(preset);
            }
            else if (current != null && current.isCustom)
            {
                values = DifficultySettings.Copy((Difficulty)SetupController.Get(page, "difficultyValues"));
            }
            else
            {
                values = new Difficulty(DifficultyDefOf.Rough);
            }
            List<SettingEntry> entries = (custom == null || custom.Settings == null ? new SettingEntry[0] : custom.Settings).ToList();
            if (entries.Any(entry => entry == null) || entries.Select(entry => (entry.Setting ?? "").Trim().ToLowerInvariant()).Distinct().Count() != entries.Count)
            {
                throw new CommandException(400, "Field 'custom.settings' names each setting once.");
            }
            foreach (SettingEntry entry in entries)
            {
                if (DifficultySettings.Named(entry.Setting) == null)
                {
                    throw new CommandException(400, "Field 'custom.settings' has an unknown setting: " + entry.Setting + "; get_setup lists them.");
                }
            }
            foreach (DifficultySetting setting in DifficultySettings.All)
            {
                SettingEntry entry = entries.FirstOrDefault(candidate => string.Equals(candidate.Setting.Trim(), setting.Name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    continue;
                }
                string unavailable = setting.Unavailable(values);
                if (unavailable != null)
                {
                    throw new CommandException(409, "The custom difficulty panel does not offer " + setting.Name + " now: " + unavailable + ".");
                }
                if (setting.Toggle)
                {
                    if (!entry.Enabled.HasValue || entry.Value.HasValue)
                    {
                        throw new CommandException(400, setting.Name + " is a checkbox; give enabled: true or false.");
                    }
                    setting.WriteToggle(values, entry.Enabled.Value);
                }
                else
                {
                    if (!entry.Value.HasValue || entry.Enabled.HasValue || float.IsNaN(entry.Value.Value) || entry.Value.Value < setting.Min || entry.Value.Value > setting.Max)
                    {
                        throw new CommandException(400, setting.Name + " is a slider; give value from " + setting.Min + " to " + setting.Max + " in steps of " + setting.Step + ".");
                    }
                    setting.Write(values, entry.Value.Value);
                }
            }
            return values;
        }

        private static DifficultyDef CustomDifficultyDef()
        {
            return DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => def.isCustom);
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
                    MinSettlementDistance = body.MinSettlementDistance,
                    MaxSettlementDistance = body.MaxSettlementDistance,
                    SettlementRelations = body.SettlementRelations == null ? null : new HashSet<FactionRelationKind>(body.SettlementRelations.Select(name => RequiredEnum<FactionRelationKind>(name, "settlementRelations"))),
                    AvoidEncroaching = body.AvoidEncroaching == true,
                    Limit = Math.Max(1, Math.Min(50, body.Limit ?? 10))
                };
                if (criteria.MinSettlementDistance < 0 || criteria.MaxSettlementDistance < 0 || criteria.MinSettlementDistance > criteria.MaxSettlementDistance)
                {
                    throw new CommandException(400, "Settlement distances are world tiles from 0, with minSettlementDistance no more than maxSettlementDistance.");
                }
                string worldId = SiteSearch.WorldId();
                return operation => SiteSearch.Run(operation, criteria, worldId);
            }, Join(body.Biomes), Join(body.Hilliness), body.River, body.Road, body.Coastal, body.MinTemperature, body.MaxTemperature, body.MinRainfall, body.MaxRainfall, body.MinGrowingDays, body.MinSettlementDistance, body.MaxSettlementDistance, Join(body.SettlementRelations), body.AvoidEncroaching, body.Limit, SiteSearch.WorldId());
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
            if (string.Equals((body.Mode ?? "").Trim(), "saved", StringComparison.OrdinalIgnoreCase))
            {
                return LoadSavedIdeoligion(request, body);
            }
            Page_ChooseIdeoPreset page = SetupController.Require<Page_ChooseIdeoPreset>(SetupController.Ideoligion);
            SetupController.RequireRevision(body.ExpectedRevision);
            string mode = (body.Mode ?? "").Trim();
            Type selection = typeof(Page_ChooseIdeoPreset).GetNestedType("PresetSelection", System.Reflection.BindingFlags.NonPublic);
            if (selection == null)
            {
                throw new CommandException(501, "This game version's ideoligion screen cannot be driven; choose in game.");
            }
            MemeDef customStructure = null;
            List<MemeDef> customMemes = null;
            if (string.Equals(mode, "classic", StringComparison.OrdinalIgnoreCase))
            {
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, "Classic"));
            }
            else if (string.Equals(mode, "preset", StringComparison.OrdinalIgnoreCase))
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
            else if (string.Equals(mode, "customFixed", StringComparison.OrdinalIgnoreCase) || string.Equals(mode, "customFluid", StringComparison.OrdinalIgnoreCase))
            {
                bool fluid = string.Equals(mode, "customFluid", StringComparison.OrdinalIgnoreCase);
                customStructure = MemeNamed(body.Structure, "structure");
                customMemes = MemesNamed(body.Memes);
                // Checked first as the meme dialogs the screen opens would check them, on a new
                // ideoligion that is never added to the game, so a refusal changes nothing.
                Ideo probe = IdeoUtility.MakeEmptyIdeo();
                probe.Fluid = fluid;
                List<string> problems = IdeoEditor.CheckMemes(probe, customStructure, customMemes, true);
                if (problems.Count > 0)
                {
                    throw new CommandException(409, "The meme dialog would not accept these memes: " + string.Join(" ", problems.ToArray()));
                }
                SetupController.Set(page, "presetSelection", Enum.Parse(selection, fluid ? "CustomFluid" : "CustomFixed"));
            }
            else
            {
                throw new CommandException(400, "Field 'mode' must be classic, preset, customFixed, customFluid, or saved.");
            }
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new CommandException(409, "The ideoligion screen did not accept this choice.");
            }
            SetupController.DoNext(page);
            object outcome = null;
            if (customStructure != null)
            {
                Page_ConfigureIdeo custom = Find.WindowStack.WindowOfType<Page_ConfigureIdeo>();
                if (custom == null)
                {
                    SetupController.Changed();
                    throw new CommandException(409, "The custom ideoligion screen did not open.");
                }
                outcome = CreateCustom(custom, customStructure, customMemes);
            }
            SetupController.Changed();
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            if (outcome != null)
            {
                state["outcome"] = outcome;
            }
            return SessionCommandService.Envelope(true, state);
        }

        // The custom screen's Create button (fixed) or the fluid button's own start, then the meme
        // dialogs it opens, answered as their accept buttons are: the structure, then the memes.
        private static object CreateCustom(Page_ConfigureIdeo page, MemeDef structure, List<MemeDef> memes)
        {
            if (page.ideo == null)
            {
                page.SelectOrMakeNewIdeo();
            }
            Dialog_ChooseMemes dialog = Find.WindowStack.WindowOfType<Dialog_ChooseMemes>();
            if (dialog == null)
            {
                // An ideoligion chosen before, reopened: its memes change as an edit would.
                List<string> problems = IdeoEditor.CheckMemes(page.ideo, structure, memes, false);
                if (problems.Count > 0)
                {
                    return Dto.Obj(Dto.Field("memes", "rejected"), Dto.Field("reason", string.Join(" ", problems.ToArray())));
                }
                IdeoEditor.ApplyMemes(page.ideo, structure, memes);
                return Dto.Obj(Dto.Field("memes", "applied"), Dto.Field("regenerated", Regenerated));
            }
            string refusal = IdeoEditor.AnswerMemeDialog(dialog, new List<MemeDef> { structure });
            Dialog_ChooseMemes normal = Find.WindowStack.WindowOfType<Dialog_ChooseMemes>();
            if (refusal == null && normal != null && normal != dialog)
            {
                refusal = IdeoEditor.AnswerMemeDialog(normal, new List<MemeDef> { structure }.Concat(memes).ToList());
            }
            return Dto.Obj(Dto.Field("memes", refusal == null ? "applied" : "rejected"), Dto.Field("reason", refusal));
        }

        // Load saved, as an operation: the screen's Load saved, then the file's button in its list.
        // The game may ask about the file's game version or mods first; the operation then waits.
        private static BridgeResponse LoadSavedIdeoligion(BridgeRequest request, IdeoligionBody body)
        {
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "set_setup_ideoligion", requestId, () =>
            {
                Page_ChooseIdeoPreset page = SetupController.Require<Page_ChooseIdeoPreset>(SetupController.Ideoligion);
                SetupController.RequireRevision(body.ExpectedRevision);
                string file = SavedIdeoligions().FirstOrDefault(name => string.Equals(name, (body.File ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (file == null)
                {
                    throw new CommandException(404, "Field 'file' must name a saved ideoligion; get_setup lists them.");
                }
                Type selection = typeof(Page_ChooseIdeoPreset).GetNestedType("PresetSelection", System.Reflection.BindingFlags.NonPublic);
                System.Reflection.MethodInfo pick = typeof(Dialog_IdeoList_Load).GetMethod("DoFileInteraction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (selection == null || pick == null)
                {
                    throw new CommandException(501, "This game version's saved ideoligions cannot be loaded here; choose the file in game.");
                }
                int own = SetupController.Revision;
                return operation => LoadSaved(operation, page, file, Enum.Parse(selection, "Load"), pick, own);
            }, "saved", body.File, body.ExpectedRevision);
        }

        private static IEnumerable<string> LoadSaved(Operation operation, Page_ChooseIdeoPreset page, string file, object load, System.Reflection.MethodInfo pick, int own)
        {
            SetupController.Continue(operation, own);
            SetupController.Set(page, "presetSelection", load);
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The ideoligion screen did not accept Load saved.");
            }
            SetupController.DoNext(page);
            Dialog_IdeoList_Load list = Find.WindowStack.WindowOfType<Dialog_IdeoList_Load>();
            if (list == null)
            {
                throw new OperationEnded(Operation.Failed, "The saved ideoligion list did not open.");
            }
            List<Window> listed = Find.WindowStack.Windows.ToList();
            pick.Invoke(list, new object[] { file });
            if (Find.WindowStack.IsOpen(page))
            {
                List<Window> asking = Find.WindowStack.Windows.Where(window => !listed.Contains(window)).ToList();
                if (asking.Count == 0)
                {
                    list.Close();
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Failed, "The game could not load '" + file + "'; get_game_log may say why.");
                }
                foreach (string step in AwaitQuestion(operation, asking, asking.Any(window => window is Dialog_ModMismatch) ? "modMismatch" : "versionMismatch", () => !Find.WindowStack.IsOpen(page)))
                {
                    yield return step;
                }
                if (Find.WindowStack.IsOpen(page))
                {
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Declined, "Loading '" + file + "' was not confirmed.");
                }
            }
            SetupController.Changed();
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            state["loaded"] = file;
            operation.Result = state;
        }

        private static IEnumerable<string> SavedIdeoligions()
        {
            return GenFilePaths.AllCustomIdeoFiles.Select(file => System.IO.Path.GetFileNameWithoutExtension(file.Name));
        }

        public static BridgeResponse EditIdeoligion(BridgeRequest request, RouteMatch route)
        {
            EditIdeoBody body = CommandUtil.ReadBody<EditIdeoBody>(request);
            Page_ConfigureIdeo page = SetupController.Require<Page_ConfigureIdeo>(SetupController.CustomIdeoligion);
            SetupController.RequireRevision(body.ExpectedRevision);
            Window busy = Find.WindowStack.Windows.FirstOrDefault(window => !(window is Page) && window.absorbInputAroundWindow || window is FloatMenu);
            if (busy != null)
            {
                throw new CommandException(409, "A " + busy.GetType().Name + " is open on the ideoligion screen; finish it in game first.");
            }
            // Everything named is checked to exist before anything changes.
            MemeDef structure = body.Structure == null ? null : MemeNamed(body.Structure, "structure");
            List<MemeDef> memes = body.Memes == null ? null : MemesNamed(body.Memes);
            CultureDef culture = body.Culture == null ? null : DefNamed<CultureDef>(body.Culture, "culture");
            List<StyleCategoryDef> styles = body.Styles == null ? null : body.Styles.Select(name => IdeoEditor.Styles().FirstOrDefault(style => string.Equals(style.defName, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) ?? Refuse<StyleCategoryDef>("Field 'styles' has an unknown or fixed-only StyleCategoryDef: " + name + ".")).ToList();
            if (styles != null && (styles.Count == 0 || styles.Count > 3 || styles.Distinct().Count() != styles.Count))
            {
                throw new CommandException(400, "Field 'styles' takes one to three different styles, the most dominant first.");
            }
            foreach (KeyValuePair<string, string> symbol in new[] { Pair("name", body.Name), Pair("adjective", body.Adjective), Pair("memberName", body.MemberName) })
            {
                if (symbol.Value != null && (symbol.Value.Trim().Length == 0 || !IdeoEditor.ValidSymbol(symbol.Value.Trim(), IdeoEditor.MaxSymbolLength)))
                {
                    throw new CommandException(400, "Field '" + symbol.Key + "' takes up to " + IdeoEditor.MaxSymbolLength + " letters, digits, spaces, apostrophes, and hyphens, as the symbols dialog does.");
                }
            }
            string randomize = body.Randomize == null ? null : body.Randomize.Trim().ToLowerInvariant();
            if (randomize != null && randomize != "all" && randomize != "precepts")
            {
                throw new CommandException(400, "Field 'randomize' must be all or precepts.");
            }
            List<PreceptEdit> edits = (body.Precepts ?? new PreceptEdit[0]).ToList();
            if (edits.Count > 40 || edits.Any(edit => edit == null))
            {
                throw new CommandException(400, "Field 'precepts' takes up to 40 edits.");
            }
            foreach (PreceptEdit edit in edits)
            {
                ValidateEdit(edit);
            }
            if (page.ideo == null)
            {
                if (structure == null || memes == null)
                {
                    throw new CommandException(409, "No ideoligion has been created on this screen yet; give structure and memes to create one.");
                }
                Ideo probe = IdeoUtility.MakeEmptyIdeo();
                List<string> problems = IdeoEditor.CheckMemes(probe, structure, memes, true);
                if (problems.Count > 0)
                {
                    throw new CommandException(409, "The meme dialog would not accept these memes: " + string.Join(" ", problems.ToArray()));
                }
            }

            List<object> outcomes = new List<object>();
            Ideo ideo = page.ideo;
            bool stopped = false;
            if (ideo == null)
            {
                outcomes.Add(Outcome("create", CreateCustom(page, structure, memes)));
                ideo = page.ideo;
                structure = null;
                memes = null;
            }
            if (randomize != null && ideo != null)
            {
                IdeoEditor.Randomize(ideo, randomize == "all");
                outcomes.Add(Dto.Obj(Dto.Field("part", "randomize"), Dto.Field("outcome", "applied"), Dto.Field("randomized", randomize)));
            }
            if ((structure != null || memes != null) && ideo != null)
            {
                MemeDef wantedStructure = structure ?? ideo.memes.First(meme => meme.category == MemeCategory.Structure);
                List<MemeDef> wantedMemes = memes ?? ideo.memes.Where(meme => meme.category == MemeCategory.Normal).ToList();
                if (wantedStructure == ideo.memes.FirstOrDefault(meme => meme.category == MemeCategory.Structure) && new HashSet<MemeDef>(wantedMemes).SetEquals(ideo.memes.Where(meme => meme.category == MemeCategory.Normal)))
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "alreadySatisfied")));
                }
                else
                {
                    List<string> problems = IdeoEditor.CheckMemes(ideo, wantedStructure, wantedMemes, false);
                    if (problems.Count > 0)
                    {
                        outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "rejected"), Dto.Field("reason", string.Join(" ", problems.ToArray()))));
                        stopped = true;
                    }
                    else
                    {
                        IdeoEditor.ApplyMemes(ideo, wantedStructure, wantedMemes);
                        outcomes.Add(Dto.Obj(Dto.Field("part", "memes"), Dto.Field("outcome", "applied"), Dto.Field("regenerated", Regenerated)));
                    }
                }
            }
            if (!stopped && ideo != null)
            {
                if (culture != null)
                {
                    bool same = ideo.culture == culture;
                    if (!same)
                    {
                        IdeoEditor.SetCulture(ideo, culture);
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", "culture"), Dto.Field("outcome", same ? "alreadySatisfied" : "applied")));
                }
                if (styles != null)
                {
                    bool same = ideo.thingStyleCategories.Select(style => style.category).SequenceEqual(styles);
                    if (!same)
                    {
                        IdeoEditor.SetStyles(ideo, styles);
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", "styles"), Dto.Field("outcome", same ? "alreadySatisfied" : "applied")));
                }
                Dictionary<int, int> renumbered = new Dictionary<int, int>();
                foreach (PreceptEdit edit in edits)
                {
                    outcomes.Add(ApplyEdit(ideo, edit, renumbered));
                }
                foreach (KeyValuePair<string, string> symbol in new[] { Pair("name", body.Name), Pair("adjective", body.Adjective), Pair("memberName", body.MemberName) })
                {
                    if (symbol.Value == null)
                    {
                        continue;
                    }
                    string text = symbol.Value.Trim();
                    string current = symbol.Key == "name" ? ideo.name : symbol.Key == "adjective" ? ideo.adjective : ideo.memberName;
                    if (current == text)
                    {
                        outcomes.Add(Dto.Obj(Dto.Field("part", symbol.Key), Dto.Field("outcome", "alreadySatisfied")));
                        continue;
                    }
                    if (symbol.Key == "name")
                    {
                        ideo.name = text;
                    }
                    else if (symbol.Key == "adjective")
                    {
                        ideo.adjective = text;
                    }
                    else
                    {
                        ideo.memberName = text;
                        ideo.MakeMemeberNamePluralDirty();
                    }
                    outcomes.Add(Dto.Obj(Dto.Field("part", symbol.Key), Dto.Field("outcome", "applied")));
                }
            }
            SetupController.Changed();
            if (body.Done == true && !stopped && ideo != null)
            {
                string blocker = IdeoEditor.Blocker(ideo);
                if (blocker != null)
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "rejected"), Dto.Field("reason", blocker)));
                }
                else if (!SetupController.CanNext(page))
                {
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "rejected"), Dto.Field("reason", "The ideoligion screen did not accept it; see the message in game.")));
                }
                else
                {
                    SetupController.DoNext(page);
                    outcomes.Add(Dto.Obj(Dto.Field("part", "done"), Dto.Field("outcome", "applied")));
                }
                SetupController.Changed();
            }
            Dictionary<string, object> state = (Dictionary<string, object>)State();
            state["outcomes"] = outcomes.ToArray();
            return SessionCommandService.Envelope(true, state);
        }

        // What the meme dialog's accept regenerates on an ideoligion that already has precepts.
        private static readonly string[] Regenerated = { "precepts", "styles", "description" };

        private static object Outcome(string part, object created)
        {
            Dictionary<string, object> outcome = (Dictionary<string, object>)created;
            outcome["part"] = part;
            outcome["outcome"] = outcome["memes"];
            outcome.Remove("memes");
            return outcome;
        }

        private static KeyValuePair<string, string> Pair(string key, string value)
        {
            return new KeyValuePair<string, string>(key, value);
        }

        private static T Refuse<T>(string message)
        {
            throw new CommandException(400, message);
        }

        private static void ValidateEdit(PreceptEdit edit)
        {
            int actions = (edit.Add != null ? 1 : 0) + (edit.ReplaceWith != null ? 1 : 0) + (edit.Regenerate == true ? 1 : 0) + (edit.Rename != null ? 1 : 0) + (edit.Remove == true ? 1 : 0);
            if (actions != 1)
            {
                throw new CommandException(400, "Each precept edit does one thing: add, or with id one of replaceWith, regenerate, rename, or remove.");
            }
            if (edit.Add == null && !edit.Id.HasValue)
            {
                throw new CommandException(400, "Precept edits other than add need the precept's id from get_setup.");
            }
            if (edit.Add != null)
            {
                DefNamed<PreceptDef>(edit.Add.Precept, "add.precept");
                if (edit.Add.Pattern != null)
                {
                    DefNamed<RitualPatternDef>(edit.Add.Pattern, "add.pattern");
                }
                if (edit.Add.Thing != null)
                {
                    DefNamed<ThingDef>(edit.Add.Thing, "add.thing");
                }
                if (edit.Add.Weapons != null)
                {
                    DefNamed<WeaponClassPairDef>(edit.Add.Weapons, "add.weapons");
                }
                if (edit.Add.Xenotype != null)
                {
                    DefNamed<XenotypeDef>(edit.Add.Xenotype, "add.xenotype");
                }
            }
            if (edit.ReplaceWith != null)
            {
                DefNamed<PreceptDef>(edit.ReplaceWith, "replaceWith");
            }
            if (edit.Rename != null && (edit.Rename.Trim().Length == 0 || !IdeoEditor.ValidSymbol(edit.Rename.Trim(), IdeoEditor.MaxPreceptNameLength)))
            {
                throw new CommandException(400, "Field 'rename' takes up to " + IdeoEditor.MaxPreceptNameLength + " letters, digits, spaces, apostrophes, and hyphens, as the precept dialog does.");
            }
        }

        // One precept edit, as the screen's Add buttons and precept boxes offer it.
        // Regenerating or replacing a precept gives it a new id, as the game does; later edits in the
        // same request that name the old id follow it.
        private static object ApplyEdit(Ideo ideo, PreceptEdit edit, Dictionary<int, int> renumbered)
        {
            if (edit.Add != null)
            {
                PreceptDef def = DefNamed<PreceptDef>(edit.Add.Precept, "add.precept");
                List<PreceptOption> matching = IdeoEditor.AddOptions(ideo).Where(option => option.Def == def
                    && (edit.Add.Pattern == null || (option.Pattern != null && option.Pattern.defName == edit.Add.Pattern.Trim()))
                    && (edit.Add.Thing == null || (option.Thing != null && option.Thing.defName == edit.Add.Thing.Trim()))
                    && (edit.Add.Weapons == null || (option.Weapons != null && option.Weapons.defName == edit.Add.Weapons.Trim()))
                    && (edit.Add.Xenotype == null || (option.Xenotype != null && option.Xenotype.defName == edit.Add.Xenotype.Trim()))).ToList();
                List<PreceptOption> usable = matching.Where(option => option.Reason == null).ToList();
                Precept occupying = def.issue == null || matching.Count > 0 || !ideo.HasMaxPreceptsForIssue(def.issue) ? null : ideo.PreceptsListForReading.FirstOrDefault(other => other.def.issue == def.issue);
                if (usable.Count == 0 && occupying != null)
                {
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", "Its issue already has " + occupying.def.defName + " (id " + occupying.Id + "); replace that one with replaceWith."));
                }
                if (usable.Count == 0)
                {
                    string reason = matching.Count > 0 ? matching[0].Reason : "The screen does not offer " + def.defName + (edit.Add.Thing != null || edit.Add.Weapons != null || edit.Add.Xenotype != null || edit.Add.Pattern != null ? " with that choice" : "") + " now; get_setup with include preceptOptions lists what it offers.";
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", reason));
                }
                if (usable.Count > 1 && (def.preceptClass == typeof(Precept_Weapon) || def.preceptClass == typeof(Precept_Xenotype) || usable.Any(option => option.Thing != null)))
                {
                    return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "rejected"), Dto.Field("reason", "Choose which " + (usable[0].Thing != null ? "thing" : usable[0].Weapons != null ? "weapons" : "xenotype") + " with it; get_setup with include preceptOptions lists them."));
                }
                Precept added = IdeoEditor.Add(ideo, usable[0]);
                return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("add", def.defName), Dto.Field("outcome", "applied"), Dto.Field("id", added.Id), Dto.Field("label", IdeoEditor.Label(added)));
            }
            int id = edit.Id.Value;
            while (renumbered.ContainsKey(id))
            {
                id = renumbered[id];
            }
            Precept precept = ideo.PreceptsListForReading.FirstOrDefault(candidate => candidate.Id == id);
            if (precept == null)
            {
                return Dto.Obj(Dto.Field("part", "precept"), Dto.Field("id", edit.Id.Value), Dto.Field("outcome", "rejected"), Dto.Field("reason", "This ideoligion has no precept " + edit.Id.Value + "; get_setup lists its precepts."));
            }
            Dictionary<string, object> result = Dto.Obj(Dto.Field("part", "precept"), Dto.Field("id", precept.Id), Dto.Field("precept", precept.def.defName));
            if (edit.ReplaceWith != null)
            {
                PreceptDef with = DefNamed<PreceptDef>(edit.ReplaceWith, "replaceWith");
                if (with == precept.def)
                {
                    result["outcome"] = "alreadySatisfied";
                    return result;
                }
                PreceptOption option = IdeoEditor.Replacements(ideo, precept).FirstOrDefault(candidate => candidate.Def == with);
                if (option == null || option.Reason != null)
                {
                    result["outcome"] = "rejected";
                    result["reason"] = option != null ? option.Reason : "The precept's box does not offer " + with.defName + "; replaceWith lists what it offers.";
                    return result;
                }
                Precept replacement = IdeoEditor.Replace(ideo, precept, with);
                renumbered[precept.Id] = replacement.Id;
                result["outcome"] = "applied";
                result["replacedBy"] = Dto.Obj(Dto.Field("id", replacement.Id), Dto.Field("precept", with.defName));
                return result;
            }
            if (edit.Regenerate == true)
            {
                if (!precept.CanRegenerate)
                {
                    result["outcome"] = "rejected";
                    result["reason"] = "The precept's box does not offer Regenerate for this precept.";
                    return result;
                }
                int before = precept.Id;
                IdeoEditor.Regenerate(ideo, precept);
                renumbered[before] = precept.Id;
                result["outcome"] = "applied";
                result["newId"] = precept.Id;
                result["label"] = IdeoEditor.Label(precept);
                return result;
            }
            if (edit.Rename != null)
            {
                string name = edit.Rename.Trim();
                if (precept.def.preceptClass == typeof(Precept))
                {
                    result["outcome"] = "rejected";
                    result["reason"] = "Precepts by issue take no name.";
                    return result;
                }
                if (precept.LabelCap.ToString() == name)
                {
                    result["outcome"] = "alreadySatisfied";
                    return result;
                }
                IdeoEditor.Rename(ideo, precept, name);
                result["outcome"] = "applied";
                return result;
            }
            string refusal = IdeoEditor.WhyNotRemovable(ideo, precept);
            if (refusal != null)
            {
                result["outcome"] = "rejected";
                result["reason"] = refusal;
                return result;
            }
            ideo.RemovePrecept(precept);
            result["outcome"] = "applied";
            return result;
        }

        private static MemeDef MemeNamed(string name, string field)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandException(400, "Field '" + field + "' must name a MemeDef; get_setup lists them.");
            }
            return DefNamed<MemeDef>(name, field);
        }

        private static List<MemeDef> MemesNamed(string[] names)
        {
            if (names == null || names.Length == 0)
            {
                throw new CommandException(400, "Field 'memes' must list the normal memes; get_setup lists them.");
            }
            List<MemeDef> memes = names.Select(name => DefNamed<MemeDef>(name, "memes")).ToList();
            if (memes.Distinct().Count() != memes.Count)
            {
                throw new CommandException(400, "Field 'memes' lists a meme twice.");
            }
            return memes;
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
                int[] order = body.Order;
                if (order != null && (order.Length != count || order.Distinct().Count() != count || order.Any(slot => slot < 0 || slot >= count)))
                {
                    throw new CommandException(400, "Field 'order' lists every slot from 0 to " + (count - 1) + " once, in the new order; the first " + Find.GameInitData.startingPawnCount + " start.");
                }
                List<Preference> preferences = (body.Preferences ?? new PreferenceEntry[0]).Select(entry => ParsePreference(entry, count)).ToList();
                if (rerolls.Count == 0 && preferences.Count == 0 && order == null && (body.Names == null || body.Names.Length == 0))
                {
                    throw new CommandException(400, "Give order, preferences, rerolls, or names.");
                }
                int own = SetupController.Revision;
                NameEntry[] names = body.Names ?? new NameEntry[0];
                return operation => Rerolls(operation, order, preferences, rerolls, names, own);
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
        private sealed class Preference
        {
            public int Slot;
            public DevelopmentalStage? Stage;
            public XenotypeDef Xenotype;
            public bool AnyNonArchite;
        }

        // The colonist card's age and xenotype buttons (Biotech), which set the slot's generation
        // request; both reroll the colonist, except choosing any non-archite xenotype.
        private static Preference ParsePreference(PreferenceEntry entry, int count)
        {
            if (entry == null || !entry.Slot.HasValue || entry.Slot.Value < 0 || entry.Slot.Value >= count)
            {
                throw new CommandException(400, "Each preference needs a slot from 0 to " + (count - 1) + ".");
            }
            if (!ModsConfig.BiotechActive)
            {
                throw new CommandException(409, "The colonist screen offers age and xenotype choices only with Biotech.");
            }
            Preference preference = new Preference { Slot = entry.Slot.Value };
            if (!string.IsNullOrWhiteSpace(entry.Stage))
            {
                DevelopmentalStage stage;
                string stageName = entry.Stage.Trim();
                if (!new[] { "Adult", "Child", "Baby" }.Contains(stageName, StringComparer.OrdinalIgnoreCase) || !Enum.TryParse(stageName, true, out stage))
                {
                    throw new CommandException(400, "Field 'stage' must be Adult, Child, or Baby.");
                }
                if (stage != DevelopmentalStage.Adult && !ScenarioUtility.AllowsChildSelection(Find.Scenario))
                {
                    throw new CommandException(409, "MessageDevelopmentalStageSelectionDisabledByScenario".Translate().ToString());
                }
                preference.Stage = stage;
            }
            if (!string.IsNullOrWhiteSpace(entry.Xenotype))
            {
                if (string.Equals(entry.Xenotype.Trim(), "anyNonArchite", StringComparison.OrdinalIgnoreCase))
                {
                    preference.AnyNonArchite = true;
                }
                else
                {
                    preference.Xenotype = DefNamed<XenotypeDef>(entry.Xenotype, "xenotype");
                }
            }
            if (!preference.Stage.HasValue && preference.Xenotype == null && !preference.AnyNonArchite)
            {
                throw new CommandException(400, "Each preference sets stage, xenotype, or both.");
            }
            return preference;
        }

        // As the screen's drag to reorder does, one colonist at a time.
        private static void Reorder(int[] order)
        {
            List<Pawn> pawns = Find.GameInitData.startingAndOptionalPawns;
            List<Pawn> wanted = order.Select(slot => pawns[slot]).ToList();
            for (int to = 0; to < wanted.Count; to++)
            {
                int from = pawns.IndexOf(wanted[to]);
                if (from == to)
                {
                    continue;
                }
                Pawn pawn = pawns[from];
                pawns.Insert(to, pawn);
                pawns.RemoveAt(from < to ? from : from + 1);
                StartingPawnUtility.ReorderRequests(from, to);
            }
        }

        // Returns whether the colonist was rerolled.
        private static bool Prefer(Preference preference)
        {
            PawnGenerationRequest request = StartingPawnUtility.GetGenerationRequest(preference.Slot);
            bool reroll = false;
            bool changed = false;
            if (preference.Stage.HasValue && !request.AllowedDevelopmentalStages.Has(preference.Stage.Value))
            {
                request.AllowedDevelopmentalStages = preference.Stage.Value;
                request.AllowDowned = preference.Stage.Value == DevelopmentalStage.Baby;
                reroll = true;
                changed = true;
            }
            if (preference.AnyNonArchite && (request.ForcedXenotype != null || request.ForcedCustomXenotype != null))
            {
                request.ForcedXenotype = null;
                request.ForcedCustomXenotype = null;
                request.AllowedXenotypes = DefDatabase<XenotypeDef>.AllDefs.Where(xenotype => !xenotype.Archite && xenotype != XenotypeDefOf.Baseliner).ToList();
                request.ForceBaselinerChance = 0.5f;
                changed = true;
            }
            else if (preference.Xenotype != null && request.ForcedXenotype != preference.Xenotype)
            {
                request.ForcedXenotype = preference.Xenotype;
                request.ForcedCustomXenotype = null;
                request.AllowedXenotypes = null;
                request.ForceBaselinerChance = 0f;
                reroll = true;
                changed = true;
            }
            if (changed)
            {
                StartingPawnUtility.SetGenerationRequest(preference.Slot, request);
            }
            if (reroll)
            {
                StartingPawnUtility.RandomizePawn(preference.Slot);
            }
            return reroll;
        }

        // Order first; preferences, rerolls, and names then refer to slots in the new order.
        private static IEnumerable<string> Rerolls(Operation operation, int[] order, List<Preference> preferences, List<Reroll> rerolls, NameEntry[] names, int own)
        {
            if (order != null)
            {
                SetupController.Continue(operation, own);
                Reorder(order);
                own = SetupController.Changed();
                operation.Result = RerollResult(rerolls);
                yield return "reordering";
            }
            foreach (Preference preference in preferences)
            {
                SetupController.Continue(operation, own);
                Prefer(preference);
                own = SetupController.Changed();
                operation.Result = RerollResult(rerolls);
                yield return "settingPreferences";
            }
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
            Dialog_ModMismatch mods = asking.OfType<Dialog_ModMismatch>().FirstOrDefault();
            operation.State = Operation.AwaitingConfirmation;
            operation.ConfirmationId = id;
            operation.Confirmation = Dto.Obj(
                Dto.Field("confirmationId", id),
                Dto.Field("kind", kind),
                Dto.Field("message", box != null ? box.text.ToString().StripTags() : mods != null ? "The file was made with a different mod list. Accepting loads it anyway; declining goes back." : "The game is asking before it goes on."),
                Dto.Field("answeredBy", BridgeRuntime.UnattendedSetup ? "agent" : "player"),
                Dto.Field("dialogs", asking.Select(window => window.GetType().Name).ToArray()));
            if (box != null || mods != null)
            {
                operation.Answer = accept =>
                {
                    SetupController.Observe();
                    if (SetupController.Revision != asked)
                    {
                        return "The setup changed after this was asked, so the answer no longer applies.";
                    }
                    // As the button does: its action, then the dialog closes. The mod list dialog's
                    // accept is Load anyway, and its decline Go back.
                    Action action = box != null ? (accept ? box.buttonAAction : box.buttonBAction) : accept ? (Action)SetupController.Get(mods, "loadAction") : null;
                    if (action != null)
                    {
                        action();
                    }
                    (box ?? (Window)mods).Close();
                    return null;
                };
            }
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

        // The custom panel's settings with the values it would start from or shows now.
        private static object[] CustomSettingsShown(Page page)
        {
            DifficultyDef current = SetupController.Get(page, "difficulty") as DifficultyDef;
            Difficulty values = current != null && current.isCustom ? (Difficulty)SetupController.Get(page, "difficultyValues") : new Difficulty(DifficultyDefOf.Rough);
            return DifficultySettings.All.Select(setting => setting.ToDto(values)).ToArray();
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
                Dto.Field("cannotDo", DefDatabase<WorkTypeDef>.AllDefs.Where(pawn.WorkTypeIsDisabled).Select(work => work.defName).ToArray()),
                Dto.Field("stage", ModsConfig.BiotechActive ? pawn.DevelopmentalStage.ToString() : null),
                Dto.Field("xenotype", !ModsConfig.BiotechActive || pawn.genes == null ? null : pawn.genes.UniqueXenotype ? pawn.genes.xenotypeName : pawn.genes.Xenotype.defName),
                Dto.Field("preference", ModsConfig.BiotechActive ? PreferenceShown(slot) : null))).ToArray();
        }

        private static object PreferenceShown(int slot)
        {
            PawnGenerationRequest request = StartingPawnUtility.GetGenerationRequest(slot);
            return Dto.Obj(
                Dto.Field("stage", request.AllowedDevelopmentalStages.ToString()),
                Dto.Field("xenotype", request.ForcedXenotype != null ? request.ForcedXenotype.defName : request.ForcedCustomXenotype != null ? request.ForcedCustomXenotype.name : request.AllowedXenotypes != null ? "anyNonArchite" : null));
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
            if (entry.Xenotypes != null)
            {
                if (!ModsConfig.BiotechActive)
                {
                    throw new CommandException(409, "Xenotypes need Biotech.");
                }
                criteria.Xenotypes = DefList<XenotypeDef>(entry.Xenotypes, "xenotypes");
            }
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
                throw new CommandException(400, "A reroll needs at least one criterion: skills, traits, excludeTraits, canDo, xenotypes, minAge, or maxAge.");
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
            string rerolls = string.Join(";", (body.Rerolls ?? new RerollEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + Join(entry.Traits) + ":" + Join(entry.ExcludeTraits) + ":" + Join(entry.CanDo) + ":" + Join(entry.Xenotypes) + ":" + entry.MinAge + "-" + entry.MaxAge + ":" + entry.MaxAttempts + ":" + string.Join(",", (entry.Skills ?? new SkillEntry[0]).Select(skill => skill == null ? "-" : skill.Skill + skill.Min + skill.Passion).ToArray())).ToArray());
            string names = string.Join(";", (body.Names ?? new NameEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + entry.First + "/" + entry.Nick + "/" + entry.Last).ToArray());
            string order = body.Order == null ? "-" : string.Join(",", body.Order.Select(slot => slot.ToString()).ToArray());
            string preferences = string.Join(";", (body.Preferences ?? new PreferenceEntry[0]).Select(entry => entry == null ? "-" : entry.Slot + ":" + entry.Stage + ":" + entry.Xenotype).ToArray());
            return rerolls + "|" + names + "|" + order + "|" + preferences + "|" + body.ExpectedRevision;
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
            [DataMember(Name = "custom")] public CustomBody Custom { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class CustomBody
        {
            [DataMember(Name = "base")] public string Base { get; set; }
            [DataMember(Name = "settings")] public SettingEntry[] Settings { get; set; }
        }

        [DataContract]
        private sealed class SettingEntry
        {
            [DataMember(Name = "setting")] public string Setting { get; set; }
            [DataMember(Name = "value")] public float? Value { get; set; }
            [DataMember(Name = "enabled")] public bool? Enabled { get; set; }
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
            [DataMember(Name = "minSettlementDistance")] public float? MinSettlementDistance { get; set; }
            [DataMember(Name = "maxSettlementDistance")] public float? MaxSettlementDistance { get; set; }
            [DataMember(Name = "settlementRelations")] public string[] SettlementRelations { get; set; }
            [DataMember(Name = "avoidEncroaching")] public bool? AvoidEncroaching { get; set; }
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
            [DataMember(Name = "memes")] public string[] Memes { get; set; }
            [DataMember(Name = "file")] public string File { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class EditIdeoBody
        {
            [DataMember(Name = "randomize")] public string Randomize { get; set; }
            [DataMember(Name = "structure")] public string Structure { get; set; }
            [DataMember(Name = "memes")] public string[] Memes { get; set; }
            [DataMember(Name = "culture")] public string Culture { get; set; }
            [DataMember(Name = "styles")] public string[] Styles { get; set; }
            [DataMember(Name = "precepts")] public PreceptEdit[] Precepts { get; set; }
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "adjective")] public string Adjective { get; set; }
            [DataMember(Name = "memberName")] public string MemberName { get; set; }
            [DataMember(Name = "done")] public bool? Done { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class PreceptEdit
        {
            [DataMember(Name = "id")] public int? Id { get; set; }
            [DataMember(Name = "add")] public PreceptAdd Add { get; set; }
            [DataMember(Name = "replaceWith")] public string ReplaceWith { get; set; }
            [DataMember(Name = "regenerate")] public bool? Regenerate { get; set; }
            [DataMember(Name = "rename")] public string Rename { get; set; }
            [DataMember(Name = "remove")] public bool? Remove { get; set; }
        }

        [DataContract]
        private sealed class PreceptAdd
        {
            [DataMember(Name = "precept")] public string Precept { get; set; }
            [DataMember(Name = "pattern")] public string Pattern { get; set; }
            [DataMember(Name = "thing")] public string Thing { get; set; }
            [DataMember(Name = "weapons")] public string Weapons { get; set; }
            [DataMember(Name = "xenotype")] public string Xenotype { get; set; }
        }

        [DataContract]
        private sealed class PawnsBody
        {
            [DataMember(Name = "order")] public int[] Order { get; set; }
            [DataMember(Name = "preferences")] public PreferenceEntry[] Preferences { get; set; }
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
            [DataMember(Name = "xenotypes")] public string[] Xenotypes { get; set; }
            [DataMember(Name = "minAge")] public int? MinAge { get; set; }
            [DataMember(Name = "maxAge")] public int? MaxAge { get; set; }
            [DataMember(Name = "maxAttempts")] public int? MaxAttempts { get; set; }
        }

        [DataContract]
        private sealed class PreferenceEntry
        {
            [DataMember(Name = "slot")] public int? Slot { get; set; }
            [DataMember(Name = "stage")] public string Stage { get; set; }
            [DataMember(Name = "xenotype")] public string Xenotype { get; set; }
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
