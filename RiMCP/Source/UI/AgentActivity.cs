using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Read;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // Shows agent log entries in game, as bubbles or messages, and as a cursor and flashes on the
    // map, as the settings choose. Main thread only.
    internal static class AgentActivity
    {
        public const int MessagesOff = 0;
        public const int DecisionsAndNotes = 1;
        public const int EveryChange = 2;
        private const int BurstSize = 3;
        private const float BurstSeconds = 3f;
        private const int MaxFlashesPerSecond = 40;
        private static readonly Queue<float> shown = new Queue<float>();
        private static readonly List<DueFlash> due = new List<DueFlash>();
        private static int folded;
        private static string foldedBy;
        private static float foldedAt;
        private static int flashSecond;
        private static int flashes;
        private static bool started;

        public static void Start()
        {
            if (started)
            {
                return;
            }
            started = true;
            AgentLog.Recorded += entry =>
            {
                Flash(entry);
                if (entry.Outcome != Outcomes.Pending)
                {
                    Announce(entry);
                }
            };
            AgentLog.Updated += Announce;
        }

        public static void Update()
        {
            FireFlashes();
            if (folded > 0 && Time.realtimeSinceStartup - foldedAt > BurstSeconds / 2f)
            {
                int count = folded;
                folded = 0;
                if (Current.Game != null)
                {
                    Post(foldedBy, null, "made " + AgentLog.Count(count, "more change") + "; the agent log lists them", false);
                }
            }
        }

        private static int Mode
        {
            get { return RiMCPMod.Settings == null ? DecisionsAndNotes : RiMCPMod.Settings.AgentMessages; }
        }

        private static void Announce(AgentEntry entry)
        {
            if (Mode == MessagesOff || Current.Game == null || entry.LoadId != GameContext.LoadId)
            {
                return;
            }
            if (entry.Note)
            {
                Say(entry);
                return;
            }
            bool made = entry.Outcome == Outcomes.Done || entry.Outcome == Outcomes.PartlyDone;
            bool decision = entry.Decision && made;
            if (!decision && !(Mode == EveryChange && (made || entry.Outcome == Outcomes.Failed)))
            {
                return;
            }
            if (!decision && Busy())
            {
                if (AgentOverlay.Showing)
                {
                    AgentOverlay.Fold(Who(entry), entry);
                    return;
                }
                folded++;
                foldedBy = entry.Client;
                foldedAt = Time.realtimeSinceStartup;
                return;
            }
            string line = entry.Line + (entry.Outcome == Outcomes.PartlyDone ? " (partly)" : entry.Outcome == Outcomes.Failed ? " (failed)" : "");
            Post(entry.Client, entry, line, decision);
        }

        // In the agent's own words, labelled as such. A letter enters the letter stack and History, as
        // every letter does, with the game's own letter type.
        private static void Say(AgentEntry entry)
        {
            string who = Who(entry);
            LookTargets look = Look(entry) ?? LookTargets.Invalid;
            if (entry.Attention)
            {
                Letter letter = LetterMaker.MakeLetter("Agent message", who + " says:\n\n" + entry.Text, LetterDefOf.NeutralEvent, look);
                Find.LetterStack.ReceiveLetter(letter);
                AgentLog.MarkPosted(letter.GetUniqueLoadID(), entry.Client);
            }
            else if (AgentOverlay.Showing)
            {
                AgentOverlay.Say(who, entry.Text, entry);
            }
            else
            {
                Messages.Message(new Message(who + " says: " + entry.Text, MessageTypeDefOf.NeutralEvent, look), false);
            }
        }

        private static bool Busy()
        {
            float now = Time.realtimeSinceStartup;
            while (shown.Count > 0 && now - shown.Peek() > BurstSeconds)
            {
                shown.Dequeue();
            }
            if (shown.Count >= BurstSize)
            {
                return true;
            }
            shown.Enqueue(now);
            return false;
        }

        // Kept in History only for decisions, with the game's own message type, so routine changes
        // never push the player's entries out and saves never depend on RimMCP.
        private static void Post(string client, AgentEntry entry, string text, bool historical)
        {
            string who = client ?? "An agent";
            Message message = new Message(who + ": " + text, historical ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.SilentInput, (entry == null ? null : Look(entry)) ?? LookTargets.Invalid);
            if (AgentOverlay.Showing)
            {
                AgentOverlay.Say(who, text, entry);
                if (historical)
                {
                    Find.Archive.Add(message);
                }
            }
            else
            {
                Messages.Message(message, historical);
            }
            if (historical)
            {
                AgentLog.MarkPosted(message.GetUniqueLoadID(), client);
            }
        }

        private static string Who(AgentEntry entry)
        {
            return entry.Client ?? "An agent";
        }

        private static LookTargets Look(AgentEntry entry)
        {
            List<GlobalTargetInfo> found = new List<GlobalTargetInfo>();
            foreach (AgentTarget target in entry.Targets.Take(5))
            {
                Map map = Find.Maps.FirstOrDefault(each => each.uniqueID.ToString() == target.MapId);
                if (map == null)
                {
                    continue;
                }
                Thing thing = target.ThingId == null ? null : map.listerThings.AllThings.FirstOrDefault(each => ReadUtil.ThingId(each) == target.ThingId && each.Spawned);
                found.Add(thing != null ? new GlobalTargetInfo(thing) : new GlobalTargetInfo(new IntVec3(target.X, 0, target.Z), map));
            }
            return found.Count == 0 ? null : new LookTargets(found);
        }

        private static void Flash(AgentEntry entry)
        {
            if (RiMCPMod.Settings != null && !RiMCPMod.Settings.ShowWhereAgentsAct || entry.Outcome == Outcomes.Refused || entry.Outcome == Outcomes.Failed || entry.Outcome == Outcomes.NoChange)
            {
                return;
            }
            Map map = Current.Game == null ? null : Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            string mapId = map.uniqueID.ToString();
            List<IntVec3> cells = entry.Targets.Where(each => each.MapId == mapId).Select(each => new IntVec3(each.X, 0, each.Z)).Where(cell => cell.InBounds(map)).ToList();
            if (cells.Count == 0)
            {
                return;
            }
            // With the cursor shown, the flash goes off when it gets there.
            float delay = AgentOverlay.Pointing ? AgentOverlay.Point(Who(entry), map, cells[0]) : 0f;
            due.Add(new DueFlash { At = Time.realtimeSinceStartup + delay, Map = map, Cells = cells });
            FireFlashes();
        }

        private static void FireFlashes()
        {
            float now = Time.realtimeSinceStartup;
            int second = (int)now;
            if (second != flashSecond)
            {
                flashSecond = second;
                flashes = 0;
            }
            for (int i = due.Count - 1; i >= 0; i--)
            {
                DueFlash flash = due[i];
                if (flash.At > now)
                {
                    continue;
                }
                due.RemoveAt(i);
                if (Current.Game == null || !Find.Maps.Contains(flash.Map))
                {
                    continue;
                }
                foreach (IntVec3 cell in flash.Cells)
                {
                    if (flashes >= MaxFlashesPerSecond)
                    {
                        break;
                    }
                    FleckMaker.Static(cell, flash.Map, FleckDefOf.FeedbackGoto);
                    flashes++;
                }
            }
        }

        private sealed class DueFlash
        {
            public float At;
            public Map Map;
            public List<IntVec3> Cells;
        }
    }
}
