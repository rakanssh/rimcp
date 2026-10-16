using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // A home map's food in nutrition: the food the game counts for the Low food alert (fresh,
    // explored, human-edible food in storage), split into meals and raw food, with how long it
    // lasts at the colonists' current rate of eating, how much of it will rot soon at the current
    // temperature, and how it has changed lately.
    internal sealed class FoodSummary
    {
        // Alert_LowFood's threshold.
        public const float LowPerColonist = 4f;

        public FoodSample Stored;
        public float EatenPerDay;
        public float RotsWithinDay;
        public float RotsWithinThreeDays;

        public static FoodSummary Of(Map map)
        {
            FoodSummary food = new FoodSummary
            {
                Stored = FoodSample.Take(map),
                EatenPerDay = map.mapPawns.FreeColonistsSpawned
                    .Where(pawn => pawn.needs != null && pawn.needs.food != null)
                    .Sum(pawn => pawn.needs.food.FoodFallPerTick) * GenDate.TicksPerDay
            };
            foreach (Thing thing in FoodSample.StoredFood(map))
            {
                CompRottable rottable = thing.TryGetComp<CompRottable>();
                if (rottable == null || !rottable.Active)
                {
                    continue;
                }
                int ticks = rottable.TicksUntilRotAtCurrentTemp;
                float nutrition = thing.GetStatValue(StatDefOf.Nutrition) * thing.stackCount;
                if (ticks < GenDate.TicksPerDay)
                {
                    food.RotsWithinDay += nutrition;
                }
                if (ticks < 3 * GenDate.TicksPerDay)
                {
                    food.RotsWithinThreeDays += nutrition;
                }
            }
            return food;
        }

        // More will rot within a day than the colonists eat in a day, so some is sure to go to waste.
        public bool Wasting
        {
            get { return RotsWithinDay > EatenPerDay; }
        }

        // Alert_LowFood, without its grace period at the start of a game.
        public static bool IsLow(Map map)
        {
            return map.IsPlayerHome && map.mapPawns.AnyColonistSpawned
                && FoodSample.Take(map).Total < LowPerColonist * map.mapPawns.FreeColonistsSpawnedCount;
        }

        public object ToDto(Map map)
        {
            return Dto.Obj(
                Dto.Field("storedNutrition", Dto.Obj(
                    Dto.Field("meals", Math.Round(Stored.Meals, 1)),
                    Dto.Field("raw", Math.Round(Stored.Raw, 1)),
                    Dto.Field("total", Math.Round(Stored.Total, 1)))),
                Dto.Field("eatenPerDay", Math.Round(EatenPerDay, 1)),
                Dto.Field("daysLeft", EatenPerDay <= 0f ? null : (object)Math.Round(Stored.Total / EatenPerDay, 1)),
                Dto.Field("rotsWithin", Dto.Obj(
                    Dto.Field("day", Math.Round(RotsWithinDay, 1)),
                    Dto.Field("threeDays", Math.Round(RotsWithinThreeDays, 1)))),
                Dto.Field("trend", FoodHistory.Trend(map, Stored)));
        }
    }

    internal sealed class FoodSample
    {
        public int Tick;
        public float Meals;
        public float Raw;

        public float Total
        {
            get { return Meals + Raw; }
        }

        // ResourceCounter.TotalHumanEdibleNutrition, split by whether the food is a meal. The things
        // are counted directly rather than from the counter's cache, which the game refreshes only
        // every few seconds of game time, so a read just after food is stored, or while paused,
        // agrees with the food's other figures.
        public static FoodSample Take(Map map)
        {
            FoodSample sample = new FoodSample { Tick = Find.TickManager.TicksGame };
            foreach (Thing thing in StoredFood(map))
            {
                float nutrition = thing.def.GetStatValueAbstract(StatDefOf.Nutrition) * thing.stackCount;
                if (thing.def.ingestible.IsMeal)
                {
                    sample.Meals += nutrition;
                }
                else
                {
                    sample.Raw += nutrition;
                }
            }
            return sample;
        }

        // The things ResourceCounter.UpdateResourceCounts counts as human-edible food.
        public static IEnumerable<Thing> StoredFood(Map map)
        {
            foreach (SlotGroup group in map.haulDestinationManager.AllGroupsListForReading)
            {
                foreach (Thing held in group.HeldThings)
                {
                    Thing thing = held.GetInnerIfMinified();
                    if (thing.def.CountAsResource && thing.def.IsNutritionGivingIngestible && thing.def.ingestible.HumanEdible
                        && !thing.IsNotFresh() && !(thing.SpawnedOrAnyParentSpawned && thing.PositionHeld.Fogged(thing.MapHeld)))
                    {
                        yield return thing;
                    }
                }
            }
        }
    }

    // Hourly samples of each home map's stored food. They are kept in memory for the loaded game
    // only, never in the save, so a trend starts again after loading.
    internal static class FoodHistory
    {
        private const int MaxSamples = 15 * 24;
        private const int Window = 3 * GenDate.TicksPerDay;
        private const int MinSpan = 6 * GenDate.TicksPerHour;

        private static readonly Dictionary<int, List<FoodSample>> samples = new Dictionary<int, List<FoodSample>>();
        private static string loadId;
        private static int nextTick;

        // Called every frame; samples at most once an in-game hour. A failure is logged once and
        // must not stop the frame, which also serves bridge requests.
        public static void Update()
        {
            try
            {
                Sample();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("RimMCP could not sample stored food: " + e, 0x52694d43);
            }
        }

        private static void Sample()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null || Find.TickManager == null)
            {
                return;
            }
            string load = GameContext.LoadId;
            if (load != loadId)
            {
                loadId = load;
                samples.Clear();
                nextTick = 0;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick < nextTick)
            {
                return;
            }
            nextTick = tick + GenDate.TicksPerHour;
            foreach (Map map in Find.Maps.Where(map => map.IsPlayerHome))
            {
                List<FoodSample> list;
                if (!samples.TryGetValue(map.uniqueID, out list))
                {
                    samples[map.uniqueID] = list = new List<FoodSample>();
                }
                list.Add(FoodSample.Take(map));
                if (list.Count > MaxSamples)
                {
                    list.RemoveAt(0);
                }
            }
        }

        public static object Trend(Map map, FoodSample now)
        {
            List<FoodSample> list;
            return samples.TryGetValue(map.uniqueID, out list) ? Trend(list, now) : null;
        }

        // Change per day from the oldest sample in the last three days to now; nothing until six
        // hours have been sampled.
        public static object Trend(IList<FoodSample> history, FoodSample now)
        {
            FoodSample from = history.FirstOrDefault(sample => sample.Tick >= now.Tick - Window);
            if (from == null || now.Tick - from.Tick < MinSpan)
            {
                return null;
            }
            float days = (now.Tick - from.Tick) / (float)GenDate.TicksPerDay;
            return Dto.Obj(
                Dto.Field("overHours", (now.Tick - from.Tick) / GenDate.TicksPerHour),
                Dto.Field("totalPerDay", Math.Round((now.Total - from.Total) / days, 1)),
                Dto.Field("mealsPerDay", Math.Round((now.Meals - from.Meals) / days, 1)),
                Dto.Field("rawPerDay", Math.Round((now.Raw - from.Raw) / days, 1)));
        }
    }
}
