using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using UnityEngine;
using Verse;

namespace RiMCP.Read
{
    internal static class GameReadService
    {
        public static BridgeResponse GetGameContext(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            object maps = Find.Maps == null
                ? new object[0]
                : Find.Maps.Select(ReadUtil.MapSummary).ToArray();

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("programState", Current.ProgramState.ToString()),
                Dto.Field("world", SerializeWorldInfo()),
                Dto.Field("time", SerializeTime(context.Tick, context.Map)),
                Dto.Field("currentMap", ReadUtil.MapSummary(context.Map)),
                Dto.Field("maps", maps),
                Dto.Field("storyteller", SerializeStoryteller()),
                Dto.Field("mods", SerializeMods())));
        }

        public static BridgeResponse GetColonyStatus(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            List<PawnReadService.PawnRole> pawns = PawnReadService.PawnsForFilter(context.Map, "all").ToList();
            List<Pawn> core = pawns.Where(role => PawnReadService.IsCoreRole(role.Role)).Select(role => role.Pawn).ToList();
            List<Pawn> hostiles = pawns.Where(role => role.Role == "hostile").Select(role => role.Pawn).ToList();
            List<ResourceReadService.ResourceGroup> resources = ResourceReadService.CollectResources(context.Map).ToList();
            List<object> risks = BuildRisks(core, hostiles, resources);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("map", ReadUtil.MapSummary(context.Map)),
                Dto.Field("time", SerializeTime(context.Tick, context.Map)),
                Dto.Field("topRisks", risks),
                Dto.Field("pawns", CountPawns(pawns)),
                Dto.Field("resources", ResourceReadService.SummarizeResources(resources)),
                Dto.Field("medical", MedicalSummary(core)),
                Dto.Field("mood", MoodSummary(core)),
                Dto.Field("threats", ThreatReadService.SummarizeThreats(context.Map)),
                Dto.Field("environment", EnvironmentReadService.SummarizeEnvironment(context.Map)),
                Dto.Field("power", PowerReadService.SummarizePower(context.Map)),
                Dto.Field("currentResearch", ResearchReadService.SerializeProject(ResearchReadService.CurrentProject(), ReadDetail.Summary)),
                Dto.Field("recentEvents", RecentEvents(8))));
        }

        public static object SerializeTime(int ticks, Map map)
        {
            Vector2 longLat = map == null || Find.WorldGrid == null ? Vector2.zero : Find.WorldGrid.LongLatOf(map.Tile);
            int day = GenDate.DayOfSeason(ticks, longLat.x);
            return Dto.Obj(
                Dto.Field("ticksGame", ticks),
                Dto.Field("dayOfSeason", day),
                Dto.Field("quadrum", GenDate.Quadrum(ticks, longLat.x).ToString()),
                Dto.Field("hourOfDay", GenDate.HourOfDay(ticks, longLat.x)),
                Dto.Field("season", GenDate.Season(ticks, longLat).ToString()));
        }

        public static object RecentEvents(int limit)
        {
            List<object> alerts = new List<object>();
            try
            {
                List<Letter> letters = Find.LetterStack == null ? new List<Letter>() : Find.LetterStack.LettersListForReading;
                foreach (Letter letter in letters.Skip(Math.Max(0, letters.Count - limit)))
                {
                    alerts.Add(Dto.Obj(
                        Dto.Field("kind", "letter"),
                        Dto.Field("label", letter.Label.ToString()),
                        Dto.Field("text", LetterText(letter)),
                        Dto.Field("defName", letter.def == null ? null : letter.def.defName)));
                }
            }
            catch
            {
            }
            return alerts;
        }

        private static object SerializeWorldInfo()
        {
            WorldInfo worldInfo = Find.World == null ? null : Find.World.info;
            return Dto.Obj(
                Dto.Field("name", worldInfo == null ? null : worldInfo.name),
                Dto.Field("seedString", worldInfo == null ? null : worldInfo.seedString));
        }

        private static object SerializeStoryteller()
        {
            Storyteller storyteller = Find.Storyteller;
            Difficulty difficulty = storyteller == null ? null : storyteller.difficulty;
            DifficultyDef difficultyDef = storyteller == null ? null : storyteller.difficultyDef;
            return Dto.Obj(
                Dto.Field("defName", storyteller == null || storyteller.def == null ? null : storyteller.def.defName),
                Dto.Field("label", ReadUtil.DefLabel(storyteller == null ? null : storyteller.def)),
                Dto.Field("difficulty", Dto.Obj(
                    Dto.Field("defName", difficultyDef == null ? null : difficultyDef.defName),
                    Dto.Field("label", ReadUtil.DefLabel(difficultyDef)),
                    Dto.Field("threatScale", difficulty == null ? 0f : difficulty.threatScale))));
        }

        private static object SerializeMods()
        {
            List<object> mods = new List<object>();
            try
            {
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    mods.Add(Dto.Obj(
                        Dto.Field("name", mod.Name),
                        Dto.Field("packageId", mod.PackageId)));
                }
            }
            catch
            {
            }
            return Dto.Obj(
                Dto.Field("count", mods.Count),
                Dto.Field("loaded", mods));
        }

        private static object CountPawns(IEnumerable<PawnReadService.PawnRole> pawns)
        {
            Dictionary<string, int> counts = new Dictionary<string, int>();
            foreach (PawnReadService.PawnRole pawn in pawns)
            {
                int count;
                counts.TryGetValue(pawn.Role, out count);
                counts[pawn.Role] = count + 1;
            }
            counts["core"] = counts.Where(pair => PawnReadService.IsCoreRole(pair.Key)).Sum(pair => pair.Value);
            return counts;
        }

        private static object MedicalSummary(IEnumerable<Pawn> core)
        {
            List<Pawn> downed = core.Where(pawn => pawn.Downed).ToList();
            List<Pawn> bleeding = core.Where(pawn => pawn.health != null && pawn.health.hediffSet != null && pawn.health.hediffSet.hediffs.Any(hediff => hediff.Bleeding)).ToList();
            return Dto.Obj(
                Dto.Field("downed", downed.Count),
                Dto.Field("bleeding", bleeding.Count),
                Dto.Field("examples", downed.Concat(bleeding).Distinct().Take(5).Select(pawn => Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(pawn)),
                    Dto.Field("name", ReadUtil.PawnName(pawn)),
                    Dto.Field("downed", pawn.Downed))).ToArray()));
        }

        private static object MoodSummary(IEnumerable<Pawn> core)
        {
            List<Pawn> moodPawns = core.Where(pawn => pawn.needs != null && pawn.needs.mood != null).ToList();
            List<Pawn> low = moodPawns.Where(pawn => pawn.needs.mood.CurLevelPercentage < 0.35f).OrderBy(pawn => pawn.needs.mood.CurLevelPercentage).ToList();
            return Dto.Obj(
                Dto.Field("average", moodPawns.Count == 0 ? 0f : moodPawns.Average(pawn => pawn.needs.mood.CurLevelPercentage)),
                Dto.Field("lowMood", low.Count),
                Dto.Field("examples", low.Take(5).Select(pawn => Dto.Obj(
                    Dto.Field("id", ReadUtil.ThingId(pawn)),
                    Dto.Field("name", ReadUtil.PawnName(pawn)),
                    Dto.Field("mood", pawn.needs.mood.CurLevelPercentage))).ToArray()));
        }

        private static List<object> BuildRisks(List<Pawn> core, List<Pawn> hostiles, List<ResourceReadService.ResourceGroup> resources)
        {
            List<object> risks = new List<object>();
            int downed = core.Count(pawn => pawn.Downed);
            if (downed > 0)
            {
                risks.Add(Risk("medical", "downedCorePawns", "Downed core pawns need attention.", downed, "list_pawns?filter=core&include=health"));
            }
            int lowMood = core.Count(pawn => pawn.needs != null && pawn.needs.mood != null && pawn.needs.mood.CurLevelPercentage < 0.35f);
            if (lowMood > 0)
            {
                risks.Add(Risk("mood", "lowMood", "Core pawns are below mental break comfort range.", lowMood, "list_pawns?filter=core&include=needs"));
            }
            if (hostiles.Count > 0)
            {
                risks.Add(Risk("threat", "hostilesPresent", "Hostile pawns are spawned on the map.", hostiles.Count, "list_threats"));
            }
            float nutrition = resources.Sum(group => group.Nutrition);
            if (core.Count > 0 && nutrition < core.Count * 2f)
            {
                risks.Add(Risk("resources", "lowFood", "Estimated stored nutrition is low relative to core pawn count.", (int)nutrition, "list_resources?include=food"));
            }
            return risks;
        }

        private static object Risk(string category, string code, string label, int count, string nextRead)
        {
            return Dto.Obj(
                Dto.Field("category", category),
                Dto.Field("code", code),
                Dto.Field("label", label),
                Dto.Field("count", count),
                Dto.Field("nextRead", nextRead));
        }

        private static string LetterText(Letter letter)
        {
            StandardLetter standard = letter as StandardLetter;
            if (standard != null)
            {
                return standard.Text.ToString();
            }
            ChoiceLetter choice = letter as ChoiceLetter;
            if (choice != null)
            {
                return choice.Text.ToString();
            }
            return null;
        }
    }
}
