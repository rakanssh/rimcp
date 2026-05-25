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
    internal static class AnimalCommandService
    {
        public static BridgeResponse SetDesignation(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            SetAnimalDesignationBody body = CommandUtil.ReadBody<SetAnimalDesignationBody>(request);
            string action = NormalizeAction(body.Action);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolveSpawnedPawn(context, pawnId);
            CommandUtil.RequireAnimal(pawn);

            DesignationDef target = DesignationForAction(action);
            ValidateDesignationAction(pawn, action);

            List<DesignationDef> previous = CurrentAnimalDesignations(context.Map, pawn);
            bool hadTarget = target != null && previous.Contains(target);
            bool hadOnlyTarget = target != null && hadTarget && previous.Count == 1;
            bool changed = target == null ? previous.Count > 0 : !hadOnlyTarget;

            if (changed)
            {
                RemoveAnimalDesignations(context.Map, pawn);
                if (target != null)
                {
                    context.Map.designationManager.AddDesignation(new Designation(new LocalTargetInfo(pawn), target, null));
                }
            }

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousDesignations", previous.Select(def => def.defName).ToArray()),
                Dto.Field("designation", CurrentAnimalDesignation(context.Map, pawn))));
        }

        public static BridgeResponse SetTraining(BridgeRequest request, RouteMatch route)
        {
            string pawnId = route["pawnId"];
            SetAnimalTrainingBody body = CommandUtil.ReadBody<SetAnimalTrainingBody>(request);
            if (body.Assignments == null || body.Assignments.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'assignments'.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            Pawn pawn = CommandUtil.ResolveSpawnedPawn(context, pawnId);
            CommandUtil.RequireColonyAnimal(pawn);
            if (pawn.training == null)
            {
                throw new CommandException(409, "Animal has no training tracker.");
            }

            List<TrainableDef> allTrainables = DefDatabase<TrainableDef>.AllDefsListForReading
                .OrderBy(def => def.listPriority)
                .ThenBy(def => def.defName)
                .ToList();
            Dictionary<string, bool> previousWanted = WantedByDefName(pawn, allTrainables);
            List<TrainingAssignment> assignments = ParseTrainingAssignments(pawn, body.Assignments);

            foreach (TrainingAssignment assignment in assignments)
            {
                pawn.training.SetWantedRecursive(assignment.Trainable, assignment.Wanted);
            }

            Dictionary<string, bool> currentWanted = WantedByDefName(pawn, allTrainables);
            object[] changedTrainables = allTrainables
                .Where(def => previousWanted[def.defName] != currentWanted[def.defName])
                .Select(def => Dto.Obj(
                    Dto.Field("trainable", CommandUtil.DefSummary(def)),
                    Dto.Field("previousWanted", previousWanted[def.defName]),
                    Dto.Field("wanted", currentWanted[def.defName])))
                .ToArray();

            return CommandEnvelope.Ok(context, changedTrainables.Length > 0, Dto.Obj(
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("previousTraining", PawnReadService.SerializeTraining(pawn, previousWanted)),
                Dto.Field("training", PawnReadService.SerializeTraining(pawn)),
                Dto.Field("changedTrainables", changedTrainables)));
        }

        private static string NormalizeAction(string action)
        {
            if (string.IsNullOrWhiteSpace(action))
            {
                throw new CommandException(400, "Missing required field 'action'.");
            }
            string normalized = action.Trim().ToLowerInvariant();
            if (normalized != "hunt" && normalized != "tame" && normalized != "slaughter" && normalized != "none")
            {
                throw new CommandException(400, "Animal designation action must be hunt, tame, slaughter, or none.");
            }
            return normalized;
        }

        private static DesignationDef DesignationForAction(string action)
        {
            switch (action)
            {
                case "hunt":
                    return DesignationDefOf.Hunt;
                case "tame":
                    return DesignationDefOf.Tame;
                case "slaughter":
                    return DesignationDefOf.Slaughter;
                default:
                    return null;
            }
        }

        private static void ValidateDesignationAction(Pawn pawn, string action)
        {
            switch (action)
            {
                case "hunt":
                    if (pawn.IsColonyAnimal)
                    {
                        throw new CommandException(409, "Colony animals cannot be designated for hunting; use slaughter instead.");
                    }
                    return;
                case "tame":
                    if (pawn.IsColonyAnimal)
                    {
                        throw new CommandException(409, "Colony animals are already tamed.");
                    }
                    if (!TameUtility.CanTame(pawn))
                    {
                        throw new CommandException(409, "Animal cannot be tamed.");
                    }
                    return;
                case "slaughter":
                    CommandUtil.RequireColonyAnimal(pawn);
                    return;
                default:
                    return;
            }
        }

        private static List<DesignationDef> CurrentAnimalDesignations(Map map, Pawn pawn)
        {
            List<DesignationDef> defs = new List<DesignationDef>();
            foreach (DesignationDef def in AnimalDesignationDefs())
            {
                if (map.designationManager.DesignationOn(pawn, def) != null)
                {
                    defs.Add(def);
                }
            }
            return defs;
        }

        private static object CurrentAnimalDesignation(Map map, Pawn pawn)
        {
            List<DesignationDef> defs = CurrentAnimalDesignations(map, pawn);
            return defs.Count == 0
                ? null
                : Dto.Obj(
                    Dto.Field("defName", defs[0].defName),
                    Dto.Field("label", ReadUtil.DefLabel(defs[0])));
        }

        private static void RemoveAnimalDesignations(Map map, Pawn pawn)
        {
            foreach (DesignationDef def in AnimalDesignationDefs())
            {
                map.designationManager.TryRemoveDesignationOn(pawn, def);
            }
        }

        private static DesignationDef[] AnimalDesignationDefs()
        {
            return new[]
            {
                DesignationDefOf.Hunt,
                DesignationDefOf.Tame,
                DesignationDefOf.Slaughter
            };
        }

        private static List<TrainingAssignment> ParseTrainingAssignments(Pawn pawn, SetAnimalTrainingAssignmentBody[] bodies)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<TrainingAssignment> assignments = new List<TrainingAssignment>();
            for (int i = 0; i < bodies.Length; i++)
            {
                SetAnimalTrainingAssignmentBody body = bodies[i];
                if (body == null)
                {
                    throw new CommandException(400, "Training assignment at index " + i + " is null.");
                }
                if (!body.Wanted.HasValue)
                {
                    throw new CommandException(400, "Missing required field 'assignments[" + i + "].wanted'.");
                }

                TrainableDef trainable = CommandUtil.ResolveDef<TrainableDef>(body.TrainableDefName, "assignments[" + i + "].trainableDefName", "Trainable");
                if (!seen.Add(trainable.defName))
                {
                    throw new CommandException(400, "Duplicate training assignment for '" + trainable.defName + "'.");
                }
                if (body.Wanted.Value && !pawn.training.GetWanted(trainable))
                {
                    AcceptanceReport report = pawn.training.CanAssignToTrain(trainable);
                    if (!report.Accepted)
                    {
                        throw new CommandException(409, "Trainable cannot be assigned: " + report.Reason);
                    }
                }

                assignments.Add(new TrainingAssignment(trainable, body.Wanted.Value));
            }
            return assignments;
        }

        private static Dictionary<string, bool> WantedByDefName(Pawn pawn, List<TrainableDef> trainables)
        {
            Dictionary<string, bool> wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (TrainableDef trainable in trainables)
            {
                wanted[trainable.defName] = pawn.training.GetWanted(trainable);
            }
            return wanted;
        }

        [DataContract]
        private sealed class SetAnimalDesignationBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "action")]
            public string Action { get; set; }
        }

        [DataContract]
        private sealed class SetAnimalTrainingBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "assignments")]
            public SetAnimalTrainingAssignmentBody[] Assignments { get; set; }
        }

        [DataContract]
        private sealed class SetAnimalTrainingAssignmentBody
        {
            [DataMember(Name = "trainableDefName")]
            public string TrainableDefName { get; set; }

            [DataMember(Name = "wanted")]
            public bool? Wanted { get; set; }
        }

        private sealed class TrainingAssignment
        {
            public readonly TrainableDef Trainable;
            public readonly bool Wanted;

            public TrainingAssignment(TrainableDef trainable, bool wanted)
            {
                Trainable = trainable;
                Wanted = wanted;
            }
        }
    }
}
