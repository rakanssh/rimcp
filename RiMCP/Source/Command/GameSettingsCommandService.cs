using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class GameSettingsCommandService
    {
        public static BridgeResponse SetSettings(BridgeRequest request, RouteMatch route)
        {
            SettingsBody body = ReadBody(request);
            Storyteller storyteller = Current.Game == null ? null : Current.Game.storyteller;
            if ((body.StorytellerDefName != null || body.DifficultyDefName != null) && storyteller == null)
            {
                throw new CommandException(409, "No colony storyteller is loaded.");
            }

            StorytellerDef storytellerDef = null;
            if (body.StorytellerDefName != null)
            {
                storytellerDef = GameSettingsReadService.SelectableStorytellers().FirstOrDefault(def => def.defName == body.StorytellerDefName);
                if (storytellerDef == null)
                {
                    throw new CommandException(400, "Unknown or unavailable storytellerDefName. Read get_game_settings for available choices.");
                }
            }
            DifficultyDef difficultyDef = null;
            if (body.DifficultyDefName != null)
            {
                difficultyDef = GameSettingsReadService.DifficultyPresets().FirstOrDefault(def => def.defName == body.DifficultyDefName);
                if (difficultyDef == null)
                {
                    throw new CommandException(400, "Unknown or custom difficultyDefName. Choose a preset from get_game_settings.");
                }
            }

            CommandContext context = CommandUtil.ContextFor(null);
            object previous = GameSettingsReadService.Snapshot();
            bool storytellerChanged = storytellerDef != null && storyteller.def != storytellerDef;
            // Anomaly options can differ from the named preset without switching to Custom.
            bool difficultyChanged = difficultyDef != null && (storyteller.difficultyDef != difficultyDef ||
                storyteller.difficulty == null ||
                storyteller.difficulty.anomalyThreatsInactiveFraction != difficultyDef.anomalyThreatsInactiveFraction ||
                storyteller.difficulty.anomalyThreatsActiveFraction != difficultyDef.anomalyThreatsActiveFraction ||
                storyteller.difficulty.studyEfficiencyFactor != difficultyDef.studyEfficiencyFactor);
            if (difficultyChanged)
            {
                if (storyteller.difficulty == null)
                {
                    storyteller.difficulty = new Difficulty(difficultyDef);
                }
                else
                {
                    storyteller.difficulty.CopyFrom(difficultyDef);
                }
                storyteller.difficultyDef = difficultyDef;
                foreach (ThingDef def in DefDatabase<ThingDef>.AllDefs.Where(def => def.costListForDifficulty != null))
                {
                    def.costListForDifficulty.RecacheApplies();
                }
                RecipeDefGenerator.ResetRecipeIngredientsForDifficulty();
            }
            if (storytellerChanged)
            {
                storyteller.def = storytellerDef;
                storyteller.Notify_DefChanged();
            }

            bool preferencesChanged = ApplyPreferences(body);
            if (preferencesChanged)
            {
                Prefs.Save();
            }
            return CommandEnvelope.Ok(context, storytellerChanged || difficultyChanged || preferencesChanged, Dto.Obj(
                Dto.Field("previous", previous),
                Dto.Field("current", GameSettingsReadService.Snapshot())));
        }

        private static bool ApplyPreferences(SettingsBody body)
        {
            bool changed = false;
            if (body.RunInBackground.HasValue && Prefs.RunInBackground != body.RunInBackground.Value)
            {
                Prefs.RunInBackground = body.RunInBackground.Value;
                changed = true;
            }
            if (body.PauseOnLoad.HasValue && Prefs.PauseOnLoad != body.PauseOnLoad.Value)
            {
                Prefs.PauseOnLoad = body.PauseOnLoad.Value;
                changed = true;
            }
            if (body.AutomaticPauseMode.HasValue && Prefs.AutomaticPauseMode != body.AutomaticPauseMode.Value)
            {
                Prefs.AutomaticPauseMode = body.AutomaticPauseMode.Value;
                changed = true;
            }
            if (body.AutosaveIntervalDays.HasValue && Prefs.AutosaveIntervalDays != body.AutosaveIntervalDays.Value)
            {
                Prefs.AutosaveIntervalDays = body.AutosaveIntervalDays.Value;
                changed = true;
            }
            if (body.VolumeMaster.HasValue && Prefs.VolumeMaster != body.VolumeMaster.Value)
            {
                Prefs.VolumeMaster = body.VolumeMaster.Value;
                changed = true;
            }
            if (body.VolumeMusic.HasValue && Prefs.VolumeMusic != body.VolumeMusic.Value)
            {
                Prefs.VolumeMusic = body.VolumeMusic.Value;
                changed = true;
            }
            return changed;
        }

        private static SettingsBody ReadBody(BridgeRequest request)
        {
            XElement root;
            try
            {
                using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(request.Body ?? ""), XmlDictionaryReaderQuotas.Max))
                {
                    root = XElement.Load(reader);
                }
            }
            catch (Exception ex) when (ex is XmlException || ex is ArgumentException || ex is InvalidOperationException)
            {
                throw new CommandException(400, "Invalid JSON body.");
            }
            if ((string)root.Attribute("type") != "object" || !root.HasElements)
            {
                throw new CommandException(400, "Provide an object containing at least one supported setting.");
            }

            SettingsBody body = new SettingsBody();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement field in root.Elements())
            {
                string name = field.Name.LocalName;
                if (!seen.Add(name))
                {
                    throw new CommandException(400, "Duplicate setting '" + name + "'.");
                }
                switch (name)
                {
                    case "storytellerDefName": body.StorytellerDefName = ReadString(field); break;
                    case "difficultyDefName": body.DifficultyDefName = ReadString(field); break;
                    case "runInBackground": body.RunInBackground = ReadBool(field); break;
                    case "pauseOnLoad": body.PauseOnLoad = ReadBool(field); break;
                    case "automaticPauseMode":
                        string mode = ReadString(field);
                        string canonicalMode = Enum.GetNames(typeof(AutomaticPauseMode)).FirstOrDefault(value => string.Equals(value, mode, StringComparison.OrdinalIgnoreCase));
                        if (canonicalMode == null)
                        {
                            throw new CommandException(400, "automaticPauseMode must be Never, MajorThreat, AnyThreat, or AnyLetter.");
                        }
                        body.AutomaticPauseMode = (AutomaticPauseMode)Enum.Parse(typeof(AutomaticPauseMode), canonicalMode);
                        break;
                    case "autosaveIntervalDays":
                        double interval = ReadNumber(field);
                        if (!GameSettingsReadService.AutosaveIntervals.Any(value => value == interval))
                        {
                            throw new CommandException(400, "autosaveIntervalDays must be 0.5, 1, 3, 7, or 14.");
                        }
                        body.AutosaveIntervalDays = (float)interval;
                        break;
                    case "volumeMaster": body.VolumeMaster = ReadVolume(field); break;
                    case "volumeMusic": body.VolumeMusic = ReadVolume(field); break;
                    default: throw new CommandException(400, "Unknown setting '" + name + "'.");
                }
            }
            return body;
        }

        private static string ReadString(XElement field)
        {
            RequireType(field, "string");
            if (string.IsNullOrWhiteSpace(field.Value))
            {
                throw new CommandException(400, field.Name.LocalName + " must not be empty.");
            }
            return field.Value;
        }

        private static bool ReadBool(XElement field)
        {
            RequireType(field, "boolean");
            return field.Value == "true";
        }

        private static double ReadNumber(XElement field)
        {
            RequireType(field, "number");
            double value;
            if (!double.TryParse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new CommandException(400, field.Name.LocalName + " must be a finite number.");
            }
            return value;
        }

        private static float ReadVolume(XElement field)
        {
            double value = ReadNumber(field);
            if (value < 0 || value > 1)
            {
                throw new CommandException(400, field.Name.LocalName + " must be between 0 and 1.");
            }
            return (float)value;
        }

        private static void RequireType(XElement field, string type)
        {
            if ((string)field.Attribute("type") != type || field.HasElements)
            {
                throw new CommandException(400, field.Name.LocalName + " must be a JSON " + type + ".");
            }
        }

        private sealed class SettingsBody
        {
            public string StorytellerDefName;
            public string DifficultyDefName;
            public bool? RunInBackground;
            public bool? PauseOnLoad;
            public AutomaticPauseMode? AutomaticPauseMode;
            public float? AutosaveIntervalDays;
            public float? VolumeMaster;
            public float? VolumeMusic;
        }
    }
}
