using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ThreatReadService
    {
        public static BridgeResponse ListThreats(ReadContext context)
        {
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            List<object> threats = new List<object>();
            foreach (Pawn pawn in context.Map.mapPawns.AllPawnsSpawned.Where(IsThreatPawn).OrderBy(p => p.LabelShortCap))
            {
                threats.Add(SerializeThreatPawn(context.Map, pawn, context.Request.Detail));
            }
            foreach (Thing fire in Fires(context.Map).OrderBy(f => f.Position.x).ThenBy(f => f.Position.z))
            {
                threats.Add(Dto.Obj(
                    Dto.Field("kind", "fire"),
                    Dto.Field("id", ReadUtil.StableSessionId("fire", context.Map.uniqueID, fire.Position, fire.def.defName)),
                    Dto.Field("def", ReadUtil.Def(fire.def)),
                    Dto.Field("position", ReadUtil.Cell(fire.Position))));
            }

            Page<object> page = new Page<object>(threats, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("summary", SummarizeThreats(context.Map)),
                Dto.Field("threats", page.Items.ToArray())), page.Truncated, page.NextCursor);
        }

        public static object SummarizeThreats(Map map)
        {
            if (map == null)
            {
                return null;
            }
            int hostiles = map.mapPawns.AllPawnsSpawned.Count(pawn => pawn.HostileTo(Faction.OfPlayer));
            int manhunters = map.mapPawns.AllPawnsSpawned.Count(IsManhunter);
            int hungryPredators = map.mapPawns.AllPawnsSpawned.Count(IsPredator);
            int fires = Fires(map).Count();
            return Dto.Obj(
                Dto.Field("hostiles", hostiles),
                Dto.Field("manhunters", manhunters),
                Dto.Field("predators", hungryPredators),
                Dto.Field("fires", fires),
                Dto.Field("total", hostiles + manhunters + fires));
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
            object predator = Reflect.Read(pawn.RaceProps, "predator");
            return predator is bool && (bool)predator;
        }

        private static object SerializeThreatPawn(Map map, Pawn pawn, ReadDetail detail)
        {
            return Dto.Obj(
                Dto.Field("kind", "pawn"),
                Dto.Field("threatKind", ThreatKind(pawn)),
                Dto.Field("ids", ReadUtil.ThingIds(pawn)),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("label", pawn.LabelShortCap),
                Dto.Field("def", ReadUtil.Def(pawn.def)),
                Dto.Field("faction", pawn.Faction == null ? null : Dto.Obj(
                    Dto.Field("name", pawn.Faction.Name),
                    Dto.Field("defName", pawn.Faction.def == null ? null : pawn.Faction.def.defName))),
                Dto.Field("position", ReadUtil.Cell(pawn.Position)),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("mentalState", pawn.MentalStateDef == null ? null : pawn.MentalStateDef.defName),
                Dto.Field("combat", detail == ReadDetail.Summary ? null : Dto.Obj(
                    Dto.Field("equipment", pawn.equipment == null ? new object[0] : pawn.equipment.AllEquipmentListForReading.Select(t => Dto.Obj(
                        Dto.Field("def", ReadUtil.Def(t.def)),
                        Dto.Field("label", t.LabelCap))).ToArray()),
                    Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)))));
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
