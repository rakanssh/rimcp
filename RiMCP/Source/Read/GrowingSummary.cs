using System;
using System.Collections.Generic;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace RiMCP.Read
{
    // A growing zone's crop, kept as three separate questions: where it could be sown now
    // (sowableCells), where it is already planted (sown, harvestable, averageGrowth), and whether
    // those plants are growing (notGrowing, with the reasons the plant's inspect pane gives).
    // Blockers say what stops the zone: no cell to plant, the wrong season, or nobody to sow.
    internal static class GrowingSummary
    {
        // The game's own answers, which it keeps protected. GrowthPerTick is what a plant grows
        // now: nothing while it rests at night, even under a sun lamp, or while it is dark, too cold
        // or hot, blighted, or in a vacuum. Each falls back to the vanilla rule if a game update
        // renames it.
        private static readonly Func<Plant, bool> Resting = Getter<bool>("Resting")
            ?? (plant => GenLocalDate.DayPercent(plant) < 0.25f || GenLocalDate.DayPercent(plant) > 0.8f);
        private static readonly Func<Plant, bool> HasEnoughLight = Getter<bool>("HasEnoughLightToGrow")
            ?? (plant => plant.GrowthRateFactor_Light > 0.001f);
        private static readonly Func<Plant, float> GrowthPerTick = Getter<float>("GrowthPerTick")
            ?? (plant => plant.LifeStage != PlantLifeStage.Growing || Resting(plant) ? 0f : plant.GrowthRate);

        public static Dictionary<string, object> Of(Zone_Growing zone)
        {
            Map map = zone.Map;
            ThingDef crop = zone.GetPlantDefToGrow();
            int sowable = 0, sown = 0, harvestable = 0, notGrowing = 0;
            int resting = 0, tooDark = 0, temperature = 0, other = 0;
            float growth = 0f;
            string unsowable = null;
            foreach (IntVec3 cell in zone.Cells)
            {
                Plant plant = cell.GetPlant(map);
                if (plant != null && plant.def == crop)
                {
                    sown++;
                    growth += plant.Growth;
                    harvestable += plant.HarvestableNow ? 1 : 0;
                    if (plant.LifeStage == PlantLifeStage.Growing && GrowthPerTick(plant) <= 0f)
                    {
                        notGrowing++;
                        bool isResting = Resting(plant);
                        bool isDark = !HasEnoughLight(plant);
                        bool badTemperature = Mathf.Approximately(plant.GrowthRateFactor_Temperature, 0f) || !PlantUtility.GrowthSeasonNow(cell, map, crop);
                        resting += isResting ? 1 : 0;
                        tooDark += isDark ? 1 : 0;
                        temperature += badTemperature ? 1 : 0;
                        other += isResting || isDark || badTemperature ? 0 : 1;
                    }
                    continue;
                }
                AcceptanceReport report = CanSowAt(crop, cell, map);
                if (report.Accepted)
                {
                    sowable++;
                }
                else if (unsowable == null)
                {
                    unsowable = string.IsNullOrWhiteSpace(report.Reason) ? "The crop cannot be sown here." : report.Reason.StripTags();
                }
            }

            List<object> blockers = new List<object>();
            if (zone.Cells.Count > 0 && sown == 0 && sowable == 0)
            {
                blockers.Add(Blockers.Of("noPlantableCells", "No cell in the zone can be sown with " + crop.label + ": " + unsowable));
            }
            if (zone.Cells.Count > 0 && !PlantUtility.GrowthSeasonNow(zone.Cells[0], map, crop))
            {
                blockers.Add(Blockers.Of("outOfSeason", "It is too cold or too hot for " + crop.label + " to grow here now."));
            }
            int minSkill = crop.plant.sowMinSkill;
            if (zone.allowSow && !Blockers.AnyWorker(map, WorkTypeDefOf.Growing, SkillDefOf.Plants, minSkill))
            {
                blockers.Add(Blockers.Of("noGrower", "No colonist has Growing on" + (minSkill > 0 ? " with Plants skill " + minSkill + " or more" : "") + ", so nothing will be sown.", Dto.Field("minSkill", minSkill)));
            }

            return Dto.Obj(
                Dto.Field("plant", ReadUtil.Def(crop)),
                Dto.Field("allowSow", zone.allowSow),
                Dto.Field("allowCut", zone.allowCut),
                Dto.Field("sowableCells", sowable),
                Dto.Field("sown", sown),
                Dto.Field("harvestable", harvestable),
                Dto.Field("averageGrowth", sown == 0 ? null : (object)Math.Round(growth / sown, 2)),
                Dto.Field("notGrowing", notGrowing > 0 ? (object)notGrowing : null),
                Dto.Field("notGrowingReasons", notGrowing == 0 ? null : Dto.Obj(
                    Dto.Field("resting", resting > 0 ? (object)resting : null),
                    Dto.Field("tooDark", tooDark > 0 ? (object)tooDark : null),
                    Dto.Field("temperature", temperature > 0 ? (object)temperature : null),
                    Dto.Field("other", other > 0 ? (object)other : null))),
                Dto.Field("blockers", Blockers.ToArray(blockers)));
        }

        private static Func<Plant, T> Getter<T>(string name)
        {
            PropertyInfo property = typeof(Plant).GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo getter = property == null || property.PropertyType != typeof(T) ? null : property.GetGetMethod(true);
            return getter == null ? null : (Func<Plant, T>)Delegate.CreateDelegate(typeof(Func<Plant, T>), getter);
        }

        // The least fertility a growing zone takes, as Designator_ZoneAdd_Growing checks: half with
        // Biotech, otherwise what potatoes need, and lower when the colony can build fungal gravel.
        public static float ZoneMinFertility()
        {
            float min = ModsConfig.BiotechActive ? 0.5f : ThingDefOf.Plant_Potato.plant.fertilityMin;
            if (ModsConfig.IdeologyActive && BuildCopyCommandUtility.FindAllowedDesignator(TerrainDefOf.FungalGravel) != null)
            {
                min = Math.Min(min, ThingDefOf.Plant_Nutrifungus.plant.fertilityMin);
            }
            return min;
        }

        // Whether the crop could be sown in a cell without it: fertility, pollution, terrain, and
        // what is in the way (PlantUtility.CanEverPlantAt), and roofs over crops that cannot grow
        // under one. Wild plants in the way do not count, because growers cut them. The map's yearly
        // temperatures do not count either: the season blocker uses the zone's own temperature, so a
        // heated greenhouse on a cold map can still grow.
        private static AcceptanceReport CanSowAt(ThingDef crop, IntVec3 cell, Map map)
        {
            Thing blocking;
            AcceptanceReport report = crop.CanEverPlantAt(cell, map, out blocking, canWipePlantsExceptTree: true, checkMapTemperature: false);
            if (report.Accepted && crop.plant.interferesWithRoof && cell.Roofed(map))
            {
                return "Roofed: " + crop.label + " cannot grow under a roof.";
            }
            return report;
        }
    }
}
