using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RiMCP.Bridge;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    internal static class DefReadService
    {
        private static readonly object DefKindLock = new object();
        private static Dictionary<string, DefKind> defKindsByKey;
        private static List<DefKind> defKinds;

        public static BridgeResponse SearchDefs(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string kind = string.IsNullOrWhiteSpace(context.Request.Get("kind")) ? "thing" : context.Request.Get("kind");
            string query = context.Request.Get("query") ?? "";
            string category = context.Request.Get("category");

            IEnumerable<Def> source;
            if (IsAllKind(kind))
            {
                source = AllSupportedDefs();
                kind = "all";
            }
            else
            {
                DefKind resolved;
                if (!TryResolveKind(kind, out resolved))
                {
                    return UnknownKind(kind);
                }
                source = DefsForKind(resolved);
                kind = resolved.Kind;
            }

            source = source
                .Where(def => MatchesQuery(def, query))
                .Where(def => MatchesCategory(def, category))
                .OrderBy(def => KindForDef(def))
                .ThenBy(def => def.defName);

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

            IEnumerable<Def> source;
            if (string.IsNullOrWhiteSpace(kind) || IsAllKind(kind))
            {
                source = AllSupportedDefs();
            }
            else
            {
                DefKind resolved;
                if (!TryResolveKind(kind, out resolved))
                {
                    return UnknownKind(kind);
                }
                source = DefsForKind(resolved);
            }

            Def def = source.FirstOrDefault(item => item.defName == defName);
            if (def == null)
            {
                return BridgeResponse.Error(404, "Def not found.");
            }
            return ReadEnvelope.Ok(context, SerializeDef(def, ReadDetail.Full));
        }

        private static IEnumerable<Def> AllSupportedDefs()
        {
            foreach (DefKind kind in DefKinds())
            {
                foreach (Def def in DefsForKind(kind))
                {
                    yield return def;
                }
            }
        }

        private static bool TryResolveKind(string kind, out DefKind resolved)
        {
            return DefKindsByKey().TryGetValue(NormalizeKind(kind), out resolved);
        }

        private static IEnumerable<Def> DefsForKind(DefKind kind)
        {
            Type databaseType = typeof(DefDatabase<>).MakeGenericType(kind.Type);
            PropertyInfo property = databaseType.GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static);
            IEnumerable defs = property == null ? null : property.GetValue(null, null) as IEnumerable;
            if (defs == null)
            {
                yield break;
            }

            foreach (object item in defs)
            {
                Def def = item as Def;
                if (def != null)
                {
                    yield return def;
                }
            }
        }

        private static Dictionary<string, DefKind> DefKindsByKey()
        {
            EnsureDefKinds();
            return defKindsByKey;
        }

        private static List<DefKind> DefKinds()
        {
            EnsureDefKinds();
            return defKinds;
        }

        private static void EnsureDefKinds()
        {
            if (defKindsByKey != null)
            {
                return;
            }

            lock (DefKindLock)
            {
                if (defKindsByKey != null)
                {
                    return;
                }

                Dictionary<Type, DefKind> byType = new Dictionary<Type, DefKind>();
                Dictionary<string, DefKind> byKey = new Dictionary<string, DefKind>();
                foreach (Type type in DiscoverDefTypes())
                {
                    DefKind kind = new DefKind(type, KindForType(type), LegacyKindSortIndex(type));
                    byType[type] = kind;
                    AddKindKey(byKey, kind.Kind, kind);
                    AddKindKey(byKey, type.Name, kind);
                    AddKindKey(byKey, TrimDefSuffix(type.Name), kind);
                }

                AddAlias(byKey, byType, typeof(ResearchProjectDef), "research");
                AddAlias(byKey, byType, typeof(WorkTypeDef), "work");
                AddAlias(byKey, byType, typeof(WorkTypeDef), "work_type");
                AddAlias(byKey, byType, typeof(PawnKindDef), "pawn_kind");
                AddAlias(byKey, byType, typeof(DesignationCategoryDef), "designation");

                defKinds = byType.Values
                    .OrderBy(kind => kind.SortIndex)
                    .ThenBy(kind => kind.Kind)
                    .ToList();
                defKindsByKey = byKey;
            }
        }

        private static IEnumerable<Type> DiscoverDefTypes()
        {
            HashSet<Type> seen = new HashSet<Type>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || type.IsAbstract || !typeof(Def).IsAssignableFrom(type) || type == typeof(Def))
                    {
                        continue;
                    }
                    if (seen.Add(type))
                    {
                        yield return type;
                    }
                }
            }
        }

        private static void AddAlias(Dictionary<string, DefKind> byKey, Dictionary<Type, DefKind> byType, Type type, string alias)
        {
            DefKind kind;
            if (byType.TryGetValue(type, out kind))
            {
                AddKindKey(byKey, alias, kind);
            }
        }

        private static void AddKindKey(Dictionary<string, DefKind> byKey, string key, DefKind kind)
        {
            string normalized = NormalizeKind(key);
            if (normalized.Length > 0 && !byKey.ContainsKey(normalized))
            {
                byKey[normalized] = kind;
            }
        }

        private static string NormalizeKind(string kind)
        {
            string text = TrimDefSuffix(kind ?? "");
            string normalized = "";
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c != '_' && c != '-' && !char.IsWhiteSpace(c))
                {
                    normalized += char.ToLowerInvariant(c);
                }
            }
            return normalized;
        }

        private static string TrimDefSuffix(string text)
        {
            return text != null && text.EndsWith("Def", StringComparison.OrdinalIgnoreCase)
                ? text.Substring(0, text.Length - 3)
                : (text ?? "");
        }

        private static bool IsAllKind(string kind)
        {
            return string.Equals(kind, "all", StringComparison.OrdinalIgnoreCase);
        }

        private static BridgeResponse UnknownKind(string kind)
        {
            string examples = string.Join(", ", DefKinds().Take(12).Select(item => item.Kind).ToArray());
            return BridgeResponse.Error(400, "Unknown def kind '" + kind + "'. Use any loaded Def type name without the Def suffix, or one of: all, " + examples + ".");
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
                Dto.Field("typeName", def.GetType().FullName),
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
            return KindForType(def.GetType());
        }

        private static string KindForType(Type type)
        {
            if (type == typeof(ThingDef)) return "thing";
            if (type == typeof(RecipeDef)) return "recipe";
            if (type == typeof(ResearchProjectDef)) return "research";
            if (type == typeof(WorkTypeDef)) return "workType";
            if (type == typeof(StatDef)) return "stat";
            if (type == typeof(TerrainDef)) return "terrain";
            if (type == typeof(BiomeDef)) return "biome";
            if (type == typeof(PawnKindDef)) return "pawnKind";
            if (type == typeof(DesignationCategoryDef)) return "designation";
            if (type == typeof(JobDef)) return "job";
            if (type == typeof(WeatherDef)) return "weather";

            string name = TrimDefSuffix(type.Name);
            return name.Length == 0 ? type.Name : char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private static int LegacyKindSortIndex(Type type)
        {
            if (type == typeof(ThingDef)) return 0;
            if (type == typeof(RecipeDef)) return 1;
            if (type == typeof(ResearchProjectDef)) return 2;
            if (type == typeof(WorkTypeDef)) return 3;
            if (type == typeof(StatDef)) return 4;
            if (type == typeof(TerrainDef)) return 5;
            if (type == typeof(BiomeDef)) return 6;
            if (type == typeof(PawnKindDef)) return 7;
            if (type == typeof(DesignationCategoryDef)) return 8;
            if (type == typeof(JobDef)) return 9;
            if (type == typeof(WeatherDef)) return 10;
            return 1000;
        }

        private sealed class DefKind
        {
            public readonly Type Type;
            public readonly string Kind;
            public readonly int SortIndex;

            public DefKind(Type type, string kind, int sortIndex)
            {
                Type = type;
                Kind = kind;
                SortIndex = sortIndex;
            }
        }
    }
}
