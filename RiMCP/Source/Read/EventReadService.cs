using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Command;
using Verse;

namespace RiMCP.Read
{
    internal static class EventReadService
    {
        private const int DefaultHistory = 20;
        private const int MaxHistory = 100;
        private const int MaxCulprits = 10;
        private const int MaxTargets = 5;

        private static readonly FieldInfo ActiveAlerts = typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.Instance | BindingFlags.NonPublic);

        public static BridgeResponse GetEvents(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                throw new BridgeException(409, "No game is loaded.");
            }
            int? limit = context.Request.OptionalInt("limit");
            bool full = context.Request.Detail != ReadDetail.Summary;
            HistoryPage history = EventHistory.Page(HistoryEntries(), context.Request.Get("since"),
                Math.Max(1, Math.Min(MaxHistory, limit ?? DefaultHistory)), GameContext.LoadId, RealTime.frameCount, context.Tick, HistoryWatch.Loss);

            object[] dialogs = Interactions.OpenNow.Select(Interactions.Describe).ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("dialogs", dialogs.Length == 0 ? null : dialogs),
                Dto.Field("alerts", Alerts(full)),
                Dto.Field("letters", Letters(context.Tick)),
                Dto.Field("history", Dto.Obj(
                    Dto.Field("entries", history.Entries.Select(entry => Entry(entry, full)).ToArray()),
                    Dto.Field("repeated", history.Repeated.Count == 0 ? null : history.Repeated.Select(entry => Entry(entry, false)).ToArray()),
                    Dto.Field("more", history.More),
                    Dto.Field("reset", history.Reset ? (object)true : null),
                    Dto.Field("incomplete", history.Incomplete ? (object)true : null),
                    Dto.Field("cursor", history.Next.Encode())))));
        }

        private static List<object> Alerts(bool full)
        {
            List<object> alerts = new List<object>();
            UIRoot_Play root = Find.UIRoot as UIRoot_Play;
            List<Alert> active = root == null || root.alerts == null || ActiveAlerts == null ? null : ActiveAlerts.GetValue(root.alerts) as List<Alert>;
            if (active == null)
            {
                return alerts;
            }
            foreach (Alert alert in active.ToList().OrderByDescending(alert => alert.Priority))
            {
                try
                {
                    AlertReport report = alert.GetReport();
                    if (!report.active)
                    {
                        continue;
                    }
                    string label = alert.Label;
                    List<object> culprits = report.AllCulprits.Select(Target).Where(target => target != null).ToList();
                    alerts.Add(Dto.Obj(
                        Dto.Field("type", alert.GetType().Name),
                        Dto.Field("label", Clean(string.IsNullOrEmpty(label) ? alert.GetLabel() : label)),
                        Dto.Field("priority", alert.Priority.ToString()),
                        Dto.Field("explanation", full ? Clean(alert.GetExplanation().ToString()) : null),
                        Dto.Field("culprits", culprits.Count == 0 ? null : culprits.Take(MaxCulprits).ToArray()),
                        Dto.Field("culpritCount", culprits.Count == 0 ? null : (object)culprits.Count),
                        Dto.Field("truncated", culprits.Count > MaxCulprits ? (object)true : null)));
                }
                catch (Exception)
                {
                    // An alert that cannot report itself now is left out, as it would draw nothing.
                }
            }
            return alerts;
        }

        private static List<object> Letters(int tick)
        {
            List<object> letters = new List<object>();
            if (Find.LetterStack == null)
            {
                return letters;
            }
            foreach (Letter letter in Find.LetterStack.LettersListForReading.ToList())
            {
                if (!letter.CanShowInLetterStack)
                {
                    continue;
                }
                ChoiceLetter choice = letter as ChoiceLetter;
                LetterWithTimeout timed = letter as LetterWithTimeout;
                Quest quest = choice == null || choice.quest == null || choice.quest.hidden ? null : choice.quest;
                List<LetterChoice> choices = choice == null ? new List<LetterChoice>() : LetterChoices.Of(choice);
                bool answerable = LetterChoices.Answerable(choices);
                letters.Add(Dto.Obj(
                    Dto.Field("id", letter.ID),
                    Dto.Field("label", Clean(letter.Label.ToString())),
                    Dto.Field("type", letter.def == null ? null : letter.def.defName),
                    Dto.Field("tick", letter.arrivalTick),
                    Dto.Field("text", Clean(choice != null ? choice.Text.ToString() : ((IArchivable)letter).ArchivedTooltip)),
                    Dto.Field("faction", letter.relatedFaction == null ? null : letter.relatedFaction.Name),
                    Dto.Field("quest", quest == null ? null : Dto.Obj(Dto.Field("id", quest.id), Dto.Field("name", Clean(quest.name)))),
                    Dto.Field("expiresInTicks", timed != null && timed.TimeoutActive ? (object)Math.Max(0, timed.disappearAtTick - tick) : null),
                    Dto.Field("canDismiss", letter.CanDismissWithRightClick),
                    Dto.Field("targets", Targets(letter.lookTargets)),
                    // A letter with its own window, which cannot be dismissed, is answered by the player.
                    Dto.Field("answeredBy", choice == null && !letter.CanDismissWithRightClick ? "player" : null),
                    Dto.Field("reason", choice == null && !letter.CanDismissWithRightClick ? LeftToPlayer(letter) : null),
                    Dto.Field("revision", answerable ? LetterChoices.Revision(GameContext.LoadId, letter, choices) : null),
                    Dto.Field("options", answerable ? choices.Select(option => (object)Dto.Obj(
                        Dto.Field("id", option.Id),
                        Dto.Field("text", option.Text),
                        Dto.Field("disabledReason", option.DisabledReason))).ToArray() : null)));
            }
            return letters;
        }

        public static string LeftToPlayer(Letter letter)
        {
            return letter is ChoiceLetter_GrowthMoment
                ? "is answered in its own window, choosing what a child learns at a growth moment; agents cannot use it yet, so it is left to the player."
                : "opens its own window, which agents cannot answer yet; it is left to the player.";
        }

        private static List<HistoryEntry> HistoryEntries()
        {
            Archive archive = Find.Archive;
            return archive == null ? new List<HistoryEntry>() : archive.ArchivablesListForReading.Where(archivable => archivable != null).Select(HistoryEntry.Of).ToList();
        }

        private static object Entry(HistoryEntry entry, bool full)
        {
            IArchivable archivable = (IArchivable)entry.Source;
            Letter letter = archivable as Letter;
            Message message = archivable as Message;
            string label = Clean(archivable.ArchivedLabel);
            string text = full && message == null ? Clean(archivable.ArchivedTooltip) : null;
            return Dto.Obj(
                Dto.Field("id", entry.LoadId),
                Dto.Field("kind", letter != null ? "letter" : message != null ? "message" : archivable is ArchivedDialog ? "dialog" : archivable.GetType().Name),
                Dto.Field("type", letter != null && letter.def != null ? letter.def.defName : message != null && message.def != null ? message.def.defName : null),
                Dto.Field("tick", entry.Tick),
                Dto.Field("label", label),
                Dto.Field("text", text == label ? null : text),
                Dto.Field("waiting", letter != null && Find.LetterStack != null && Find.LetterStack.LettersListForReading.Contains(letter) ? (object)true : null),
                Dto.Field("targets", Targets(archivable.LookTargets)));
        }

        private static object Targets(LookTargets lookTargets)
        {
            if (lookTargets == null || lookTargets.targets == null)
            {
                return null;
            }
            object[] targets = lookTargets.targets.Select(Target).Where(target => target != null).Take(MaxTargets).ToArray();
            return targets.Length == 0 ? null : targets;
        }

        public static object Target(GlobalTargetInfo target)
        {
            if (!target.IsValid)
            {
                return null;
            }
            if (target.HasThing)
            {
                Thing thing = target.Thing;
                Pawn pawn = thing as Pawn;
                Map map = thing.Destroyed ? null : thing.MapHeld;
                return Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(thing)),
                    Dto.Field("label", Clean(pawn != null ? pawn.LabelShortCap : thing.LabelCap)),
                    Dto.Field("mapId", map == null ? null : map.uniqueID.ToString()),
                    Dto.Field("position", map == null ? null : ReadUtil.Cell(thing.PositionHeld)));
            }
            if (target.HasWorldObject)
            {
                WorldObject worldObject = target.WorldObject;
                return Dto.Obj(
                    Dto.Field("worldObjectId", worldObject.ID),
                    Dto.Field("label", Clean(worldObject.LabelCap)),
                    Dto.Field("tile", ReadUtil.TileId(worldObject.Tile)));
            }
            if (target.Map != null && target.Cell.IsValid)
            {
                return Dto.Obj(
                    Dto.Field("mapId", target.Map.uniqueID.ToString()),
                    Dto.Field("position", ReadUtil.Cell(target.Cell)));
            }
            return Dto.Obj(Dto.Field("tile", ReadUtil.TileId(target.Tile)));
        }

        private static string Clean(string text)
        {
            return string.IsNullOrEmpty(text) ? text : text.StripTags().Trim();
        }
    }
}
