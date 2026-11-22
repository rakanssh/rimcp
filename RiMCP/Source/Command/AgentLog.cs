using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.UI;
using UnityEngine;
using Verse;

namespace RiMCP.Command
{
    internal static class Outcomes
    {
        public const string Pending = "pending";
        public const string Done = "done";
        public const string PartlyDone = "partlyDone";
        public const string NoChange = "noChange";
        public const string Refused = "refused";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
    }

    // What an entry acted on, kept as plain values so the log keeps no game object alive.
    internal sealed class AgentTarget
    {
        public readonly string ThingId;
        public readonly string Label;
        public readonly int X;
        public readonly int Z;
        public readonly string MapId;

        public AgentTarget(string thingId, string label, IntVec3 cell, Map map)
        {
            ThingId = thingId;
            Label = label;
            X = cell.x;
            Z = cell.z;
            MapId = map == null ? null : map.uniqueID.ToString();
        }

        public object ToDto()
        {
            return Dto.Obj(
                Dto.Field("id", ThingId),
                Dto.Field("label", Label),
                Dto.Field("x", X),
                Dto.Field("z", Z),
                Dto.Field("mapId", MapId));
        }
    }

    internal sealed class AgentEntry
    {
        public int Id;
        public string Tool;
        public string Client;
        public string LoadId;
        public int? Tick;
        public string When;
        public string Line;
        public string Outcome;
        public string Detail;
        public bool Decision;
        // The agent's own words, not something it did.
        public bool Note;
        public bool Attention;
        public string Text;
        public List<AgentTarget> Targets = new List<AgentTarget>();
        internal string DoneLine;
        internal readonly HashSet<string> WaitingOn = new HashSet<string>();

        public object ToDto()
        {
            return Dto.Obj(
                Dto.Field("id", Id),
                Dto.Field("tool", Tool),
                Dto.Field("client", Client),
                Dto.Field("loadId", LoadId),
                Dto.Field("tick", Tick),
                Dto.Field("when", When),
                Dto.Field("line", Line),
                Dto.Field("outcome", Outcome),
                Dto.Field("detail", Detail),
                Dto.Field("note", Note ? (object)true : null),
                Dto.Field("targets", Targets.Count == 0 ? null : Targets.Select(target => target.ToDto()).ToArray()));
        }
    }

    // What agents did: one entry per call that changes the game, kept for the RimWorld session.
    // Commands report their own outcome and targets; the router records the entry, and refusals a
    // command did not describe. Nothing here may fail a call. Main thread only.
    internal static class AgentLog
    {
        public const int MaxEntries = 200;
        public const int MaxCells = 20;
        private static readonly List<AgentEntry> entries = new List<AgentEntry>();
        private static readonly Dictionary<string, AgentEntry> waiting = new Dictionary<string, AgentEntry>();
        // Work that finished before the call that started it was recorded.
        private static readonly Dictionary<string, Completion> early = new Dictionary<string, Completion>();
        // History entries RimMCP posted, by load id, with the client that caused them.
        private static readonly Dictionary<string, string> posted = new Dictionary<string, string>();
        private static readonly Queue<string> postedOrder = new Queue<string>();
        private static Call call;
        private static int lastId;
        private static bool warned;

        public static event Action<AgentEntry> Recorded;
        public static event Action<AgentEntry> Updated;

        private sealed class Call
        {
            public string Tool;
            public string Client;
            public string About;
            public List<AgentTarget> AboutTargets;
            public string Line;
            public string Outcome;
            public string Detail;
            public List<AgentTarget> Targets;
            public bool Replayed;
            public bool Decision;
            public bool Note;
            public bool Attention;
            public string Text;
            public string DoneLine;
            public readonly List<string> Keys = new List<string>();
        }

        private sealed class Completion
        {
            public string Outcome;
            public string Line;
            public string Detail;
        }

        public static List<AgentEntry> Entries
        {
            get { return entries.ToList(); }
        }

        public static void Begin(string tool, string client)
        {
            Guard(() => call = new Call { Tool = tool, Client = client });
        }

        // The router's end of a call: the entry the command reported, or one for its refusal.
        // A busy game (503) and a retry answered from its receipt add nothing.
        public static void End(int status, string error, bool? changed)
        {
            Call ended = call;
            call = null;
            Guard(() =>
            {
                if (ended == null || ended.Replayed || status == 503)
                {
                    return;
                }
                AgentEntry entry = Stamp(new AgentEntry { Tool = ended.Tool, Client = ended.Client, Decision = ended.Decision });
                if (status < 400)
                {
                    entry.Note = ended.Note;
                    entry.Attention = ended.Attention;
                    entry.Text = ended.Text;
                }
                if (status >= 400)
                {
                    entry.Outcome = status >= 500 ? Outcomes.Failed : Outcomes.Refused;
                    entry.Line = ended.About ?? ended.Line ?? Label(ended.Tool);
                    entry.Detail = status >= 500 ? Keys.AgentLog_MayHavePartlyRun.Text(error) : error;
                    entry.Targets = ended.Targets ?? ended.AboutTargets ?? new List<AgentTarget>();
                }
                else
                {
                    entry.Outcome = ended.Outcome ?? (changed == false ? Outcomes.NoChange : Outcomes.Done);
                    entry.Line = ended.Line ?? (ended.Keys.Count == 0 && entry.Outcome == Outcomes.Done ? ended.DoneLine : null) ?? ended.About ?? Label(ended.Tool);
                    entry.Detail = ended.Detail;
                    entry.Targets = ended.Targets ?? ended.AboutTargets ?? new List<AgentTarget>();
                    entry.DoneLine = ended.DoneLine;
                    if (entry.Outcome != Outcomes.Refused)
                    {
                        foreach (string key in ended.Keys)
                        {
                            entry.WaitingOn.Add(key);
                            waiting[key] = entry;
                        }
                    }
                    if (entry.WaitingOn.Count > 0)
                    {
                        entry.Outcome = Outcomes.Pending;
                    }
                }
                Add(entry);
                foreach (string key in entry.WaitingOn.ToList())
                {
                    Completion done;
                    if (early.TryGetValue(key, out done))
                    {
                        early.Remove(key);
                        Finish(key, done.Outcome, done.Line, done.Detail);
                    }
                }
            });
        }

        // What the call is about, for its entry if it is refused or waits without saying more.
        public static void About(string line, params object[] targets)
        {
            Guard(() =>
            {
                if (call != null)
                {
                    call.About = line;
                    call.AboutTargets = Targets(targets);
                }
            });
        }

        public static void Done(string line, params object[] targets)
        {
            Report(Outcomes.Done, line, null, targets);
        }

        public static void NoChange(string line, params object[] targets)
        {
            Report(Outcomes.NoChange, line, null, targets);
        }

        public static void Partly(string line, string detail, params object[] targets)
        {
            Report(Outcomes.PartlyDone, line, detail, targets);
        }

        // For a call that answers normally but refused what it was asked, such as every placement
        // rejected.
        public static void Refused(string line, string reason, params object[] targets)
        {
            Report(Outcomes.Refused, line, reason, targets);
        }

        // The outcome a batch reports from its counts: doneLine says what was applied, asked what
        // was asked for.
        public static void Counted(string doneLine, string asked, int applied, int already, int rejected, string reasons, params object[] targets)
        {
            string refusals = string.IsNullOrEmpty(reasons)
                ? Keys.AgentLog_RejectedCount.Text(rejected)
                : Keys.AgentLog_RejectedReasons.Text(rejected, reasons);
            if (applied > 0 && rejected > 0)
            {
                Partly(Keys.AgentLog_PartlyRejected.Text(doneLine, rejected), refusals, targets);
            }
            else if (applied > 0)
            {
                Done(doneLine, targets);
            }
            else if (rejected > 0)
            {
                Refused(asked, refusals, targets);
            }
            else
            {
                NoChange((already > 0 ? Keys.AgentLog_AlreadySo : Keys.AgentLog_NothingToDo).Text(asked), targets);
            }
        }

        // Stacks and homogeneous groups keep the source label, while mixed groups use their category
        public static string Count(ICollection<Thing> things, TextKey fallback)
        {
            ThingDef kind = things.Count > 0 && things.All(thing => thing.def == things.First().def) ? things.First().def : null;
            if (kind != null && kind.stackLimit > 1)
            {
                return Keys.AgentLog_CountStack.Text(kind.label.StripTags(), things.Sum(thing => thing.stackCount));
            }
            if (things.Count == 1)
            {
                return things.First().LabelNoCount.StripTags();
            }
            if (kind == null)
            {
                TextKey category = things.Count > 0 && things.All(thing => thing.def.plant != null && thing.def.plant.IsTree)
                    ? Keys.AgentLog_Count_tree : fallback;
                return category.Text(things.Count);
            }
            return Keys.AgentLog_CountNamed.Text(things.Count, kind.label.StripTags());
        }

        // A decision shows in game as well as in the log.
        public static void Decision()
        {
            Guard(() =>
            {
                if (call != null)
                {
                    call.Decision = true;
                }
            });
        }

        public static void Note(string text, bool attention, params object[] targets)
        {
            Report(Outcomes.Done, Keys.AgentLog_QuotedMessage.Text(text), null, targets);
            Guard(() =>
            {
                if (call != null)
                {
                    call.Note = true;
                    call.Attention = attention;
                    call.Text = text;
                }
            });
        }

        public static void Replayed()
        {
            Guard(() =>
            {
                if (call != null)
                {
                    call.Replayed = true;
                }
            });
        }

        // What a call is asked to do, and how its entry reads once done.
        public static void Expect(string asked, string done, params object[] targets)
        {
            About(asked, targets);
            WhenDone(done);
        }

        // The line an entry takes once the work it waits for succeeds.
        public static void WhenDone(string line)
        {
            Guard(() =>
            {
                if (call != null)
                {
                    call.DoneLine = line;
                }
            });
        }

        // The entry stays pending until Finish is called with this key; doneLine replaces its line
        // when it succeeds.
        public static void WaitFor(string key, string doneLine = null)
        {
            Guard(() =>
            {
                if (call != null && key != null)
                {
                    call.Keys.Add(key);
                    if (doneLine != null)
                    {
                        call.DoneLine = doneLine;
                    }
                }
            });
        }

        public static void Finish(string key, string outcome, string line = null, string detail = null)
        {
            Guard(() =>
            {
                if (key == null)
                {
                    return;
                }
                AgentEntry entry;
                if (!waiting.TryGetValue(key, out entry))
                {
                    if (call != null && call.Keys.Contains(key))
                    {
                        early[key] = new Completion { Outcome = outcome, Line = line, Detail = detail };
                    }
                    return;
                }
                waiting.Remove(key);
                entry.WaitingOn.Remove(key);
                if (detail != null)
                {
                    entry.Detail = detail;
                }
                if (entry.WaitingOn.Count > 0)
                {
                    return;
                }
                entry.Outcome = outcome;
                entry.Line = line ?? (outcome == Outcomes.Done ? entry.DoneLine : null) ?? entry.Line;
                Notify(Updated, entry);
            });
        }

        public static AgentTarget Target(Thing thing)
        {
            if (thing == null)
            {
                return null;
            }
            return new AgentTarget(ReadUtil.ThingId(thing), Name(thing), thing.PositionHeld, thing.MapHeld);
        }

        public static AgentTarget Cell(Map map, IntVec3 cell, string label = null)
        {
            return map == null || !cell.IsValid ? null : new AgentTarget(null, label, cell, map);
        }

        // Up to MaxCells cells, spread evenly across the list.
        public static List<AgentTarget> Cells(Map map, IEnumerable<IntVec3> cells, string label = null)
        {
            List<IntVec3> all = cells == null ? new List<IntVec3>() : cells.ToList();
            List<AgentTarget> spread = new List<AgentTarget>();
            if (map == null || all.Count == 0)
            {
                return spread;
            }
            int count = Math.Min(all.Count, MaxCells);
            for (int i = 0; i < count; i++)
            {
                spread.Add(new AgentTarget(null, label, all[(int)((long)i * all.Count / count)], map));
            }
            return spread;
        }

        public static string Name(Thing thing)
        {
            return thing is Pawn pawn && pawn.Name != null ? pawn.Name.ToStringFull : thing?.LabelNoCount.StripTags() ?? "?";
        }

        public static string Label(string tool)
        {
            return string.IsNullOrEmpty(tool) ? Keys.AgentLog_AgentCall.Text() : Keys.AgentLog_ToolCall.Text(tool);
        }

        // Jumps to what an entry acted on, only within the loaded game it came from.
        public static bool CanJump(AgentEntry entry)
        {
            return entry != null && entry.Targets.Count > 0 && entry.LoadId != null && entry.LoadId == GameContext.LoadId;
        }

        public static void Jump(AgentEntry entry)
        {
            Guard(() =>
            {
                if (!CanJump(entry))
                {
                    return;
                }
                AgentTarget target = entry.Targets[0];
                Map map = Find.Maps.FirstOrDefault(each => each.uniqueID.ToString() == target.MapId);
                if (map == null)
                {
                    return;
                }
                Thing thing = target.ThingId == null ? null : map.listerThings.AllThings.FirstOrDefault(each => ReadUtil.ThingId(each) == target.ThingId && each.Spawned);
                if (thing != null)
                {
                    CameraJumper.TryJumpAndSelect(thing);
                }
                else
                {
                    CameraJumper.TryJump(new GlobalTargetInfo(new IntVec3(target.X, 0, target.Z), map));
                }
            });
        }

        public static void MarkPosted(string loadId, string client)
        {
            Guard(() =>
            {
                posted[loadId] = client ?? "an agent";
                postedOrder.Enqueue(loadId);
                while (postedOrder.Count > 500)
                {
                    posted.Remove(postedOrder.Dequeue());
                }
            });
        }

        public static string PostedBy(string loadId)
        {
            string client;
            return loadId != null && posted.TryGetValue(loadId, out client) ? client : null;
        }

        internal static void Reset()
        {
            entries.Clear();
            waiting.Clear();
            early.Clear();
            call = null;
        }

        private static void Report(string outcome, string line, string detail, object[] targets)
        {
            Guard(() =>
            {
                if (call != null)
                {
                    call.Outcome = outcome;
                    call.Line = line;
                    call.Detail = detail;
                    call.Targets = Targets(targets);
                }
            });
        }

        private static AgentEntry Stamp(AgentEntry entry)
        {
            entry.Id = ++lastId;
            entry.LoadId = GameContext.LoadId;
            bool inGame = Current.Game != null && Find.TickManager != null;
            entry.Tick = inGame ? (int?)Find.TickManager.TicksGame : null;
            entry.When = inGame ? GameTime() : DateTime.Now.ToString("HH:mm:ss");
            return entry;
        }

        private static string GameTime()
        {
            Map map = Find.CurrentMap;
            Vector2 place = map == null || Find.WorldGrid == null ? Vector2.zero : Find.WorldGrid.LongLatOf(map.Tile);
            return GenDate.DateFullStringWithHourAt(GenTicks.TicksAbs, place);
        }

        private static void Add(AgentEntry entry)
        {
            entries.Add(entry);
            while (entries.Count > MaxEntries)
            {
                AgentEntry dropped = entries[0];
                entries.RemoveAt(0);
                foreach (string key in dropped.WaitingOn)
                {
                    waiting.Remove(key);
                }
            }
            Notify(Recorded, entry);
        }

        private static List<AgentTarget> Targets(object[] targets)
        {
            List<AgentTarget> found = new List<AgentTarget>();
            if (targets != null)
            {
                foreach (object target in targets)
                {
                    Collect(target, found);
                }
            }
            return found;
        }

        private static void Collect(object target, List<AgentTarget> found)
        {
            if (target == null || found.Count >= MaxCells)
            {
                return;
            }
            AgentTarget plain = target as AgentTarget;
            Thing thing = target as Thing;
            if (plain != null)
            {
                found.Add(plain);
            }
            else if (thing != null)
            {
                found.Add(Target(thing));
            }
            else if (target is LookTargets)
            {
                Collect(((LookTargets)target).targets, found);
            }
            else if (target is GlobalTargetInfo)
            {
                GlobalTargetInfo global = (GlobalTargetInfo)target;
                AgentTarget converted = global.HasThing ? Target(global.Thing) : global.Map != null ? Cell(global.Map, global.Cell) : null;
                if (converted != null)
                {
                    found.Add(converted);
                }
            }
            else if (target is LocalTargetInfo)
            {
                LocalTargetInfo local = (LocalTargetInfo)target;
                AgentTarget converted = local.HasThing ? Target(local.Thing) : Cell(Find.CurrentMap, local.Cell);
                if (converted != null)
                {
                    found.Add(converted);
                }
            }
            else if (target is IEnumerable && !(target is string))
            {
                foreach (object each in (IEnumerable)target)
                {
                    Collect(each, found);
                }
            }
        }

        private static void Notify(Action<AgentEntry> handlers, AgentEntry entry)
        {
            if (handlers == null)
            {
                return;
            }
            foreach (Action<AgentEntry> handler in handlers.GetInvocationList())
            {
                Guard(() => handler(entry));
            }
        }

        private static void Guard(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("RimMCP: the agent log failed and skipped an entry: " + ex);
                }
            }
        }
    }
}
