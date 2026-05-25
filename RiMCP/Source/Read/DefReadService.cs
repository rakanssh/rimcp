using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    internal static class DefReadService
    {
        public static BridgeResponse SearchDefs(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string kind = context.Request.Get("kind") ?? "thing";
            string query = context.Request.Get("query") ?? "";
            string category = context.Request.Get("category");
            IEnumerable<Def> source = DefsForKind(kind)
                .Where(def => MatchesQuery(def, query))
                .Where(def => MatchesCategory(def, category))
                .OrderBy(def => def.defName);

            Page<Def> page = new Page<Def>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("kind", kind),
                Dto.Field("query", query),
                Dto.Field("category", category),
                Dto.Field("defs", page.Items.Select(def => SerializeDef(def, ReadDetail.Summary)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse GetDef(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string defName = route["defName"];
            string kind = context.Request.Get("kind");
            Def def = string.IsNullOrWhiteSpace(kind)
                ? AllSupportedDefs().FirstOrDefault(item => item.defName == defName)
                : DefsForKind(kind).FirstOrDefault(item => item.defName == defName);
            if (def == null)
            {
                return BridgeResponse.Error(404, "Def not found.");
            }
            return ReadEnvelope.Ok(context, SerializeDef(def, ReadDetail.Full));
        }

        private static IEnumerable<Def> AllSupportedDefs()
        {
            foreach (string kind in SupportedKinds())
            {
                foreach (Def def in DefsForKind(kind))
                {
                    yield return def;
                }
            }
        }

        private static IEnumerable<string> SupportedKinds()
        {
            yield return "thing";
            yield return "recipe";
            yield return "research";
            yield return "workType";
            yield return "stat";
            yield return "terrain";
            yield return "biome";
            yield return "pawnKind";
            yield return "designation";
            yield return "job";
            yield return "weather";
        }

        private static IEnumerable<Def> DefsForKind(string kind)
        {
            switch ((kind ?? "thing").ToLowerInvariant())
            {
                case "thing":
                    return DefDatabase<ThingDef>.AllDefsListForReading.Cast<Def>();
                case "recipe":
                    return DefDatabase<RecipeDef>.AllDefsListForReading.Cast<Def>();
                case "research":
                    return DefDatabase<ResearchProjectDef>.AllDefsListForReading.Cast<Def>();
                case "worktype":
                case "work_type":
                case "work":
                    return DefDatabase<WorkTypeDef>.AllDefsListForReading.Cast<Def>();
                case "stat":
                    return DefDatabase<StatDef>.AllDefsListForReading.Cast<Def>();
                case "terrain":
                    return DefDatabase<TerrainDef>.AllDefsListForReading.Cast<Def>();
                case "biome":
                    return DefDatabase<BiomeDef>.AllDefsListForReading.Cast<Def>();
                case "pawnkind":
                case "pawn_kind":
                    return DefDatabase<PawnKindDef>.AllDefsListForReading.Cast<Def>();
                case "designation":
                    return DefDatabase<DesignationCategoryDef>.AllDefsListForReading.Cast<Def>();
                case "job":
                    return DefDatabase<JobDef>.AllDefsListForReading.Cast<Def>();
                case "weather":
                    return DefDatabase<WeatherDef>.AllDefsListForReading.Cast<Def>();
                default:
                    return Enumerable.Empty<Def>();
            }
        }

        private static bool MatchesQuery(Def def, string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return true;
            }
            string needle = query.ToLowerInvariant();
            return (def.defName ?? "").ToLowerInvariant().Contains(needle) ||
                   (def.label ?? "").ToLowerInvariant().Contains(needle) ||
                   (def.description ?? "").ToLowerInvariant().Contains(needle);
        }

        private static bool MatchesCategory(Def def, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return true;
            }
            string needle = category.ToLowerInvariant();
            ThingDef thing = def as ThingDef;
            if (thing != null)
            {
                if (thing.category.ToString().ToLowerInvariant().Contains(needle))
                {
                    return true;
                }
                return thing.thingCategories != null && thing.thingCategories.Any(cat => cat.defName.ToLowerInvariant().Contains(needle));
            }
            return def.GetType().Name.ToLowerInvariant().Contains(needle);
        }

        private static object SerializeDef(Def def, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("kind", KindForDef(def)),
                Dto.Field("defName", def.defName),
                Dto.Field("label", ReadUtil.DefLabel(def)),
                Dto.Field("description", detail == ReadDetail.Summary ? null : def.description));

            ThingDef thing = def as ThingDef;
            if (thing != null)
            {
                dto["thing"] = SerializeThingDef(thing, detail);
            }
            RecipeDef recipe = def as RecipeDef;
            if (recipe != null)
            {
                dto["recipe"] = SerializeRecipeDef(recipe, detail);
            }
            ResearchProjectDef research = def as ResearchProjectDef;
            if (research != null)
            {
                dto["research"] = ResearchReadService.SerializeProject(research, detail);
            }
            WorkTypeDef workType = def as WorkTypeDef;
            if (workType != null)
            {
                dto["workType"] = Dto.Obj(
                    Dto.Field("naturalPriority", workType.naturalPriority),
                    Dto.Field("workTags", workType.workTags.ToString()));
            }
            StatDef stat = def as StatDef;
            if (stat != null)
            {
                dto["stat"] = Dto.Obj(
                    Dto.Field("category", stat.category == null ? null : stat.category.defName),
                    Dto.Field("toStringStyle", stat.toStringStyle.ToString()));
            }
            TerrainDef terrain = def as TerrainDef;
            if (terrain != null)
            {
                dto["terrain"] = Dto.Obj(
                    Dto.Field("fertility", terrain.fertility),
                    Dto.Field("pathCost", terrain.pathCost),
                    Dto.Field("affordances", terrain.affordances == null ? new object[0] : terrain.affordances.Select(a => a.defName).ToArray()));
            }
            return dto;
        }

        private static object SerializeThingDef(ThingDef def, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("category", def.category.ToString()),
                Dto.Field("thingCategories", def.thingCategories == null ? new object[0] : def.thingCategories.Select(cat => cat.defName).OrderBy(name => name).ToArray()),
                Dto.Field("size", Dto.Obj(Dto.Field("x", def.size.x), Dto.Field("z", def.size.z))),
                Dto.Field("selectable", def.selectable),
                Dto.Field("haulable", def.EverHaulable),
                Dto.Field("nutrition", ResourceReadService.NutritionPerUnit(def)));

            if (detail != ReadDetail.Summary)
            {
                dto["race"] = def.race == null ? null : Dto.Obj(
                    Dto.Field("humanlike", def.race.Humanlike),
                    Dto.Field("animal", def.race.Animal),
                    Dto.Field("fleshType", def.race.FleshType == null ? null : def.race.FleshType.defName));
                dto["plant"] = def.plant == null ? null : Dto.Obj(
                    Dto.Field("sowTags", def.plant.sowTags),
                    Dto.Field("harvestYield", def.plant.harvestYield),
                    Dto.Field("growDays", def.plant.growDays));
                dto["building"] = def.building == null ? null : Dto.Obj(
                    Dto.Field("isEdifice", def.building.isEdifice),
                    Dto.Field("buildingTags", def.building.buildingTags));
                dto["statBases"] = def.statBases == null
                    ? new object[0]
                    : def.statBases.OrderBy(stat => stat.stat.defName).Select(stat => Dto.Obj(
                        Dto.Field("stat", stat.stat.defName),
                        Dto.Field("value", stat.value))).ToArray();
            }
            return dto;
        }

        private static object SerializeRecipeDef(RecipeDef recipe, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("workAmount", recipe.WorkAmountTotal(null)),
                Dto.Field("workSkill", recipe.workSkill == null ? null : recipe.workSkill.defName),
                Dto.Field("workSpeedStat", recipe.workSpeedStat == null ? null : recipe.workSpeedStat.defName));
            if (detail != ReadDetail.Summary)
            {
                dto["products"] = recipe.products == null
                    ? new object[0]
                    : recipe.products.Select(product => Dto.Obj(
                        Dto.Field("defName", product.thingDef == null ? null : product.thingDef.defName),
                        Dto.Field("count", product.count))).ToArray();
                dto["ingredients"] = recipe.ingredients == null
                    ? new object[0]
                    : recipe.ingredients.Select(ingredient => Dto.Obj(
                        Dto.Field("count", ingredient.GetBaseCount()),
                        Dto.Field("filterSummary", ingredient.filter == null ? null : ingredient.filter.Summary))).ToArray();
            }
            return dto;
        }

        private static string KindForDef(Def def)
        {
            if (def is ThingDef) return "thing";
            if (def is RecipeDef) return "recipe";
            if (def is ResearchProjectDef) return "research";
            if (def is WorkTypeDef) return "workType";
            if (def is StatDef) return "stat";
            if (def is TerrainDef) return "terrain";
            if (def is BiomeDef) return "biome";
            if (def is PawnKindDef) return "pawnKind";
            if (def is DesignationCategoryDef) return "designation";
            if (def is JobDef) return "job";
            if (def is WeatherDef) return "weather";
            return def.GetType().Name;
        }
    }
}
