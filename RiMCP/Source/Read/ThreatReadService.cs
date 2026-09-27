using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ThreatReadService
    {
        public static BridgeResponse ListThreats(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);

            IEnumerable<Thing> threats = context.Map.mapPawns.AllPawnsSpawned
                .Where(IsThreatPawn)
                .OrderBy(pawn => pawn.LabelShortCap)
                .Cast<Thing>()
                .Concat(Fires(context.Map).OrderBy(fire => fire.Position.x).ThenBy(fire => fire.Position.z));

            Page<Thing> page = new Page<Thing>(threats, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", SummarizeThreats(context.Map)),
                Dto.Field("threats", page.Items.Select(thing => thing is Pawn
                    ? SerializeThreatPawn(context.Map, (Pawn)thing, context.Request.Detail)
                    : SerializeFire(context.Map, thing)).ToArray())), page.Truncated, page.NextCursor);
        }

        public static object SummarizeThreats(Map map)
        {
            if (map == null)
            {
                return null;
            }
            int hostiles = 0;
            int manhunters = 0;
            int hungryPredators = 0;
            int threatPawns = 0;
            foreach (Pawn pawn in map.mapPawns.AllPawnsSpawned)
            {
                bool hostile = pawn.HostileTo(Faction.OfPlayer);
                bool manhunter = IsManhunter(pawn);
                bool predator = IsPredator(pawn);
                if (hostile)
                {
                    hostiles++;
                }
                if (manhunter)
                {
                    manhunters++;
                }
                if (predator)
                {
                    hungryPredators++;
                }
                if (hostile || manhunter || predator)
                {
                    threatPawns++;
                }
            }
            int fires = Fires(map).Count();
            return Dto.Obj(
                Dto.Field("hostiles", hostiles),
                Dto.Field("manhunters", manhunters),
                Dto.Field("predators", hungryPredators),
                Dto.Field("fires", fires),
                Dto.Field("total", threatPawns + fires));
        }

        private static bool IsThreatPawn(Pawn pawn)
        {
            return pawn != null && (pawn.HostileTo(Faction.OfPlayer) || IsManhunter(pawn) || IsPredator(pawn));
        }

        private static bool IsManhunter(Pawn pawn)
        {
            return pawn != null && pawn.MentalStateDef != null && pawn.MentalStateDef.defName.IndexOf("Manhunter", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsPredator(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null || !pawn.RaceProps.Animal || pawn.Faction == Faction.OfPlayer)
            {
                return false;
            }
            return pawn.RaceProps.predator;
        }

        private static object SerializeThreatPawn(Map map, Pawn pawn, ReadDetail detail)
        {
            Dictionary<string, object> dto = SummaryDto.Pawn(pawn);
            dto["kind"] = "pawn";
            dto["threatKind"] = ThreatKind(pawn);
            dto["def"] = ReadUtil.Def(pawn.def);
            dto["faction"] = SummaryDto.FactionIdentity(pawn.Faction);
            dto["position"] = ReadUtil.Cell(pawn.Position);
            dto["downed"] = pawn.Downed;
            dto["mentalState"] = pawn.MentalStateDef == null ? null : pawn.MentalStateDef.defName;
            dto["combat"] = detail == ReadDetail.Summary ? null : Dto.Obj(
                Dto.Field("equipment", pawn.equipment == null ? new object[0] : pawn.equipment.AllEquipmentListForReading.Select(t => Dto.Obj(
                    Dto.Field("def", ReadUtil.Def(t.def)),
                    Dto.Field("label", t.LabelCap))).ToArray()),
                Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)));
            return dto;
        }

        private static object SerializeFire(Map map, Thing fire)
        {
            return Dto.Obj(
                Dto.Field("kind", "fire"),
                Dto.Field("id", ReadUtil.StableSessionId("fire", map.uniqueID, fire.Position, fire.def.defName)),
                Dto.Field("def", ReadUtil.Def(fire.def)),
                Dto.Field("position", ReadUtil.Cell(fire.Position)));
        }

        private static string ThreatKind(Pawn pawn)
        {
            if (IsManhunter(pawn))
            {
                return "manhunter";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
            {
                return "animal";
            }
            if (pawn.RaceProps != null && pawn.RaceProps.Humanlike)
            {
                return "humanlike";
            }
            return "other";
        }

        private static IEnumerable<Thing> Fires(Map map)
        {
            if (map == null || map.listerThings == null)
            {
                return Enumerable.Empty<Thing>();
            }
            return map.listerThings.ThingsOfDef(ThingDefOf.Fire);
        }
    }
}
