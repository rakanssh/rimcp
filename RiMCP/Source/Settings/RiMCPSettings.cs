using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Settings
{
    public sealed class RiMCPSettings : ModSettings
    {
        public bool BridgeEnabled = true;
        // One of AccessPresets; null until EnsureAccess migrates older settings.
        public string AccessPreset;
        // The enabled groups when AccessPreset is Custom.
        public List<string> CustomGroups = new List<string>();
        // With Custom: tools turned on in a group that is off, and off in a group that is on.
        public List<string> ToolsOn = new List<string>();
        public List<string> ToolsOff = new List<string>();
        // The RimMCP button on the play screen's bottom bar.
        public bool ShowQuickControl = true;
        public int Port = 39571;
        public string Token;
        // Agents answer the game's questions during new-game setup and start the colony without the
        // player's approval. Off by default: the player approves and answers on screen.
        public bool UnattendedSetup;
        // Mods' right-click options and buttons, which RimMCP cannot check before they run.
        public bool AllowModActions = true;
        // Only read, to migrate settings saved before access presets.
        private bool legacyAllowChanges = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref BridgeEnabled, "bridgeEnabled", true);
            Scribe_Values.Look(ref AccessPreset, "accessPreset");
            Scribe_Collections.Look(ref CustomGroups, "customGroups", LookMode.Value);
            Scribe_Collections.Look(ref ToolsOn, "toolsOn", LookMode.Value);
            Scribe_Collections.Look(ref ToolsOff, "toolsOff", LookMode.Value);
            Scribe_Values.Look(ref ShowQuickControl, "showQuickControl", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Values.Look(ref legacyAllowChanges, "allowColonyChanges", true);
            }
            Scribe_Values.Look(ref Port, "port", 39571);
            Scribe_Values.Look(ref Token, "token");
            Scribe_Values.Look(ref UnattendedSetup, "unattendedSetup", false);
            Scribe_Values.Look(ref AllowModActions, "allowModActions", true);
        }

        internal string[] EnabledGroups
        {
            get
            {
                return AccessPreset == AccessPresets.Custom
                    ? ToolGroups.All.Where(CustomGroups.Contains).ToArray()
                    : AccessPresets.Groups(AccessPreset);
            }
        }

        // Tool exceptions apply only with Custom; a named preset is its groups, exactly.
        internal string[] EnabledToolsOn
        {
            get { return AccessPreset == AccessPresets.Custom ? ToolsOn.ToArray() : new string[0]; }
        }

        internal string[] EnabledToolsOff
        {
            get { return AccessPreset == AccessPresets.Custom ? ToolsOff.ToArray() : new string[0]; }
        }

        internal bool IsToolOn(ToolSpec spec)
        {
            return AccessTools.IsOn(spec, EnabledGroups, EnabledToolsOn, EnabledToolsOff);
        }

        public void EnsureAccess()
        {
            AccessPreset = AccessPresets.Migrate(AccessPreset, legacyAllowChanges);
            if (CustomGroups == null)
            {
                CustomGroups = new List<string>();
            }
            if (ToolsOn == null)
            {
                ToolsOn = new List<string>();
            }
            if (ToolsOff == null)
            {
                ToolsOff = new List<string>();
            }
        }

        // A named preset is its groups, with no tool exceptions.
        internal void SetPreset(string preset)
        {
            AccessPreset = preset;
            ToolsOn = new List<string>();
            ToolsOff = new List<string>();
        }

        // Changing one group switches to Custom, unless the result matches a named preset. The
        // group's own tool exceptions go, so all of its tools follow it.
        internal void SetGroup(string group, bool enabled)
        {
            HashSet<string> groups = new HashSet<string>(EnabledGroups);
            HashSet<string> members = new HashSet<string>(ToolManifest.McpTools.Where(spec => spec.Group == group).Select(spec => spec.Name));
            HashSet<string> on = new HashSet<string>(EnabledToolsOn.Where(name => !members.Contains(name)));
            HashSet<string> off = new HashSet<string>(EnabledToolsOff.Where(name => !members.Contains(name)));
            if (enabled)
            {
                groups.Add(group);
            }
            else
            {
                groups.Remove(group);
            }
            Store(groups, on, off);
        }

        // Turning one tool on or off makes it an exception to its group, unless it then agrees.
        internal void SetTool(ToolSpec spec, bool enabled)
        {
            HashSet<string> groups = new HashSet<string>(EnabledGroups);
            HashSet<string> on = new HashSet<string>(EnabledToolsOn);
            HashSet<string> off = new HashSet<string>(EnabledToolsOff);
            on.Remove(spec.Name);
            off.Remove(spec.Name);
            bool groupOn = groups.Contains(spec.Group);
            if (enabled && !groupOn)
            {
                on.Add(spec.Name);
            }
            else if (!enabled && groupOn)
            {
                off.Add(spec.Name);
            }
            Store(groups, on, off);
        }

        private void Store(HashSet<string> groups, HashSet<string> on, HashSet<string> off)
        {
            AccessTools.Normalize(groups, on, off);
            CustomGroups = ToolGroups.All.Where(groups.Contains).ToList();
            ToolsOn = on.OrderBy(name => name).ToList();
            ToolsOff = off.OrderBy(name => name).ToList();
            AccessPreset = AccessTools.Matching(CustomGroups, ToolsOn, ToolsOff);
            if (AccessPreset != AccessPresets.Custom)
            {
                ToolsOn = new List<string>();
                ToolsOff = new List<string>();
            }
        }

        public void EnsureToken()
        {
            if (string.IsNullOrEmpty(Token))
            {
                RegenerateToken();
            }
        }

        public void RegenerateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            Token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
