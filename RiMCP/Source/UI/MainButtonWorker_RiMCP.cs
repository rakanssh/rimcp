using System.Collections.Generic;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Settings;
using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // The bottom bar's RimMCP button: a dot shows whether agents are on and whether one is working,
    // and a click pauses or resumes agents, changes the access level, or opens the settings.
    public sealed class MainButtonWorker_RiMCP : MainButtonWorker
    {
        private static readonly Color Off = new Color(0.55f, 0.55f, 0.55f);
        private static readonly Color Idle = new Color(0.35f, 0.75f, 0.4f);
        private static readonly Color Working = new Color(0.95f, 0.75f, 0.2f);

        public override bool Visible
        {
            get { return RiMCPMod.Settings != null && RiMCPMod.Settings.ShowQuickControl && base.Visible; }
        }

        public override bool Disabled
        {
            get { return false; }
        }

        public override void DoButton(Rect rect)
        {
            base.DoButton(rect);
            RiMCPSettings settings = RiMCPMod.Settings;
            bool on = settings != null && settings.BridgeEnabled && BridgeRuntime.IsRunning;
            bool working = on && BridgeRuntime.AgentWorking;
            Rect dot = new Rect(rect.xMax - 11f, rect.y + 5f, 7f, 7f);
            Color previous = GUI.color;
            GUI.color = !on ? Off : working ? Working : Idle;
            GUI.DrawTexture(dot, BaseContent.WhiteTex);
            GUI.color = previous;
            TooltipHandler.TipRegion(rect, Status());
        }

        public override void Activate()
        {
            RiMCPSettings settings = RiMCPMod.Settings;
            if (settings == null)
            {
                return;
            }
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            if (settings.BridgeEnabled)
            {
                options.Add(new FloatMenuOption("RiMCP.PauseAgents".Translate(), () => Change(() => settings.BridgeEnabled = false)));
            }
            else
            {
                options.Add(new FloatMenuOption("RiMCP.ResumeAgents".Translate(), () => Change(() => settings.BridgeEnabled = true)));
            }
            foreach (string preset in AccessPresets.Named)
            {
                string chosen = preset;
                string label = (settings.AccessPreset == preset ? "RiMCP.AccessCurrent" : "RiMCP.AccessChoice").Translate(UiText.PresetLabel(preset)).ToString();
                options.Add(new FloatMenuOption(label, settings.AccessPreset == preset ? null : (System.Action)(() => Change(() => settings.SetPreset(chosen)))));
            }
            if (settings.AccessPreset == AccessPresets.Custom)
            {
                options.Add(new FloatMenuOption("RiMCP.AccessCurrent".Translate(UiText.PresetLabel(AccessPresets.Custom)), null));
            }
            options.Add(new FloatMenuOption("RiMCP.OpenSettings".Translate(), () =>
            {
                Mod mod = LoadedModManager.GetMod<RiMCPMod>();
                if (mod != null)
                {
                    Find.WindowStack.Add(new Dialog_ModSettings(mod));
                }
            }));
            Find.WindowStack.Add(new FloatMenu(options));
        }

        private static void Change(System.Action change)
        {
            change();
            BridgeRuntime.ApplySettings();
            Mod mod = LoadedModManager.GetMod<RiMCPMod>();
            if (mod != null)
            {
                mod.WriteSettings();
            }
        }

        private static string Status()
        {
            RiMCPSettings settings = RiMCPMod.Settings;
            if (settings == null || !settings.BridgeEnabled)
            {
                return "RiMCP.PausedStatus".Translate();
            }
            if (!BridgeRuntime.IsRunning)
            {
                return "RiMCP.StoppedStatus".Translate();
            }
            return "RiMCP.QuickStatus".Translate(UiText.PresetLabel(settings.AccessPreset),
                (BridgeRuntime.AgentWorking ? "RiMCP.AgentWorking" : "RiMCP.AgentsIdle").Translate(),
                (BridgeRuntime.RecentClientCount == 1 ? "RiMCP.ClientOne" : "RiMCP.ClientMany").Translate(BridgeRuntime.RecentClientCount), UiText.LastRequest);
        }
    }
}
