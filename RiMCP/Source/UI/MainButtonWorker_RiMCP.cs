using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Command;
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
        private const int StatusTipId = 0x52694D43;

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
            // A fixed id: the text changes every second, and a tip keyed by its text restarts its delay.
            UiDisplay.Tip(rect, Status, StatusTipId);
            AgentOverlay.Draw(rect);
        }

        public override void Activate()
        {
            RiMCPSettings settings = RiMCPMod.Settings;
            if (settings == null)
            {
                return;
            }
            List<FloatMenuOption> options = new List<FloatMenuOption>();
            options.Add(UiDisplay.Option(Keys.AgentActivity_LogTitle.Display(), Window_AgentLog.Open));
            if (settings.BridgeEnabled)
            {
                options.Add(UiDisplay.Option(Keys.PauseAgents.Display(), () => Change(() => settings.BridgeEnabled = false)));
            }
            else
            {
                options.Add(UiDisplay.Option(Keys.ResumeAgents.Display(), () => Change(() => settings.BridgeEnabled = true)));
            }
            foreach (string preset in AccessPresets.Named)
            {
                string chosen = preset;
                DisplayText label = (settings.AccessPreset == preset ? Keys.AccessCurrent : Keys.AccessChoice).Display(UiText.PresetLabel(preset).Value);
                options.Add(UiDisplay.Option(label, settings.AccessPreset == preset ? null : (System.Action)(() => Change(() => settings.SetPreset(chosen)))));
            }
            if (settings.AccessPreset == AccessPresets.Custom)
            {
                options.Add(UiDisplay.Option(Keys.AccessCurrent.Display(UiText.PresetLabel(AccessPresets.Custom).Value), null));
            }
            options.Add(UiDisplay.Option(Keys.OpenSettings.Display(), () =>
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

        private static DisplayText Status()
        {
            RiMCPSettings settings = RiMCPMod.Settings;
            if (settings == null || !settings.BridgeEnabled)
            {
                return Keys.PausedStatus.Display();
            }
            if (!BridgeRuntime.IsRunning)
            {
                return Keys.StoppedStatus.Display();
            }
            List<AgentEntry> recent = AgentLog.Entries;
            recent.Reverse();
            DisplayText status = Keys.QuickStatus.Display(UiText.PresetLabel(settings.AccessPreset).Value,
                (BridgeRuntime.AgentWorking ? Keys.AgentWorking : Keys.AgentsIdle).Text(),
                (BridgeRuntime.RecentClientCount == 1 ? Keys.ClientOne : Keys.ClientMany).Text(BridgeRuntime.RecentClientCount), UiText.LastRequest);
            if (recent.Count == 0)
            {
                return status;
            }
            string entries = string.Join("\n", recent.Take(3).Select(entry =>
                Keys.AgentActivity_QuickStatusEntry.Text(entry.Line, Window_AgentLog.OutcomeLabel(entry.Outcome))));
            return Keys.AgentActivity_QuickStatusWithEntries.Display(status.Value, entries);
        }
    }
}
