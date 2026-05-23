using System;
using System.IO;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Util;
using UnityEngine;
using Verse;

namespace RiMCP
{
    public sealed class RiMCPMod : Mod
    {
        private const float SettingsViewHeight = 680f;

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
            return "RiMCP";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Settings.EnsureToken();

            Rect viewRect = new Rect(0f, 0f, inRect.width - 20f, SettingsViewHeight);
            Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);

            listing.Label("RiMCP");
            listing.GapLine();
            listing.Label("Status: " + (BridgeRuntime.IsRunning ? "running" : "stopped"));
            listing.Label("Local address: http://127.0.0.1:" + Settings.Port);
            listing.Label("Recent clients: " + BridgeRuntime.RecentClientCount);
            listing.Label("Last request: " + BridgeRuntime.LastRequestDescription);
            listing.Label("RimWorld config folder: " + GenFilePaths.ConfigFolderPath);

            listing.Gap();
            bool enabled = Settings.BridgeEnabled;
            listing.CheckboxLabeled("Enable RiMCP", ref enabled, "Allows local MCP clients to read colony data through a token-protected bridge.");
            if (enabled != Settings.BridgeEnabled)
            {
                Settings.BridgeEnabled = enabled;
                BridgeRuntime.ApplySettings();
            }

            listing.Gap();
            listing.Label("Connection");
            listing.GapLine();
            if (listing.ButtonText("Copy MCP config"))
            {
                GUIUtility.systemCopyBuffer = BuildMcpConfig();
                Messages.Message("RiMCP MCP config copied.", MessageTypeDefOf.PositiveEvent, false);
            }

            if (listing.ButtonText("Regenerate token"))
            {
                Settings.RegenerateToken();
                BridgeRuntime.ApplySettings();
                Messages.Message("RiMCP token regenerated. Copy MCP config again.", MessageTypeDefOf.NeutralEvent, false);
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

        public override void WriteSettings()
        {
            base.WriteSettings();
            Settings.EnsureToken();
            BridgeRuntime.ApplySettings();
        }

        private static string BuildMcpConfig()
        {
            string proxyExecutable = Path.GetFullPath(Path.Combine(modRootPath, "Tools/McpProxy/publish/RiMCP.McpProxy"));
            string url = "http://127.0.0.1:" + Settings.Port;
            return "{\n" +
                   "  \"mcpServers\": {\n" +
                   "    \"rimcp\": {\n" +
                   "      \"command\": " + Json.String(proxyExecutable) + ",\n" +
                   "      \"args\": [],\n" +
                   "      \"env\": {\n" +
                   "        \"RIMCP_URL\": " + Json.String(url) + ",\n" +
                   "        \"RIMCP_TOKEN\": " + Json.String(Settings.Token) + "\n" +
                   "      }\n" +
                   "    }\n" +
                   "  }\n" +
                   "}";
        }
    }
}
