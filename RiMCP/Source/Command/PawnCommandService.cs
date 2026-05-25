using System.Collections.Generic;
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

        public static BridgeResponse SetSchedule(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            SetPawnScheduleBody body = CommandUtil.ReadBody<SetPawnScheduleBody>(request);
            if (body.Assignments == null || body.Assignments.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'assignments'.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolvePlayerControlledPawn(context, pawnId);
            if (pawn.timetable == null)
            {
                throw new CommandException(409, "Pawn has no timetable.");
            }

            object previousSchedule = PawnReadService.SerializeSchedule(pawn);
            ScheduleAssignment[] assignments = ParseScheduleAssignments(body.Assignments);
            List<object> changedHours = new List<object>();
            foreach (ScheduleAssignment assignment in assignments)
            {
                for (int hour = assignment.StartHour; hour < assignment.EndHour; hour++)
                {
                    TimeAssignmentDef previous = pawn.timetable.GetAssignment(hour);
                    if (previous == assignment.Assignment)
                    {
                        continue;
                    }

                    changedHours.Add(Dto.Obj(
                        Dto.Field("hour", hour),
                        Dto.Field("previousAssignment", previous == null ? null : previous.defName),
                        Dto.Field("assignment", assignment.Assignment.defName)));
                    pawn.timetable.SetAssignment(hour, assignment.Assignment);
                }
            }

            return CommandEnvelope.Ok(context, changedHours.Count > 0, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousSchedule", previousSchedule),
                Dto.Field("schedule", PawnReadService.SerializeSchedule(pawn)),
                Dto.Field("changedHours", changedHours.ToArray())));
        }

        private static ScheduleAssignment[] ParseScheduleAssignments(SetPawnScheduleAssignmentBody[] bodies)
        {
            bool[] covered = new bool[24];
            List<ScheduleAssignment> assignments = new List<ScheduleAssignment>();
            for (int i = 0; i < bodies.Length; i++)
            {
                SetPawnScheduleAssignmentBody body = bodies[i];
                if (body == null)
                {
                    throw new CommandException(400, "Schedule assignment at index " + i + " is null.");
                }
                if (!body.StartHour.HasValue)
                {
                    throw new CommandException(400, "Missing required field 'assignments[" + i + "].startHour'.");
                }
                if (!body.EndHour.HasValue)
                {
                    throw new CommandException(400, "Missing required field 'assignments[" + i + "].endHour'.");
                }

                int start = body.StartHour.Value;
                int end = body.EndHour.Value;
                if (start < 0 || start >= 24 || end <= 0 || end > 24 || start >= end)
                {
                    throw new CommandException(400, "Schedule assignment at index " + i + " must use a half-open hour range within 0..24.");
                }
                for (int hour = start; hour < end; hour++)
                {
                    if (covered[hour])
                    {
                        throw new CommandException(400, "Schedule assignments overlap at hour " + hour + ".");
                    }
                    covered[hour] = true;
                }

                TimeAssignmentDef assignment = CommandUtil.ResolveDef<TimeAssignmentDef>(body.AssignmentDefName, "assignments[" + i + "].assignmentDefName", "Time assignment");
                assignments.Add(new ScheduleAssignment(start, end, assignment));
            }

            return assignments.ToArray();
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

        [DataContract]
        private sealed class SetPawnScheduleBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "assignments")]
            public SetPawnScheduleAssignmentBody[] Assignments { get; set; }
        }

        [DataContract]
        private sealed class SetPawnScheduleAssignmentBody
        {
            [DataMember(Name = "startHour")]
            public int? StartHour { get; set; }

            [DataMember(Name = "endHour")]
            public int? EndHour { get; set; }

            [DataMember(Name = "assignmentDefName")]
            public string AssignmentDefName { get; set; }
        }

        private sealed class ScheduleAssignment
        {
            public readonly int StartHour;
            public readonly int EndHour;
            public readonly TimeAssignmentDef Assignment;

            public ScheduleAssignment(int startHour, int endHour, TimeAssignmentDef assignment)
            {
                StartHour = startHour;
                EndHour = endHour;
                Assignment = assignment;
            }
        }
    }
}
