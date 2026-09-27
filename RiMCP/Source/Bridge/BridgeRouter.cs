using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Command;
using RiMCP.Read;
using RiMCP.Shared;

namespace RiMCP.Bridge
{
    internal static class BridgeRouter
    {
        private static readonly Dictionary<string, BridgeRoute.Handler> Handlers = new Dictionary<string, BridgeRoute.Handler>
        {
            { "game-context", GameReadService.GetGameContext },
            { "colony-status", GameReadService.GetColonyStatus },
            { "pawns", PawnReadService.ListPawns },
            { "pawn", PawnReadService.GetPawn },
            { "resources", ResourceReadService.ListResources },
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
            { "tools", ToolReadService.GetTools }
        };

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
                        if (!route.MethodMatches("GET") && !BridgeRuntime.AllowColonyChanges)
                        {
                            return BridgeResponse.Error(403, "Colony changes are disabled. Enable \"Allow colony changes\" in RimWorld's RiMCP mod settings to use this command.");
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

        private static BridgeRoute[] CreateRoutes()
        {
            return ToolManifest.All
                .Select(spec => BridgeRoute.Create(spec.Method, spec.PathTemplate, Handlers[spec.HandlerId]))
                .ToArray();
        }

    }
}
