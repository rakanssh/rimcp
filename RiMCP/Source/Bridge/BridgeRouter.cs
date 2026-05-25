using System;
using RiMCP.Read;

namespace RiMCP.Bridge
{
    internal static class BridgeRouter
    {
        public static BridgeResponse Handle(Uri uri)
        {
            try
            {
                string path = uri.AbsolutePath.TrimEnd('/');
                if (path.Length == 0)
                {
                    path = "/";
                }

                ReadRequest request = ReadRequest.FromUri(uri);
                ReadContext context = new ReadContext(request, ReadUtil.ResolveMap(request));

                if (path == "/v1/game-context") return GameReadService.GetGameContext(context);
                if (path == "/v1/colony-status") return GameReadService.GetColonyStatus(context);
                if (path == "/v1/pawns") return PawnReadService.ListPawns(context);
                if (path.StartsWith("/v1/pawns/", StringComparison.Ordinal)) return PawnReadService.GetPawn(context, PathPart(path, 3));
                if (path == "/v1/resources") return ResourceReadService.ListResources(context);
                if (path == "/v1/work") return WorkReadService.ListWork(context);
                if (path == "/v1/production") return ProductionReadService.ListProduction(context);
                if (path.StartsWith("/v1/bills/", StringComparison.Ordinal)) return ProductionReadService.GetBill(context, PathPart(path, 3));
                if (path == "/v1/zones") return ZoneReadService.ListZones(context);
                if (path.StartsWith("/v1/zones/", StringComparison.Ordinal)) return ZoneReadService.GetZone(context, PathPart(path, 3));
                if (path == "/v1/environment") return EnvironmentReadService.GetEnvironment(context);
                if (path == "/v1/power") return PowerReadService.GetPower(context);
                if (path == "/v1/threats") return ThreatReadService.ListThreats(context);
                if (path == "/v1/research") return ResearchReadService.GetResearch(context);
                if (path == "/v1/quests") return QuestWorldReadService.ListQuests(context);
                if (path == "/v1/buildings") return BuildingReadService.ListBuildings(context);
                if (path.StartsWith("/v1/buildings/", StringComparison.Ordinal)) return BuildingReadService.GetBuilding(context, PathPart(path, 3));
                if (path == "/v1/world") return QuestWorldReadService.ListWorld(context);
                if (path == "/v1/defs/search") return DefReadService.SearchDefs(context);
                if (path.StartsWith("/v1/defs/", StringComparison.Ordinal)) return DefReadService.GetDef(context, PathPart(path, 3));
                return BridgeResponse.Error(404, "Unknown endpoint.");
            }
            catch (Exception ex)
            {
                return BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static string PathPart(string path, int index)
        {
            string[] parts = path.Split('/');
            return parts.Length > index ? Uri.UnescapeDataString(parts[index]) : null;
        }
    }
}
