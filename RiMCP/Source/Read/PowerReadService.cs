using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class PowerReadService
    {
        public static BridgeResponse GetPower(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            IEnumerable<PowerNet> source = PowerNets(context.Map).OrderByDescending(net => net.CurrentStoredEnergy());
            Page<PowerNet> page = new Page<PowerNet>(source, context.Request);
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
            List<PowerNet> nets = PowerNets(map).ToList();
            float netPower = nets.Sum(NetPowerWatts);
            float stored = nets.Sum(net => net.CurrentStoredEnergy());
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
                Dto.Field("netPowerW", netPower),
                Dto.Field("storedEnergy", stored),
                Dto.Field("poweredComponents", componentCount),
                Dto.Field("poweredOff", poweredOff));
        }

        private static object SerializePowerNet(PowerNet net)
        {
            return Dto.Obj(
                Dto.Field("storedEnergy", net.CurrentStoredEnergy()),
                Dto.Field("netPowerW", NetPowerWatts(net)),
                Dto.Field("powerComps", net.powerComps.Count),
                Dto.Field("batteryComps", net.batteryComps.Count));
        }

        private static object SerializePowerComponents(Map map)
        {
            // Filter before sorting: most colony buildings (walls, furniture, conduits) have no power comps.
            List<object> components = new List<object>();
            IEnumerable<Building> powered = map.listerBuildings.allBuildingsColonist
                .Where(b => b.TryGetComp<CompPowerTrader>() != null || b.TryGetComp<CompPowerBattery>() != null || b.TryGetComp<CompRefuelable>() != null)
                .OrderBy(b => b.def.defName, StringComparer.Ordinal)
                .ThenBy(b => b.thingIDNumber);
            foreach (Building building in powered)
            {
                CompPowerTrader power = building.TryGetComp<CompPowerTrader>();
                CompPowerBattery battery = building.TryGetComp<CompPowerBattery>();
                CompRefuelable fuel = building.TryGetComp<CompRefuelable>();
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

        // CurrentEnergyGainRate is in Wd per tick; report watts to match building powerOutput.
        private static float NetPowerWatts(PowerNet net)
        {
            return (float)Math.Round(net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick, 1);
        }

        private static IEnumerable<PowerNet> PowerNets(Map map)
        {
            if (map == null || map.powerNetManager == null)
            {
                yield break;
            }
            foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
            {
                yield return net;
            }
        }
    }
}
