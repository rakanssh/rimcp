using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class EnvironmentReadService
    {
        public static BridgeResponse GetEnvironment(BridgeRequest request, RouteMatch route)
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

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", SummarizeEnvironment(context.Map)),
                Dto.Field("weather", SerializeWeather(context.Map)),
                Dto.Field("conditions", SerializeGameConditions(context.Map)),
                Dto.Field("rooms", SerializeRooms(context.Map, context.Request)),
                Dto.Field("hazards", SerializeHazards(context.Map))));
        }

        public static object SummarizeEnvironment(Map map)
        {
            if (map == null)
            {
                return null;
            }
            int fires = map.listerThings == null ? 0 : map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            float outdoorTemp = map.mapTemperature == null ? 0f : map.mapTemperature.OutdoorTemp;
            int hotRooms = 0;
            int coldRooms = 0;
            foreach (Room room in Rooms(map))
            {
                float temp = room.Temperature;
                if (temp > 45f)
                {
                    hotRooms++;
                }
                if (temp < 0f)
                {
                    coldRooms++;
                }
            }
            return Dto.Obj(
                Dto.Field("weather", map.weatherManager == null || map.weatherManager.curWeather == null ? null : map.weatherManager.curWeather.defName),
                Dto.Field("outdoorTemperatureC", outdoorTemp),
                Dto.Field("fires", fires),
                Dto.Field("hotRooms", hotRooms),
                Dto.Field("freezingRooms", coldRooms));
        }

        private static object SerializeWeather(Map map)
        {
            WeatherDef weather = map.weatherManager == null ? null : map.weatherManager.curWeather;
            return Dto.Obj(
                Dto.Field("current", weather == null ? null : ReadUtil.Def(weather)),
                Dto.Field("outdoorTemperatureC", map.mapTemperature == null ? 0f : map.mapTemperature.OutdoorTemp),
                Dto.Field("season", GameReadService.SerializeTime(Find.TickManager == null ? 0 : Find.TickManager.TicksGame, map)));
        }

        private static object SerializeGameConditions(Map map)
        {
            if (map.gameConditionManager == null)
            {
                return new object[0];
            }
            return map.gameConditionManager.ActiveConditions
                .OrderBy(condition => condition.def.defName)
                .Select(condition => Dto.Obj(
                    Dto.Field("defName", condition.def.defName),
                    Dto.Field("label", ReadUtil.DefLabel(condition.def)),
                    Dto.Field("durationTicks", condition.Duration),
                    Dto.Field("ticksLeft", condition.TicksLeft)))
                .ToArray();
        }

        private static object SerializeRooms(Map map, ReadRequest request)
        {
            IEnumerable<Room> source = Rooms(map)
                .OrderByDescending(room => System.Math.Abs(room.Temperature - 21f));
            Page<Room> page = new Page<Room>(source, request);
            return Dto.Obj(
                Dto.Field("truncated", page.Truncated),
                Dto.Field("nextCursor", page.NextCursor),
                Dto.Field("items", page.Items.Select(room => SerializeRoom(map, room)).ToArray()));
        }

        private static object SerializeRoom(Map map, Room room)
        {
            RoomRoleDef role = room.Role;
            Region firstRegion = room.FirstRegion;
            IntVec3? firstCell = firstRegion == null ? (IntVec3?)null : firstRegion.AnyCell;
            return Dto.Obj(
                Dto.Field("id", firstCell.HasValue ? ReadUtil.StableSessionId("room", map.uniqueID, firstCell.Value, role == null ? null : role.defName) : null),
                Dto.Field("role", role == null ? null : role.defName),
                Dto.Field("cellCount", room.CellCount),
                Dto.Field("temperatureC", room.Temperature),
                Dto.Field("usesOutdoorTemperature", room.UsesOutdoorTemperature));
        }

        private static object SerializeHazards(Map map)
        {
            List<object> hazards = new List<object>();
            foreach (Thing fire in map.listerThings.ThingsOfDef(ThingDefOf.Fire).Take(50))
            {
                hazards.Add(Dto.Obj(
                    Dto.Field("kind", "fire"),
                    Dto.Field("position", ReadUtil.Cell(fire.Position))));
            }
            if (map.pollutionGrid != null)
            {
                hazards.Add(Dto.Obj(
                    Dto.Field("kind", "pollution"),
                    Dto.Field("percent", map.pollutionGrid.TotalPollutionPercent)));
            }
            return hazards;
        }

        private static IEnumerable<Room> Rooms(Map map)
        {
            if (map == null || map.regionGrid == null)
            {
                yield break;
            }
            foreach (Room room in map.regionGrid.AllRooms)
            {
                if (room != null)
                {
                    yield return room;
                }
            }
        }
    }
}
