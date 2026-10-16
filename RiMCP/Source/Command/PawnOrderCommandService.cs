using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RiMCP.Command
{
    // Direct orders to one pawn, through the game's right-click options so every check the menu
    // makes applies; a disabled option's label explains a refusal. Each order's menu provider is
    // asked the way FloatMenuMakerMap asks it: as the current provider, for a click on the target.
    // An order replaces what the pawn is doing, as a click does, or with queue waits behind its
    // current job and earlier orders, as a shift-click does.
    internal static class PawnOrderCommandService
    {
        private enum TargetKind
        {
            Item,
            Thing,
            Pawn,
            Cell
        }

        private static readonly PawnOrder[] Orders =
        {
            new PawnOrder
            {
                Name = "equip",
                Target = TargetKind.Item,
                Jobs = () => new[] { JobDefOf.Equip },
                Provider = () => new FloatMenuOptionProvider_Equip(),
                IsDone = (pawn, target) => pawn.equipment != null && pawn.equipment.Primary == target.Thing,
                Refusal = EquipRefusal,
                Question = EquipQuestion,
                QueuedJob = (pawn, target) => JobMaker.MakeJob(JobDefOf.Equip, target),
                PawnUnable = "The pawn cannot equip things right now.",
                TargetUnsuitable = "This item cannot be equipped."
            },
            new PawnOrder
            {
                Name = "wear",
                Target = TargetKind.Item,
                Jobs = () => new[] { JobDefOf.Wear },
                Provider = () => new FloatMenuOptionProvider_Wear(),
                IsDone = (pawn, target) => pawn.apparel != null && pawn.apparel.WornApparel.Contains(target.Thing as Apparel),
                Question = WearQuestion,
                QueuedJob = (pawn, target) => JobMaker.MakeJob(JobDefOf.Wear, target),
                PawnUnable = "The pawn cannot wear apparel right now.",
                TargetUnsuitable = "This item cannot be worn."
            },
            // The game picks a standable cell near the one asked for, so the pawn may stop a
            // little way off; destination reports where it is going.
            new PawnOrder
            {
                Name = "move",
                Target = TargetKind.Cell,
                DraftedOnly = true,
                Jobs = () => new[] { JobDefOf.Goto },
                Provider = () => new FloatMenuOptionProvider_DraftedMove(),
                // Standing there, rather than passing through on the way to another job.
                IsDone = (pawn, target) => pawn.Position == target.Cell && (pawn.CurJob == null || pawn.CurJob.def == JobDefOf.Wait_Combat),
                PawnUnable = "The pawn cannot move right now.",
                TargetUnsuitable = "There is no cell to stand on near there."
            },
            // Fires at the target with a ranged weapon, or attacks it in melee without one. Out of
            // range, a ranged attack is refused, as the menu greys it out: move the pawn closer.
            new PawnOrder
            {
                Name = "attack",
                Target = TargetKind.Thing,
                DraftedOnly = true,
                Jobs = () => new[] { JobDefOf.AttackStatic, JobDefOf.AttackMelee },
                Provider = () => new FloatMenuOptionProvider_DraftedAttack(),
                PawnUnable = "The pawn cannot attack right now.",
                TargetUnsuitable = "This cannot be attacked: only hostiles, animals, and some buildings can be."
            },
            new PawnOrder
            {
                Name = "rescue",
                Target = TargetKind.Pawn,
                Jobs = () => new[] { JobDefOf.Rescue },
                Provider = () => new FloatMenuOptionProvider_RescuePawn(),
                IsDone = (pawn, target) => ((Pawn)target.Thing).InBed(),
                PawnUnable = "The pawn cannot carry anyone right now.",
                TargetUnsuitable = "Only a downed pawn who is not hostile can be rescued."
            },
            new PawnOrder
            {
                Name = "tend",
                Target = TargetKind.Pawn,
                DraftedOnly = true,
                Jobs = () => new[] { JobDefOf.TendPatient },
                Provider = () => new FloatMenuOptionProvider_DraftedTend(),
                IsDone = (pawn, target) => !((Pawn)target.Thing).health.HasHediffsNeedingTend(),
                PawnUnable = "The pawn cannot tend right now.",
                TargetUnsuitable = "This pawn cannot be tended."
            }
        };

        public static BridgeResponse OrderPawn(BridgeRequest request, RouteMatch route)
        {
            OrderPawnBody body = CommandUtil.ReadBody<OrderPawnBody>(request);
            PawnOrder order = Orders.FirstOrDefault(candidate => string.Equals(candidate.Name, body.Order == null ? null : body.Order.Trim(), StringComparison.OrdinalIgnoreCase));
            if (order == null)
            {
                throw new CommandException(400, "Field 'order' must be one of: " + string.Join(", ", Orders.Select(candidate => candidate.Name).ToArray()) + ".");
            }
            bool queue = body.Queue == true;
            if (queue && order.QueuedJob == null)
            {
                throw new CommandException(400, "A " + order.Name + " order cannot be queued; give it without queue.");
            }
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;
            RequestKey key = RequestKey.For("order_pawn", requestId, map, route["pawnId"], order.Name, body.TargetId, body.X, body.Z, queue);
            return CommandReceipts.Once(context, key, () => Give(context, order, body, route["pawnId"], queue));
        }

        // Gives the order unless the pawn is already carrying it out. A queued order is already
        // given if the pawn is doing or has queued it; an immediate one only if the pawn is doing it
        // now, so a matching queued job is brought forward.
        private static CommandResult Give(CommandContext context, PawnOrder order, OrderPawnBody body, string pawnId, bool queue)
        {
            Map map = context.Map;
            Pawn pawn = CommandUtil.ResolvePlayerControlledPawn(context, pawnId);
            LocalTargetInfo target = ResolveTarget(order, body, map, pawn);
            string outcome;
            string reason = null;
            if (order.IsDone(pawn, target) || HasJob(pawn, order, target, queue))
            {
                outcome = "alreadySatisfied";
            }
            else
            {
                outcome = Issue(order, pawn, target, map, queue, out reason);
            }

            Job current = pawn.CurJob;
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("order", order.Name),
                Dto.Field("queue", queue),
                Dto.Field("pawn", CommandUtil.PawnSummary(pawn)),
                Dto.Field("target", target.HasThing ? SummaryDto.ThingWithPosition(target.Thing) : Dto.Obj(Dto.Field("x", target.Cell.x), Dto.Field("z", target.Cell.z))),
                Dto.Field("outcome", outcome),
                Dto.Field("reason", reason),
                Dto.Field("destination", order.Target == TargetKind.Cell && current != null && current.def == JobDefOf.Goto ? Dto.Obj(Dto.Field("x", current.targetA.Cell.x), Dto.Field("z", current.targetA.Cell.z)) : null),
                Dto.Field("currentJob", PawnReadService.SerializeCurrentJob(pawn)),
                Dto.Field("queuedJobs", PawnReadService.SerializeQueuedJobs(pawn)));
            return new CommandResult(outcome == "jobIssued", data);
        }

        private static LocalTargetInfo ResolveTarget(PawnOrder order, OrderPawnBody body, Map map, Pawn pawn)
        {
            if (order.Target == TargetKind.Cell)
            {
                if (!body.X.HasValue || !body.Z.HasValue)
                {
                    throw new CommandException(400, "A " + order.Name + " order needs x and z.");
                }
                IntVec3 cell = new IntVec3(body.X.Value, 0, body.Z.Value);
                if (!cell.InBounds(map))
                {
                    throw new CommandException(400, "x and z are outside the map.");
                }
                return cell;
            }
            if (string.IsNullOrWhiteSpace(body.TargetId))
            {
                throw new CommandException(400, "A " + order.Name + " order needs targetId.");
            }
            // An item the ordering pawn already carries or wears counts too, so retrying an order it
            // has carried out finds the item. Another pawn's gear never does.
            if (order.Target == TargetKind.Item)
            {
                Thing own = OwnGear(pawn).FirstOrDefault(thing => thing.ThingID == body.TargetId || thing.GetUniqueLoadID() == body.TargetId);
                if (own != null)
                {
                    return own;
                }
            }
            IEnumerable<Thing> candidates = order.Target == TargetKind.Item ? map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                : order.Target == TargetKind.Pawn ? map.mapPawns.AllPawnsSpawned.Cast<Thing>()
                : map.mapPawns.AllPawnsSpawned.Cast<Thing>().Concat(map.listerThings.AllThings);
            Thing target = candidates.FirstOrDefault(thing => thing.Spawned && !thing.Position.Fogged(map) && (thing.ThingID == body.TargetId || thing.GetUniqueLoadID() == body.TargetId));
            if (target == null)
            {
                // Only a holder the player can see is named; one under fog is not found, as in reads.
                Pawn holder = order.Target == TargetKind.Item ? map.mapPawns.AllPawnsSpawned.FirstOrDefault(other => PawnReadService.Visible(map, other) && Holds(other, body.TargetId)) : null;
                throw holder != null
                    ? new CommandException(409, "That item is held by " + holder.LabelShortCap + ", not lying on the map; a pawn can only be ordered to take items on the ground or already its own.")
                    : new CommandException(404, "No such " + order.Target.ToString().ToLowerInvariant() + " on this map.");
            }
            return target;
        }

        private static string Issue(PawnOrder order, Pawn pawn, LocalTargetInfo target, Map map, bool queue, out string reason)
        {
            reason = null;
            AcceptanceReport gate = FloatMenuMakerMap.ShouldGenerateFloatMenuForPawn(pawn);
            if (!gate.Accepted)
            {
                reason = string.IsNullOrWhiteSpace(gate.Reason) ? "The pawn cannot take orders right now." : gate.Reason.StripTags();
                return "rejected";
            }
            List<FloatMenuOption> options = Options(order, pawn, target, map, out reason);
            if (reason != null)
            {
                return "rejected";
            }
            // The menu lists an order's main option first, such as firing before melee; a disabled
            // one is refused with its reason rather than swapped for another kind of order.
            FloatMenuOption option = options.FirstOrDefault();
            if (option == null)
            {
                reason = order.Target == TargetKind.Cell && target.Cell == pawn.Position
                    ? "The pawn is already on that cell; the game only orders moves to other cells."
                    : order.TargetUnsuitable;
                return "rejected";
            }
            if (option.Disabled)
            {
                reason = option.Label.StripTags();
                return "rejected";
            }
            reason = order.Refusal(pawn, target.Thing);
            if (reason != null)
            {
                return "rejected";
            }

            string question = order.Question(pawn, target.Thing);
            if (queue)
            {
                // The menu option reads the shift key to decide whether to queue, so a queued order
                // gives the option's job itself, as the option does: unforbid an item, then order
                // the job. A question the game would ask cannot wait in the queue.
                if (question != null)
                {
                    reason = question + " Give this order without queue so the player can answer.";
                    return "rejected";
                }
                if (order.Target == TargetKind.Item)
                {
                    target.Thing.SetForbidden(false);
                }
                pawn.jobs.TryTakeOrderedJob(order.QueuedJob(pawn, target), JobTag.Misc, requestQueueing: true);
            }
            else
            {
                // Questions are left on screen for the player rather than answered.
                int windows = Find.WindowStack.Count;
                option.Chosen(true, null);
                if (Find.WindowStack.Count > windows)
                {
                    reason = question ?? "The game is asking the player to confirm this order.";
                    return "awaitingInput";
                }
            }
            if (HasJob(pawn, order, target, true) || (order.Target == TargetKind.Cell && HasJob(pawn, order, LocalTargetInfo.Invalid, true)))
            {
                return "jobIssued";
            }
            reason = "The pawn did not take the job.";
            return "rejected";
        }

        // The options the menu would show for this click, gathered with the order's provider as the
        // current one, as FloatMenuMakerMap does: that is what makes the provider's rules about
        // which pawns may take the order apply, such as only drafted pawns.
        private static List<FloatMenuOption> Options(PawnOrder order, Pawn pawn, LocalTargetInfo target, Map map, out string reason)
        {
            reason = null;
            FloatMenuOptionProvider provider = order.Provider();
            Vector3 click = target.HasThing ? target.Thing.DrawPos : target.Cell.ToVector3Shifted();
            FloatMenuContext context = new FloatMenuContext(new List<Pawn> { pawn }, click, map);
            FloatMenuOptionProvider previous = FloatMenuMakerMap.currentProvider;
            FloatMenuMakerMap.currentProvider = provider;
            try
            {
                if (context.FirstSelectedPawn == null)
                {
                    reason = order.DraftedOnly && !pawn.Drafted
                        ? "Only a drafted pawn can be ordered to " + order.Name + "; draft " + pawn.LabelShort + " with set_pawn_drafted first."
                        : order.PawnUnable;
                    return null;
                }
                if (!provider.Applies(context))
                {
                    reason = order.PawnUnable;
                    return null;
                }
                switch (order.Target)
                {
                    case TargetKind.Cell:
                        return provider.GetOptions(context).ToList();
                    case TargetKind.Pawn:
                        return provider.TargetPawnValid((Pawn)target.Thing, context) ? provider.GetOptionsFor((Pawn)target.Thing, context).ToList() : new List<FloatMenuOption>();
                    default:
                        return provider.TargetThingValid(target.Thing, context) ? provider.GetOptionsFor(target.Thing, context).ToList() : new List<FloatMenuOption>();
                }
            }
            finally
            {
                FloatMenuMakerMap.currentProvider = previous;
            }
        }

        // Whether the pawn is doing, or with includeQueued has queued, one of the order's jobs on the
        // target. An invalid target matches any target, for orders where the game picks the exact cell.
        private static bool HasJob(Pawn pawn, PawnOrder order, LocalTargetInfo target, bool includeQueued)
        {
            JobDef[] jobs = order.Jobs();
            Func<Job, bool> matches = job => jobs.Contains(job.def) && (!target.IsValid || (target.HasThing ? job.targetA.Thing == target.Thing : job.targetA.Cell == target.Cell));
            if (pawn.CurJob != null && matches(pawn.CurJob))
            {
                return true;
            }
            return includeQueued && pawn.jobs != null && pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Any(queued => matches(queued.job));
        }

        private static IEnumerable<Thing> OwnGear(Pawn pawn)
        {
            IEnumerable<Thing> equipment = pawn.equipment == null ? Enumerable.Empty<Thing>() : pawn.equipment.AllEquipmentListForReading.Cast<Thing>();
            IEnumerable<Thing> apparel = pawn.apparel == null ? Enumerable.Empty<Thing>() : pawn.apparel.WornApparel.Cast<Thing>();
            return equipment.Concat(apparel);
        }

        private static bool Holds(Pawn pawn, string id)
        {
            IEnumerable<Thing> inventory = pawn.inventory == null ? Enumerable.Empty<Thing>() : pawn.inventory.innerContainer;
            return OwnGear(pawn).Concat(inventory).Any(thing => thing.ThingID == id || thing.GetUniqueLoadID() == id);
        }

        // FloatMenuOptionProvider_Equip offers equipping a persona weapon while the pawn is bonded to
        // another, but only explains why it cannot.
        private static string EquipRefusal(Pawn pawn, Thing item)
        {
            return EquipmentUtility.AlreadyBondedToWeapon(item, pawn) ? "The pawn is already bonded to another persona weapon." : null;
        }

        // It asks first before equipping a persona weapon, which bonds the pawn to it.
        private static string EquipQuestion(Pawn pawn, Thing item)
        {
            return EquipmentUtility.GetPersonaWeaponConfirmationText(item, pawn).NullOrEmpty() ? null : "Equipping this persona weapon would bond the pawn to it, so the game asks the player first.";
        }

        // FloatMenuOptionProvider_Wear asks first when a mechanitor would take off apparel that gives
        // mech bandwidth and lose control of mechs.
        private static string WearQuestion(Pawn pawn, Thing item)
        {
            Apparel apparel = item as Apparel;
            Apparel replaced = apparel == null ? null : ApparelUtility.GetApparelReplacedByNewApparel(pawn, apparel);
            List<Pawn> lostMechs;
            int bandwidthLost;
            if (replaced == null || !ModsConfig.BiotechActive || !MechanitorUtility.IsMechanitor(pawn) || !pawn.apparel.Wearing(replaced)
                || !MechanitorUtility.TryGetMechsLostFromDroppingThing(pawn, replaced, out lostMechs, out bandwidthLost))
            {
                return null;
            }
            return "Taking off " + replaced.LabelShort + " would lose control of " + lostMechs.Count + " mechs, so the game asks the player first.";
        }

        // One kind of order: what it targets, the game's menu provider for it (which makes the checks
        // and explains refusals), the jobs that carry it out, when it already holds, and the cases
        // where the menu refuses or stops to ask the player without saying so in the option. Only
        // orders that say how to build their job directly can be queued.
        private sealed class PawnOrder
        {
            public string Name;
            public TargetKind Target;
            public bool DraftedOnly;
            public Func<JobDef[]> Jobs;
            public Func<FloatMenuOptionProvider> Provider;
            public Func<Pawn, LocalTargetInfo, bool> IsDone = (pawn, target) => false;
            public Func<Pawn, Thing, string> Refusal = (pawn, target) => null;
            public Func<Pawn, Thing, string> Question = (pawn, target) => null;
            public Func<Pawn, LocalTargetInfo, Job> QueuedJob;
            public string PawnUnable;
            public string TargetUnsuitable;
        }

        [DataContract]
        private sealed class OrderPawnBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "order")]
            public string Order { get; set; }

            [DataMember(Name = "targetId")]
            public string TargetId { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "queue")]
            public bool? Queue { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
