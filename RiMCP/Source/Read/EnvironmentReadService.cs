using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class EnvironmentReadService
    {
        public static BridgeResponse GetEnvironment(ReadContext context)
        {
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
            foreach (object room in Rooms(map))
            {
                float temp = Reflect.ReadFloat(room, "Temperature");
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
                    Dto.Field("label", condition.LabelCap),
                    Dto.Field("durationTicks", condition.Duration),
                    Dto.Field("ticksLeft", condition.TicksLeft)))
                .ToArray();
        }

        private static object SerializeRooms(Map map, ReadRequest request)
        {
            IEnumerable<object> source = Rooms(map)
                .OrderByDescending(room => System.Math.Abs(Reflect.ReadFloat(room, "Temperature") - 21f));
            Page<object> page = new Page<object>(source, request);
            return Dto.Obj(
                Dto.Field("truncated", page.Truncated),
                Dto.Field("nextCursor", page.NextCursor),
                Dto.Field("items", page.Items.Select(SerializeRoom).ToArray()));
        }

        private static object SerializeRoom(object room)
        {
            object role = Reflect.Read(room, "Role");
            object firstCell = Reflect.Read(room, "FirstRegion") == null ? null : Reflect.Read(Reflect.Read(room, "FirstRegion"), "AnyCell");
            return Dto.Obj(
                Dto.Field("id", firstCell is IntVec3 ? ReadUtil.StableSessionId("room", 0, (IntVec3)firstCell, Reflect.ReadString(role, "defName")) : null),
                Dto.Field("role", role == null ? null : Reflect.ReadString(role, "defName")),
                Dto.Field("cellCount", Reflect.Read(room, "CellCount")),
                Dto.Field("temperatureC", Reflect.ReadFloat(room, "Temperature")),
                Dto.Field("usesOutdoorTemperature", Reflect.Read(room, "UsesOutdoorTemperature")));
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
            object pollutionGrid = Reflect.Read(map, "pollutionGrid");
            object pollutedCount = Reflect.Read(pollutionGrid, "TotalPollutionPercent");
            if (pollutedCount != null)
            {
                hazards.Add(Dto.Obj(
                    Dto.Field("kind", "pollution"),
                    Dto.Field("percent", pollutedCount)));
            }
            return hazards;
        }

        private static IEnumerable<object> Rooms(Map map)
        {
            object rooms = map == null || map.regionGrid == null ? null : Reflect.Read(map.regionGrid, "allRooms");
            System.Collections.IEnumerable enumerable = rooms as System.Collections.IEnumerable;
            if (enumerable == null)
            {
                yield break;
            }
            foreach (object room in enumerable)
            {
                if (room != null)
                {
                    yield return room;
                }
            }
        }
    }
}
