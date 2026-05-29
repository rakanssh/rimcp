using RiMCP.Bridge;
using RiMCP.Shared;
using RiMCP.Util;

namespace RiMCP.Read
{
    internal static class ToolReadService
    {
        public static BridgeResponse GetTools(BridgeRequest request, RouteMatch route)
        {
            return BridgeResponse.Json(200, Json.Serialize(ToolManifest.ToDto()));
        }
    }
}
