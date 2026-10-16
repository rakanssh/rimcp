using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Read
{
    // What a stockpile or storage building accepts, by top-level thing category (Foods,
    // ResourcesRaw, Weapons, ...): each is accepted fully, partly, or not at all. Only things the
    // storage could ever hold count, so a shelf is not said to refuse corpses. A category is
    // accepted fully only when no special filter that is off could refuse anything in it, as
    // ThingFilter.Allows applies them to each thing: specialFiltersOff lists those turned off in
    // the storage's own settings, such as AllowRotten, and fixedSpecialFiltersOff those the
    // building refuses whatever its settings, such as a shelf refusing large corpses; each is
    // listed when it could apply to something the storage holds. hitPoints and quality are the
    // ranges it takes, when its settings or the building narrow them; they apply to each item on
    // top of the categories.
    internal static class StorageSummary
    {
        public static object Accepts(IStoreSettingsParent parent)
        {
            StorageSettings settings = parent.GetStoreSettings();
            StorageSettings limit = parent.GetParentStoreSettings();
            return settings == null || settings.filter == null ? null : Accepts(settings.filter, limit == null ? null : limit.filter, ThingCategoryDefOf.Root);
        }

        public static object Accepts(ThingFilter filter, ThingFilter limit, ThingCategoryDef root)
        {
            List<SpecialThingFilterDef> ownOff = Off(filter);
            List<SpecialThingFilterDef> fixedOff = limit == null ? new List<SpecialThingFilterDef>() : Off(limit);
            List<SpecialThingFilterDef> off = ownOff.Union(fixedOff).ToList();

            List<string> all = new List<string>();
            List<string> some = new List<string>();
            List<string> none = new List<string>();
            List<ThingDef> holdable = new List<ThingDef>();
            foreach (ThingCategoryDef category in root.childCategories)
            {
                List<ThingDef> defs = category.DescendantThingDefs
                    .Distinct()
                    .Where(def => limit == null || limit.Allows(def))
                    .ToList();
                if (defs.Count == 0)
                {
                    continue;
                }
                holdable.AddRange(defs);
                int accepted = 0, refused = 0;
                foreach (ThingDef def in defs)
                {
                    if (!filter.Allows(def) || off.Any(filterDef => InScope(filterDef, def) && filterDef.Worker.AlwaysMatches(def)))
                    {
                        refused++;
                    }
                    else if (!off.Any(filterDef => CanApply(filterDef, def)))
                    {
                        accepted++;
                    }
                }
                (accepted == defs.Count ? all : refused == defs.Count ? none : some).Add(category.defName);
            }
            return Dto.Obj(
                Dto.Field("all", all.Count == 0 ? null : all.ToArray()),
                Dto.Field("some", some.Count == 0 ? null : some.ToArray()),
                Dto.Field("none", none.Count == 0 ? null : none.ToArray()),
                Dto.Field("specialFiltersOff", Applying(ownOff, holdable)),
                Dto.Field("fixedSpecialFiltersOff", Applying(fixedOff, holdable)),
                Dto.Field("hitPoints", HitPoints(filter, limit)),
                Dto.Field("quality", Quality(filter, limit)));
        }

        // The special filters off in a filter that could refuse some of these things.
        public static string[] OffFor(ThingFilter filter, List<ThingDef> defs)
        {
            return Applying(Off(filter), defs);
        }

        private static List<SpecialThingFilterDef> Off(ThingFilter filter)
        {
            return DefDatabase<SpecialThingFilterDef>.AllDefsListForReading.Where(filterDef => !filter.Allows(filterDef)).ToList();
        }

        private static string[] Applying(List<SpecialThingFilterDef> filters, List<ThingDef> holdable)
        {
            string[] names = filters
                .Where(filterDef => holdable.Any(def => CanApply(filterDef, def)))
                .Select(filterDef => filterDef.defName)
                .ToArray();
            return names.Length == 0 ? null : names;
        }

        // A special filter refuses only things within its parent category, and its worker says
        // which kinds of thing it could ever match, such as only food that can rot for AllowRotten.
        private static bool InScope(SpecialThingFilterDef filterDef, ThingDef def)
        {
            return filterDef.parentCategory != null && def.IsWithinCategory(filterDef.parentCategory);
        }

        private static bool CanApply(SpecialThingFilterDef filterDef, ThingDef def)
        {
            return InScope(filterDef, def) && filterDef.Worker.CanEverMatch(def);
        }

        // Fractions of a thing's maximum hit points; a filter ignores its range when the range
        // cannot be configured (CaresAboutHitPoints).
        private static object HitPoints(ThingFilter filter, ThingFilter limit)
        {
            FloatRange range = HitPointRange(filter);
            if (limit != null)
            {
                FloatRange fixedRange = HitPointRange(limit);
                range = new FloatRange(Math.Max(range.min, fixedRange.min), Math.Min(range.max, fixedRange.max));
            }
            return range == FloatRange.ZeroToOne ? null : Dto.Obj(Dto.Field("min", Math.Round(range.min, 2)), Dto.Field("max", Math.Round(range.max, 2)));
        }

        private static FloatRange HitPointRange(ThingFilter filter)
        {
            return filter.CaresAboutHitPoints ? filter.AllowedHitPointsPercents : FloatRange.ZeroToOne;
        }

        private static object Quality(ThingFilter filter, ThingFilter limit)
        {
            QualityRange range = filter.AllowedQualityLevels;
            if (limit != null)
            {
                QualityRange fixedRange = limit.AllowedQualityLevels;
                range = new QualityRange(range.min > fixedRange.min ? range.min : fixedRange.min, range.max < fixedRange.max ? range.max : fixedRange.max);
            }
            return range == QualityRange.All ? null : Dto.Obj(Dto.Field("min", range.min.ToString()), Dto.Field("max", range.max.ToString()));
        }
    }
}
