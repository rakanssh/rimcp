using System;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Shared;
using Verse;

namespace RiMCP.UI
{
    internal static class UiText
    {
        public static string PresetLabel(string preset)
        {
            return Translate("RiMCP.Preset." + preset + ".Label", AccessPresets.Label(preset));
        }

        public static string PresetDescription(string preset)
        {
            return Translate("RiMCP.Preset." + preset + ".Description", AccessPresets.Description(preset));
        }

        public static string GroupLabel(string group)
        {
            return Translate("RiMCP.Group." + group + ".Label", ToolGroups.Label(group));
        }

        public static string GroupDescription(string group)
        {
            return Translate("RiMCP.Group." + group + ".Description", ToolGroups.Description(group));
        }

        public static string ToolDescription(ToolSpec spec)
        {
            return Translate("RiMCP.Tool." + spec.Name + ".Description", spec.Description);
        }

        public static string LastRequest
        {
            get
            {
                DateTime? last = BridgeRuntime.LastRequestUtc;
                if (!last.HasValue)
                {
                    return "RiMCP.Never".Translate();
                }
                int seconds = (int)(DateTime.UtcNow - last.Value).TotalSeconds;
                return seconds < 2 ? "RiMCP.JustNow".Translate().ToString() : "RiMCP.SecondsAgo".Translate(seconds).ToString();
            }
        }

        private static string Translate(string key, string fallback)
        {
            TaggedString text;
            return Translator.TryTranslate(key, out text) ? text.ToString() : fallback;
        }
    }
}
