using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class PawnCommandService
    {
        public static BridgeResponse SetDrafted(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            SetPawnDraftedBody body = CommandUtil.ReadBody<SetPawnDraftedBody>(request);
            if (!body.Drafted.HasValue)
            {
                throw new CommandException(400, "Missing required field 'drafted'.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolvePlayerControlledPawn(context, pawnId);
            if (pawn.drafter == null)
            {
                throw new CommandException(409, "Pawn cannot be drafted.");
            }

            bool previous = pawn.Drafted;
            bool current = body.Drafted.Value;
            bool changed = previous != current;
            if (changed)
            {
                pawn.drafter.Drafted = current;
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousDrafted", previous),
                Dto.Field("drafted", pawn.Drafted)));
        }

        public static BridgeResponse SetWorkPriority(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            string workTypeDefName = route["workTypeDefName"];
            SetWorkPriorityBody body = CommandUtil.ReadBody<SetWorkPriorityBody>(request);
            if (!body.Priority.HasValue)
            {
                throw new CommandException(400, "Missing required field 'priority'.");
            }
            if (body.Priority.Value < 0 || body.Priority.Value > 4)
            {
                throw new CommandException(400, "Priority must be between 0 and 4.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolvePlayerControlledPawn(context, pawnId);
            if (pawn.workSettings == null)
            {
                throw new CommandException(409, "Pawn has no work settings.");
            }

            WorkTypeDef workType = CommandUtil.ResolveDef<WorkTypeDef>(workTypeDefName, "workTypeDefName", "Work type");
            if (pawn.WorkTypeIsDisabled(workType))
            {
                throw new CommandException(409, "Pawn cannot do this work type.");
            }

            int previous = pawn.workSettings.GetPriority(workType);
            int current = body.Priority.Value;
            bool manualPrioritiesChanged = Find.PlaySettings != null && !Find.PlaySettings.useWorkPriorities;
            if (manualPrioritiesChanged)
            {
                Find.PlaySettings.useWorkPriorities = true;
            }

            bool changed = previous != current || manualPrioritiesChanged;
            if (previous != current)
            {
                pawn.workSettings.SetPriority(workType, current);
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("workType", Dto.Obj(
                    Dto.Field("defName", workType.defName),
                    Dto.Field("label", ReadUtil.DefLabel(workType)))),
                Dto.Field("previousPriority", previous),
                Dto.Field("priority", pawn.workSettings.GetPriority(workType)),
                Dto.Field("manualPrioritiesChanged", manualPrioritiesChanged)));
        }

        [DataContract]
        private sealed class SetPawnDraftedBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "drafted")]
            public bool? Drafted { get; set; }
        }

        [DataContract]
        private sealed class SetWorkPriorityBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "priority")]
            public int? Priority { get; set; }
        }
    }
}
