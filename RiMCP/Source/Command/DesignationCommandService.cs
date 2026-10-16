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
    // Map orders on things, through the game's own designators so their checks apply.
    internal static class DesignationCommandService
    {
        private const int MaxTargets = 300;

        private static readonly DesignationKind[] Kinds =
        {
            new DesignationKind
            {
                Name = "forbid",
                Targets = ThingRequestGroup.HaulableEver,
                Designator = () => new Designator_Forbid(),
                IsCandidate = (thing, tool) => Forbiddable(thing),
                IsSatisfied = (thing, tool) => Forbidden(thing) == true,
                Refusal = ForbidRefusal
            },
            new DesignationKind
            {
                Name = "unforbid",
                Targets = ThingRequestGroup.HaulableEver,
                Designator = () => new Designator_Unforbid(),
                IsCandidate = (thing, tool) => Forbiddable(thing),
                IsSatisfied = (thing, tool) => Forbidden(thing) == false,
                Refusal = ForbidRefusal
            },
            new DesignationKind
            {
                Name = "haul",
                Targets = ThingRequestGroup.HaulableEver,
                Designator = () => new Designator_Haul(),
                IsCandidate = (thing, tool) => thing.def.designateHaulable,
                IsSatisfied = (thing, tool) => HasDesignation(thing, DesignationDefOf.Haul),
                Refusal = thing => thing.def.designateHaulable
                    ? "Cannot be designated for hauling."
                    : "Hauled automatically once it is unforbidden and a stockpile accepts it."
            },
            // The game's three plant tools: cut clears plants, but leaves grown trees to chopWood,
            // which takes them for wood; harvest gathers crops and other harvestable plants.
            new DesignationKind
            {
                Name = "cut",
                Targets = ThingRequestGroup.Plant,
                Designator = () => new Designator_PlantsCut(),
                IsCandidate = (thing, tool) => thing.def.plant != null && !GrownTree(thing),
                IsSatisfied = (thing, tool) => HasDesignation(thing, DesignationDefOf.CutPlant),
                Refusal = thing => GrownTree(thing) ? "Grown trees are chopped for wood: use chopWood." : "This plant cannot be cut."
            },
            new DesignationKind
            {
                Name = "harvest",
                Targets = ThingRequestGroup.Plant,
                Designator = () => new Designator_PlantsHarvest(),
                IsCandidate = (thing, tool) => thing.def.plant != null && !thing.def.plant.IsTree && thing.def.plant.harvestTag == "Standard" && thing is Plant && ((Plant)thing).HarvestableNow,
                IsSatisfied = (thing, tool) => thing.def.plant != null && !thing.def.plant.IsTree && HasDesignation(thing, DesignationDefOf.HarvestPlant),
                Refusal = thing => "This plant cannot be harvested now."
            },
            new DesignationKind
            {
                Name = "chopWood",
                Targets = ThingRequestGroup.Plant,
                Designator = () => new Designator_PlantsHarvestWood(),
                IsCandidate = (thing, tool) => GrownTree(thing),
                IsSatisfied = (thing, tool) => thing.def.plant != null && thing.def.plant.IsTree && HasDesignation(thing, DesignationDefOf.HarvestPlant),
                Refusal = thing => "This tree cannot be chopped now."
            },
            // Mining is marked on the cell of the rock or ore; unexplored cells are left alone.
            new DesignationKind
            {
                Name = "mine",
                Targets = ThingRequestGroup.Everything,
                Designator = () => new Designator_Mine(),
                IsCandidate = (thing, tool) => thing.def.mineable,
                IsSatisfied = (thing, tool) => thing.Map.designationManager.DesignationAt(thing.Position, DesignationDefOf.Mine) != null
                    || thing.Map.designationManager.DesignationAt(thing.Position, DesignationDefOf.MineVein) != null,
                Refusal = thing => thing.def.mineable ? "This cannot be mined now." : "Only rock and ore can be mined."
            },
            new DesignationKind
            {
                Name = "deconstruct",
                Targets = ThingRequestGroup.BuildingArtificial,
                Designator = () => new Designator_Deconstruct(),
                IsCandidate = (thing, tool) => thing.def.category == ThingCategory.Building && !thing.def.mineable,
                IsSatisfied = (thing, tool) => HasDesignation(thing, DesignationDefOf.Deconstruct),
                Refusal = thing => "This cannot be deconstructed."
            },
            // Cancel removes the orders on a thing, or a blueprint or frame itself. It holds once
            // the game's cancel tool finds nothing left to cancel.
            new DesignationKind
            {
                Name = "cancel",
                Targets = ThingRequestGroup.Everything,
                Designator = () => new Designator_Cancel(),
                IsCandidate = (thing, tool) => tool.CanDesignateThing(thing).Accepted,
                IsSatisfied = (thing, tool) => !tool.CanDesignateThing(thing).Accepted,
                Refusal = thing => "Nothing to cancel."
            }
        };

        public static BridgeResponse Designate(BridgeRequest request, RouteMatch route)
        {
            DesignateBody body = CommandUtil.ReadBody<DesignateBody>(request);
            DesignationKind kind = Kinds.FirstOrDefault(candidate => string.Equals(candidate.Name, body.Kind == null ? null : body.Kind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (kind == null)
            {
                throw new CommandException(400, "Field 'kind' must be one of: " + string.Join(", ", Kinds.Select(candidate => candidate.Name).ToArray()) + ".");
            }
            bool byIds = body.ThingIds != null && body.ThingIds.Length > 0;
            bool byRects = body.Rects != null && body.Rects.Length > 0;
            if (byIds == byRects)
            {
                throw new CommandException(400, "Give either thingIds or rects.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;
            Designator designator = kind.Designator();
            ThingDef defFilter = string.IsNullOrWhiteSpace(body.DefName) ? null : CommandUtil.ResolveDef<ThingDef>(body.DefName, "defName", "Thing");

            List<object> results = new List<object>();
            int applied = 0, satisfied = 0, rejected = 0, outside = 0, skipped = 0;
            Action<string, Thing> record = (id, thing) =>
            {
                string reason = thing == null ? "No such " + (kind.Targets == ThingRequestGroup.Plant ? "plant" : kind.Targets == ThingRequestGroup.HaulableEver ? "item" : "thing") + " on this map." : null;
                string outcome = thing == null ? "rejected" : Apply(designator, kind, thing, out reason);
                applied += outcome == "applied" ? 1 : 0;
                satisfied += outcome == "alreadySatisfied" ? 1 : 0;
                rejected += outcome == "rejected" ? 1 : 0;
                results.Add(Dto.Obj(
                    Dto.Field("id", id),
                    Dto.Field("def", thing == null ? null : SummaryDto.Def(thing.def)),
                    Dto.Field("outcome", outcome),
                    Dto.Field("reason", reason)));
            };

            if (byIds)
            {
                List<string> ids = body.ThingIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                if (ids.Count > MaxTargets)
                {
                    throw new CommandException(400, "At most " + MaxTargets + " thingIds per call.");
                }
                Dictionary<string, Thing> byId = new Dictionary<string, Thing>();
                HashSet<string> wanted = new HashSet<string>(ids);
                foreach (Thing thing in map.listerThings.ThingsInGroup(kind.Targets).Where(thing => thing.Spawned && !thing.Position.Fogged(map)))
                {
                    if (wanted.Contains(thing.ThingID))
                    {
                        byId[thing.ThingID] = thing;
                    }
                    else if (wanted.Contains(thing.GetUniqueLoadID()))
                    {
                        byId[thing.GetUniqueLoadID()] = thing;
                    }
                }
                foreach (string id in ids)
                {
                    Thing thing;
                    byId.TryGetValue(id, out thing);
                    record(id, thing);
                }
            }
            else
            {
                // A building covering several cells is listed in each of them, hence Distinct.
                List<Thing> inCells = CommandCells.FromRects(body.Rects, map, "rects", out outside)
                    .Where(cell => !cell.Fogged(map))
                    .SelectMany(cell => cell.GetThingList(map))
                    .Distinct()
                    .Where(thing => thing.Spawned && kind.IsCandidate(thing, designator) && (defFilter == null || thing.def == defFilter))
                    .OrderBy(thing => thing.thingIDNumber)
                    .ToList();
                skipped = Math.Max(0, inCells.Count - MaxTargets);
                foreach (Thing thing in inCells.Take(MaxTargets))
                {
                    record(ReadUtil.ThingId(thing), thing);
                }
            }

            return CommandEnvelope.Ok(context, applied > 0, Dto.Obj(
                Dto.Field("kind", kind.Name),
                Dto.Field("applied", applied),
                Dto.Field("alreadySatisfied", satisfied),
                Dto.Field("rejectedCount", rejected),
                Dto.Field("results", results.ToArray()),
                Dto.Field("notProcessed", skipped > 0 ? (object)skipped : null),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null)));
        }

        private static string Apply(Designator designator, DesignationKind kind, Thing thing, out string reason)
        {
            reason = null;
            if (kind.IsSatisfied(thing, designator))
            {
                return "alreadySatisfied";
            }
            AcceptanceReport report = designator.CanDesignateThing(thing);
            if (!report.Accepted)
            {
                reason = string.IsNullOrWhiteSpace(report.Reason) ? kind.Refusal(thing) : report.Reason.StripTags();
                return "rejected";
            }
            designator.DesignateThing(thing);
            return "applied";
        }

        private static bool Forbiddable(Thing thing)
        {
            return thing.def.category == ThingCategory.Item && thing.TryGetComp<CompForbiddable>() != null;
        }

        private static bool? Forbidden(Thing thing)
        {
            CompForbiddable forbiddable = thing.TryGetComp<CompForbiddable>();
            return forbiddable == null ? (bool?)null : forbiddable.Forbidden;
        }

        private static string ForbidRefusal(Thing thing)
        {
            return thing.def.category != ThingCategory.Item ? "Only items can be forbidden." : "This item cannot be forbidden.";
        }

        private static bool HasDesignation(Thing thing, DesignationDef def)
        {
            return thing.Map.designationManager.DesignationOn(thing, def) != null;
        }

        // A tree with wood to take, which the cut tool leaves alone.
        private static bool GrownTree(Thing thing)
        {
            Plant plant = thing as Plant;
            return plant != null && plant.def.plant.IsTree && plant.HarvestableNow;
        }

        // One kind of order: the designator that checks and applies it, where to look up its targets
        // by id, which things in a rectangle it applies to, when it already holds, and what to say
        // when the designator refuses without a reason. The checks get the request's designator for
        // orders, like cancel, that only the designator can judge.
        private sealed class DesignationKind
        {
            public string Name;
            public ThingRequestGroup Targets;
            public Func<Designator> Designator;
            public Func<Thing, Designator, bool> IsCandidate;
            public Func<Thing, Designator, bool> IsSatisfied;
            public Func<Thing, string> Refusal;
        }

        [DataContract]
        private sealed class DesignateBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "kind")]
            public string Kind { get; set; }

            [DataMember(Name = "thingIds")]
            public string[] ThingIds { get; set; }

            [DataMember(Name = "rects")]
            public CellRectBody[] Rects { get; set; }

            [DataMember(Name = "defName")]
            public string DefName { get; set; }
        }
    }
}
