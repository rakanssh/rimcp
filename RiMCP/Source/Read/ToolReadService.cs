using System.Collections.Generic;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Shared;
using RiMCP.Util;

namespace RiMCP.Read
{
    internal static class ToolReadService
    {
        // Reachable with any access level, so clients can always learn what is enabled.
        public static BridgeResponse GetTools(BridgeRequest request, RouteMatch route)
        {
            AccessState access = BridgeRuntime.Access;
            Dictionary<string, object> manifest = ToolManifest.ToDto();
            manifest["access"] = Dto.Obj(
                Dto.Field("preset", access.Preset),
                Dto.Field("groups", ToolGroups.All.Where(access.Allows).ToArray()),
                Dto.Field("revision", access.Revision),
                // Changes when RimWorld restarts, which resets the revision.
                Dto.Field("instance", GameContext.SessionId));
            manifest["tools"] = ToolManifest.All
                .Select(spec =>
                {
                    Dictionary<string, object> dto = spec.ToDto();
                    dto["enabled"] = access.AllowsTool(spec);
                    return dto;
                })
                .ToArray();
            return BridgeResponse.Json(200, Json.Serialize(manifest));
        }

        // What agents may use, small enough to poll: the tools that are on, and a version that
        // changes with every access change (instance, then revision).
        public static BridgeResponse GetAccess(AccessState access)
        {
            object dto = Dto.Obj(
                Dto.Field("instance", GameContext.SessionId),
                Dto.Field("revision", access.Revision),
                Dto.Field("enabled", access.Enabled),
                Dto.Field("preset", access.Preset),
                Dto.Field("tools", ToolManifest.McpTools.Where(access.AllowsTool).Select(spec => spec.Name).ToArray()));
            return BridgeResponse.Json(200, Json.Serialize(dto));
        }
    }
}
