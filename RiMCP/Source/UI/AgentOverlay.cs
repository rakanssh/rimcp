using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld.Planet;
using RiMCP.Command;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // An agent cursor gliding over the map to where an agent acts, and speech bubbles by the RimMCP
    // button for what agents say. Drawn from the button's own pass, so nothing enters saves; while
    // the button is hidden, or if a game version lacks something used here, AgentActivity falls back
    // to the game's messages. Main thread only.
    internal static class AgentOverlay
    {
        private const float Glide = 0.45f;
        private const float Press = 0.15f;
        private const float Linger = 2.5f;
        private const float FadeOut = 0.8f;
        private const float BubbleWidth = 300f;
        private const int MaxBubbles = 4;
        private static readonly Color[] Palette =
        {
            new Color(0.45f, 0.78f, 1f),
            new Color(1f, 0.72f, 0.35f),
            new Color(0.6f, 0.9f, 0.5f),
            new Color(0.95f, 0.55f, 0.8f),
            new Color(0.8f, 0.7f, 1f),
        };
        private static readonly Dictionary<string, Pointer> pointers = new Dictionary<string, Pointer>();
        private static readonly List<Bubble> bubbles = new List<Bubble>();
        private static float drawnAt = -10f;
        private static bool pointersBroken;
        private static bool bubblesBroken;

        public static bool Showing
        {
            get { return !bubblesBroken && Time.realtimeSinceStartup - drawnAt < 1f; }
        }

        public static bool Pointing
        {
            get { return !pointersBroken && Showing; }
        }

        // Moves the agent's cursor to the cell; returns the seconds until it gets there.
        public static float Point(string who, Map map, IntVec3 cell)
        {
            float now = Time.realtimeSinceStartup;
            Pointer pointer;
            if (!pointers.TryGetValue(who, out pointer))
            {
                pointer = new Pointer();
                pointers[who] = pointer;
            }
            string mapId = map.uniqueID.ToString();
            bool seen = pointer.MapId == mapId && Alpha(pointer, now) > 0f;
            Vector3 to = cell.ToVector3Shifted();
            pointer.From = seen ? At(pointer, now) : to;
            pointer.To = to;
            pointer.MapId = mapId;
            pointer.MoveStart = now;
            pointer.Arrive = now + (seen ? Glide : 0f);
            if (!seen)
            {
                pointer.Appeared = now;
            }
            return pointer.Arrive - now;
        }

        // Longer text stays longer.
        public static void Say(string who, string text, AgentEntry entry)
        {
            Add(new Bubble { Who = who, Text = text, Entry = entry, Born = Time.realtimeSinceStartup, Life = Mathf.Clamp(5f + text.Length * 0.05f, 7f, 16f) });
        }

        // A burst shows as one bubble that counts up.
        public static void Fold(string who, AgentEntry entry)
        {
            float now = Time.realtimeSinceStartup;
            Bubble bubble = bubbles.LastOrDefault(each => each.Fold && each.Who == who && Remaining(each, now) > 1f);
            if (bubble == null)
            {
                bubble = new Bubble { Who = who, Fold = true, Life = 6f };
                Add(bubble);
            }
            bubble.Count++;
            bubble.Entry = entry;
            bubble.Born = now;
            bubble.Text = "Made " + AgentLog.Count(bubble.Count, "more change") + ". Click to open the agent log.";
        }

        public static void Draw(Rect button)
        {
            float now = Time.realtimeSinceStartup;
            try
            {
                if (!pointersBroken && Event.current.type == EventType.Repaint)
                {
                    try
                    {
                        DrawPointers(now);
                    }
                    catch (Exception e)
                    {
                        pointersBroken = true;
                        Log.Warning("RimMCP: the agent cursor is off; agent actions still flash on the map. " + e.Message);
                    }
                }
                if (!bubblesBroken)
                {
                    try
                    {
                        DrawBubbles(button, now);
                        drawnAt = now;
                    }
                    catch (Exception e)
                    {
                        bubblesBroken = true;
                        Log.Warning("RimMCP: agent bubbles are off; agent messages show as game messages instead. " + e.Message);
                    }
                }
            }
            finally
            {
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }

        private static void DrawPointers(float now)
        {
            Map map = Find.CurrentMap;
            if (map == null || !WorldRendererUtility.DrawingMap)
            {
                return;
            }
            string mapId = map.uniqueID.ToString();
            Texture2D texture = AgentCursorArt.Cursor;
            foreach (KeyValuePair<string, Pointer> pair in pointers)
            {
                Pointer pointer = pair.Value;
                float alpha = Alpha(pointer, now);
                if (alpha <= 0f || pointer.MapId != mapId)
                {
                    continue;
                }
                Color color = ColorOf(pair.Key);
                Vector2 tip = At(pointer, now).MapToUIPosition();
                float scale = (now >= pointer.Arrive && now - pointer.Arrive < Press ? 0.85f : 1f) / Prefs.UIScale;
                float size = (texture == null ? 16f : texture.width) * scale;
                GUI.color = new Color(color.r, color.g, color.b, alpha);
                if (texture != null)
                {
                    GUI.DrawTexture(new Rect(tip.x - 3f * scale, tip.y - 3f * scale, size, size), texture);
                }
                else
                {
                    Widgets.DrawBoxSolid(new Rect(tip.x - 4f, tip.y - 4f, 8f, 8f), GUI.color);
                }
                Text.Font = GameFont.Tiny;
                Vector2 label = Text.CalcSize(pair.Key);
                Rect tag = new Rect(tip.x + size * 0.55f, tip.y + size * 0.8f, label.x + 8f, label.y + 2f);
                Widgets.DrawBoxSolid(tag, new Color(0.08f, 0.08f, 0.08f, 0.8f * alpha));
                Widgets.Label(new Rect(tag.x + 4f, tag.y + 1f, label.x + 2f, label.y), pair.Key);
            }
        }

        private static void DrawBubbles(Rect button, float now)
        {
            if (Event.current.type == EventType.Repaint)
            {
                bubbles.RemoveAll(each => Remaining(each, now) <= 0f);
            }
            // Left of the alerts and letters on the right edge.
            float right = Mathf.Min(button.xMax, Verse.UI.screenWidth - 160f);
            float x = Mathf.Max(10f, right - BubbleWidth);
            float bottom = button.y - 8f;
            for (int i = bubbles.Count - 1; i >= 0; i--)
            {
                Bubble bubble = bubbles[i];
                Text.Font = GameFont.Small;
                float textHeight = Text.CalcHeight(bubble.Text, BubbleWidth - 20f);
                Rect rect = new Rect(x, bottom - textHeight - 30f, BubbleWidth, textHeight + 30f);
                if (rect.y < 80f)
                {
                    break;
                }
                bottom = rect.y - 6f;
                if (Mouse.IsOver(rect))
                {
                    bubble.Born = Mathf.Max(bubble.Born, now - bubble.Life + 3f);
                }
                DrawBubble(bubble, rect, Mathf.Clamp01(Remaining(bubble, now) / FadeOut));
            }
        }

        private static void DrawBubble(Bubble bubble, Rect rect, float alpha)
        {
            Color color = ColorOf(bubble.Who);
            Widgets.DrawBoxSolid(rect, new Color(0.32f, 0.32f, 0.34f, 0.95f * alpha));
            Widgets.DrawBoxSolid(rect.ContractedBy(1f), new Color(0.11f, 0.12f, 0.13f, 0.94f * alpha));
            Widgets.DrawBoxSolid(new Rect(rect.x + 1f, rect.y + 1f, 3f, rect.height - 2f), new Color(color.r, color.g, color.b, alpha));
            Widgets.DrawHighlightIfMouseover(rect);

            Text.Font = GameFont.Tiny;
            Vector2 logSize = Text.CalcSize("Log");
            Rect log = new Rect(rect.xMax - logSize.x - 10f, rect.y + 4f, logSize.x + 4f, 18f);
            GUI.color = new Color(color.r, color.g, color.b, alpha);
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 4f, log.x - rect.x - 14f, 18f), bubble.Who);
            GUI.color = new Color(0.8f, 0.8f, 0.8f, alpha * (Mouse.IsOver(log) ? 1f : 0.6f));
            Widgets.Label(log, "Log");

            Text.Font = GameFont.Small;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            Widgets.Label(new Rect(rect.x + 10f, rect.y + 22f, rect.width - 20f, rect.height - 26f), bubble.Text);
            GUI.color = Color.white;

            bool jump = !bubble.Fold && AgentLog.CanJump(bubble.Entry);
            TooltipHandler.TipRegion(rect, (jump ? "Click to jump to it" : "Click to open the agent log") + "; right-click to dismiss.");
            if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(rect))
            {
                bubble.Dismissed = true;
                Event.current.Use();
            }
            else if (Widgets.ButtonInvisible(log))
            {
                Window_AgentLog.Open();
            }
            else if (Widgets.ButtonInvisible(rect))
            {
                if (jump)
                {
                    AgentLog.Jump(bubble.Entry);
                }
                else
                {
                    Window_AgentLog.Open();
                }
            }
        }

        private static void Add(Bubble bubble)
        {
            bubbles.Add(bubble);
            while (bubbles.Count > MaxBubbles)
            {
                bubbles.RemoveAt(0);
            }
        }

        private static float Remaining(Bubble bubble, float now)
        {
            return bubble.Dismissed ? 0f : bubble.Born + bubble.Life - now;
        }

        private static float Alpha(Pointer pointer, float now)
        {
            float idle = now - pointer.Arrive - Linger;
            float fading = idle <= 0f ? 1f : 1f - idle / FadeOut;
            return Mathf.Clamp01(Mathf.Min(fading, (now - pointer.Appeared) / 0.15f));
        }

        private static Vector3 At(Pointer pointer, float now)
        {
            float span = pointer.Arrive - pointer.MoveStart;
            float t = span <= 0f ? 1f : Mathf.Clamp01((now - pointer.MoveStart) / span);
            t = 1f - (1f - t) * (1f - t) * (1f - t);
            return Vector3.Lerp(pointer.From, pointer.To, t);
        }

        // Not string.GetHashCode, which can change between runs, so an agent keeps its color.
        private static Color ColorOf(string who)
        {
            int hash = 0;
            foreach (char c in who)
            {
                hash = unchecked(hash * 31 + c);
            }
            return Palette[(hash & int.MaxValue) % Palette.Length];
        }

        private sealed class Pointer
        {
            public string MapId;
            public Vector3 From;
            public Vector3 To;
            public float MoveStart;
            public float Arrive;
            public float Appeared;
        }

        private sealed class Bubble
        {
            public string Who;
            public string Text;
            public AgentEntry Entry;
            public float Born;
            public float Life;
            public bool Fold;
            public int Count;
            public bool Dismissed;
        }
    }

    // Loaded at startup on the main thread, as the game requires of textures kept in fields. Apart
    // from AgentOverlay, so a failure here turns off only the cursor.
    [StaticConstructorOnStartup]
    internal static class AgentCursorArt
    {
        public static readonly Texture2D Cursor = ContentFinder<Texture2D>.Get("UI/Cursors/CursorCustom", false);
    }
}
