using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;
using Verse.AI.Group;

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
                AcceptanceReport allowed = CanToggleDraft(pawn);
                if (!allowed.Accepted)
                {
                    throw new CommandException(409, string.IsNullOrWhiteSpace(allowed.Reason) ? "Pawn cannot be drafted or undrafted right now." : allowed.Reason.StripTags());
                }
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

            if (!pawn.workSettings.EverWork)
            {
                throw new CommandException(409, "Pawn does not do work.");
            }

            // In simple mode the work tab shows every non-zero priority as 3, so 0 and 3 can be
            // applied without switching the colony to manual priorities.
            int previous = pawn.workSettings.GetPriority(workType);
            int current = body.Priority.Value;
            bool manualPrioritiesChanged = Find.PlaySettings != null && !Find.PlaySettings.useWorkPriorities &&
                                           pawn.RaceProps.Humanlike && current != 0 && current != 3;
            if (manualPrioritiesChanged)
            {
                EnableManualPriorities();
            }

            // Re-read after a mode switch: stored priorities may differ from the simple-mode view.
            int stored = pawn.workSettings.GetPriority(workType);
            bool changed = previous != current || manualPrioritiesChanged;
            if (stored != current)
            {
                pawn.workSettings.SetPriority(workType, current);
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("workType", SummaryDto.Def(workType)),
                Dto.Field("previousPriority", previous),
                Dto.Field("priority", pawn.workSettings.GetPriority(workType)),
                Dto.Field("manualPrioritiesChanged", manualPrioritiesChanged)));
        }

        // Mirrors the draft toggle in Pawn.GetGizmos and Pawn_DraftController.GetGizmos.
        private static AcceptanceReport CanToggleDraft(Pawn pawn)
        {
            if (!pawn.drafter.ShowDraftGizmo)
            {
                return "Pawn has no draft control.";
            }
            if (pawn.Downed)
            {
                return "Pawn is downed.";
            }
            if (pawn.Deathresting)
            {
                return "Pawn is deathresting.";
            }
            if (ModsConfig.BiotechActive && pawn.IsColonyMech)
            {
                AcceptanceReport mechReport = MechanitorUtility.CanDraftMech(pawn);
                if (!mechReport.Accepted)
                {
                    return mechReport;
                }
            }
            Lord lord = pawn.GetLord();
            if (lord != null)
            {
                AcceptanceReport lordReport = lord.AllowsDrafting(pawn);
                if (!lordReport.Accepted)
                {
                    return lordReport;
                }
            }
            return AcceptanceReport.WasAccepted;
        }

        // Mirrors MainTabWindow_Work: every player pawn caches its work giver order by priority mode.
        private static void EnableManualPriorities()
        {
            Find.PlaySettings.useWorkPriorities = true;
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_Alive)
            {
                if (pawn.Faction == Faction.OfPlayer && pawn.workSettings != null)
                {
                    pawn.workSettings.Notify_UseWorkPrioritiesChanged();
                }
            }
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
