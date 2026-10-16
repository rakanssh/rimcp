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
        private const float SettingsViewHeight = 1000f;

        public static RiMCPSettings Settings;
        private static string modRootPath;

        private Vector2 scrollPosition;

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

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.EnsureToken();

            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, SettingsViewHeight);
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("RimMCP");
            listing.GapLine();
            listing.Label("Status: " + (BridgeRuntime.IsRunning ? "running" : "stopped"));
            listing.Label("Access: " + (!BridgeRuntime.IsRunning ? "disabled" : AccessPresets.Label(Settings.AccessPreset)));
            listing.Label("Local address: http://127.0.0.1:" + Settings.Port);
            listing.Label("Recent clients: " + BridgeRuntime.RecentClientCount);
            listing.Label("Last request: " + BridgeRuntime.LastRequestDescription);
            listing.Label("RimWorld config folder: " + GenFilePaths.ConfigFolderPath);

            listing.Gap();
            bool enabled = Settings.BridgeEnabled;
            listing.CheckboxLabeled("Enable RimMCP", ref enabled, "Enables the token-protected local bridge. What clients can use is set under Access.");
            if (enabled != Settings.BridgeEnabled)
            {
                Settings.BridgeEnabled = enabled;
                BridgeRuntime.ApplySettings();
            }

            DoAccessSettings(listing);

            listing.Gap();
            listing.Label("Connection");
            listing.GapLine();
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
            listing.Label("Recent requests");
            listing.GapLine();
            foreach (string entry in BridgeRuntime.RecentLogLines)
            {
                listing.Label(entry);
            }

            listing.End();
            Widgets.EndScrollView();

            base.DoSettingsWindowContents(inRect);
        }

        private static void DoAccessSettings(Listing_Standard listing)
        {
            listing.Gap();
            listing.Label("Access");
            listing.GapLine();
            if (listing.ButtonText("Preset: " + AccessPresets.Label(Settings.AccessPreset)))
            {
                Find.WindowStack.Add(new FloatMenu(AccessPresets.Named
                    .Select(preset => new FloatMenuOption(AccessPresets.Label(preset), () =>
                    {
                        Settings.SetPreset(preset);
                        BridgeRuntime.ApplySettings();
                    }))
                    .ToList()));
            }
            listing.Label(AccessPresets.Description(Settings.AccessPreset));

            HashSet<string> enabledGroups = new HashSet<string>(Settings.EnabledGroups);
            foreach (string group in ToolGroups.All)
            {
                bool enabled = enabledGroups.Contains(group);
                bool next = enabled;
                int tools = ToolManifest.McpTools.Count(spec => spec.Group == group);
                listing.CheckboxLabeled(ToolGroups.Label(group) + " (" + tools + (tools == 1 ? " tool)" : " tools)"), ref next, ToolGroups.Description(group));
                if (next != enabled)
                {
                    Settings.SetGroup(group, next);
                    BridgeRuntime.ApplySettings();
                }
            }
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
