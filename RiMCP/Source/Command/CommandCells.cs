using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // A rectangle of cells: (x, z) is the south-west corner, extending east and north.
    [DataContract]
    internal sealed class CellRectBody
    {
        [DataMember(Name = "x")]
        public int? X { get; set; }

        [DataMember(Name = "z")]
        public int? Z { get; set; }

        [DataMember(Name = "width")]
        public int? Width { get; set; }

        [DataMember(Name = "height")]
        public int? Height { get; set; }
    }

    internal static class CommandCells
    {
        public const int MaxSide = 60;
        public const int MaxCells = 3600;

        // Cells of the rectangles, without duplicates, in the order given. Cells outside the map
        // are counted rather than returned.
        public static List<IntVec3> FromRects(CellRectBody[] rects, Map map, string field, out int outside)
        {
            List<IntVec3> cells = new List<IntVec3>();
            HashSet<IntVec3> seen = new HashSet<IntVec3>();
            outside = 0;
            if (rects == null)
            {
                return cells;
            }
            for (int i = 0; i < rects.Length; i++)
            {
                CellRectBody rect = rects[i];
                if (rect == null || !rect.X.HasValue || !rect.Z.HasValue)
                {
                    throw new CommandException(400, "Field '" + field + "[" + i + "]' needs x and z.");
                }
                int width = rect.Width ?? 1;
                int height = rect.Height ?? 1;
                if (width < 1 || height < 1 || width > MaxSide || height > MaxSide)
                {
                    throw new CommandException(400, "Field '" + field + "[" + i + "]' width and height must be 1 to " + MaxSide + ".");
                }
                if (cells.Count + outside + width * height > MaxCells)
                {
                    throw new CommandException(400, "The rectangles cover more than " + MaxCells + " cells; split the change into smaller calls.");
                }
                for (int x = rect.X.Value; x < rect.X.Value + width; x++)
                {
                    for (int z = rect.Z.Value; z < rect.Z.Value + height; z++)
                    {
                        IntVec3 cell = new IntVec3(x, 0, z);
                        if (!cell.InBounds(map))
                        {
                            outside++;
                        }
                        else if (seen.Add(cell))
                        {
                            cells.Add(cell);
                        }
                    }
                }
            }
            return cells;
        }

    }

    // Outcomes for a batch of cells. Every rejected cell is listed with its reason, up to a cap;
    // the counts always cover all of them.
    internal sealed class CellOutcomes
    {
        private const int MaxListed = 200;
        private readonly List<object> rejected = new List<object>();
        private int rejectedCount;
        public int Applied;
        public int AlreadySatisfied;
        public int Outside;

        public void Reject(IntVec3 cell, string reason)
        {
            rejectedCount++;
            if (rejected.Count < MaxListed)
            {
                rejected.Add(Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z), Dto.Field("reason", reason)));
            }
        }

        public object ToDto()
        {
            return Dto.Obj(
                Dto.Field("applied", Applied),
                Dto.Field("alreadySatisfied", AlreadySatisfied),
                Dto.Field("rejectedCount", rejectedCount),
                Dto.Field("rejected", rejected.ToArray()),
                Dto.Field("rejectedListTruncated", rejectedCount > rejected.Count ? (object)true : null),
                Dto.Field("outsideMap", Outside > 0 ? (object)Outside : null));
        }
    }

    // The identity of a command for retries: the tool, the caller's requestId, the map, and the
    // arguments as submitted. Defaults the game would pick from the colony's current state,
    // such as a building's material, stay out of it, so an identical retry still matches after the
    // colony changes.
    internal sealed class RequestKey
    {
        public readonly string Tool;
        public readonly string RequestId;
        public readonly string Fingerprint;

        private RequestKey(string tool, string requestId, string fingerprint)
        {
            Tool = tool;
            RequestId = requestId;
            Fingerprint = fingerprint;
        }

        // Null without a requestId: such a call has no receipt.
        public static RequestKey For(string tool, string requestId, Map map, params object[] arguments)
        {
            if (requestId == null)
            {
                return null;
            }
            string mapId = map == null ? "-" : map.uniqueID.ToString(CultureInfo.InvariantCulture);
            return new RequestKey(tool, requestId, "map " + mapId + "|" + string.Join("|", arguments.Select(Normalize).ToArray()));
        }

        private static string Normalize(object argument)
        {
            CellRectBody[] rects = argument as CellRectBody[];
            if (rects != null)
            {
                return string.Join(";", rects.Select(rect => rect == null ? "null" : rect.X + "," + rect.Z + "," + (rect.Width ?? 1) + "," + (rect.Height ?? 1)).ToArray());
            }
            if (argument is bool)
            {
                return (bool)argument ? "true" : "false";
            }
            return argument == null ? "" : Convert.ToString(argument, CultureInfo.InvariantCulture).Trim();
        }
    }

    // What a command did: whether it changed the game, and the result it answers with.
    internal sealed class CommandResult
    {
        public readonly bool Changed;
        public readonly object Data;

        public CommandResult(bool changed, object data)
        {
            Changed = changed;
            Data = data;
        }
    }

    // Results of recent commands that took a requestId, in the loaded game, so a retry returns the
    // original result even after the game has moved on, and a requestId reused for a different call
    // is refused. Main thread only.
    internal static class CommandReceipts
    {
        public const int MaxReceipts = 64;
        private static readonly List<Receipt> receipts = new List<Receipt>();

        // Runs a command once per key. Whatever it answers is kept, alreadySatisfied and rejected
        // outcomes too, and a retry gets that answer back, marked unchanged, without the command
        // running again: so a retry never redoes or undoes what a player did in between. A command
        // that fails with an error changed nothing and keeps nothing. Without a key it just runs.
        public static BridgeResponse Once(CommandContext context, RequestKey key, Func<CommandResult> run)
        {
            object kept = Find(key);
            if (kept != null)
            {
                return CommandEnvelope.Ok(context, false, kept);
            }
            CommandResult result = run();
            Record(key, result.Data);
            return CommandEnvelope.Ok(context, result.Changed, result.Data);
        }

        private static object Find(RequestKey key)
        {
            if (key == null)
            {
                return null;
            }
            receipts.RemoveAll(receipt => !ReferenceEquals(receipt.Game, Current.Game));
            Receipt found = receipts.LastOrDefault(receipt => receipt.Key.Tool == key.Tool && receipt.Key.RequestId == key.RequestId);
            if (found != null && found.Key.Fingerprint != key.Fingerprint)
            {
                throw new CommandException(409, "requestId '" + key.RequestId + "' was already used for a different " + key.Tool + " call.");
            }
            return found == null ? null : found.Data;
        }

        private static void Record(RequestKey key, object data)
        {
            if (key == null)
            {
                return;
            }
            receipts.Add(new Receipt { Game = Current.Game, Key = key, Data = data });
            if (receipts.Count > MaxReceipts)
            {
                receipts.RemoveAt(0);
            }
        }

        private sealed class Receipt
        {
            public object Game;
            public RequestKey Key;
            public object Data;
        }
    }
}
