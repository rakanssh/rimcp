using System;
using System.Linq;
using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Util;

namespace RiMCP.Read
{
    // The agent log, newest first, for tests and diagnostics. Not an MCP tool.
    internal static class AgentLogReadService
    {
        public static BridgeResponse GetLog(BridgeRequest request, RouteMatch route)
        {
            int? limit = ReadRequest.FromUri(request.Uri).OptionalInt("limit");
            int count = Math.Max(1, Math.Min(AgentLog.MaxEntries, limit ?? 50));
            object[] entries = AgentLog.Entries.AsEnumerable().Reverse().Take(count).Select(entry => entry.ToDto()).ToArray();
            return BridgeResponse.Json(200, Json.Serialize(Dto.Obj(Dto.Field("entries", entries))));
        }
    }
}
