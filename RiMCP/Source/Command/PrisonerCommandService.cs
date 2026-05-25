using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class PrisonerCommandService
    {
        public static BridgeResponse SetInteraction(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            SetPrisonerInteractionBody body = CommandUtil.ReadBody<SetPrisonerInteractionBody>(request);
            PrisonerInteractionModeDef interactionMode = CommandUtil.ResolveDef<PrisonerInteractionModeDef>(
                body.InteractionModeDefName,
                "interactionModeDefName",
                "Prisoner interaction mode");

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolveSpawnedPawn(context, pawnId);
            CommandUtil.RequireColonyPrisoner(pawn);
            ValidateInteractionMode(pawn, interactionMode);

            PrisonerInteractionModeDef previous = pawn.guest.ExclusiveInteractionMode;
            bool changed = previous != interactionMode;
            if (changed)
            {
                pawn.guest.SetExclusiveInteraction(interactionMode);
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousInteractionMode", CommandUtil.DefSummary(previous)),
                Dto.Field("interactionMode", CommandUtil.DefSummary(pawn.guest.ExclusiveInteractionMode))));
        }

        private static void ValidateInteractionMode(Pawn pawn, PrisonerInteractionModeDef interactionMode)
        {
            if (interactionMode.isNonExclusiveInteraction)
            {
                throw new CommandException(409, "Non-exclusive prisoner interactions are not supported by this command.");
            }
            if (!interactionMode.allowOnWildMan && WildManUtility.IsWildMan(pawn))
            {
                throw new CommandException(409, "Interaction mode is not available for wild people.");
            }
            if (interactionMode.mustBeAwake && !RestUtility.Awake(pawn))
            {
                throw new CommandException(409, "Interaction mode requires the prisoner to be awake.");
            }
            if (interactionMode.hideIfNotRecruitable && !pawn.guest.Recruitable)
            {
                throw new CommandException(409, "Interaction mode is not available for non-recruitable prisoners.");
            }
        }

        [DataContract]
        private sealed class SetPrisonerInteractionBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "interactionModeDefName")]
            public string InteractionModeDefName { get; set; }
        }
    }
}
