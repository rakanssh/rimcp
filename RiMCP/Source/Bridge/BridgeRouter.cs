using System;
using RiMCP.Data;

namespace RiMCP.Bridge
{
    internal static class BridgeRouter
    {
        public static BridgeResponse Handle(string rawPath)
        {
            try
            {
                string path = rawPath.TrimEnd('/');
                if (path.Length == 0)
                {
                    path = "/";
                }

                if (path == "/v1/colony-summary")
                {
                    return ColonyDataService.GetColonySummary();
                }
                if (path == "/v1/core-pawns")
                {
                    return ColonyDataService.ListCorePawns();
                }
                if (path.StartsWith("/v1/pawns/", StringComparison.Ordinal))
                {
                    string[] parts = path.Split('/');
                    if (parts.Length == 4)
                    {
                        return ColonyDataService.GetPawn(Uri.UnescapeDataString(parts[3]));
                    }
                }
                if (path == "/v1/inventory")
                {
                    return ColonyDataService.ListInventory();
                }
                if (path == "/v1/bills")
                {
                    return ColonyDataService.ListBills();
                }
                if (path == "/v1/alerts")
                {
                    return ColonyDataService.ListAlerts();
                }
                if (path == "/v1/animals")
                {
                    return ColonyDataService.ListAnimals();
                }
                if (path == "/v1/hostiles")
                {
                    return ColonyDataService.ListHostiles();
                }

                return BridgeResponse.Error(404, "Unknown endpoint.");
            }
            catch (Exception ex)
            {
                return BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
