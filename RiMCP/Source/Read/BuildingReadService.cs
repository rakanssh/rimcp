using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class BuildingReadService
    {
        public static BridgeResponse ListBuildings(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            string category = context.Request.Get("category");
            IEnumerable<Building> source = context.Map.listerBuildings.allBuildingsColonist
                .Where(building => MatchesCategory(building, category))
                .OrderBy(building => building.def.defName)
                .ThenBy(building => building.ThingID);
            Page<Building> page = new Page<Building>(source, context.Request);
            bool includeContents = context.Request.Wants("contents");

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("category", category),
                Dto.Field("buildings", page.Items.Select(building => SerializeBuilding(building, context.Request.Detail, includeContents)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse GetBuilding(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string id = route["id"];
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            Building building = FindBuilding(context.Map, id);
            if (building == null)
            {
                return BridgeResponse.Error(404, "Building not found.");
            }

            return ReadEnvelope.Ok(context, SerializeBuilding(building, ReadDetail.Full, true));
        }

        public static Building FindBuilding(Map map, string id)
        {
            if (map == null || string.IsNullOrWhiteSpace(id) || map.listerBuildings == null)
            {
                return null;
            }
            return map.listerBuildings.allBuildingsColonist
                .FirstOrDefault(building => building.ThingID == id || building.GetUniqueLoadID() == id);
        }

        private static bool MatchesCategory(Building building, string category)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return true;
            }
            string normalized = category.ToLowerInvariant();
            if (normalized == "production")
            {
                return building is IBillGiver;
            }
            if (normalized == "power")
            {
                return building.TryGetComp<CompPowerTrader>() != null ||
                       building.TryGetComp<CompPowerBattery>() != null ||
                       building.TryGetComp<CompRefuelable>() != null;
            }
            if (normalized == "bed")
            {
                return building is Building_Bed;
            }
            if (normalized == "storage")
            {
                return building is ISlotGroupParent;
            }
            return building.def.defName.ToLowerInvariant().Contains(normalized);
        }

        private static object SerializeBuilding(Building building, ReadDetail detail, bool includeContents)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("ids", ReadUtil.ThingIds(building)),
                Dto.Field("def", ReadUtil.Def(building.def)),
                Dto.Field("label", building.LabelCap),
                Dto.Field("position", ReadUtil.Cell(building.Position)),
                Dto.Field("hitPoints", building.HitPoints),
                Dto.Field("maxHitPoints", building.MaxHitPoints),
                Dto.Field("faction", building.Faction == null ? null : building.Faction.Name),
                Dto.Field("stuff", building.Stuff == null ? null : ReadUtil.Def(building.Stuff)));

            if (detail != ReadDetail.Summary)
            {
                CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                CompPowerBattery battery = building.TryGetComp<CompPowerBattery>();
                CompRefuelable fuel = building.TryGetComp<CompRefuelable>();
                dto["size"] = Dto.Obj(Dto.Field("x", building.def.size.x), Dto.Field("z", building.def.size.z));
                dto["passability"] = building.def.passability.ToString();
                dto["power"] = power == null ? null : Dto.Obj(
                    Dto.Field("powerOn", power.PowerOn),
                    Dto.Field("powerOutput", power.PowerOutput));
                dto["battery"] = battery == null ? null : Dto.Obj(
                    Dto.Field("storedEnergy", battery.StoredEnergy));
                dto["fuel"] = fuel == null ? null : Dto.Obj(
                    Dto.Field("fuel", fuel.Fuel),
                    Dto.Field("targetFuelLevel", fuel.TargetFuelLevel));
                dto["billGiver"] = building is IBillGiver;
            }
            if (includeContents)
            {
                dto["contents"] = SerializeContents(building);
            }
            return dto;
        }

        private static object SerializeContents(Building building)
        {
            ISlotGroupParent storage = building as ISlotGroupParent;
            if (storage == null)
            {
                return Dto.Obj(
                    Dto.Field("supported", false),
                    Dto.Field("items", new object[0]));
            }

            SlotGroup slotGroup = storage.GetSlotGroup();
            object[] items = slotGroup == null
                ? new object[0]
                : slotGroup.HeldThings
                    .Where(thing => thing != null)
                    .OrderBy(thing => thing.def == null ? "" : thing.def.defName)
                    .ThenBy(thing => thing.ThingID)
                    .Select(SerializeContainedItem)
                    .ToArray();

            return Dto.Obj(
                Dto.Field("supported", true),
                Dto.Field("items", items));
        }

        private static object SerializeContainedItem(Thing thing)
        {
            return Dto.Obj(
                Dto.Field("ids", ReadUtil.ThingIds(thing)),
                Dto.Field("def", ReadUtil.Def(thing.def)),
                Dto.Field("label", thing.LabelCap),
                Dto.Field("stackCount", thing.stackCount),
                Dto.Field("hitPoints", thing.HitPoints),
                Dto.Field("forbidden", thing.IsForbidden(Faction.OfPlayer)),
                Dto.Field("position", ReadUtil.Cell(thing.Position)),
                Dto.Field("quality", ReadUtil.QualityLabel(thing)));
        }
    }
}
