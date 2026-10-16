using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ThingReadService
    {
        // Items on a map: anything that can be hauled, such as resources, food, weapons, apparel,
        // chunks, corpses, and minified buildings. Things under fog are never reported. Rows are
        // flat and short because a search can return many; each names its storage by id, and the
        // storages on the page are labelled once.
        public static BridgeResponse FindThings(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;

            ThingDef def = query.OptionalDef<ThingDef>("def");
            ThingCategoryDef category = query.OptionalDef<ThingCategoryDef>("category");
            List<ThingCategoryDef> excluded = query.DefList<ThingCategoryDef>("excludeCategory");
            string text = query.Get("query");
            bool? forbidden = query.OptionalBool("forbidden");
            bool? inStorage = query.OptionalBool("inStorage");
            int? x = query.OptionalInt("x");
            int? z = query.OptionalInt("z");
            int? radius = query.OptionalInt("radius");
            if (x.HasValue != z.HasValue || (radius.HasValue && !x.HasValue))
            {
                throw new BridgeException(400, "Give x and z together; radius needs them too.");
            }
            IntVec3? near = x.HasValue ? new IntVec3(x.Value, 0, z.Value) : (IntVec3?)null;

            List<Thing> matches = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(thing => thing.Spawned && !thing.Position.Fogged(map))
                .Where(thing => def == null || thing.def == def)
                .Where(thing => category == null || thing.def.IsWithinCategory(category))
                .Where(thing => !excluded.Any(thing.def.IsWithinCategory))
                .Where(thing => string.IsNullOrWhiteSpace(text) || thing.LabelNoCount.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 || thing.def.defName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(thing => !forbidden.HasValue || thing.IsForbidden(Faction.OfPlayer) == forbidden.Value)
                .Where(thing => !inStorage.HasValue || thing.IsInValidStorage() == inStorage.Value)
                .Where(thing => !radius.HasValue || thing.Position.DistanceTo(near.Value) <= radius.Value)
                .ToList();
            IEnumerable<Thing> ordered = near.HasValue
                ? matches.OrderBy(thing => thing.Position.DistanceToSquared(near.Value)).ThenBy(thing => thing.thingIDNumber)
                : matches.OrderBy(thing => thing.def.defName, StringComparer.Ordinal).ThenBy(thing => thing.thingIDNumber);
            Page<Thing> page = new Page<Thing>(ordered, query);

            Dictionary<string, object> storages = new Dictionary<string, object>();
            object[] rows = page.Items
                .Select(thing => query.IdsOnly ? (object)ReadUtil.ThingId(thing) : SerializeThing(thing, map, near, query.Detail, storages))
                .ToArray();
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", Dto.Obj(
                    Dto.Field("matches", matches.Count),
                    Dto.Field("totalStackCount", matches.Sum(thing => thing.stackCount)))),
                Dto.Field("things", rows),
                Dto.Field("storages", storages.Count == 0 ? null : storages)),
                page.Truncated,
                page.NextCursor);
        }

        private static object SerializeThing(Thing thing, Map map, IntVec3? near, ReadDetail detail, Dictionary<string, object> storages)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(thing)),
                Dto.Field("def", thing.def.defName),
                Dto.Field("label", thing.LabelCapNoCount),
                Dto.Field("x", thing.Position.x),
                Dto.Field("z", thing.Position.z),
                Dto.Field("count", thing.stackCount),
                Dto.Field("forbidden", thing.IsForbidden(Faction.OfPlayer)),
                // Valid storage accepts the item; an item in storage that no longer accepts it gets hauled away.
                Dto.Field("inStorage", thing.IsInValidStorage()),
                Dto.Field("storage", StorageId(thing, map, storages)));
            if (near.HasValue)
            {
                dto["distance"] = Math.Round(thing.Position.DistanceTo(near.Value), 1);
            }
            if (detail != ReadDetail.Summary)
            {
                dto["hitPoints"] = thing.HitPoints;
                dto["maxHitPoints"] = thing.MaxHitPoints;
                dto["quality"] = ReadUtil.QualityLabel(thing);
            }
            return dto;
        }

        // The id of the stockpile or storage building holding the item, recording its label in storages.
        private static string StorageId(Thing thing, Map map, Dictionary<string, object> storages)
        {
            SlotGroup group = thing.GetSlotGroup();
            Zone_Stockpile stockpile = group == null ? null : group.parent as Zone_Stockpile;
            Building building = group == null ? null : group.parent as Building;
            string id = stockpile != null ? ZoneReadService.ZoneId(map, stockpile) : ReadUtil.ThingId(building);
            if (id != null)
            {
                storages[id] = stockpile != null ? stockpile.label : building.LabelCap;
            }
            return id;
        }
    }
}
