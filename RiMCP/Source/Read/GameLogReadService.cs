using System;
using System.Collections.Generic;
using System.Linq;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class GameLogReadService
    {
        public static BridgeResponse GetLog(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string level = context.Request.Get("level");
            level = string.IsNullOrWhiteSpace(level) ? "all" : level.Trim().ToLowerInvariant();
            if (level != "all" && level != "message" && level != "warning" && level != "error")
            {
                throw new BridgeException(400, "Level must be all, message, warning, or error.");
            }

            string query = context.Request.Get("query") ?? "";
            bool includeStackTrace = context.Request.Wants("stackTrace");
            List<LogEntry> entries = Snapshot(includeStackTrace || query.Length > 0);
            entries.Reverse();
            IEnumerable<LogEntry> source = entries
                .Where(entry => level == "all" || entry.Level == level)
                .Where(entry => query.Length == 0 || Contains(entry.Message, query) || Contains(entry.StackTrace, query));
            Page<LogEntry> page = new Page<LogEntry>(source, context.Request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("source", "retainedGameLog"),
                Dto.Field("level", level),
                Dto.Field("query", query),
                Dto.Field("entries", page.Items.Select(entry => Dto.Obj(
                    Dto.Field("level", entry.Level),
                    Dto.Field("message", entry.Message),
                    Dto.Field("repeats", entry.Repeats),
                    Dto.Field("stackTrace", includeStackTrace ? entry.StackTrace : null))).ToArray())),
                page.Truncated, page.NextCursor);
        }

        private static List<LogEntry> Snapshot(bool includeStackTrace)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    List<LogEntry> entries = new List<LogEntry>();
                    foreach (LogMessage message in Log.Messages)
                    {
                        entries.Add(new LogEntry(message, includeStackTrace));
                    }
                    return entries;
                }
                catch (InvalidOperationException)
                {
                    // Background logging can invalidate the queue's enumerator.
                }
            }
            throw new BridgeException(503, "The game log is busy. Try again shortly.");
        }

        private static bool Contains(string value, string query)
        {
            return value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private sealed class LogEntry
        {
            public readonly string Level;
            public readonly string Message;
            public readonly int Repeats;
            public readonly string StackTrace;

            public LogEntry(LogMessage message, bool includeStackTrace)
            {
                Level = message.type.ToString().ToLowerInvariant();
                Message = message.text;
                Repeats = message.repeats;
                StackTrace = includeStackTrace ? message.StackTrace : null;
            }
        }
    }
}
