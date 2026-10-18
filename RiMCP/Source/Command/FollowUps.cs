using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class FollowUps
    {
        public static List<Window> Snapshot()
        {
            WindowStack stack = Find.WindowStack;
            return stack == null ? new List<Window>() : stack.Windows.ToList();
        }

        public static List<Window> OpenedSince(List<Window> before)
        {
            WindowStack stack = Find.WindowStack;
            return stack == null ? new List<Window>() : stack.Windows.Where(window => !before.Contains(window)).ToList();
        }

        public static void Report(Dictionary<string, object> data, string tool, string subject, IEnumerable<Window> opened, Interaction continuing = null, Thing selected = null, ActionOrigin origin = null)
        {
            List<object> dialogs = new List<object>();
            if (continuing != null && continuing.State == Interaction.Open)
            {
                dialogs.Add(Interactions.Describe(continuing));
            }
            List<string> left = new List<string>();
            foreach (Window window in opened.Where(each => Find.WindowStack.IsOpen(each) && !Interactions.Owns(each)))
            {
                DialogAdapter dialog = DialogAdapter.For(window);
                if (dialog == null)
                {
                    left.Add(window.GetType().Name);
                    continue;
                }
                Interaction interaction = Interactions.Start(tool, subject, dialog, selected ?? (continuing == null ? null : continuing.Selected), origin ?? (continuing == null ? null : continuing.Origin));
                dialogs.Add(Interactions.Describe(interaction));
            }
            if (dialogs.Count > 0)
            {
                data["dialogs"] = dialogs.ToArray();
                AdvanceRun ended = AdvanceRunner.EndForDialog("A dialog opened by " + tool + " is waiting for an answer.");
                if (ended != null)
                {
                    data["endedAdvance"] = TimeCommandService.SerializeRun(ended);
                }
            }
            if (left.Count > 0)
            {
                data["windowsLeftForPlayer"] = left.ToArray();
            }
        }

        // So the player sees what was decided for them, in game and in the History tab.
        public static void Announce(string text, LookTargets targets = null)
        {
            if (targets != null && targets.IsValid)
            {
                Messages.Message("RimMCP " + text, targets, MessageTypeDefOf.NeutralEvent);
            }
            else
            {
                Messages.Message("RimMCP " + text, MessageTypeDefOf.NeutralEvent);
            }
        }
    }
}
