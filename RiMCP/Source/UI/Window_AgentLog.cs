using System.Collections.Generic;
using System.Linq;
using RiMCP.Command;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    public sealed class Window_AgentLog : Window
    {
        private const float Gap = 6f;
        private const float OutcomeWidth = 92f;
        private static readonly Color Muted = new Color(0.62f, 0.62f, 0.62f);
        private Vector2 scroll;

        public Window_AgentLog()
        {
            doCloseX = true;
            draggable = true;
            resizeable = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            forcePause = false;
            onlyOneOfTypeAllowed = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(660f, 480f); }
        }

        public static void Open()
        {
            if (!Find.WindowStack.IsOpen<Window_AgentLog>())
            {
                Find.WindowStack.Add(new Window_AgentLog());
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "Agent log");
            Text.Font = GameFont.Small;
            List<AgentEntry> entries = AgentLog.Entries;
            entries.Reverse();
            Rect outRect = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f);
            if (entries.Count == 0)
            {
                GUI.color = Muted;
                Widgets.Label(outRect, "Nothing yet: changes agents make this session show here, newest first.");
                GUI.color = Color.white;
                return;
            }
            float textWidth = outRect.width - 16f - OutcomeWidth - Gap * 3;
            List<float> heights = entries.Select(entry => RowHeight(entry, textWidth)).ToList();
            float dividers = Dividers(entries) * 24f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, heights.Sum() + dividers);
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float y = 0f;
            string loadId = entries[0].LoadId;
            for (int i = 0; i < entries.Count; i++)
            {
                AgentEntry entry = entries[i];
                if (entry.LoadId != loadId)
                {
                    loadId = entry.LoadId;
                    GUI.color = Muted;
                    Widgets.Label(new Rect(Gap, y + 2f, view.width, 22f), loadId == null ? "At the main menu" : "In an earlier game");
                    GUI.color = Color.white;
                    y += 24f;
                }
                DrawRow(new Rect(0f, y, view.width, heights[i]), entry, textWidth);
                y += heights[i];
            }
            Widgets.EndScrollView();
        }

        private static int Dividers(List<AgentEntry> entries)
        {
            int count = 0;
            for (int i = 1; i < entries.Count; i++)
            {
                count += entries[i].LoadId != entries[i - 1].LoadId ? 1 : 0;
            }
            return count;
        }

        private static float RowHeight(AgentEntry entry, float width)
        {
            Text.Font = GameFont.Small;
            float line = Text.CalcHeight(entry.Line ?? "", width);
            Text.Font = GameFont.Tiny;
            float meta = Text.CalcHeight(Meta(entry), width);
            Text.Font = GameFont.Small;
            return line + meta + Gap * 2;
        }

        private static void DrawRow(Rect row, AgentEntry entry, float width)
        {
            bool jumps = AgentLog.CanJump(entry);
            if (jumps)
            {
                Widgets.DrawHighlightIfMouseover(row);
            }
            Rect outcome = new Rect(row.x + Gap, row.y + Gap, OutcomeWidth, 22f);
            GUI.color = entry.Note ? new Color(0.55f, 0.75f, 0.95f) : OutcomeColor(entry.Outcome);
            Widgets.Label(outcome, entry.Note ? (entry.Attention ? "said (letter)" : "said") : OutcomeLabel(entry.Outcome));
            GUI.color = Color.white;
            float x = outcome.xMax + Gap;
            float lineHeight = Text.CalcHeight(entry.Line ?? "", width);
            Widgets.Label(new Rect(x, row.y + Gap, width, lineHeight), entry.Line);
            Text.Font = GameFont.Tiny;
            GUI.color = Muted;
            string meta = Meta(entry);
            Widgets.Label(new Rect(x, row.y + Gap + lineHeight, width, Text.CalcHeight(meta, width)), meta);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                TooltipHandler.TipRegion(row, entry.Detail);
            }
            if (jumps && Widgets.ButtonInvisible(row))
            {
                AgentLog.Jump(entry);
            }
        }

        private static string Meta(AgentEntry entry)
        {
            List<string> parts = new List<string> { entry.When };
            if (!string.IsNullOrEmpty(entry.Client))
            {
                parts.Add(entry.Client);
            }
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                parts.Add(entry.Detail.Length > 120 ? entry.Detail.Substring(0, 117) + "..." : entry.Detail);
            }
            return string.Join(" · ", parts.Where(part => !string.IsNullOrEmpty(part)).ToArray());
        }

        public static string OutcomeLabel(string outcome)
        {
            switch (outcome)
            {
                case Outcomes.Pending: return "in progress";
                case Outcomes.PartlyDone: return "partly done";
                case Outcomes.NoChange: return "no change";
                default: return outcome;
            }
        }

        private static Color OutcomeColor(string outcome)
        {
            switch (outcome)
            {
                case Outcomes.Done: return new Color(0.45f, 0.8f, 0.5f);
                case Outcomes.Pending: return new Color(0.95f, 0.8f, 0.35f);
                case Outcomes.PartlyDone: return new Color(0.95f, 0.65f, 0.3f);
                case Outcomes.Refused: return new Color(0.9f, 0.45f, 0.4f);
                case Outcomes.Failed: return new Color(1f, 0.3f, 0.3f);
                default: return Muted;
            }
        }
    }
}
