using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Shared;
using RiMCP.Util;
using RiMCP.UI;
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
            Connection,
            Access,
            Activity,
            Safety
        }

        private const float HeaderHeight = 34f;
        private const float TabHeight = 30f;
        private const float GroupRowHeight = 30f;
        private const float ToolRowHeight = 46f;

        // Until an agent has made a request, the settings open on Connection; after that, on the
        // tab last used.
        private static SettingsTab? tab;
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
            switch (CurrentTab)
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

        private static SettingsTab CurrentTab
        {
            get
            {
                if (!tab.HasValue)
                {
                    tab = BridgeRuntime.LastRequestUtc.HasValue ? SettingsTab.Access : SettingsTab.Connection;
                }
                return tab.Value;
            }
        }

        // The status line and the master switch, Agents on or off. The window already shows the
        // mod's name above.
        private static void DoHeader(Rect rect)
        {
            Text.Font = GameFont.Small;
            DisplayText status = !Settings.BridgeEnabled ? Keys.AgentsOff.Display()
                : !BridgeRuntime.IsRunning ? Keys.NotRunning.Display()
                : (BridgeRuntime.RecentClientCount == 1 ? Keys.RunningOne : Keys.RunningMany).Display(BridgeRuntime.RecentClientCount, UiText.LastRequest);
            GUI.color = Settings.BridgeEnabled && BridgeRuntime.IsRunning ? new Color(0.55f, 0.85f, 0.55f) : Color.gray;
            UiDisplay.LabelMiddle(new Rect(rect.x, rect.y, rect.width - 200f, rect.height), status);
            GUI.color = Color.white;
            bool enabled = Settings.BridgeEnabled;
            UiDisplay.Checkbox(new Rect(rect.xMax - 150f, rect.y + 5f, 150f, 24f), Keys.AgentsOn.Display(), ref enabled);
            UiDisplay.Tip(new Rect(rect.xMax - 150f, rect.y, 150f, rect.height), Keys.MasterTip.Display());
            if (enabled != Settings.BridgeEnabled)
            {
                Settings.BridgeEnabled = enabled;
                BridgeRuntime.ApplySettings();
            }
        }

        private static void DoTabs(Rect rect)
        {
            float x = rect.x;
            foreach (SettingsTab each in (SettingsTab[])Enum.GetValues(typeof(SettingsTab)))
            {
                Rect button = new Rect(x, rect.y, 130f, rect.height);
                if (UiDisplay.Button(button, TabLabel(each).Display()))
                {
                    tab = each;
                }
                if (each == CurrentTab)
                {
                    Widgets.DrawBoxSolid(new Rect(button.x, button.yMax - 3f, button.width, 3f), new Color(0.85f, 0.85f, 0.85f));
                }
                x += button.width + 6f;
            }
            Widgets.DrawLineHorizontal(rect.x, rect.yMax + 2f, rect.width);
        }

        private static TextKey TabLabel(SettingsTab value)
        {
            switch (value)
            {
                case SettingsTab.Connection: return Keys.Tab_Connection;
                case SettingsTab.Access: return Keys.Tab_Access;
                case SettingsTab.Activity: return Keys.Tab_Activity;
                case SettingsTab.Safety: return Keys.Tab_Safety;
                default: throw new ArgumentOutOfRangeException(nameof(value));
            }
        }

        private static void DoAccessTab(Rect rect)
        {
            float y = rect.y;
            UiDisplay.LabelMiddle(new Rect(rect.x, y, 110f, 30f), Keys.AccessLevel.Display());
            float x = rect.x + 110f;
            foreach (string preset in AccessPresets.Named.Concat(new[] { AccessPresets.Custom }))
            {
                Rect button = new Rect(x, y, 128f, 30f);
                bool current = Settings.AccessPreset == preset;
                if (UiDisplay.Button(button, UiText.PresetLabel(preset), preset != AccessPresets.Custom) && preset != AccessPresets.Custom && !current)
                {
                    Settings.SetPreset(preset);
                    BridgeRuntime.ApplySettings();
                }
                if (current)
                {
                    Widgets.DrawBox(button, 2);
                }
                UiDisplay.Tip(button, UiText.PresetDescription(preset));
                x += button.width + 4f;
            }
            y += 36f;
            GUI.color = Color.gray;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Settings.BridgeEnabled ? UiText.PresetDescription(Settings.AccessPreset) : Keys.OffAccessTip.Display());
            GUI.color = Color.white;
            y += 30f;

            UiDisplay.LabelMiddle(new Rect(rect.x, y, 60f, 28f), Keys.Search.Display());
            search = Widgets.TextField(new Rect(rect.x + 60f, y, 300f, 28f), search ?? "");
            if (!string.IsNullOrEmpty(search) && UiDisplay.Button(new Rect(rect.x + 366f, y, 70f, 28f), Keys.Clear.Display()))
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
            UiDisplay.Label(new Rect(rect.x, rect.yMax - footer + 4f, rect.width, footer), Keys.ToolsEnabled.Display(on, all.Count));
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
                UiDisplay.LabelMiddle(label, UiText.GroupLabel(group));
                GUI.color = Color.gray;
                UiDisplay.LabelRight(new Rect(row.xMax - 52f, row.y, 48f, row.height), Keys.GroupToolCount.Display(on, members.Count));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(label))
                {
                    selectedGroup = group;
                    search = "";
                    toolScroll = Vector2.zero;
                }
                UiDisplay.Tip(row, UiText.GroupDescription(group));
                y += GroupRowHeight + 2f;
            }
        }

        // The selected group's tools, or every tool matching the search, each with its own checkbox.
        private static void DoTools(Rect rect)
        {
            string query = (search ?? "").Trim();
            List<ToolSpec> shown = string.IsNullOrEmpty(query)
                ? ToolManifest.McpTools.Where(spec => spec.Group == selectedGroup).ToList()
                : ToolManifest.McpTools.Where(spec => spec.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || (spec.Description ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 || UiText.ToolDescription(spec).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            float y = rect.y;
            Text.Font = GameFont.Medium;
            UiDisplay.LabelMiddle(new Rect(rect.x, y, rect.width - 200f, 30f), string.IsNullOrEmpty(query) ? UiText.GroupLabel(selectedGroup) : (shown.Count == 1 ? Keys.MatchingOne : Keys.MatchingMany).Display(shown.Count));
            Text.Font = GameFont.Small;
            if (string.IsNullOrEmpty(query))
            {
                if (UiDisplay.Button(new Rect(rect.xMax - 196f, y, 94f, 28f), Keys.AllOn.Display()))
                {
                    Settings.SetGroup(selectedGroup, true);
                    BridgeRuntime.ApplySettings();
                }
                if (UiDisplay.Button(new Rect(rect.xMax - 96f, y, 94f, 28f), Keys.AllOff.Display()))
                {
                    Settings.SetGroup(selectedGroup, false);
                    BridgeRuntime.ApplySettings();
                }
                y += 32f;
                GUI.color = Color.gray;
                UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), UiText.GroupDescription(selectedGroup));
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
            UiDisplay.Label(new Rect(x, row.y + 2f, row.width - 34f, 22f), DisplayText.Raw(spec.Name));
            float after = x + Text.CalcSize(spec.Name).x + 10f;
            object destructive;
            if (spec.Annotations.TryGetValue("destructiveHint", out destructive) && destructive is bool && (bool)destructive)
            {
                GUI.color = new Color(0.95f, 0.7f, 0.3f);
                Text.Font = GameFont.Tiny;
                UiDisplay.Label(new Rect(after, row.y + 4f, 140f, 20f), Keys.Destructive.Display());
                after += 130f;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            if (showGroup)
            {
                GUI.color = Color.gray;
                Text.Font = GameFont.Tiny;
                UiDisplay.Label(new Rect(after, row.y + 4f, 160f, 20f), UiText.GroupLabel(spec.Group));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            GUI.color = Color.gray;
            Text.Font = GameFont.Tiny;
            string description = UiText.ToolDescription(spec);
            UiDisplay.Label(new Rect(x, row.y + 23f, row.width - 34f, 20f), DisplayText.Raw(description.Truncate(row.width - 40f)));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            UiDisplay.Tip(row, Keys.ToolTooltip.Display(spec.Name, description));
        }

        // Connecting an agent: whether one is connected, the config to give it, and the details
        // most people never need.
        private static void DoConnectionTab(Rect rect)
        {
            float y = rect.y;
            bool connected = Settings.BridgeEnabled && BridgeRuntime.IsRunning && BridgeRuntime.RecentClientCount > 0;
            DisplayText status;
            Color color;
            if (!Settings.BridgeEnabled)
            {
                status = Keys.OffConnection.Display();
                color = Color.gray;
            }
            else if (!BridgeRuntime.IsRunning)
            {
                status = Keys.NotRunningConnection.Display();
                color = new Color(0.95f, 0.45f, 0.4f);
            }
            else if (connected)
            {
                status = (BridgeRuntime.RecentClientCount == 1 ? Keys.ConnectedOne : Keys.ConnectedMany).Display(BridgeRuntime.RecentClientCount, UiText.LastRequest);
                color = new Color(0.55f, 0.85f, 0.55f);
            }
            else
            {
                status = Keys.Waiting.Display();
                color = new Color(0.95f, 0.75f, 0.3f);
            }
            Text.Font = GameFont.Medium;
            GUI.color = color;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 34f), status);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 44f;

            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.StepOne.Display());
            y += 28f;
            if (UiDisplay.Button(new Rect(rect.x + 18f, y, 320f, 40f), Keys.CopyConfig.Display()))
            {
                GUIUtility.systemCopyBuffer = BuildMcpConfig();
                UiDisplay.Message(Keys.ConfigCopied.Display(), MessageTypeDefOf.PositiveEvent, false);
            }
            y += 50f;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.StepTwo.Display());
            y += 28f;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.StepThree.Display());
            y += 28f;
            GUI.color = Color.gray;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.ConfigPrivacy.Display());
            GUI.color = Color.white;
            y += 44f;

            Widgets.DrawLineHorizontal(rect.x, y, rect.width);
            y += 10f;
            GUI.color = Color.gray;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.Advanced.Display());
            GUI.color = Color.white;
            y += 28f;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.LocalAddress.Display("http://127.0.0.1:" + Settings.Port));
            y += 28f;
            if (UiDisplay.Button(new Rect(rect.x, y, 200f, 30f), Keys.RegenerateToken.Display()))
            {
                Settings.RegenerateToken();
                BridgeRuntime.ApplySettings();
                UiDisplay.Message(Keys.TokenRegenerated.Display(), MessageTypeDefOf.NeutralEvent, false);
            }
            GUI.color = Color.gray;
            UiDisplay.LabelMiddle(new Rect(rect.x + 212f, y, rect.width - 212f, 30f), Keys.NewConfigRequired.Display());
            GUI.color = Color.white;
            y += 40f;
            GUI.color = Color.gray;
            UiDisplay.Label(new Rect(rect.x, y, rect.width, 24f), Keys.ConfigFolder.Display(GenFilePaths.ConfigFolderPath));
            GUI.color = Color.white;
        }

        private static void DoActivityTab(Rect rect)
        {
            Rect shown = new Rect(rect.x, rect.y, rect.width, 64f);
            UiDisplay.Label(new Rect(shown.x, shown.y, 260f, 30f), Keys.AgentActivity_ShowChanges.Display());
            if (UiDisplay.Button(new Rect(shown.x + 264f, shown.y, 200f, 28f), AgentMessagesLabel(Settings.AgentMessages)))
            {
                Find.WindowStack.Add(new FloatMenu(new[] { UI.AgentActivity.MessagesOff, UI.AgentActivity.DecisionsAndNotes, UI.AgentActivity.EveryChange }
                    .Select(mode => UiDisplay.Option(AgentMessagesLabel(mode), () => Settings.AgentMessages = mode)).ToList()));
            }
            Listing_Standard where = new Listing_Standard();
            where.Begin(new Rect(shown.x, shown.y + 34f, 464f, 30f));
            UiDisplay.Checkbox(where, Keys.AgentActivity_ShowWhere.Display(), ref Settings.ShowWhereAgentsAct, Keys.AgentActivity_ShowWhereTip.Display());
            where.End();
            rect = new Rect(rect.x, rect.y + 72f, rect.width, rect.height - 72f);
            float half = (rect.width - 12f) / 2f;
            Rect calls = new Rect(rect.x, rect.y, half, rect.height);
            Rect recent = new Rect(calls.xMax + 12f, rect.y, half, rect.height);
            Text.Font = GameFont.Medium;
            UiDisplay.Label(new Rect(calls.x, calls.y, calls.width, 30f), Keys.ToolCalls.Display());
            UiDisplay.Label(new Rect(recent.x, recent.y, recent.width, 30f), Keys.RecentRequests.Display());
            Text.Font = GameFont.Small;
            List<KeyValuePair<string, int>> counts = BridgeRuntime.ToolCalls.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).ToList();
            Rect outRect = new Rect(calls.x, calls.y + 34f, calls.width, calls.height - 34f);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Math.Max(1, counts.Count) * 24f);
            Widgets.BeginScrollView(outRect, ref activityScroll, view);
            if (counts.Count == 0)
            {
                GUI.color = Color.gray;
                UiDisplay.Label(new Rect(0f, 0f, view.width, 24f), Keys.NoCalls.Display());
                GUI.color = Color.white;
            }
            for (int i = 0; i < counts.Count; i++)
            {
                UiDisplay.Label(new Rect(0f, i * 24f, view.width - 60f, 24f), DisplayText.Raw(counts[i].Key));
                UiDisplay.LabelRight(new Rect(view.width - 60f, i * 24f, 56f, 24f), DisplayText.Raw(counts[i].Value.ToString()));
            }
            Widgets.EndScrollView();
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(new Rect(recent.x, recent.y + 34f, recent.width, recent.height - 34f));
            foreach (string entry in BridgeRuntime.RecentLogLines)
            {
                UiDisplay.Label(listing, DisplayText.Raw(entry));
            }
            listing.End();
        }

        private static DisplayText AgentMessagesLabel(int mode)
        {
            return (mode == UI.AgentActivity.MessagesOff ? Keys.AgentActivity_ModeOff
                : mode == UI.AgentActivity.EveryChange ? Keys.AgentActivity_ModeEveryChange : Keys.AgentActivity_ModeDecisionsAndNotes).Display();
        }

        private static void DoSafetyTab(Rect rect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(rect);
            UiDisplay.Checkbox(listing, Keys.UnattendedSetup.Display(), ref Settings.UnattendedSetup, Keys.UnattendedTip.Display());
            GUI.color = Color.gray;
            UiDisplay.Label(listing, Keys.UnattendedOff.Display());
            GUI.color = Color.white;
            listing.Gap();
            UiDisplay.Checkbox(listing, Keys.ModActions.Display(), ref Settings.AllowModActions, Keys.ModActionsTip.Display());
            GUI.color = Color.gray;
            UiDisplay.Label(listing, Keys.ModActionsOff.Display());
            GUI.color = Color.white;
            listing.Gap();
            UiDisplay.Checkbox(listing, Keys.QuickButton.Display(), ref Settings.ShowQuickControl, Keys.QuickButtonTip.Display());
            GUI.color = Color.gray;
            UiDisplay.Label(listing, Keys.QuickButtonHelp.Display());
            GUI.color = Color.white;
            listing.Gap();
            GUI.color = Color.gray;
            UiDisplay.Label(listing, Keys.Multiplayer.Display());
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
