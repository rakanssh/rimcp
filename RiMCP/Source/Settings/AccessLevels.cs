using System.Collections.Generic;
using System.Linq;
using RiMCP.Shared;

namespace RiMCP.Settings
{
    internal static class AccessPresets
    {
        public const string Full = "full";
        public const string Control = "control";
        public const string Administration = "administration";
        public const string ReadOnly = "readOnly";
        public const string Custom = "custom";

        public static readonly string[] Named = { Full, Control, Administration, ReadOnly };

        // Groups for a named preset. Presets list their groups, so groups added later stay off
        // unless a preset includes them.
        public static string[] Groups(string preset)
        {
            switch (preset)
            {
                case Full:
                    return ToolGroups.All;
                case Control:
                    // Game setup can abandon a colony or overwrite a save, so only Full control has it.
                    return ToolGroups.All.Where(group => group != ToolGroups.GameSettings && group != ToolGroups.GameSetup).ToArray();
                case Administration:
                    return new[] { ToolGroups.Observe, ToolGroups.Diagnostics, ToolGroups.Time, ToolGroups.Administration };
                default:
                    return new[] { ToolGroups.Observe, ToolGroups.Diagnostics };
            }
        }

        // Settings saved before presets only had Allow changes. A new install has neither, and its
        // Allow changes default was on, so it starts with Full control.
        public static string Migrate(string savedPreset, bool legacyAllowChanges)
        {
            if (savedPreset == null)
            {
                return legacyAllowChanges ? Full : ReadOnly;
            }
            // An unrecognised value fails closed.
            return savedPreset == Custom || Named.Contains(savedPreset) ? savedPreset : ReadOnly;
        }

        // The named preset with exactly these groups, or Custom.
        public static string Matching(IEnumerable<string> groups)
        {
            HashSet<string> set = new HashSet<string>(groups);
            return Named.FirstOrDefault(preset => set.SetEquals(Groups(preset))) ?? Custom;
        }

        public static string Label(string preset)
        {
            switch (preset)
            {
                case Full: return "Full control";
                case Control: return "Control";
                case Administration: return "Administration";
                case ReadOnly: return "Read only";
                default: return "Custom";
            }
        }

        public static string Description(string preset)
        {
            switch (preset)
            {
                case Full: return "Every tool, including game settings such as storyteller and difficulty, and saving, loading, and leaving games.";
                case Control: return "Everything except game settings and game setup (saving, loading, and leaving games).";
                case Administration: return "Reads, colony settings, and time control, so an agent can check its changes by advancing time. No direct orders.";
                case ReadOnly: return "Reads only. Agents cannot change anything.";
                default: return "A hand-picked set of groups.";
            }
        }
    }

    // What agents may use right now. Immutable, so HTTP threads can read it without locking.
    internal sealed class AccessState
    {
        public static readonly AccessState None = new AccessState(AccessPresets.Custom, new string[0], 0);

        public readonly string Preset;
        // Changes whenever the preset, the groups, or whether RimMCP is enabled change.
        public readonly int Revision;
        // False while RimMCP is disabled in its settings. Nothing runs then, including requests
        // that were already waiting for the main thread when it was disabled.
        public readonly bool Enabled;
        private readonly HashSet<string> groups;

        public AccessState(string preset, IEnumerable<string> groups, int revision, bool enabled = true)
        {
            Preset = preset;
            this.groups = new HashSet<string>(groups);
            Revision = revision;
            Enabled = enabled;
        }

        // Bridge routes outside any group, such as the access endpoint, are reachable whenever
        // RimMCP is enabled.
        public bool Allows(string group)
        {
            return Enabled && (group == null || groups.Contains(group));
        }

        public bool AllowsChanges
        {
            get { return Enabled && groups.Any(group => group != ToolGroups.Observe && group != ToolGroups.Diagnostics); }
        }

        public bool SameAs(string preset, IEnumerable<string> groups)
        {
            return Enabled && Preset == preset && this.groups.SetEquals(groups);
        }

        // The same preferences, refused until RimMCP is enabled again.
        public AccessState Disabled()
        {
            return Enabled ? new AccessState(Preset, groups, Revision + 1, false) : this;
        }
    }
}
