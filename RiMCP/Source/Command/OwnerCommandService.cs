using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class OwnerCommandService
    {
        private static readonly MethodInfo Offered = typeof(CompAssignableToPawn).GetMethod("ShouldShowAssignmentGizmo", BindingFlags.Instance | BindingFlags.NonPublic);

        public static BridgeResponse AssignOwner(BridgeRequest request, RouteMatch route)
        {
            AssignOwnerBody body = CommandUtil.ReadBody<AssignOwnerBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            string action = string.IsNullOrWhiteSpace(body.Action) ? "assign" : body.Action.Trim();
            if (action != "assign" && action != "unassign")
            {
                throw new CommandException(400, "Field 'action' must be assign or unassign.");
            }
            if (string.IsNullOrWhiteSpace(body.PawnId))
            {
                throw new CommandException(400, "Missing required field 'pawnId'.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            RequestKey key = RequestKey.For("assign_owner", requestId, context.Map, route["buildingId"], body.PawnId, action);
            return CommandReceipts.Once(context, key, () => Assign(context, route["buildingId"], body.PawnId, action == "assign"));
        }

        private static CommandResult Assign(CommandContext context, string buildingId, string pawnId, bool assign)
        {
            Building building = BuildingReadService.FindBuilding(context.Map, buildingId);
            if (building == null)
            {
                throw new CommandException(404, "Building not found.");
            }
            CompAssignableToPawn owners = building.TryGetComp<CompAssignableToPawn>();
            string label = building.LabelCap.StripTags();
            if (owners == null)
            {
                throw new CommandException(409, label + " has no owners to assign.");
            }
            // The Assign button shows only where owners can be set, such as not on a medical bed.
            if (Offered != null && !(bool)Offered.Invoke(owners, null))
            {
                throw new CommandException(409, label + " does not take owners now" + (building is Building_Bed ? " (a medical or prisoner bed has none)." : "."));
            }
            // An owner away from the map, such as on a caravan, can still be unassigned.
            Pawn pawn = owners.AssignedPawnsForReading.FirstOrDefault(owner => owner.ThingID == pawnId || owner.GetUniqueLoadID() == pawnId)
                ?? PawnReadService.FindPawn(context.Map, pawnId);
            if (pawn == null)
            {
                throw new CommandException(404, "Pawn not found.");
            }
            string name = ReadUtil.PawnName(pawn);
            List<Pawn> before = owners.AssignedPawnsForReading.ToList();
            Building_Bed previousBed = pawn.ownership == null ? null : pawn.ownership.OwnedBed;
            if (assign)
            {
                if (before.Contains(pawn))
                {
                    return Result(false, building, owners, before, name + " already owns " + label + ".", null);
                }
                // The dialog lists only its candidates, greys out those it cannot take with the
                // reason, and refuses those their ideoligion forbids.
                if (!owners.AssigningCandidates.Contains(pawn))
                {
                    throw new CommandException(409, name + " cannot be assigned to " + label + ": it takes " + (building is Building_Bed && !building.def.building.bed_humanlike ? "colony animals." : "free colonists on its map."));
                }
                AcceptanceReport report = owners.CanAssignTo(pawn);
                if (!report.Accepted)
                {
                    throw new CommandException(409, name + " cannot be assigned to " + label + (string.IsNullOrEmpty(report.Reason) ? "." : ": " + report.Reason.StripTags()));
                }
                if (Find.IdeoManager != null && !Find.IdeoManager.classicMode && owners.IdeoligionForbids(pawn))
                {
                    throw new CommandException(409, name + " cannot be assigned to " + label + ": " + "IdeoligionForbids".Translate().ToString().StripTags());
                }
                owners.TryAssignPawn(pawn);
                if (!owners.AssignedPawnsForReading.Contains(pawn))
                {
                    throw new CommandException(409, "The game did not assign " + name + " to " + label + ".");
                }
            }
            else
            {
                // On a bed, unassigning frees whatever bed the pawn owns, so check it is this one.
                if (!before.Contains(pawn))
                {
                    return Result(false, building, owners, before, name + " does not own " + label + ".", null);
                }
                owners.TryUnassignPawn(pawn);
            }
            string moved = assign && previousBed != null && previousBed != building ? ReadUtil.ThingId(previousBed) : null;
            return Result(true, building, owners, before, null, moved);
        }

        private static CommandResult Result(bool changed, Building building, CompAssignableToPawn owners, List<Pawn> before, string alreadySatisfied, string movedFrom)
        {
            List<Pawn> after = owners.AssignedPawnsForReading.ToList();
            return new CommandResult(changed, Dto.Obj(
                Dto.Field("building", SummaryDto.Thing(building)),
                Dto.Field("owners", after.Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray()),
                Dto.Field("slots", owners.MaxAssignedPawnsCount),
                // Assigning to a full bed replaces an owner, as in game.
                Dto.Field("unassigned", changed && before.Except(after).Any() ? before.Except(after).Select(pawn => (object)SummaryDto.Pawn(pawn)).ToArray() : null),
                Dto.Field("movedFromBed", movedFrom),
                Dto.Field("alreadySatisfied", alreadySatisfied)));
        }

        [DataContract]
        private sealed class AssignOwnerBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "pawnId")]
            public string PawnId { get; set; }

            [DataMember(Name = "action")]
            public string Action { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
