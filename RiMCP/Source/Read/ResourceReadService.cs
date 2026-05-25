using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ResourceReadService
    {
        public static BridgeResponse ListResources(ReadContext context)
        {
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<ResourceGroup> source = CollectResources(context.Map)
                .OrderBy(group => group.Group)
                .ThenBy(group => group.DefName);
            Page<ResourceGroup> page = new Page<ResourceGroup>(source, context.Request);
            object[] resources = page.Items
                .Select(group => context.Request.IdsOnly ? group.DefName : SerializeResourceGroup(group, context.Request.Detail))
                .ToArray();

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", SummarizeResources(context.Map)),
                Dto.Field("resources", resources)), page.Truncated, page.NextCursor);
        }

        public static object SummarizeResources(Map map)
        {
            List<ResourceGroup> groups = CollectResources(map).ToList();
            float totalNutrition = groups.Sum(group => group.Nutrition);
            int medicine = groups.Where(group => group.IsMedicine).Sum(group => group.Count);
            int meals = groups.Where(group => group.IsMeal).Sum(group => group.Count);
            int foodTypes = groups.Count(group => group.Nutrition > 0f);
            return Dto.Obj(
                Dto.Field("trackedTypes", groups.Count),
                Dto.Field("foodTypes", foodTypes),
                Dto.Field("estimatedNutrition", totalNutrition),
                Dto.Field("meals", meals),
                Dto.Field("medicine", medicine));
        }

        public static IEnumerable<ResourceGroup> CollectResources(Map map)
        {
            Dictionary<string, ResourceGroup> groups = new Dictionary<string, ResourceGroup>();
            if (map == null || map.listerThings == null)
            {
                yield break;
            }

            foreach (Thing thing in map.listerThings.AllThings)
            {
                if (thing == null || thing.def == null || !thing.Spawned)
                {
                    continue;
                }
                if (thing.def.category != ThingCategory.Item && !thing.def.EverHaulable)
                {
                    continue;
                }

                ResourceGroup group;
                if (!groups.TryGetValue(thing.def.defName, out group))
                {
                    group = new ResourceGroup(thing.def);
                    groups[thing.def.defName] = group;
                }
                group.Count += Math.Max(1, thing.stackCount);
                group.Stacks++;
                group.HitPoints += thing.HitPoints;
                if (thing.IsForbidden(Faction.OfPlayer))
                {
                    group.ForbiddenStacks++;
                }
                if (!thing.Position.Roofed(map))
                {
                    group.UnroofedStacks++;
                }
                CompRottable rottable = thing.TryGetComp<CompRottable>();
                if (rottable != null)
                {
                    group.RottableStacks++;
                    group.MinTicksToRot = group.MinTicksToRot < 0 ? rottable.TicksUntilRotAtCurrentTemp : Math.Min(group.MinTicksToRot, rottable.TicksUntilRotAtCurrentTemp);
                }
                group.Nutrition += Math.Max(1, thing.stackCount) * NutritionPerUnit(thing.def);
            }

            foreach (ResourceGroup group in groups.Values)
            {
                yield return group;
            }
        }

        private static object SerializeResourceGroup(ResourceGroup group, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("defName", group.DefName),
                Dto.Field("label", group.Label),
                Dto.Field("group", group.Group),
                Dto.Field("thingCategories", group.ThingCategories),
                Dto.Field("count", group.Count),
                Dto.Field("stacks", group.Stacks),
                Dto.Field("estimatedNutrition", group.Nutrition),
                Dto.Field("forbiddenStacks", group.ForbiddenStacks),
                Dto.Field("unroofedStacks", group.UnroofedStacks));

            if (group.MinTicksToRot >= 0)
            {
                dto["minTicksToRot"] = group.MinTicksToRot;
            }
            if (detail != ReadDetail.Summary)
            {
                dto["averageHitPoints"] = group.Stacks == 0 ? 0 : group.HitPoints / group.Stacks;
                dto["isMedicine"] = group.IsMedicine;
                dto["isMeal"] = group.IsMeal;
                dto["isRottable"] = group.RottableStacks > 0;
            }
            return dto;
        }

        public static float NutritionPerUnit(ThingDef def)
        {
            object ingestible = def == null ? null : Reflect.Read(def, "ingestible");
            if (ingestible == null)
            {
                return 0f;
            }
            return Reflect.ReadFloat(ingestible, "CachedNutrition");
        }

        public sealed class ResourceGroup
        {
            public readonly string DefName;
            public readonly string Label;
            public readonly string Group;
            public readonly object ThingCategories;
            public readonly bool IsMedicine;
            public readonly bool IsMeal;
            public int Count;
            public int Stacks;
            public int HitPoints;
            public int ForbiddenStacks;
            public int UnroofedStacks;
            public int RottableStacks;
            public int MinTicksToRot = -1;
            public float Nutrition;

            public ResourceGroup(ThingDef def)
            {
                DefName = def.defName;
                Label = def.LabelCap;
                ThingCategories = def.thingCategories == null
                    ? new object[0]
                    : def.thingCategories.Select(category => category.defName).OrderBy(name => name).ToArray();
                Group = FirstCategory(def);
                IsMedicine = IsDefMedicine(def);
                IsMeal = IsDefMeal(def);
            }

            private static string FirstCategory(ThingDef def)
            {
                if (def.thingCategories != null && def.thingCategories.Count > 0)
                {
                    return def.thingCategories.OrderBy(category => category.defName).First().defName;
                }
                return def.category.ToString();
            }

            private static bool IsDefMedicine(ThingDef def)
            {
                object medicine = Reflect.Read(def, "medicine");
                return medicine != null;
            }

            private static bool IsDefMeal(ThingDef def)
            {
                object ingestible = Reflect.Read(def, "ingestible");
                object taste = Reflect.Read(ingestible, "tasteThought");
                return taste != null && NutritionPerUnit(def) >= 0.5f;
            }
        }
    }
}
