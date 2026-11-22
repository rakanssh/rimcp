using System;
using System.Collections.Generic;
using RiMCP.Bridge;
using RiMCP.Settings;
using RiMCP.Shared;
using Verse;

namespace RiMCP.UI
{
    internal static class UiText
    {
        private static readonly Dictionary<string, (TextKey Label, TextKey Description)> presets =
            new Dictionary<string, (TextKey, TextKey)>
        {
            [AccessPresets.Full] = (Keys.Preset_full_Label, Keys.Preset_full_Description),
            [AccessPresets.Control] = (Keys.Preset_control_Label, Keys.Preset_control_Description),
            [AccessPresets.Administration] = (Keys.Preset_administration_Label, Keys.Preset_administration_Description),
            [AccessPresets.ReadOnly] = (Keys.Preset_readOnly_Label, Keys.Preset_readOnly_Description),
            [AccessPresets.Custom] = (Keys.Preset_custom_Label, Keys.Preset_custom_Description),
        };

        public static DisplayText PresetLabel(string preset)
        {
            return preset != null && presets.TryGetValue(preset, out var text) ? text.Label.Display() : Keys.Preset_custom_Label.Display();
        }

        public static DisplayText PresetDescription(string preset)
        {
            return preset != null && presets.TryGetValue(preset, out var text) ? text.Description.Display() : Keys.Preset_custom_Description.Display();
        }

        private static readonly Dictionary<string, (TextKey Label, TextKey Description)> groups =
            new Dictionary<string, (TextKey, TextKey)>
        {
            [ToolGroups.Observe] = (Keys.Group_observe_Label, Keys.Group_observe_Description),
            [ToolGroups.Diagnostics] = (Keys.Group_diagnostics_Label, Keys.Group_diagnostics_Description),
            [ToolGroups.Time] = (Keys.Group_time_Label, Keys.Group_time_Description),
            [ToolGroups.Administration] = (Keys.Group_administration_Label, Keys.Group_administration_Description),
            [ToolGroups.Orders] = (Keys.Group_orders_Label, Keys.Group_orders_Description),
            [ToolGroups.PawnControl] = (Keys.Group_pawnControl_Label, Keys.Group_pawnControl_Description),
            [ToolGroups.Decisions] = (Keys.Group_decisions_Label, Keys.Group_decisions_Description),
            [ToolGroups.GameSettings] = (Keys.Group_gameSettings_Label, Keys.Group_gameSettings_Description),
            [ToolGroups.GameSetup] = (Keys.Group_gameSetup_Label, Keys.Group_gameSetup_Description),
        };

        public static DisplayText GroupLabel(string group)
        {
            return group != null && groups.TryGetValue(group, out var text) ? text.Label.Display() : DisplayText.Raw(group);
        }

        public static DisplayText GroupDescription(string group)
        {
            return group != null && groups.TryGetValue(group, out var text) ? text.Description.Display() : DisplayText.Raw("");
        }

        public static string ToolDescription(ToolSpec spec)
        {
            LoadedLanguage language = LanguageDatabase.activeLanguage;
            if (language != null && language.folderName != LanguageDatabase.DefaultLangFolderName
                && language.TryGetTextFromKey("RiMCP.Tool." + spec.Name + ".Description", out var text)
                && !string.IsNullOrWhiteSpace(text.RawText))
                return text.RawText;
            return spec.Description;
        }

        public static string LastRequest
        {
            get
            {
                DateTime? last = BridgeRuntime.LastRequestUtc;
                if (!last.HasValue)
                {
                    return Keys.Never.Text();
                }
                int seconds = (int)(DateTime.UtcNow - last.Value).TotalSeconds;
                return seconds < 2 ? Keys.JustNow.Text() : Keys.SecondsAgo.Text(seconds);
            }
        }
    }
}
