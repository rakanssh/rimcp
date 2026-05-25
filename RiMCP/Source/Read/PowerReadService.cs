using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class PowerReadService
    {
        public static BridgeResponse GetPower(ReadContext context)
        {
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<object> source = PowerNets(context.Map).OrderByDescending(net => ReadFloatOrInvoke(net, "CurrentStoredEnergy"));
            Page<object> page = new Page<object>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", SummarizePower(context.Map)),
                Dto.Field("grids", page.Items.Select(SerializePowerNet).ToArray()),
                Dto.Field("components", context.Request.Detail == ReadDetail.Summary ? null : SerializePowerComponents(context.Map))),
                page.Truncated,
                page.NextCursor);
        }

        public static object SummarizePower(Map map)
        {
            if (map == null)
            {
                return null;
            }
            List<object> nets = PowerNets(map).ToList();
            float generationRate = nets.Sum(net => ReadFloatOrInvoke(net, "CurrentEnergyGainRate"));
            float stored = nets.Sum(net => ReadFloatOrInvoke(net, "CurrentStoredEnergy"));
            int componentCount = 0;
            int poweredOff = 0;
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                if (power == null)
                {
                    continue;
                }
                componentCount++;
                if (!power.PowerOn)
                {
                    poweredOff++;
                }
            }
            return Dto.Obj(
                Dto.Field("gridCount", nets.Count),
                Dto.Field("netEnergyGainRate", generationRate),
                Dto.Field("storedEnergy", stored),
                Dto.Field("poweredComponents", componentCount),
                Dto.Field("poweredOff", poweredOff));
        }

        private static object SerializePowerNet(object net)
        {
            return Dto.Obj(
                Dto.Field("storedEnergy", ReadFloatOrInvoke(net, "CurrentStoredEnergy")),
                Dto.Field("energyGainRate", ReadFloatOrInvoke(net, "CurrentEnergyGainRate")),
                Dto.Field("powerComps", Reflect.ReadEnumerable(net, "powerComps").Count()),
                Dto.Field("batteryComps", Reflect.ReadEnumerable(net, "batteryComps").Count()));
        }

        private static object SerializePowerComponents(Map map)
        {
            List<object> components = new List<object>();
            foreach (Building building in map.listerBuildings.allBuildingsColonist.OrderBy(b => b.def.defName))
            {
                CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                CompPowerBattery battery = building.TryGetComp<CompPowerBattery>();
                CompRefuelable fuel = building.TryGetComp<CompRefuelable>();
                if (power == null && battery == null && fuel == null)
                {
                    continue;
                }
                components.Add(Dto.Obj(
                    Dto.Field("ids", ReadUtil.ThingIds(building)),
                    Dto.Field("def", ReadUtil.Def(building.def)),
                    Dto.Field("position", ReadUtil.Cell(building.Position)),
                    Dto.Field("powerOn", power == null ? null : (object)power.PowerOn),
                    Dto.Field("powerOutput", power == null ? null : (object)power.PowerOutput),
                    Dto.Field("storedEnergy", battery == null ? null : (object)battery.StoredEnergy),
                    Dto.Field("fuel", fuel == null ? null : (object)fuel.Fuel)));
            }
            return components;
        }

        private static IEnumerable<object> PowerNets(Map map)
        {
            object manager = map == null ? null : map.powerNetManager;
            foreach (object net in Reflect.ReadEnumerable(manager, "AllNetsListForReading"))
            {
                yield return net;
            }
        }

        private static float ReadFloatOrInvoke(object instance, string name)
        {
            object value = Reflect.Invoke(instance, name);
            if (value == null)
            {
                value = Reflect.Read(instance, name);
            }
            if (value == null)
            {
                return 0f;
            }
            try
            {
                return System.Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return 0f;
            }
        }
    }
}
