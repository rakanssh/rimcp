using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ZoneReadService
    {
        public static BridgeResponse ListZones(ReadContext context)
        {
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<ZoneRecord> source = ZoneRecords(context.Map)
                .OrderBy(record => record.Kind)
                .ThenBy(record => record.Label);
            Page<ZoneRecord> page = new Page<ZoneRecord>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("zones", page.Items.Select(record => SerializeZone(record, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse GetZone(ReadContext context, string id)
        {
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            ZoneRecord record = ZoneRecords(context.Map).FirstOrDefault(zone => zone.Id == id);
            if (record == null)
            {
                return BridgeResponse.Error(404, "Zone not found.");
            }
            return ReadEnvelope.Ok(context, SerializeZone(record, ReadDetail.Full));
        }

        private static IEnumerable<ZoneRecord> ZoneRecords(Map map)
        {
            foreach (Zone zone in map.zoneManager.AllZones)
            {
                yield return ZoneRecord.ForZone(map, zone);
            }

            foreach (Area area in map.areaManager.AllAreas)
            {
                ZoneRecord record = ZoneRecord.ForArea(map, area);
                if (record != null)
                {
                    yield return record;
                }
            }
        }

        private static object SerializeZone(ZoneRecord record, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", record.Id),
                Dto.Field("kind", record.Kind),
                Dto.Field("label", record.Label),
                Dto.Field("cellCount", record.CellCount),
                Dto.Field("representativeCell", record.RepresentativeCell.HasValue ? ReadUtil.Cell(record.RepresentativeCell.Value) : null));

            if (record.Zone is Zone_Stockpile)
            {
                Zone_Stockpile stockpile = (Zone_Stockpile)record.Zone;
                dto["stockpile"] = Dto.Obj(
                    Dto.Field("priority", stockpile.settings == null ? null : stockpile.settings.Priority.ToString()),
                    Dto.Field("filterSummary", stockpile.settings == null || stockpile.settings.filter == null ? null : stockpile.settings.filter.Summary));
            }
            if (record.Zone is Zone_Growing)
            {
                Zone_Growing growing = (Zone_Growing)record.Zone;
                ThingDef plant = growing.GetPlantDefToGrow();
                dto["growing"] = Dto.Obj(
                    Dto.Field("plant", ReadUtil.Def(plant)),
                    Dto.Field("allowSow", growing.allowSow),
                    Dto.Field("allowCut", growing.allowCut));
            }
            if (record.Area != null)
            {
                dto["area"] = Dto.Obj(
                    Dto.Field("assignable", record.Area.AssignableAsAllowed()),
                    Dto.Field("color", record.Area.Color));
            }

            if (detail == ReadDetail.Full)
            {
                dto["cells"] = record.Cells.Take(200).Select(ReadUtil.Cell).ToArray();
                dto["cellsTruncated"] = record.Cells.Count > 200;
            }
            return dto;
        }

        private sealed class ZoneRecord
        {
            public string Id;
            public string Kind;
            public string Label;
            public int CellCount;
            public IntVec3? RepresentativeCell;
            public List<IntVec3> Cells;
            public Zone Zone;
            public Area Area;

            public static ZoneRecord ForZone(Map map, Zone zone)
            {
                List<IntVec3> cells = zone.Cells.ToList();
                IntVec3 representative = cells.Count == 0 ? IntVec3.Invalid : cells[0];
                string kind = zone is Zone_Stockpile ? "stockpile" : zone is Zone_Growing ? "growing" : "zone";
                return new ZoneRecord
                {
                    Id = "zone:" + map.uniqueID + ":" + zone.ID,
                    Kind = kind,
                    Label = zone.label,
                    CellCount = cells.Count,
                    RepresentativeCell = representative.IsValid ? (IntVec3?)representative : null,
                    Cells = cells,
                    Zone = zone
                };
            }

            public static ZoneRecord ForArea(Map map, Area area)
            {
                if (area == null)
                {
                    return null;
                }
                List<IntVec3> cells = area.ActiveCells.ToList();
                IntVec3 representative = cells.Count == 0 ? IntVec3.Invalid : cells[0];
                return new ZoneRecord
                {
                    Id = "area:" + map.uniqueID + ":" + area.ID,
                    Kind = "area",
                    Label = area.Label,
                    CellCount = cells.Count == 0 ? area.TrueCount : cells.Count,
                    RepresentativeCell = representative.IsValid ? (IntVec3?)representative : null,
                    Cells = cells,
                    Area = area
                };
            }
        }
    }
}
