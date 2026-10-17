using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // One control on the storyteller screen's custom difficulty panel (StorytellerUI.DrawCustomLeft
    // and DrawCustomRight). Values are in the control's own terms, as the player sees them: a slider's
    // position (some show the reciprocal of the stored value), or a checkbox as shown (some show the
    // opposite of the stored flag).
    internal sealed class DifficultySetting
    {
        public string Name;
        public string LabelKey;
        public bool Toggle;
        public float Min;
        public float Max;
        public float Step = 0.01f;
        public Func<Difficulty, float> Read;
        public Action<Difficulty, float> Write;
        public Func<Difficulty, bool> ReadToggle;
        public Action<Difficulty, bool> WriteToggle;
        // Why the panel does not offer the control with these values, or null when it does.
        public Func<Difficulty, string> Unavailable = values => null;

        public object ToDto(Difficulty values)
        {
            string unavailable = Unavailable(values);
            return Dto.Obj(
                Dto.Field("setting", Name),
                Dto.Field("label", LabelKey.Translate().ToString()),
                Dto.Field("kind", Toggle ? "checkbox" : "slider"),
                Dto.Field("min", Toggle ? null : (object)Min),
                Dto.Field("max", Toggle ? null : (object)Max),
                Dto.Field("step", Toggle ? null : (object)Step),
                Dto.Field("value", Toggle ? null : (object)Math.Round(Read(values), 3)),
                Dto.Field("enabled", Toggle ? (object)ReadToggle(values) : null),
                Dto.Field("unavailable", unavailable));
        }
    }

    internal static class DifficultySettings
    {
        private static List<DifficultySetting> all;

        // In the panel's order, so a control that gates another (no children, fixed wealth) comes first.
        public static List<DifficultySetting> All
        {
            get { return all ?? (all = Build()); }
        }

        public static DifficultySetting Named(string name)
        {
            return All.FirstOrDefault(setting => string.Equals(setting.Name, (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // A copy of a difficulty's values, so a request can be checked in full before it changes any.
        public static Difficulty Copy(Difficulty values)
        {
            return (Difficulty)typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(values, null);
        }

        private static List<DifficultySetting> Build()
        {
            Func<Difficulty, string> biotech = values => ModsConfig.BiotechActive ? null : "needs Biotech";
            Func<Difficulty, string> children = values => biotech(values) ?? (values.noBabiesOrChildren ? "BabiesAreHealthyDisableReason".Translate().ToString() : null);
            Func<Difficulty, string> anomaly = values => !ModsConfig.AnomalyActive ? "needs Anomaly" : values.AnomalyPlaystyleDef == null || !values.AnomalyPlaystyleDef.enableAnomalyContent ? "the Anomaly playstyle has no anomaly threats" : null;
            List<DifficultySetting> settings = new List<DifficultySetting>
            {
                Slider("threatScale", 0f, 5f),
                Toggle("allowBigThreats"),
                Toggle("allowViolentQuests"),
                Toggle("allowIntroThreats"),
                Toggle("predatorsHuntHumanlikes"),
                Toggle("allowExtremeWeatherIncidents"),
                Slider("wastepackInfestationChanceFactor", 0f, 5f, unavailable: biotech),
                Slider("cropYieldFactor", 0f, 5f),
                Slider("mineYieldFactor", 0f, 5f),
                Slider("butcherYieldFactor", 0f, 5f),
                Slider("fishingYieldFactor", 0f, 5f, unavailable: values => ModsConfig.OdysseyActive ? null : "needs Odyssey"),
                Slider("researchSpeedFactor", 0f, 5f),
                Slider("questRewardValueFactor", 0f, 5f),
                Slider("raidLootPointsFactor", 0f, 5f),
                Slider("tradePriceFactorLoss", 0f, 0.5f),
                Slider("maintenanceCostFactor", 0.01f, 1f),
                Slider("scariaRotChance", 0f, 1f),
                Slider("enemyDeathOnDownedChanceFactor", 0f, 1f),
                Slider("nomadicMineableResourcesFactor", 0f, 2f),
                Slider("lowPopConversionBoost", 1f, 5f, 1f, unavailable: values => ModsConfig.IdeologyActive ? null : "needs Ideology"),
                Custom("anomalyThreatsFraction", "Difficulty_AnomalyThreats_Label", 0f, 1f, 0.01f,
                    values => values.overrideAnomalyThreatsFraction ?? 0.15f,
                    (values, value) => values.overrideAnomalyThreatsFraction = value,
                    values => anomaly(values) ?? (values.AnomalyPlaystyleDef.overrideThreatFraction ? null : "this Anomaly playstyle sets inactive and active threats instead")),
                Slider("anomalyThreatsInactiveFraction", 0f, 1f, labelKey: "Difficulty_AnomalyThreatsInactive_Label",
                    unavailable: values => anomaly(values) ?? (values.AnomalyPlaystyleDef.overrideThreatFraction ? "this Anomaly playstyle sets one threat fraction instead" : null)),
                Slider("anomalyThreatsActiveFraction", 0.1f, 1f, labelKey: "Difficulty_AnomalyThreatsActive_Label",
                    unavailable: values => anomaly(values) ?? (values.AnomalyPlaystyleDef.overrideThreatFraction ? "this Anomaly playstyle sets one threat fraction instead" : null)),
                Slider("studyEfficiencyFactor", 0f, 5f, labelKey: "Difficulty_StudyEfficiency_Label", unavailable: anomaly),
                Toggle("noBabiesOrChildren", unavailable: biotech),
                Toggle("babiesAreHealthy", unavailable: biotech),
                Toggle("childRaidersAllowed", unavailable: children),
                Toggle("childShamblersAllowed", unavailable: values => children(values) ?? (ModsConfig.AnomalyActive ? null : "needs Anomaly")),
                Slider("childAgingRate", 1f, 6f, 1f, unavailable: biotech),
                Slider("adultAgingRate", 1f, 6f, 1f, unavailable: biotech),
                Slider("colonistMoodOffset", -20f, 20f, 1f),
                Slider("foodPoisonChanceFactor", 0f, 5f),
                Slider("manhunterChanceOnDamageFactor", 0f, 5f),
                Slider("playerPawnInfectionChanceFactor", 0f, 5f),
                Slider("diseaseIntervalFactor", 0f, 5f, reciprocate: true, cutoff: 100f),
                Slider("enemyReproductionRateFactor", 0f, 5f),
                Slider("deepDrillInfestationChanceFactor", 0f, 5f),
                Slider("friendlyFireChanceFactor", 0f, 1f),
                Slider("allowInstantKillChance", 0f, 1f),
                Toggle("peacefulTemples", invert: true),
                Toggle("allowCaveHives"),
                Toggle("unwaveringPrisoners"),
                Toggle("allowTraps"),
                Toggle("allowTurrets"),
                Toggle("allowMortars"),
                Toggle("classicMortars"),
                Slider("adaptationGrowthRateFactorOverZero", 0f, 1f),
                Slider("adaptationEffectFactor", 0f, 1f),
                Toggle("fixedWealthMode"),
                // Shown as the number of twelfths, 12 over the stored factor, as the panel shows it.
                Custom("fixedWealthTimeFactor", "Difficulty_FixedWealthTimeFactor_Label", 1f, 20f, 1f,
                    values => (float)Math.Round(12f / values.fixedWealthTimeFactor),
                    (values, value) => values.fixedWealthTimeFactor = 12f / value,
                    values => values.fixedWealthMode ? null : "needs fixedWealthMode")
            };
            // A game version without one of these fields does not have its control.
            return settings.Where(setting => setting != null).ToList();
        }

        private static DifficultySetting Slider(string field, float min, float max, float step = 0.01f, bool reciprocate = false, float cutoff = 1000f, string labelKey = null, Func<Difficulty, string> unavailable = null)
        {
            FieldInfo info = typeof(Difficulty).GetField(field);
            if (info == null || info.FieldType != typeof(float))
            {
                return null;
            }
            return new DifficultySetting
            {
                Name = field,
                LabelKey = labelKey ?? "Difficulty_" + field.CapitalizeFirst() + (reciprocate ? "_Inverted" : "") + "_Label",
                Min = min,
                Max = max,
                Step = step,
                Read = values => reciprocate ? Reciprocal((float)info.GetValue(values), cutoff) : (float)info.GetValue(values),
                Write = (values, value) =>
                {
                    value = GenMath.RoundTo(value, step);
                    info.SetValue(values, reciprocate ? Reciprocal(value, cutoff) : value);
                },
                Unavailable = unavailable ?? (values => null)
            };
        }

        private static DifficultySetting Custom(string name, string labelKey, float min, float max, float step, Func<Difficulty, float> read, Action<Difficulty, float> write, Func<Difficulty, string> unavailable)
        {
            return new DifficultySetting
            {
                Name = name,
                LabelKey = labelKey,
                Min = min,
                Max = max,
                Step = step,
                Read = read,
                Write = (values, value) => write(values, GenMath.RoundTo(value, step)),
                Unavailable = unavailable
            };
        }

        private static DifficultySetting Toggle(string field, bool invert = false, Func<Difficulty, string> unavailable = null)
        {
            FieldInfo info = typeof(Difficulty).GetField(field);
            if (info == null || info.FieldType != typeof(bool))
            {
                return null;
            }
            return new DifficultySetting
            {
                Name = field,
                LabelKey = "Difficulty_" + field.CapitalizeFirst() + (invert ? "_Inverted" : "") + "_Label",
                Toggle = true,
                ReadToggle = values => (bool)info.GetValue(values) != invert,
                WriteToggle = (values, shown) => info.SetValue(values, shown != invert),
                Unavailable = unavailable ?? (values => null)
            };
        }

        // StorytellerUI.Reciprocal.
        private static float Reciprocal(float f, float cutOff)
        {
            cutOff *= 10f;
            if (Math.Abs(f) < 0.01f)
            {
                return cutOff;
            }
            if (f >= 0.99f * cutOff)
            {
                return 0f;
            }
            return 1f / f;
        }
    }
}
