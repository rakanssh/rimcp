using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RiMCP.Command;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    public sealed class Window_AgentLog : Window
    {
        private const int DefaultDetailLimit = 120;
        private const string DetailEllipsis = "...";
        private const float Gap = 6f;
        private const float OutcomeWidth = 92f;
        private static readonly Color Muted = new Color(0.62f, 0.62f, 0.62f);
        private readonly ConditionalWeakTable<AgentEntry, MetaCacheEntry> metaCache = new ConditionalWeakTable<AgentEntry, MetaCacheEntry>();
        private Vector2 scroll;

        private sealed class MetaCacheEntry
        {
            public (string When, string Client, string Detail, int Limit, LoadedLanguage Language) Inputs;
            public DisplayText Text;
        }

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
            UiDisplay.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), Keys.AgentActivity_LogTitle.Display());
            Text.Font = GameFont.Small;
            List<AgentEntry> entries = AgentLog.Entries;
            entries.Reverse();
            Rect outRect = new Rect(inRect.x, inRect.y + 38f, inRect.width, inRect.height - 38f);
            if (entries.Count == 0)
            {
                GUI.color = Muted;
                UiDisplay.Label(outRect, Keys.AgentActivity_LogEmpty.Display());
                GUI.color = Color.white;
                return;
            }
            float textWidth = outRect.width - 16f - OutcomeWidth - Gap * 3;
            int detailLimit = Keys.AgentActivity_LogDetailLimit.ReadLimit(DetailEllipsis.Length, DefaultDetailLimit);
            var rows = entries.Select(entry =>
            {
                DisplayText meta = CachedMeta(entry, detailLimit);
                return (Meta: meta, Height: RowHeight(entry, meta, textWidth));
            }).ToList();
            float dividers = Dividers(entries) * 24f;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, rows.Sum(row => row.Height) + dividers);
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
                    UiDisplay.Label(new Rect(Gap, y + 2f, view.width, 22f), (loadId == null ? Keys.AgentActivity_MainMenu : Keys.AgentActivity_EarlierGame).Display());
                    GUI.color = Color.white;
                    y += 24f;
                }
                var row = rows[i];
                DrawRow(new Rect(0f, y, view.width, row.Height), entry, row.Meta, textWidth);
                y += row.Height;
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

        private static float RowHeight(AgentEntry entry, DisplayText meta, float width)
        {
            Text.Font = GameFont.Small;
            float line = Text.CalcHeight(entry.Line ?? "", width);
            Text.Font = GameFont.Tiny;
            float metaHeight = Text.CalcHeight(meta.Value, width);
            Text.Font = GameFont.Small;
            return line + metaHeight + Gap * 2;
        }

        private static void DrawRow(Rect row, AgentEntry entry, DisplayText meta, float width)
        {
            bool jumps = AgentLog.CanJump(entry);
            if (jumps)
            {
                Widgets.DrawHighlightIfMouseover(row);
            }
            Rect outcome = new Rect(row.x + Gap, row.y + Gap, OutcomeWidth, 22f);
            GUI.color = entry.Note ? new Color(0.55f, 0.75f, 0.95f) : OutcomeColor(entry.Outcome);
            UiDisplay.Label(outcome, entry.Note ? (entry.Attention ? Keys.AgentActivity_SaidLetter : Keys.AgentActivity_Said).Display() : OutcomeDisplayLabel(entry.Outcome));
            GUI.color = Color.white;
            float x = outcome.xMax + Gap;
            float lineHeight = Text.CalcHeight(entry.Line ?? "", width);
            UiDisplay.Label(new Rect(x, row.y + Gap, width, lineHeight), DisplayText.Raw(entry.Line));
            Text.Font = GameFont.Tiny;
            GUI.color = Muted;
            UiDisplay.Label(new Rect(x, row.y + Gap + lineHeight, width, Text.CalcHeight(meta.Value, width)), meta);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                UiDisplay.Tip(row, DisplayText.Raw(entry.Detail));
            }
            if (jumps && Widgets.ButtonInvisible(row))
            {
                AgentLog.Jump(entry);
            }
        }

        private DisplayText CachedMeta(AgentEntry entry, int detailLimit)
        {
            MetaCacheEntry cached = metaCache.GetValue(entry, _ => new MetaCacheEntry());
            var inputs = (entry.When, entry.Client, entry.Detail, detailLimit, LanguageDatabase.activeLanguage);
            if (!inputs.Equals(cached.Inputs))
            {
                cached.Text = Meta(entry, detailLimit);
                cached.Inputs = inputs;
            }
            return cached.Text;
        }

        private static DisplayText Meta(AgentEntry entry, int detailLimit)
        {
            bool hasClient = !string.IsNullOrEmpty(entry.Client);
            if (string.IsNullOrEmpty(entry.Detail))
            {
                return hasClient ? Keys.AgentActivity_LogMetaClient.Display(entry.When, entry.Client) : DisplayText.Raw(entry.When);
            }
            string detail = TruncateDetail(entry.Detail, detailLimit);
            return hasClient ? Keys.AgentActivity_LogMetaClientDetail.Display(entry.When, entry.Client, detail)
                : Keys.AgentActivity_LogMetaDetail.Display(entry.When, detail);
        }

        private static string TruncateDetail(string detail, int limit)
        {
            if (detail.Length <= limit)
            {
                return detail;
            }
            int cutoff = limit - DetailEllipsis.Length;
            if (cutoff > 0 && char.IsSurrogatePair(detail, cutoff - 1))
            {
                cutoff--;
            }
            return detail.Substring(0, cutoff) + DetailEllipsis;
        }

        public static string OutcomeLabel(string outcome) => OutcomeDisplayLabel(outcome).Value;

        private static DisplayText OutcomeDisplayLabel(string outcome)
        {
            switch (outcome)
            {
                case Outcomes.Pending: return Keys.AgentActivity_Pending.Display();
                case Outcomes.Done: return Keys.AgentActivity_Done.Display();
                case Outcomes.PartlyDone: return Keys.AgentActivity_PartlyDone.Display();
                case Outcomes.NoChange: return Keys.AgentActivity_NoChange.Display();
                case Outcomes.Refused: return Keys.AgentActivity_Refused.Display();
                case Outcomes.Failed: return Keys.AgentActivity_Failed.Display();
                case Outcomes.Cancelled: return Keys.AgentActivity_Cancelled.Display();
                default: return DisplayText.Raw(outcome);
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
