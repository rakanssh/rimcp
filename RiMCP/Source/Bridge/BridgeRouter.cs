using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Command;
using RiMCP.Read;
using RiMCP.Settings;
using RiMCP.Shared;

namespace RiMCP.Bridge
{
    internal static class BridgeRouter
    {
        private static readonly Dictionary<string, BridgeRoute.Handler> Handlers = new Dictionary<string, BridgeRoute.Handler>
        {
            { "game-context", GameReadService.GetGameContext },
            { "mods", ModReadService.ListMods },
            { "mod", ModReadService.GetMod },
            { "game-log", GameLogReadService.GetLog },
            { "game-settings", GameSettingsReadService.GetSettings },
            { "set-game-settings", GameSettingsCommandService.SetSettings },
            { "colony-status", GameReadService.GetColonyStatus },
            { "pawns", PawnReadService.ListPawns },
            { "pawn", PawnReadService.GetPawn },
            { "resources", ResourceReadService.ListResources },
            { "things", ThingReadService.FindThings },
            { "map-view", MapViewReadService.GetMapView },
            { "designate", DesignationCommandService.Designate },
            { "set-zone", ZoneCommandService.SetZone },
            { "set-storage", ZoneCommandService.SetStorage },
            { "set-growing-zone", ZoneCommandService.SetGrowingZone },
            { "check-placement", BuildService.CheckPlacement },
            { "place-blueprint", BuildService.PlaceBlueprint },
            { "set-area", ZoneCommandService.SetArea },
            { "order-pawn", PawnOrderCommandService.OrderPawn },
            { "work", WorkReadService.ListWork },
            { "production", ProductionReadService.ListProduction },
            { "bill", ProductionReadService.GetBill },
            { "set-bill", ProductionCommandService.SetBill },
            { "workshops", ProductionReadService.ListWorkshops },
            { "workshop", ProductionReadService.GetWorkshop },
            { "add-bill-to-workshop", ProductionCommandService.AddBillToWorkshop },
            { "zones", ZoneReadService.ListZones },
            { "zone", ZoneReadService.GetZone },
            { "environment", EnvironmentReadService.GetEnvironment },
            { "power", PowerReadService.GetPower },
            { "threats", ThreatReadService.ListThreats },
            { "research", ResearchReadService.GetResearch },
            { "ideoligions", IdeoligionReadService.ListIdeoligions },
            { "ideoligion", IdeoligionReadService.GetIdeoligion },
            { "quests", QuestWorldReadService.ListQuests },
            { "buildings", BuildingReadService.ListBuildings },
            { "building", BuildingReadService.GetBuilding },
            { "world", QuestWorldReadService.ListWorld },
            { "defs-search", DefReadService.SearchDefs },
            { "def", DefReadService.GetDef },
            { "set-pawn-drafted", PawnCommandService.SetDrafted },
            { "set-pawn-work-priority", PawnCommandService.SetWorkPriority },
            { "set-pawn-assignment", PawnAssignmentCommandService.SetAssignment },
            { "designate-animal", AnimalCommandService.SetDesignation },
            { "set-animal-training", AnimalCommandService.SetTraining },
            { "set-research-current", ResearchCommandService.SetCurrent },
            { "set-game-speed", TimeCommandService.SetGameSpeed },
            { "advance-time", TimeCommandService.StartAdvance },
            { "get-advance", TimeCommandService.GetAdvance },
            { "get-operation", SessionCommandService.GetOperation },
            { "cancel-operation", SessionCommandService.CancelOperation },
            { "list-saves", SessionCommandService.ListSaves },
            { "save-game", SessionCommandService.SaveGame },
            { "load-game", SessionCommandService.LoadGame },
            { "leave-game", SessionCommandService.LeaveGame },
            { "list-scenarios", SetupCommandService.ListScenarios },
            { "get-setup", SetupCommandService.GetSetup },
            { "find-starting-sites", SetupCommandService.FindStartingSites },
            { "start-setup", SetupCommandService.StartSetup },
            { "cancel-setup", SetupCommandService.CancelSetup },
            { "setup-back", SetupCommandService.SetupBack },
            { "set-setup-storyteller", SetupCommandService.SetStoryteller },
            { "generate-world", SetupCommandService.GenerateWorld },
            { "choose-starting-site", SetupCommandService.ChooseStartingSite },
            { "set-setup-ideoligion", SetupCommandService.SetIdeoligion },
            { "edit-setup-ideoligion", SetupCommandService.EditIdeoligion },
            { "set-starting-pawns", SetupCommandService.SetStartingPawns },
            { "start-game", SetupCommandService.StartGame },
            { "answer-confirmation", SetupCommandService.AnswerConfirmation },
            { "tools", ToolReadService.GetTools }
        };

        // While RimWorld loads, saves, or changes games, the game may be half built, so only these
        // answer: they read the operation registry, not the game.
        private static readonly HashSet<string> AnswerWhileBusy = new HashSet<string> { "get-operation", "cancel-operation", "tools" };

        private static readonly BridgeRoute[] Routes = CreateRoutes();

        static BridgeRouter()
        {
            ToolManifest.Validate(Handlers.Keys);
        }

        public static BridgeResponse Handle(BridgeRequest bridgeRequest)
        {
            try
            {
                bool pathMatched = false;
                foreach (BridgeRoute route in Routes)
                {
                    RouteMatch match;
                    if (!route.TryMatchPath(bridgeRequest.Uri.AbsolutePath, out match))
                    {
                        continue;
                    }

                    pathMatched = true;
                    if (route.MethodMatches(bridgeRequest.Method))
                    {
                        AccessState access = BridgeRuntime.Access;
                        if (!access.Enabled)
                        {
                            return BridgeResponse.Error(403, "RimMCP is disabled in its settings.");
                        }
                        if (!access.Allows(route.Spec.Group))
                        {
                            return BridgeResponse.Error(403, (route.Spec.Name ?? route.Template) + " is disabled: the " + ToolGroups.Label(route.Spec.Group) + " group is off in RimMCP settings (access: " + AccessPresets.Label(access.Preset) + ").");
                        }
                        if (!AnswerWhileBusy.Contains(route.Spec.HandlerId) && GameIsChanging())
                        {
                            Operation changing = OperationRunner.Active;
                            return BridgeResponse.Error(503, "RimWorld is loading, saving, or changing games" + (changing == null ? "" : " (operation " + changing.Id + ", " + changing.Tool + ")") + ", so the game cannot be read or changed right now. Check progress with get_operation, then try again.");
                        }
                        if (!route.MethodMatches("GET"))
                        {
                            if (GameContext.InMultiplayer)
                            {
                                return BridgeResponse.Error(409, "RimMCP does not change the game or control time during a Multiplayer session, so every player's game stays in sync. Reads still work.");
                            }
                            string expected = CommandUtil.ReadBody<LoadGuardBody>(bridgeRequest).ExpectedLoadId;
                            string loaded = GameContext.LoadId;
                            if (expected != null && expected != loaded)
                            {
                                return BridgeResponse.Error(409, "This command expected load " + expected + ", but the loaded game is " + (loaded ?? "none") + ". Another save was loaded; read the colony again before acting.");
                            }
                        }
                        return route.Handle(bridgeRequest, match);
                    }
                }

                return pathMatched ? BridgeResponse.Error(405, "Method not allowed for endpoint.") : BridgeResponse.Error(404, "Unknown endpoint.");
            }
            catch (BridgeException ex)
            {
                return BridgeResponse.Error(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool GameIsChanging()
        {
            return OperationRunner.ReplacingGame || Verse.LongEventHandler.AnyEventNowOrWaiting;
        }

        [DataContract]
        private sealed class LoadGuardBody
        {
            [DataMember(Name = "expectedLoadId")]
            public string ExpectedLoadId { get; set; }
        }

        private static BridgeRoute[] CreateRoutes()
        {
            return ToolManifest.All
                .Select(spec => BridgeRoute.Create(spec, Handlers[spec.HandlerId]))
                .ToArray();
        }

    }
}
