using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Command
{
    // The storyteller screen: storyteller, difficulty (a preset or the custom panel), and commitment mode.
    internal static partial class SetupCommandService
    {
        public static BridgeResponse SetStoryteller(BridgeRequest request, RouteMatch route)
        {
            StorytellerBody body = CommandUtil.ReadBody<StorytellerBody>(request);
            Page_SelectStoryteller page = SetupController.Require<Page_SelectStoryteller>(SetupController.Storyteller);
            SetupController.RequireRevision(body.ExpectedRevision);
            StorytellerDef storyteller = DefDatabase<StorytellerDef>.AllDefs.FirstOrDefault(def => def.listVisible && string.Equals(def.defName, (body.Storyteller ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (storyteller == null)
            {
                throw new CommandException(400, "Field 'storyteller' must be one of: " + string.Join(", ", DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible).Select(def => def.defName).ToArray()) + ".");
            }
            DifficultyDef difficulty = DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => string.Equals(def.defName, (body.Difficulty ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (difficulty == null)
            {
                throw new CommandException(400, "Field 'difficulty' must be one of: " + string.Join(", ", DefDatabase<DifficultyDef>.AllDefs.Select(def => def.defName).ToArray()) + ".");
            }
            if (!body.CommitmentMode.HasValue)
            {
                throw new CommandException(400, "Field 'commitmentMode' is required, as the screen asks: true for commitment mode, false to reload any time.");
            }
            Difficulty values;
            if (difficulty.isCustom)
            {
                values = CustomDifficulty(page, body.Custom);
            }
            else if (body.Custom != null)
            {
                throw new CommandException(400, "Field 'custom' is for the custom difficulty (" + CustomDifficultyDef().defName + ").");
            }
            else
            {
                values = new Difficulty(difficulty);
            }
            SetupController.Set(page, "storyteller", storyteller);
            SetupController.Set(page, "difficulty", difficulty);
            SetupController.Set(page, "difficultyValues", values);
            Find.GameInitData.permadeathChosen = true;
            Find.GameInitData.permadeath = body.CommitmentMode.Value;
            if (!SetupController.CanNext(page))
            {
                SetupController.Changed();
                throw new CommandException(409, "The storyteller screen did not accept these choices.");
            }
            SetupController.DoNext(page);
            SetupController.Changed();
            return SessionCommandService.Envelope(true, State());
        }

        // The custom difficulty panel: its values start from a preset (Rough when first chosen, as the
        // screen does, or the one given in base, as its Reset menu does), then each setting is set as
        // its control would set it. Checked in full before the screen changes.
        private static Difficulty CustomDifficulty(Page_SelectStoryteller page, CustomBody custom)
        {
            DifficultyDef current = SetupController.Get(page, "difficulty") as DifficultyDef;
            Difficulty values;
            if (custom != null && !string.IsNullOrWhiteSpace(custom.Base))
            {
                DifficultyDef preset = DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => !def.isCustom && string.Equals(def.defName, custom.Base.Trim(), StringComparison.OrdinalIgnoreCase));
                if (preset == null)
                {
                    throw new CommandException(400, "Field 'custom.base' must be a preset: " + string.Join(", ", DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom).Select(def => def.defName).ToArray()) + ".");
                }
                values = new Difficulty(preset);
            }
            else if (current != null && current.isCustom)
            {
                values = DifficultySettings.Copy((Difficulty)SetupController.Get(page, "difficultyValues"));
            }
            else
            {
                values = new Difficulty(DifficultyDefOf.Rough);
            }
            List<SettingEntry> entries = (custom == null || custom.Settings == null ? new SettingEntry[0] : custom.Settings).ToList();
            if (entries.Any(entry => entry == null) || entries.Select(entry => (entry.Setting ?? "").Trim().ToLowerInvariant()).Distinct().Count() != entries.Count)
            {
                throw new CommandException(400, "Field 'custom.settings' names each setting once.");
            }
            foreach (SettingEntry entry in entries)
            {
                if (DifficultySettings.Named(entry.Setting) == null)
                {
                    throw new CommandException(400, "Field 'custom.settings' has an unknown setting: " + entry.Setting + "; get_setup lists them.");
                }
            }
            foreach (DifficultySetting setting in DifficultySettings.All)
            {
                SettingEntry entry = entries.FirstOrDefault(candidate => string.Equals(candidate.Setting.Trim(), setting.Name, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    continue;
                }
                string unavailable = setting.Unavailable(values);
                if (unavailable != null)
                {
                    throw new CommandException(409, "The custom difficulty panel does not offer " + setting.Name + " now: " + unavailable + ".");
                }
                if (setting.Toggle)
                {
                    if (!entry.Enabled.HasValue || entry.Value.HasValue)
                    {
                        throw new CommandException(400, setting.Name + " is a checkbox; give enabled: true or false.");
                    }
                    setting.WriteToggle(values, entry.Enabled.Value);
                }
                else
                {
                    if (!entry.Value.HasValue || entry.Enabled.HasValue || float.IsNaN(entry.Value.Value) || entry.Value.Value < setting.Min || entry.Value.Value > setting.Max)
                    {
                        throw new CommandException(400, setting.Name + " is a slider; give value from " + setting.Min + " to " + setting.Max + " in steps of " + setting.Step + ".");
                    }
                    setting.Write(values, entry.Value.Value);
                }
            }
            return values;
        }

        private static DifficultyDef CustomDifficultyDef()
        {
            return DefDatabase<DifficultyDef>.AllDefs.FirstOrDefault(def => def.isCustom);
        }

        // The custom panel's settings with the values it would start from or shows now.
        private static object[] CustomSettingsShown(Page page)
        {
            DifficultyDef current = SetupController.Get(page, "difficulty") as DifficultyDef;
            Difficulty values = current != null && current.isCustom ? (Difficulty)SetupController.Get(page, "difficultyValues") : new Difficulty(DifficultyDefOf.Rough);
            return DifficultySettings.All.Select(setting => setting.ToDto(values)).ToArray();
        }

        [DataContract]
        private sealed class StorytellerBody
        {
            [DataMember(Name = "storyteller")] public string Storyteller { get; set; }
            [DataMember(Name = "difficulty")] public string Difficulty { get; set; }
            [DataMember(Name = "commitmentMode")] public bool? CommitmentMode { get; set; }
            [DataMember(Name = "custom")] public CustomBody Custom { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
        }

        [DataContract]
        private sealed class CustomBody
        {
            [DataMember(Name = "base")] public string Base { get; set; }
            [DataMember(Name = "settings")] public SettingEntry[] Settings { get; set; }
        }

        [DataContract]
        private sealed class SettingEntry
        {
            [DataMember(Name = "setting")] public string Setting { get; set; }
            [DataMember(Name = "value")] public float? Value { get; set; }
            [DataMember(Name = "enabled")] public bool? Enabled { get; set; }
        }
    }
}
