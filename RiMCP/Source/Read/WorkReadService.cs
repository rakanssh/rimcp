using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class WorkReadService
    {
        public static BridgeResponse ListWork(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            var pawns = PawnReadService.PawnsForFilter(context.Map, "core")
                .OrderBy(role => role.Pawn.LabelShortCap);
            Page<PawnReadService.PawnRole> page = new Page<PawnReadService.PawnRole>(pawns, context.Request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("manualPriorities", Find.PlaySettings == null ? false : Find.PlaySettings.useWorkPriorities),
                Dto.Field("workTypes", DefDatabase<WorkTypeDef>.AllDefsListForReading
                    .OrderBy(def => def.naturalPriority)
                    .Select(def => Dto.Obj(
                        Dto.Field("defName", def.defName),
                        Dto.Field("label", ReadUtil.DefLabel(def)),
                        Dto.Field("naturalPriority", def.naturalPriority)))
                    .ToArray()),
                Dto.Field("pawns", page.Items.Select(role => SerializeWorkPawn(role.Pawn, role.Role, context.Request)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        private static object SerializeWorkPawn(Pawn pawn, string role, ReadRequest request)
        {
            return Dto.Obj(
                Dto.Field("id", ReadUtil.ThingId(pawn)),
                Dto.Field("name", ReadUtil.PawnName(pawn)),
                Dto.Field("role", role),
                Dto.Field("drafted", pawn.Drafted),
                Dto.Field("downed", pawn.Downed),
                Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)),
                Dto.Field("work", request.Detail == ReadDetail.Summary ? CompactWork(pawn) : PawnReadService.SerializePawnWork(pawn)),
                Dto.Field("schedule", request.Detail == ReadDetail.Summary && !request.Wants("schedule") ? null : PawnReadService.SerializeSchedule(pawn)));
        }

        private static object CompactWork(Pawn pawn)
        {
            if (pawn.workSettings == null)
            {
                return new object[0];
            }
            return DefDatabase<WorkTypeDef>.AllDefsListForReading
                .OrderBy(def => pawn.workSettings.GetPriority(def) == 0 ? 99 : pawn.workSettings.GetPriority(def))
                .ThenBy(def => def.naturalPriority)
                .Where(def =>
                {
                    try
                    {
                        return !pawn.WorkTypeIsDisabled(def) && pawn.workSettings.WorkIsActive(def);
                    }
                    catch
                    {
                        return false;
                    }
                })
                .Take(8)
                .Select(def => Dto.Obj(
                    Dto.Field("defName", def.defName),
                    Dto.Field("priority", pawn.workSettings.GetPriority(def))))
                .ToArray();
        }
    }
}
