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
                case Full: return "Every tool, including game settings, and saving, loading, and setting up games.";
                case Control: return "Everything except game settings and game setup.";
                case Administration: return "Reads, colony settings, and time, so an agent can check its changes by advancing time. No direct orders.";
                case ReadOnly: return "Reads only. Agents can't change anything.";
                default: return "Your own choice of groups and tools. Tools added in later versions follow their group.";
            }
        }
    }

    // Per-tool choices on top of the groups: tools turned on in a group that is off, and tools
    // turned off in a group that is on. Keeping them as exceptions means tools added in later
    // versions follow their group.
    internal static class AccessTools
    {
        public static bool IsOn(ToolSpec spec, ICollection<string> groups, ICollection<string> toolsOn, ICollection<string> toolsOff)
        {
            if (spec.Group == null)
            {
                return true;
            }
            if (spec.Name != null && toolsOn.Contains(spec.Name))
            {
                return true;
            }
            return groups.Contains(spec.Group) && (spec.Name == null || !toolsOff.Contains(spec.Name));
        }

        // Exceptions that agree with their group are dropped; a group whose every tool is an
        // exception flips instead; exceptions naming no tool are dropped.
        public static void Normalize(HashSet<string> groups, HashSet<string> toolsOn, HashSet<string> toolsOff)
        {
            List<ToolSpec> tools = ToolManifest.McpTools.ToList();
            HashSet<string> known = new HashSet<string>(tools.Select(spec => spec.Name));
            toolsOn.RemoveWhere(name => !known.Contains(name));
            toolsOff.RemoveWhere(name => !known.Contains(name));
            foreach (string group in ToolGroups.All)
            {
                List<string> members = tools.Where(spec => spec.Group == group).Select(spec => spec.Name).ToList();
                if (members.Count == 0)
                {
                    continue;
                }
                if (groups.Contains(group))
                {
                    toolsOn.ExceptWith(members);
                    if (members.All(toolsOff.Contains))
                    {
                        groups.Remove(group);
                        toolsOff.ExceptWith(members);
                    }
                }
                else
                {
                    toolsOff.ExceptWith(members);
                    if (members.All(toolsOn.Contains))
                    {
                        groups.Add(group);
                        toolsOn.ExceptWith(members);
                    }
                }
            }
        }

        // The named preset these choices amount to, or Custom.
        public static string Matching(IEnumerable<string> groups, ICollection<string> toolsOn, ICollection<string> toolsOff)
        {
            return toolsOn.Count == 0 && toolsOff.Count == 0 ? AccessPresets.Matching(groups) : AccessPresets.Custom;
        }
    }

    // What agents may use right now. Immutable, so HTTP threads can read it without locking.
    internal sealed class AccessState
    {
        public static readonly AccessState None = new AccessState(AccessPresets.Custom, new string[0], 0);

        public readonly string Preset;
        // Changes whenever the preset, the groups, a tool, or whether RimMCP is enabled change.
        public readonly int Revision;
        // False while RimMCP is disabled in its settings. Nothing runs then, including requests
        // that were already waiting for the main thread when it was disabled.
        public readonly bool Enabled;
        private readonly HashSet<string> groups;
        private readonly HashSet<string> toolsOn;
        private readonly HashSet<string> toolsOff;

        public AccessState(string preset, IEnumerable<string> groups, int revision, bool enabled = true)
            : this(preset, groups, new string[0], new string[0], revision, enabled)
        {
        }

        public AccessState(string preset, IEnumerable<string> groups, IEnumerable<string> toolsOn, IEnumerable<string> toolsOff, int revision, bool enabled = true)
        {
            Preset = preset;
            this.groups = new HashSet<string>(groups);
            this.toolsOn = new HashSet<string>(toolsOn);
            this.toolsOff = new HashSet<string>(toolsOff);
            Revision = revision;
            Enabled = enabled;
        }

        // Whether the group is on as a whole. Bridge routes outside any group, such as the access
        // endpoint, are reachable whenever RimMCP is enabled.
        public bool Allows(string group)
        {
            return Enabled && (group == null || groups.Contains(group));
        }

        // Whether a tool can be used: its group, unless that one tool was turned on or off.
        public bool AllowsTool(ToolSpec spec)
        {
            return Enabled && AccessTools.IsOn(spec, groups, toolsOn, toolsOff);
        }

        public bool AllowsTool(string name)
        {
            ToolSpec spec = ToolManifest.McpTools.FirstOrDefault(candidate => candidate.Name == name);
            return spec != null && AllowsTool(spec);
        }

        public bool AllowsChanges
        {
            get { return Enabled && ToolManifest.McpTools.Any(spec => spec.Group != ToolGroups.Observe && spec.Group != ToolGroups.Diagnostics && AllowsTool(spec)); }
        }

        public bool SameAs(string preset, IEnumerable<string> groups, IEnumerable<string> toolsOn, IEnumerable<string> toolsOff)
        {
            return Enabled && Preset == preset && this.groups.SetEquals(groups) && this.toolsOn.SetEquals(toolsOn) && this.toolsOff.SetEquals(toolsOff);
        }

        // The same preferences, refused until RimMCP is enabled again.
        public AccessState Disabled()
        {
            return Enabled ? new AccessState(Preset, groups, toolsOn, toolsOff, Revision + 1, false) : this;
        }
    }
}
