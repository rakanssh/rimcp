using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Shared;
using RiMCP.Util;
using UnityEngine;
using Verse;

namespace RiMCP
{
    public sealed class RiMCPMod : Mod
    {
        public static RiMCPSettings Settings;
        private static string modRootPath;

        public RiMCPMod(ModContentPack content) : base(content)
        {
            modRootPath = content.RootDir;
            Settings = GetSettings<RiMCPSettings>();
            Settings.EnsureToken();
            BridgeRuntime.Configure(Settings);
            RiMCPUnityDriver.EnsureStarted();
            BridgeRuntime.ApplySettings();
        }

        public override string SettingsCategory()
        {
            return "RimMCP";
        }

        private enum SettingsTab
        {
            Access,
            Connection,
            Activity,
            Safety
        }

        private const float HeaderHeight = 34f;
        private const float TabHeight = 30f;
        private const float GroupRowHeight = 30f;
        private const float ToolRowHeight = 46f;

        private static SettingsTab tab = SettingsTab.Access;
        private static string selectedGroup = ToolGroups.Observe;
        private static string search = "";
        private static Vector2 toolScroll;
        private static Vector2 activityScroll;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.EnsureToken();
            Settings.EnsureAccess();
            DoHeader(new Rect(inRect.x, inRect.y, inRect.width, HeaderHeight));
            Rect tabs = new Rect(inRect.x, inRect.y + HeaderHeight + 6f, inRect.width, TabHeight);
            DoTabs(tabs);
            Rect body = new Rect(inRect.x, tabs.yMax + 10f, inRect.width, inRect.yMax - tabs.yMax - 10f);
            switch (tab)
            {
                case SettingsTab.Access:
                    DoAccessTab(body);
                    break;
                case SettingsTab.Connection:
                    DoConnectionTab(body);
                    break;
                case SettingsTab.Activity:
                    DoActivityTab(body);
                    break;
                default:
                    DoSafetyTab(body);
                    break;
            }
            base.DoSettingsWindowContents(inRect);
        }

        // The status line and the master switch: Agents on or off.
        private static void DoHeader(Rect rect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 140f, rect.height), "RimMCP");
            Text.Font = GameFont.Small;
            string status = !Settings.BridgeEnabled ? "Agents are off"
                : !BridgeRuntime.IsRunning ? "Not running; see Activity"
                : "Running · " + BridgeRuntime.RecentClientCount + (BridgeRuntime.RecentClientCount == 1 ? " client" : " clients") + " · last request " + BridgeRuntime.LastRequestDescription;
            GUI.color = Settings.BridgeEnabled && BridgeRuntime.IsRunning ? new Color(0.55f, 0.85f, 0.55f) : Color.gray;
            LabelMiddle(new Rect(rect.x + 120f, rect.y, rect.width - 320f, rect.height), status);
            GUI.color = Color.white;
            bool enabled = Settings.BridgeEnabled;
            Widgets.CheckboxLabeled(new Rect(rect.xMax - 150f, rect.y + 5f, 150f, 24f), "Agents on", ref enabled);
            TooltipHandler.TipRegion(new Rect(rect.xMax - 150f, rect.y, 150f, rect.height), "The master switch. Off stops the bridge, so no agent can read or change anything, and a running advance or operation stops. Your access choices are kept.");
            if (enabled != Settings.BridgeEnabled)
            {
                Settings.BridgeEnabled = enabled;
                BridgeRuntime.ApplySettings();
            }
        }

        private static void DoTabs(Rect rect)
        {
            float x = rect.x;
            foreach (SettingsTab each in new[] { SettingsTab.Access, SettingsTab.Connection, SettingsTab.Activity, SettingsTab.Safety })
            {
                Rect button = new Rect(x, rect.y, 130f, rect.height);
                if (Button(button, each.ToString()))
                {
                    tab = each;
                }
                if (each == tab)
                {
                    Widgets.DrawBoxSolid(new Rect(button.x, button.yMax - 3f, button.width, 3f), new Color(0.85f, 0.85f, 0.85f));
                }
                x += button.width + 6f;
            }
            Widgets.DrawLineHorizontal(rect.x, rect.yMax + 2f, rect.width);
        }

        private static void DoAccessTab(Rect rect)
        {
            float y = rect.y;
            LabelMiddle(new Rect(rect.x, y, 110f, 30f), "Access level");
            float x = rect.x + 110f;
            foreach (string preset in AccessPresets.Named.Concat(new[] { AccessPresets.Custom }))
            {
                Rect button = new Rect(x, y, 128f, 30f);
                bool current = Settings.AccessPreset == preset;
                if (Button(button, AccessPresets.Label(preset), preset != AccessPresets.Custom) && preset != AccessPresets.Custom && !current)
                {
                    Settings.SetPreset(preset);
                    BridgeRuntime.ApplySettings();
                }
                if (current)
                {
                    Widgets.DrawBox(button, 2);
                }
                TooltipHandler.TipRegion(button, AccessPresets.Description(preset));
                x += button.width + 4f;
            }
            y += 36f;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x, y, rect.width, 24f), Settings.BridgeEnabled ? AccessPresets.Description(Settings.AccessPreset) : "Agents are off. Nothing runs until you turn them back on; these choices apply then.");
            GUI.color = Color.white;
            y += 30f;

            LabelMiddle(new Rect(rect.x, y, 60f, 28f), "Search");
            search = Widgets.TextField(new Rect(rect.x + 60f, y, 300f, 28f), search ?? "");
            if (!string.IsNullOrEmpty(search) && Button(new Rect(rect.x + 366f, y, 70f, 28f), "Clear"))
            {
                search = "";
            }
            y += 36f;

            float footer = 24f;
            Rect groups = new Rect(rect.x, y, 240f, rect.yMax - y - footer);
            Rect tools = new Rect(groups.xMax + 12f, y, rect.xMax - groups.xMax - 12f, groups.height);
            DoGroups(groups);
            DoTools(tools);

            List<ToolSpec> all = ToolManifest.McpTools.ToList();
            int on = all.Count(Settings.IsToolOn);
            GUI.color = Color.gray;
            Widgets.Label(new Rect(rect.x, rect.yMax - footer + 4f, rect.width, footer), on + " of " + all.Count + " tools on. Agents see only the tools that are on, so the list they load stays short.");
            GUI.color = Color.white;
        }

        // Each group with how many of its tools are on; its checkbox turns the whole group on or off.
        private static void DoGroups(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect inner = rect.ContractedBy(4f);
            float y = inner.y;
            foreach (string group in ToolGroups.All)
            {
                List<ToolSpec> members = ToolManifest.McpTools.Where(spec => spec.Group == group).ToList();
                if (members.Count == 0)
                {
                    continue;
                }
                int on = members.Count(Settings.IsToolOn);
                Rect row = new Rect(inner.x, y, inner.width, GroupRowHeight);
                if (group == selectedGroup && string.IsNullOrEmpty(search))
                {
                    Widgets.DrawHighlightSelected(row);
                }
                Widgets.DrawHighlightIfMouseover(row);
                MultiCheckboxState state = on == members.Count ? MultiCheckboxState.On : on == 0 ? MultiCheckboxState.Off : MultiCheckboxState.Partial;
                Rect box = new Rect(row.x + 4f, row.y + 3f, 24f, 24f);
                if (Widgets.CheckboxMulti(box, state) != state)
                {
                    Settings.SetGroup(group, state != MultiCheckboxState.On);
                    BridgeRuntime.ApplySettings();
                }
                Rect label = new Rect(box.xMax + 8f, row.y, row.width - box.width - 60f, row.height);
                LabelMiddle(label, ToolGroups.Label(group));
                GUI.color = Color.gray;
                LabelRight(new Rect(row.xMax - 52f, row.y, 48f, row.height), on + "/" + members.Count);
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(label))
                {
                    selectedGroup = group;
                    search = "";
                    toolScroll = Vector2.zero;
                }
                TooltipHandler.TipRegion(row, ToolGroups.Description(group));
                y += GroupRowHeight + 2f;
            }
        }

        // The selected group's tools, or every tool matching the search, each with its own checkbox.
        private static void DoTools(Rect rect)
        {
            string query = (search ?? "").Trim();
            List<ToolSpec> shown = string.IsNullOrEmpty(query)
                ? ToolManifest.McpTools.Where(spec => spec.Group == selectedGroup).ToList()
                : ToolManifest.McpTools.Where(spec => spec.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (spec.Description ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            float y = rect.y;
            Text.Font = GameFont.Medium;
            LabelMiddle(new Rect(rect.x, y, rect.width - 200f, 30f), string.IsNullOrEmpty(query) ? ToolGroups.Label(selectedGroup) : shown.Count + (shown.Count == 1 ? " matching tool" : " matching tools"));
            Text.Font = GameFont.Small;
            if (string.IsNullOrEmpty(query))
            {
                if (Button(new Rect(rect.xMax - 196f, y, 94f, 28f), "All on"))
                {
                    Settings.SetGroup(selectedGroup, true);
                    BridgeRuntime.ApplySettings();
                }
                if (Button(new Rect(rect.xMax - 96f, y, 94f, 28f), "All off"))
                {
                    Settings.SetGroup(selectedGroup, false);
                    BridgeRuntime.ApplySettings();
                }
                y += 32f;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(rect.x, y, rect.width, 24f), ToolGroups.Description(selectedGroup));
                GUI.color = Color.white;
                y += 26f;
            }
            else
            {
                y += 34f;
            }
            Rect outRect = new Rect(rect.x, y, rect.width, rect.yMax - y);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, shown.Count * ToolRowHeight);
            Widgets.BeginScrollView(outRect, ref toolScroll, view);
            float rowY = 0f;
            foreach (ToolSpec spec in shown)
            {
                DoToolRow(new Rect(0f, rowY, view.width, ToolRowHeight), spec, !string.IsNullOrEmpty(query));
                rowY += ToolRowHeight;
            }
            Widgets.EndScrollView();
        }

        private static void DoToolRow(Rect row, ToolSpec spec, bool showGroup)
        {
            Widgets.DrawHighlightIfMouseover(row);
            bool on = Settings.IsToolOn(spec);
            bool next = on;
            Widgets.Checkbox(new Vector2(row.x + 2f, row.y + 11f), ref next);
            if (next != on)
            {
                Settings.SetTool(spec, next);
                BridgeRuntime.ApplySettings();
            }
            float x = row.x + 34f;
            Widgets.Label(new Rect(x, row.y + 2f, row.width - 34f, 22f), spec.Name);
            float after = x + Text.CalcSize(spec.Name).x + 10f;
            object destructive;
            if (spec.Annotations.TryGetValue("destructiveHint", out destructive) && destructive is bool && (bool)destructive)
            {
                GUI.color = new Color(0.95f, 0.7f, 0.3f);
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(after, row.y + 4f, 140f, 20f), "can undo player work");
                after += 130f;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            if (showGroup)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(after, row.y + 4f, 160f, 20f), ToolGroups.Label(spec.Group));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(x, row.y + 23f, row.width - 34f, 20f), Summary(spec.Description).Truncate(row.width - 40f));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            TooltipHandler.TipRegion(row, spec.Name + "\n\n" + spec.Description);
        }

        // Labels centred vertically, or also right-aligned, measured rather than anchored, which keeps
        // to the Unity modules the mod already references.
        // A button drawn from the game's button graphic, without the text-anchor parameter of
        // Widgets.ButtonText. An inactive button is dimmed and does nothing.
        private static bool Button(Rect rect, string label, bool active = true)
        {
            Color previous = GUI.color;
            if (!active)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.5f);
            }
            Widgets.DrawButtonGraphic(rect);
            float width = Math.Min(rect.width, Text.CalcSize(label).x + 1f);
            LabelMiddle(new Rect(rect.center.x - width / 2f, rect.y, width, rect.height), label);
            GUI.color = previous;
            if (!active)
            {
                return false;
            }
            Widgets.DrawHighlightIfMouseover(rect);
            return Widgets.ButtonInvisible(rect);
        }

        private static void LabelMiddle(Rect rect, string text)
        {
            Widgets.Label(new Rect(rect.x, rect.y + (rect.height - Text.LineHeight) / 2f, rect.width, Text.LineHeight), text);
        }

        private static void LabelRight(Rect rect, string text)
        {
            float width = Math.Min(rect.width, Text.CalcSize(text).x + 1f);
            Widgets.Label(new Rect(rect.xMax - width, rect.y + (rect.height - Text.LineHeight) / 2f, width, Text.LineHeight), text);
        }

        // A tool's description up to its first sentence.
        private static string Summary(string description)
        {
            if (string.IsNullOrEmpty(description))
            {
                return "";
            }
            int end = description.IndexOf(". ", StringComparison.Ordinal);
            return end > 0 ? description.Substring(0, end + 1) : description;
        }

        private static void DoConnectionTab(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.Label("Local address: http://127.0.0.1:" + Settings.Port);
            listing.Label("Status: " + (BridgeRuntime.IsRunning ? "running" : Settings.BridgeEnabled ? "not running" : "off (Agents are off)"));
            listing.Gap();
            listing.Label("Agents connect through the RimMCP helper, which this config starts. It includes the token, so keep it private.");
            if (listing.ButtonText("Copy MCP config"))
            {
                GUIUtility.systemCopyBuffer = BuildMcpConfig();
                Messages.Message("RimMCP config copied.", MessageTypeDefOf.PositiveEvent, false);
            }
            if (listing.ButtonText("Regenerate token"))
            {
                Settings.RegenerateToken();
                BridgeRuntime.ApplySettings();
                Messages.Message("RimMCP token regenerated. Copy MCP config again.", MessageTypeDefOf.NeutralEvent, false);
            }
            listing.Gap();
            listing.Label("RimWorld config folder: " + GenFilePaths.ConfigFolderPath);
            listing.End();
        }

        private static void DoActivityTab(Rect rect)
        {
            float half = (rect.width - 12f) / 2f;
            Rect calls = new Rect(rect.x, rect.y, half, rect.height);
            Rect recent = new Rect(calls.xMax + 12f, rect.y, half, rect.height);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(calls.x, calls.y, calls.width, 30f), "Tool calls");
            Widgets.Label(new Rect(recent.x, recent.y, recent.width, 30f), "Recent requests");
            Text.Font = GameFont.Small;
            List<KeyValuePair<string, int>> counts = BridgeRuntime.ToolCalls.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToList();
            Rect outRect = new Rect(calls.x, calls.y + 34f, calls.width, calls.height - 34f);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(1, counts.Count) * 24f);
            Widgets.BeginScrollView(outRect, ref activityScroll, view);
            if (counts.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, 0f, view.width, 24f), "No tool calls since RimWorld started.");
                GUI.color = Color.white;
            }
            for (int i = 0; i < counts.Count; i++)
            {
                Widgets.Label(new Rect(0f, i * 24f, view.width - 60f, 24f), counts[i].Key);
                LabelRight(new Rect(view.width - 60f, i * 24f, 56f, 24f), counts[i].Value.ToString());
            }
            Widgets.EndScrollView();
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(recent.x, recent.y + 34f, recent.width, recent.height - 34f));
            foreach (string entry in BridgeRuntime.RecentLogLines)
            {
                listing.Label(entry);
            }
            listing.End();
        }

        private static void DoSafetyTab(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            listing.CheckboxLabeled("Unattended game setup", ref Settings.UnattendedSetup, "Lets agents answer the game's questions while setting up a new game, and start the colony without asking you.");
            GUI.color = Color.gray;
            listing.Label("Off: you approve the start of a new colony and answer the game's questions on screen.");
            GUI.color = Color.white;
            listing.Gap();
            listing.CheckboxLabeled("RimMCP button in game", ref Settings.ShowQuickControl, "The button on the bottom bar that shows whether agents are on and working, and pauses them or changes the access level in a click.");
            GUI.color = Color.gray;
            listing.Label("Shows whether agents are on and working; click it to pause them or change the access level.");
            GUI.color = Color.white;
            listing.Gap();
            GUI.color = Color.gray;
            listing.Label("During a Multiplayer session, agents can read but never change the game or control time.");
            GUI.color = Color.white;
            listing.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            Settings.EnsureToken();
            BridgeRuntime.ApplySettings();
        }

        private static string BuildMcpConfig()
        {
            string command = IsWindowsPlatform() ? "cmd.exe" : "/bin/sh";
            string args = IsWindowsPlatform()
                ? Json.Array(new[] { Json.String("/c"), Json.String(LauncherPath("rimcp-proxy.cmd")) })
                : Json.Array(new[] { Json.String(LauncherPath("rimcp-proxy")) });
            string url = "http://127.0.0.1:" + Settings.Port;
            return "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"rimcp\": {\n" +
                   "      \"command\": " + Json.String(command) + ",\n" +
                   "      \"args\": " + args + ",\n" +
                   "      \"env\": {\n" +
                   "        \"RIMCP_URL\": " + Json.String(url) + ",\n" +
                   "        \"RIMCP_TOKEN\": " + Json.String(Settings.Token) + "\n" +
                   "      }\n" +
                   "    }\n" +
                   "  }\n" +
                   "}";
        }

        private static string LauncherPath(string fileName)
        {
            return Path.GetFullPath(Path.Combine(modRootPath, "Tools/McpProxy", fileName));
        }

        private static bool IsWindowsPlatform()
        {
            return Application.platform == RuntimePlatform.WindowsPlayer ||
                   Application.platform == RuntimePlatform.WindowsEditor;
        }
    }
}
