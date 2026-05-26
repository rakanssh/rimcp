using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class PawnAssignmentCommandService
    {
        public static BridgeResponse SetAssignment(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            string assignmentKind = NormalizeAssignmentKind(route["assignmentKind"]);
            SetPawnAssignmentBody body = CommandUtil.ReadBody<SetPawnAssignmentBody>(request);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            switch (assignmentKind)
            {
                case "schedule":
                    return SetSchedule(context, pawnId, body);
                case "policy":
                    return SetPolicy(context, pawnId, body);
                case "medicalcare":
                    return SetMedicalCare(context, pawnId, body);
                case "selftend":
                    return SetSelfTend(context, pawnId, body);
                case "hostilityresponse":
                    return SetHostilityResponse(context, pawnId, body);
                case "allowedarea":
                    return SetAllowedArea(context, pawnId, body);
                case "carrymedicine":
                    return SetCarryMedicine(context, pawnId, body);
                case "prisonerinteraction":
                    return SetPrisonerInteraction(context, pawnId, body);
                default:
                    throw new CommandException(400, "Unknown assignment kind.");
            }
        }

        private static BridgeResponse SetSchedule(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            if (body.Assignments == null || body.Assignments.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'assignments'.");
            }

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
                Dto.Field("assignmentKind", "schedule"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousSchedule", previousSchedule),
                Dto.Field("schedule", PawnReadService.SerializeSchedule(pawn)),
                Dto.Field("changedHours", changedHours.ToArray())));
        }

        private static BridgeResponse SetPolicy(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            string policyKind = NormalizePolicyKind(body.PolicyKind);
            if (string.IsNullOrWhiteSpace(body.PolicyId))
            {
                throw new CommandException(400, "Missing required field 'policyId'.");
            }

            Pawn pawn = ResolveManagedPawn(context, pawnId);
            Policy previous;
            Policy current;
            switch (policyKind)
            {
                case "apparel":
                    if (pawn.outfits == null)
                    {
                        throw new CommandException(409, "Pawn has no apparel policy tracker.");
                    }
                    ApparelPolicy apparelPolicy = ResolvePolicy(Current.Game.outfitDatabase == null ? null : Current.Game.outfitDatabase.AllOutfits, body.PolicyId, "Apparel policy");
                    previous = pawn.outfits.CurrentApparelPolicy;
                    pawn.outfits.CurrentApparelPolicy = apparelPolicy;
                    current = pawn.outfits.CurrentApparelPolicy;
                    break;
                case "food":
                    if (pawn.foodRestriction == null)
                    {
                        throw new CommandException(409, "Pawn has no food policy tracker.");
                    }
                    if (!pawn.foodRestriction.Configurable)
                    {
                        throw new CommandException(409, "Pawn food policy is not configurable.");
                    }
                    FoodPolicy foodPolicy = ResolvePolicy(Current.Game.foodRestrictionDatabase == null ? null : Current.Game.foodRestrictionDatabase.AllFoodRestrictions, body.PolicyId, "Food policy");
                    previous = pawn.foodRestriction.CurrentFoodPolicy;
                    pawn.foodRestriction.CurrentFoodPolicy = foodPolicy;
                    current = pawn.foodRestriction.CurrentFoodPolicy;
                    break;
                case "drug":
                    if (pawn.drugs == null)
                    {
                        throw new CommandException(409, "Pawn has no drug policy tracker.");
                    }
                    DrugPolicy drugPolicy = ResolvePolicy(Current.Game.drugPolicyDatabase == null ? null : Current.Game.drugPolicyDatabase.AllPolicies, body.PolicyId, "Drug policy");
                    previous = pawn.drugs.CurrentPolicy;
                    pawn.drugs.CurrentPolicy = drugPolicy;
                    current = pawn.drugs.CurrentPolicy;
                    break;
                case "reading":
                    if (pawn.reading == null)
                    {
                        throw new CommandException(409, "Pawn has no reading policy tracker.");
                    }
                    ReadingPolicy readingPolicy = ResolvePolicy(Current.Game.readingPolicyDatabase == null ? null : Current.Game.readingPolicyDatabase.AllReadingPolicies, body.PolicyId, "Reading policy");
                    previous = pawn.reading.CurrentPolicy;
                    pawn.reading.CurrentPolicy = readingPolicy;
                    current = pawn.reading.CurrentPolicy;
                    break;
                default:
                    throw new CommandException(400, "Unknown policy kind.");
            }

            return CommandEnvelope.Ok(context, previous != current, Dto.Obj(
                Dto.Field("assignmentKind", "policy"),
                Dto.Field("policyKind", policyKind),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousPolicy", PawnReadService.SerializePolicy(previous)),
                Dto.Field("policy", PawnReadService.SerializePolicy(current))));
        }

        private static BridgeResponse SetMedicalCare(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            Pawn pawn = ResolveManagedPawn(context, pawnId);
            Pawn_PlayerSettings settings = RequirePlayerSettings(pawn);
            MedicalCareCategory care = ParseEnum<MedicalCareCategory>(body.Care, "care", "Medical care");
            MedicalCareCategory previous = settings.medCare;
            settings.medCare = care;

            return CommandEnvelope.Ok(context, previous != settings.medCare, Dto.Obj(
                Dto.Field("assignmentKind", "medicalCare"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousMedicalCare", previous.ToString()),
                Dto.Field("medicalCare", settings.medCare.ToString())));
        }

        private static BridgeResponse SetSelfTend(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            if (!body.Enabled.HasValue)
            {
                throw new CommandException(400, "Missing required field 'enabled'.");
            }

            Pawn pawn = ResolveManagedPawn(context, pawnId);
            Pawn_PlayerSettings settings = RequirePlayerSettings(pawn);
            bool previous = settings.selfTend;
            settings.selfTend = body.Enabled.Value;

            return CommandEnvelope.Ok(context, previous != settings.selfTend, Dto.Obj(
                Dto.Field("assignmentKind", "selfTend"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousSelfTend", previous),
                Dto.Field("selfTend", settings.selfTend)));
        }

        private static BridgeResponse SetHostilityResponse(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            Pawn pawn = ResolveManagedPawn(context, pawnId);
            Pawn_PlayerSettings settings = RequirePlayerSettings(pawn);
            if (!settings.UsesConfigurableHostilityResponse)
            {
                throw new CommandException(409, "Pawn does not use configurable hostility response.");
            }

            HostilityResponseMode mode = ParseEnum<HostilityResponseMode>(body.Mode, "mode", "Hostility response mode");
            HostilityResponseMode previous = settings.hostilityResponse;
            settings.hostilityResponse = mode;

            return CommandEnvelope.Ok(context, previous != settings.hostilityResponse, Dto.Obj(
                Dto.Field("assignmentKind", "hostilityResponse"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousHostilityResponse", previous.ToString()),
                Dto.Field("hostilityResponse", settings.hostilityResponse.ToString())));
        }

        private static BridgeResponse SetAllowedArea(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            if (string.IsNullOrWhiteSpace(body.AreaId))
            {
                throw new CommandException(400, "Missing required field 'areaId'.");
            }

            Pawn pawn = ResolveManagedPawn(context, pawnId);
            Pawn_PlayerSettings settings = RequirePlayerSettings(pawn);
            if (!settings.SupportsAllowedAreas)
            {
                throw new CommandException(409, "Pawn does not support allowed areas.");
            }

            Area previous = settings.AreaRestrictionInPawnCurrentMap;
            Area current = ResolveAllowedArea(context.Map, body.AreaId);
            settings.AreaRestrictionInPawnCurrentMap = current;

            return CommandEnvelope.Ok(context, previous != settings.AreaRestrictionInPawnCurrentMap, Dto.Obj(
                Dto.Field("assignmentKind", "allowedArea"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousAllowedArea", PawnReadService.SerializeAllowedArea(previous, context.Map)),
                Dto.Field("allowedArea", PawnReadService.SerializeAllowedArea(settings.AreaRestrictionInPawnCurrentMap, context.Map))));
        }

        private static BridgeResponse SetCarryMedicine(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            if (!body.Count.HasValue)
            {
                throw new CommandException(400, "Missing required field 'count'.");
            }
            if (body.Count.Value < 0 || body.Count.Value > 3)
            {
                throw new CommandException(400, "Medicine carry count must be between 0 and 3.");
            }

            Pawn pawn = CommandUtil.ResolvePlayerControlledPawn(context, pawnId);
            if (pawn.inventoryStock == null)
            {
                throw new CommandException(409, "Pawn has no inventory stock tracker.");
            }

            InventoryStockGroupDef group = InventoryStockGroupDefOf.Medicine;
            if (group == null)
            {
                throw new CommandException(409, "Medicine inventory stock group is unavailable.");
            }

            int previousCount = pawn.inventoryStock.GetDesiredCountForGroup(group);
            ThingDef previousThing = pawn.inventoryStock.GetDesiredThingForGroup(group);
            object previous = PawnReadService.SerializeCarryMedicine(pawn);
            ThingDef medicine = ResolveMedicine(group, body.MedicineDefName);
            if (medicine != null)
            {
                pawn.inventoryStock.SetThingForGroup(group, medicine);
            }
            pawn.inventoryStock.SetCountForGroup(group, body.Count.Value);

            int currentCount = pawn.inventoryStock.GetDesiredCountForGroup(group);
            ThingDef currentThing = pawn.inventoryStock.GetDesiredThingForGroup(group);
            object current = PawnReadService.SerializeCarryMedicine(pawn);
            bool changed = previousCount != currentCount || previousThing != currentThing;
            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("assignmentKind", "carryMedicine"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousCarryMedicine", previous),
                Dto.Field("carryMedicine", current)));
        }

        private static BridgeResponse SetPrisonerInteraction(CommandContext context, string pawnId, SetPawnAssignmentBody body)
        {
            PrisonerInteractionModeDef interactionMode = CommandUtil.ResolveDef<PrisonerInteractionModeDef>(
                body.InteractionModeDefName,
                "interactionModeDefName",
                "Prisoner interaction mode");

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
                Dto.Field("assignmentKind", "prisonerInteraction"),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousInteractionMode", CommandUtil.DefSummary(previous)),
                Dto.Field("interactionMode", CommandUtil.DefSummary(pawn.guest.ExclusiveInteractionMode))));
        }

        private static string NormalizeAssignmentKind(string assignmentKind)
        {
            if (string.IsNullOrWhiteSpace(assignmentKind))
            {
                throw new CommandException(400, "Missing assignment kind.");
            }

            string normalized = assignmentKind.Trim().Replace("_", "").Replace("-", "").ToLowerInvariant();
            switch (normalized)
            {
                case "schedule":
                case "policy":
                case "medicalcare":
                case "selftend":
                case "hostilityresponse":
                case "allowedarea":
                case "carrymedicine":
                case "prisonerinteraction":
                    return normalized;
                default:
                    throw new CommandException(400, "Assignment kind must be schedule, policy, medicalCare, selfTend, hostilityResponse, allowedArea, carryMedicine, or prisonerInteraction.");
            }
        }

        private static string NormalizePolicyKind(string policyKind)
        {
            if (string.IsNullOrWhiteSpace(policyKind))
            {
                throw new CommandException(400, "Missing required field 'policyKind'.");
            }

            string normalized = policyKind.Trim().ToLowerInvariant();
            if (normalized != "apparel" && normalized != "food" && normalized != "drug" && normalized != "reading")
            {
                throw new CommandException(400, "Policy kind must be apparel, food, drug, or reading.");
            }
            return normalized;
        }

        private static Pawn ResolveManagedPawn(CommandContext context, string pawnId)
        {
            Pawn pawn = CommandUtil.ResolveSpawnedPawn(context, pawnId);
            if (!pawn.IsPlayerControlled && !pawn.IsPrisonerOfColony && !pawn.IsColonyAnimal)
            {
                throw new CommandException(409, "Pawn is not managed by the colony.");
            }
            return pawn;
        }

        private static Pawn_PlayerSettings RequirePlayerSettings(Pawn pawn)
        {
            if (pawn.playerSettings == null)
            {
                throw new CommandException(409, "Pawn has no player settings.");
            }
            return pawn.playerSettings;
        }

        private static T ParseEnum<T>(string value, string fieldName, string kind) where T : struct
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new CommandException(400, "Missing required field '" + fieldName + "'.");
            }

            T parsed;
            if (!Enum.TryParse(value, true, out parsed) || !Enum.IsDefined(typeof(T), parsed))
            {
                throw new CommandException(400, kind + " is invalid.");
            }
            return parsed;
        }

        private static T ResolvePolicy<T>(IEnumerable<T> policies, string policyId, string kind) where T : Policy
        {
            if (policies == null)
            {
                throw new CommandException(409, kind + " database is unavailable.");
            }

            T policy = policies.FirstOrDefault(item => PolicyIdMatches(item, policyId));
            if (policy == null)
            {
                throw new CommandException(404, kind + " not found.");
            }
            return policy;
        }

        private static bool PolicyIdMatches(Policy policy, string policyId)
        {
            return policy != null &&
                (string.Equals(policy.GetUniqueLoadID(), policyId, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(policy.id.ToString(), policyId, StringComparison.OrdinalIgnoreCase));
        }

        private static Area ResolveAllowedArea(Map map, string areaId)
        {
            if (string.Equals(areaId, "unrestricted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(areaId, "none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (Area area in map.areaManager.AllAreas)
            {
                if (!area.AssignableAsAllowed())
                {
                    continue;
                }
                if (string.Equals(PawnReadService.AllowedAreaId(area, map), areaId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(area.ID.ToString(), areaId, StringComparison.OrdinalIgnoreCase))
                {
                    return area;
                }
            }
            throw new CommandException(404, "Allowed area not found.");
        }

        private static ThingDef ResolveMedicine(InventoryStockGroupDef group, string medicineDefName)
        {
            if (string.IsNullOrWhiteSpace(medicineDefName))
            {
                return null;
            }

            ThingDef medicine = CommandUtil.ResolveDef<ThingDef>(medicineDefName, "medicineDefName", "Medicine");
            if (group.thingDefs == null || !group.thingDefs.Contains(medicine))
            {
                throw new CommandException(409, "ThingDef is not a valid carried medicine option.");
            }
            return medicine;
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
        private sealed class SetPawnAssignmentBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "assignments")]
            public SetPawnScheduleAssignmentBody[] Assignments { get; set; }

            [DataMember(Name = "policyKind")]
            public string PolicyKind { get; set; }

            [DataMember(Name = "policyId")]
            public string PolicyId { get; set; }

            [DataMember(Name = "care")]
            public string Care { get; set; }

            [DataMember(Name = "enabled")]
            public bool? Enabled { get; set; }

            [DataMember(Name = "mode")]
            public string Mode { get; set; }

            [DataMember(Name = "areaId")]
            public string AreaId { get; set; }

            [DataMember(Name = "count")]
            public int? Count { get; set; }

            [DataMember(Name = "medicineDefName")]
            public string MedicineDefName { get; set; }

            [DataMember(Name = "interactionModeDefName")]
            public string InteractionModeDefName { get; set; }
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
