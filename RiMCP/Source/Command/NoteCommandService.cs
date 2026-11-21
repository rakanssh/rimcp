using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // One note every few seconds of real time, across clients, and a few letters per in-game day per
    // loaded game, so loading an earlier save starts a new allowance but cannot speed notes up.
    internal static class NoteLimits
    {
        public const int MaxLength = 280;
        public const int CooldownSeconds = 10;
        public const int LettersPerDay = 3;
        private static readonly Dictionary<string, int> letters = new Dictionary<string, int>();
        private static DateTime? lastNoteUtc;

        public static string Refusal(string loadId, int day, bool attention, DateTime nowUtc)
        {
            if (lastNoteUtc.HasValue && (nowUtc - lastNoteUtc.Value).TotalSeconds < CooldownSeconds)
            {
                int wait = (int)Math.Ceiling(CooldownSeconds - (nowUtc - lastNoteUtc.Value).TotalSeconds);
                return "One note every " + CooldownSeconds + " seconds; try again in " + wait + (wait == 1 ? " second." : " seconds.") + " Nothing was shown.";
            }
            int sent;
            if (attention && letters.TryGetValue(Key(loadId, day), out sent) && sent >= LettersPerDay)
            {
                return LettersPerDay + " letters were already sent today, in game; send a plain note, or wait for the next day. Nothing was shown.";
            }
            return null;
        }

        public static void Use(string loadId, int day, bool attention, DateTime nowUtc)
        {
            lastNoteUtc = nowUtc;
            if (attention)
            {
                int sent;
                letters.TryGetValue(Key(loadId, day), out sent);
                letters[Key(loadId, day)] = sent + 1;
            }
        }

        internal static void Reset()
        {
            lastNoteUtc = null;
            letters.Clear();
        }

        private static string Key(string loadId, int day)
        {
            return loadId + "|" + day;
        }
    }

    internal static class NoteCommandService
    {
        public static BridgeResponse TellPlayer(BridgeRequest request, RouteMatch route)
        {
            TellPlayerBody body = CommandUtil.ReadBody<TellPlayerBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            string text = body.Text == null ? null : body.Text.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw new CommandException(400, "Missing required field 'text'.");
            }
            if (text.Length > NoteLimits.MaxLength)
            {
                throw new CommandException(400, "Field 'text' is " + text.Length + " characters; at most " + NoteLimits.MaxLength + ".");
            }
            if (body.X.HasValue != body.Z.HasValue)
            {
                throw new CommandException(400, "Give both x and z for a cell.");
            }
            AgentLog.About("Tell the player: \"" + text + "\"");
            if (Current.Game == null || Current.ProgramState != ProgramState.Playing)
            {
                throw new CommandException(409, "No game is loaded; notes show in game.");
            }
            CommandContext context = CommandUtil.ContextFor(null);
            RequestKey key = RequestKey.For("tell_player", requestId, null, text, body.About == null ? null : string.Join(",", body.About), body.X, body.Z, body.Attention);
            return CommandReceipts.Once(context, key, () => Tell(body, text));
        }

        private static CommandResult Tell(TellPlayerBody body, string text)
        {
            bool attention = body.Attention == true;
            List<object> about = About(body);
            if (RiMCPMod.Settings != null && RiMCPMod.Settings.AgentMessages == UI.AgentActivity.MessagesOff)
            {
                AgentLog.Note(text, false, about.ToArray());
                return new CommandResult(false, Dto.Obj(
                    Dto.Field("shown", false),
                    Dto.Field("reason", "The player turned agent messages off in RimMCP settings, so the note is only in the agent log.")));
            }
            string loadId = GameContext.LoadId;
            int day = GenDate.DaysPassed;
            DateTime now = DateTime.UtcNow;
            string refusal = NoteLimits.Refusal(loadId, day, attention, now);
            if (refusal != null)
            {
                throw new CommandException(429, refusal);
            }
            NoteLimits.Use(loadId, day, attention, now);
            AgentLog.Note(text, attention, about.ToArray());
            return new CommandResult(true, Dto.Obj(
                Dto.Field("shown", true),
                Dto.Field("as", attention ? "letter" : "message")));
        }

        private static List<object> About(TellPlayerBody body)
        {
            List<object> about = new List<object>();
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return about;
            }
            foreach (string id in body.About ?? new string[0])
            {
                Thing thing = map.listerThings.AllThings.FirstOrDefault(each => each.Spawned && !each.Position.Fogged(map) && (each.ThingID == id || each.GetUniqueLoadID() == id));
                if (thing == null)
                {
                    throw new CommandException(404, "No thing '" + id + "' on the map shown.");
                }
                about.Add(thing);
            }
            if (body.X.HasValue)
            {
                IntVec3 cell = new IntVec3(body.X.Value, 0, body.Z.Value);
                if (!cell.InBounds(map))
                {
                    throw new CommandException(400, "That cell is off the map.");
                }
                about.Add(AgentLog.Cell(map, cell));
            }
            return about;
        }

        [DataContract]
        private sealed class TellPlayerBody
        {
            [DataMember(Name = "text")]
            public string Text { get; set; }

            [DataMember(Name = "about")]
            public string[] About { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "attention")]
            public bool? Attention { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
