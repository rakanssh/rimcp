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
        public int Port = 39571;
        public string Token;
        // Only read, to migrate settings saved before access presets.
        private bool legacyAllowChanges = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref BridgeEnabled, "bridgeEnabled", true);
            Scribe_Values.Look(ref AccessPreset, "accessPreset");
            Scribe_Collections.Look(ref CustomGroups, "customGroups", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Scribe_Values.Look(ref legacyAllowChanges, "allowColonyChanges", true);
            }
            Scribe_Values.Look(ref Port, "port", 39571);
            Scribe_Values.Look(ref Token, "token");
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

        public void EnsureAccess()
        {
            AccessPreset = AccessPresets.Migrate(AccessPreset, legacyAllowChanges);
            if (CustomGroups == null)
            {
                CustomGroups = new List<string>();
            }
        }

        internal void SetPreset(string preset)
        {
            AccessPreset = preset;
        }

        // Changing one group switches to Custom, unless the result matches a named preset.
        internal void SetGroup(string group, bool enabled)
        {
            HashSet<string> groups = new HashSet<string>(EnabledGroups);
            if (enabled)
            {
                groups.Add(group);
            }
            else
            {
                groups.Remove(group);
            }
            CustomGroups = ToolGroups.All.Where(groups.Contains).ToList();
            AccessPreset = AccessPresets.Matching(CustomGroups);
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
