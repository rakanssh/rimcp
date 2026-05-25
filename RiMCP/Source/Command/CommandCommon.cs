using System;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Util;
using Verse;

namespace RiMCP.Command
{
    internal sealed class CommandContext
    {
        public readonly Map Map;
        public readonly int Tick;

        public CommandContext(string mapId)
        {
            Map = GameContext.ResolveMap(mapId);
            Tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
        }
    }

    internal sealed class CommandException : Exception
    {
        public readonly int StatusCode;

        public CommandException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal static class CommandEnvelope
    {
        public static BridgeResponse Ok(CommandContext context, bool changed, object data)
        {
            object envelope = Dto.Obj(
                Dto.Field("schemaVersion", "1"),
                Dto.Field("tick", context.Tick),
                Dto.Field("mapId", context.Map == null ? null : context.Map.uniqueID.ToString()),
                Dto.Field("changed", changed),
                Dto.Field("data", data));
            return BridgeResponse.Json(200, Json.Serialize(envelope));
        }
    }

    internal static class CommandUtil
    {
        public static T ReadBody<T>(BridgeRequest request) where T : class, new()
        {
            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return new T();
            }

            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(T));
                byte[] bytes = Encoding.UTF8.GetBytes(request.Body);
                using (MemoryStream stream = new MemoryStream(bytes))
                {
                    T body = serializer.ReadObject(stream) as T;
                    return body ?? new T();
                }
            }
            catch (Exception ex)
            {
                throw new CommandException(400, "Invalid JSON body: " + ex.Message);
            }
        }

        public static CommandContext ContextFor(string mapId)
        {
            return new CommandContext(mapId);
        }

        public static void RequireMap(CommandContext context)
        {
            if (context.Map == null)
            {
                throw new CommandException(409, "No active map is loaded.");
            }
        }

        public static object PawnSummary(Pawn pawn)
        {
            return Dto.Obj(
                Dto.Field("ids", ReadUtil.ThingIds(pawn)),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("label", pawn.LabelShortCap));
        }
    }
}
