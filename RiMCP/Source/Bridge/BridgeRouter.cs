using System;
using RiMCP.Command;
using RiMCP.Read;

namespace RiMCP.Bridge
{
    internal static class BridgeRouter
    {
        private static readonly BridgeRoute[] Routes =
        {
            BridgeRoute.Get("/v1/game-context", GameReadService.GetGameContext),
            BridgeRoute.Get("/v1/colony-status", GameReadService.GetColonyStatus),
            BridgeRoute.Get("/v1/pawns", PawnReadService.ListPawns),
            BridgeRoute.Get("/v1/pawns/{id}", PawnReadService.GetPawn),
            BridgeRoute.Get("/v1/resources", ResourceReadService.ListResources),
            BridgeRoute.Get("/v1/work", WorkReadService.ListWork),
            BridgeRoute.Get("/v1/production", ProductionReadService.ListProduction),
            BridgeRoute.Get("/v1/bills/{id}", ProductionReadService.GetBill),
            BridgeRoute.Get("/v1/workshops", ProductionReadService.ListWorkshops),
            BridgeRoute.Get("/v1/workshops/{id}", ProductionReadService.GetWorkshop),
            BridgeRoute.Get("/v1/zones", ZoneReadService.ListZones),
            BridgeRoute.Get("/v1/zones/{id}", ZoneReadService.GetZone),
            BridgeRoute.Get("/v1/environment", EnvironmentReadService.GetEnvironment),
            BridgeRoute.Get("/v1/power", PowerReadService.GetPower),
            BridgeRoute.Get("/v1/threats", ThreatReadService.ListThreats),
            BridgeRoute.Get("/v1/research", ResearchReadService.GetResearch),
            BridgeRoute.Get("/v1/quests", QuestWorldReadService.ListQuests),
            BridgeRoute.Get("/v1/buildings", BuildingReadService.ListBuildings),
            BridgeRoute.Get("/v1/buildings/{id}", BuildingReadService.GetBuilding),
            BridgeRoute.Get("/v1/world", QuestWorldReadService.ListWorld),
            BridgeRoute.Get("/v1/defs/search", DefReadService.SearchDefs),
            BridgeRoute.Get("/v1/defs/{defName}", DefReadService.GetDef),
            BridgeRoute.Put("/v1/pawns/{pawnId}/drafted", PawnCommandService.SetDrafted),
            BridgeRoute.Put("/v1/pawns/{pawnId}/work/{workTypeDefName}", PawnCommandService.SetWorkPriority),
            BridgeRoute.Put("/v1/research/current", ResearchCommandService.SetCurrent)
        };

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
                        return route.Handle(bridgeRequest, match);
                    }
                }

                return pathMatched ? BridgeResponse.Error(405, "Method not allowed for endpoint.") : BridgeResponse.Error(404, "Unknown endpoint.");
            }
            catch (CommandException ex)
            {
                return BridgeResponse.Error(ex.StatusCode, ex.Message);
            }
            catch (Exception ex)
            {
                return BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }
        }

    }
}
