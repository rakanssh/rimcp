using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace RiMCP.Read
{
    // What bills on one map share during a read: the items that could be ingredients (spawned,
    // explored, not forbidden) and whether each workbench can reach them, each worked out once.
    internal sealed class BillInputs
    {
        private readonly Map map;
        private readonly TraverseParms traverse = TraverseParms.For(TraverseMode.PassDoors);
        private readonly Dictionary<IntVec3, Dictionary<Thing, bool>> reachable = new Dictionary<IntVec3, Dictionary<Thing, bool>>();
        private List<Thing> candidates;

        public BillInputs(Map map)
        {
            this.map = map;
        }

        public List<Thing> Candidates
        {
            get
            {
                return candidates ?? (candidates = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                    .Where(thing => thing.Spawned && !thing.Position.Fogged(map) && !thing.IsForbidden(Faction.OfPlayer))
                    .ToList());
            }
        }

        public bool Reachable(IntVec3 root, Thing thing)
        {
            Dictionary<Thing, bool> fromRoot;
            if (!reachable.TryGetValue(root, out fromRoot))
            {
                reachable[root] = fromRoot = new Dictionary<Thing, bool>();
            }
            bool result;
            if (!fromRoot.TryGetValue(thing, out result))
            {
                fromRoot[thing] = result = map.reachability.CanReach(root, thing, PathEndMode.ClosestTouch, traverse);
            }
            return result;
        }
    }

    // Whether a production bill will run and, if not, why. The state mirrors
    // Bill_Production.ShouldDoNow without its side effect on the paused flag. An active bill's
    // blockers mirror what WorkGiver_DoBill needs: a usable workbench, a colonist the bill and
    // recipe allow, and enough usable ingredients, chosen by the game's rules for mixing kinds or
    // not. Usable means allowed by the bill, not forbidden, inside its search radius, and reachable
    // from the workbench.
    //
    // Accuracy: reservations are left out because they change from moment to moment, so
    // ingredients another job has claimed still count, and a bill can look able to run a moment
    // before a colonist finds them taken.
    internal sealed class BillStatus
    {
        public string State;
        public int? Count;
        public List<object> Blocking = new List<object>();
        public List<IngredientNeed> Ingredients = new List<IngredientNeed>();

        // detailed false, for reads that only need to know whether the bill is blocked, stops
        // counting a mixable ingredient once there is enough of it. Single-kind ingredients are
        // always counted in full, since which kind the game takes depends on all of them.
        public static BillStatus Of(Bill bill, Building workbench, IBillGiver giver, BillInputs inputs, bool detailed)
        {
            BillStatus status = new BillStatus();
            status.State = StateOf(bill, out status.Count);
            // Blockers need a workbench on a map; a bill read elsewhere gets its state alone.
            if (status.State != "active" || workbench == null || !workbench.Spawned)
            {
                return status;
            }
            Map map = workbench.Map;
            if (!giver.CurrentlyUsableForBills())
            {
                status.Blocking.Add(giver.UsableForBillsAfterFueling()
                    ? Blockers.Of("needsFuel", workbench.LabelCap + " needs fuel.", Dto.Field("workbenchId", ReadUtil.ThingId(workbench)))
                    : Blockers.Of("workbenchUnusable", workbench.LabelCap + " cannot be used now, for example without power.", Dto.Field("workbenchId", ReadUtil.ThingId(workbench))));
            }
            AddWorkerBlocker(status.Blocking, bill, workbench, map);
            status.Ingredients = Needs(bill, workbench, inputs ?? new BillInputs(map), detailed);
            List<IngredientNeed> missing = status.Ingredients.Where(need => !need.Enough).ToList();
            if (missing.Count > 0)
            {
                status.Blocking.Add(Blockers.Of("missingIngredients",
                    "Not enough usable " + string.Join(" or ", missing.Select(need => need.Label).ToArray()) + " for one " + bill.LabelCap + (missing.Any(need => need.SingleKind) ? ", in one kind each" : "") + ".",
                    Dto.Field("billId", bill.GetUniqueLoadID())));
            }
            return status;
        }

        public static string StateOf(Bill bill, out int? count)
        {
            count = null;
            if (bill.suspended)
            {
                return "suspended";
            }
            Bill_Production production = bill as Bill_Production;
            if (production == null || production.repeatMode == BillRepeatModeDefOf.Forever)
            {
                return "active";
            }
            if (production.repeatMode == BillRepeatModeDefOf.RepeatCount)
            {
                return production.repeatCount > 0 ? "active" : "done";
            }
            if (production.repeatMode == BillRepeatModeDefOf.TargetCount)
            {
                int have = production.recipe.WorkerCounter.CountProducts(production);
                count = have;
                bool paused = production.paused || (production.pauseWhenSatisfied && have >= production.targetCount);
                if (have <= production.unpauseWhenYouHave || !production.pauseWhenSatisfied)
                {
                    paused = false;
                }
                if (paused)
                {
                    return "paused";
                }
                return have < production.targetCount ? "active" : "targetReached";
            }
            return "active";
        }

        // The work type is the one whose bill work giver serves this workbench, such as Cooking for
        // a campfire. A worker is a colonist or colony mechanoid who has it on and is someone the
        // bill allows (its skill range and pawn restriction, Bill.PawnAllowedToStartAnew) and the
        // recipe accepts (its skill requirements). FreeColonistsSpawned includes colony slaves: they
        // join the player's faction with no host faction, which is all that list asks.
        private static void AddWorkerBlocker(List<object> blockers, Bill bill, Building workbench, Map map)
        {
            WorkTypeDef work = DefDatabase<WorkGiverDef>.AllDefsListForReading
                .Where(giver => giver.fixedBillGiverDefs != null && giver.fixedBillGiverDefs.Contains(workbench.def) && typeof(WorkGiver_DoBill).IsAssignableFrom(giver.giverClass))
                .Select(giver => giver.workType)
                .FirstOrDefault(type => type != null);
            if (work == null)
            {
                return;
            }
            bool anyone = map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SpawnedColonyMechs)
                .Any(pawn => !pawn.Downed && pawn.workSettings != null && pawn.workSettings.WorkIsActive(work)
                    && bill.PawnAllowedToStartAnew(pawn) && bill.recipe.PawnSatisfiesSkillRequirements(pawn));
            if (anyone)
            {
                return;
            }
            SkillDef skill = bill.recipe.workSkill;
            blockers.Add(Blockers.Of("noWorker",
                "No colonist or mechanoid who may do " + bill.LabelCap + " has " + work.defName + " on. The bill's skill range and pawn restriction, and the recipe's skill requirements, all count.",
                Dto.Field("workType", work.defName),
                Dto.Field("skill", skill == null ? null : skill.defName),
                Dto.Field("skillRange", skill == null ? null : Dto.Obj(Dto.Field("min", bill.allowedSkillRange.min), Dto.Field("max", bill.allowedSkillRange.max))),
                Dto.Field("onlyPawnId", bill.PawnRestriction == null ? null : ReadUtil.ThingId(bill.PawnRestriction))));
        }

        private static List<IngredientNeed> Needs(Bill bill, Building workbench, BillInputs inputs, bool detailed)
        {
            IntVec3 root = workbench.def.hasInteractionCell ? workbench.InteractionCell : workbench.Position;
            float radiusSquared = bill.ingredientSearchRadius * bill.ingredientSearchRadius;
            // Reachability is checked only for things that pass the cheaper checks, and only when they
            // are counted.
            IEnumerable<Thing> nearby = inputs.Candidates
                .Where(thing => (thing.Position - workbench.Position).LengthHorizontalSquared < radiusSquared
                    && bill.IsFixedOrAllowedIngredient(thing)
                    && bill.recipe.ingredients.Any(ingredient => ingredient.filter.Allows(thing)));
            if (bill.recipe.allowMixingIngredients)
            {
                // A sum, so the order things are counted in does not matter.
                return Mixed(bill, nearby.Where(thing => inputs.Reachable(root, thing)), detailed);
            }
            // Nearest first, as the game takes them, because the nearest kinds are tried first.
            return SingleKind(bill, nearby.OrderBy(thing => (thing.Position - root).LengthHorizontalSquared).Where(thing => inputs.Reachable(root, thing)).ToList());
        }

        // TryFindBestBillIngredientsInSet_AllowMix: any mix of allowed kinds adds up.
        private static List<IngredientNeed> Mixed(Bill bill, IEnumerable<Thing> usable, bool detailed)
        {
            IngredientValueGetter value = bill.recipe.IngredientValueGetter;
            List<IngredientNeed> needs = new List<IngredientNeed>();
            foreach (IngredientCount ingredient in bill.recipe.ingredients)
            {
                IngredientNeed need = Need(bill, ingredient);
                foreach (Thing thing in usable.Where(thing => Fits(bill, ingredient, thing.def)))
                {
                    need.Available += thing.stackCount * value.ValuePerUnitOf(thing.def);
                    if (!detailed && need.Available + 0.0001f >= need.Needed)
                    {
                        break;
                    }
                }
                need.Enough = need.Available + 0.0001f >= need.Needed;
                needs.Add(need);
            }
            return needs;
        }

        // TryFindBestIngredientsInSet_NoMixHelper: each ingredient comes from one kind, nearest kinds
        // first, and what it takes is not there for the next ingredient. Available is the most of any
        // one allowed kind seen.
        private static List<IngredientNeed> SingleKind(Bill bill, List<Thing> usable)
        {
            IngredientValueGetter value = bill.recipe.IngredientValueGetter;
            List<ThingDef> kinds = usable.Select(thing => thing.def).Distinct().ToList();
            Dictionary<ThingDef, float> counts = usable.GroupBy(thing => thing.def).ToDictionary(group => group.Key, group => (float)group.Sum(thing => thing.stackCount));
            List<IngredientNeed> needs = new List<IngredientNeed>();
            foreach (IngredientCount ingredient in bill.recipe.ingredients)
            {
                IngredientNeed need = Need(bill, ingredient);
                need.SingleKind = true;
                foreach (ThingDef kind in kinds.Where(kind => Fits(bill, ingredient, kind)))
                {
                    float count = counts[kind];
                    need.Available = Math.Max(need.Available, count * value.ValuePerUnitOf(kind));
                    float required = ingredient.CountRequiredOfFor(kind, bill.recipe, bill);
                    if (bill.recipe.ignoreIngredientCountTakeEntireStacks ? count > 0 : count >= required)
                    {
                        counts[kind] = count - required;
                        need.Enough = true;
                        break;
                    }
                }
                needs.Add(need);
            }
            return needs;
        }

        private static bool Fits(Bill bill, IngredientCount ingredient, ThingDef kind)
        {
            return ingredient.filter.Allows(kind) && (ingredient.IsFixedIngredient || bill.ingredientFilter.Allows(kind));
        }

        private static IngredientNeed Need(Bill bill, IngredientCount ingredient)
        {
            return new IngredientNeed
            {
                Label = ingredient.filter.Summary,
                Unit = bill.recipe.IngredientValueGetter is IngredientValueGetter_Nutrition ? "nutrition" : "count",
                Needed = ingredient.GetBaseCount()
            };
        }

        public sealed class IngredientNeed
        {
            public string Label;
            public string Unit;
            public float Needed;
            public float Available;
            public bool Enough;
            public bool SingleKind;

            public object ToDto()
            {
                return Dto.Obj(
                    Dto.Field("ingredient", Label),
                    Dto.Field("unit", Unit),
                    Dto.Field("needed", Math.Round(Needed, 2)),
                    Dto.Field("available", Math.Round(Available, 2)),
                    Dto.Field("enough", Enough),
                    Dto.Field("oneKind", SingleKind ? (object)true : null));
            }
        }
    }
}
