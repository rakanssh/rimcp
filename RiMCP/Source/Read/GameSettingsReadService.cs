using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class GameSettingsReadService
    {
        internal static readonly float[] AutosaveIntervals = { 0.5f, 1f, 3f, 7f, 14f };

        public static BridgeResponse GetSettings(BridgeRequest request, RouteMatch route)
        {
            return ReadEnvelope.Ok(ReadContext.From(request), Dto.Obj(
                Dto.Field("programState", Current.ProgramState.ToString()),
                Dto.Field("current", Snapshot()),
                Dto.Field("choices", Dto.Obj(
                    Dto.Field("storytellers", SelectableStorytellers().Select(SerializeChoice).ToArray()),
                    Dto.Field("difficultyPresets", DifficultyPresets().Select(SerializeChoice).ToArray()),
                    Dto.Field("automaticPauseMode", Enum.GetNames(typeof(AutomaticPauseMode))),
                    Dto.Field("autosaveIntervalDays", AutosaveIntervals),
                    Dto.Field("volumeMaster", Dto.Obj(Dto.Field("min", 0f), Dto.Field("max", 1f))),
                    Dto.Field("volumeMusic", Dto.Obj(Dto.Field("min", 0f), Dto.Field("max", 1f)))))));
        }

        internal static IEnumerable<StorytellerDef> SelectableStorytellers()
        {
            return DefDatabase<StorytellerDef>.AllDefs.Where(def => def.listVisible).OrderBy(def => def.listOrder);
        }

        internal static IEnumerable<DifficultyDef> DifficultyPresets()
        {
            return DefDatabase<DifficultyDef>.AllDefs.Where(def => !def.isCustom);
        }

        internal static object Snapshot()
        {
            Storyteller storyteller = Current.Game == null ? null : Current.Game.storyteller;
            return Dto.Obj(
                Dto.Field("global", Dto.Obj(
                    Dto.Field("runInBackground", Prefs.RunInBackground),
                    Dto.Field("pauseOnLoad", Prefs.PauseOnLoad),
                    Dto.Field("automaticPauseMode", Prefs.AutomaticPauseMode.ToString()),
                    Dto.Field("autosaveIntervalDays", Prefs.AutosaveIntervalDays),
                    Dto.Field("volumeMaster", Prefs.VolumeMaster),
                    Dto.Field("volumeMusic", Prefs.VolumeMusic))),
                Dto.Field("colony", Dto.Obj(
                    Dto.Field("available", storyteller != null),
                    Dto.Field("storytellerDefName", storyteller == null || storyteller.def == null ? null : storyteller.def.defName),
                    Dto.Field("storytellerLabel", ReadUtil.DefLabel(storyteller == null ? null : storyteller.def)),
                    Dto.Field("difficultyDefName", storyteller == null || storyteller.difficultyDef == null ? null : storyteller.difficultyDef.defName),
                    Dto.Field("difficultyLabel", ReadUtil.DefLabel(storyteller == null ? null : storyteller.difficultyDef)),
                    Dto.Field("difficultyIsCustom", storyteller == null || storyteller.difficultyDef == null ? (object)null : storyteller.difficultyDef.isCustom),
                    Dto.Field("threatScale", storyteller == null || storyteller.difficulty == null ? (object)null : storyteller.difficulty.threatScale))));
        }

        private static object SerializeChoice(Def def)
        {
            return Dto.Obj(Dto.Field("defName", def.defName), Dto.Field("label", ReadUtil.DefLabel(def)));
        }
    }
}
