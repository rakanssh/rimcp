using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // One building to place: its def, material and rotation, checked and placed with the game's
    // build tool (Designator_Build). The tool applies the architect menu's rules (research, tech
    // level, difficulty, prerequisite buildings) and GenConstruct.CanPlaceBlueprintAt for the spot,
    // which never places into unexplored cells. A position is the building's position cell as the
    // game defines it; footprintOffset gives the cells it covers from there.
    internal sealed class BuildPlacement
    {
        private static readonly string[] RotationNames = { "north", "east", "south", "west" };

        public readonly ThingDef Def;
        public readonly ThingDef Stuff;
        public readonly Rot4 Rotation;
        private readonly Tool tool;

        private BuildPlacement(ThingDef def, ThingDef stuff, Rot4 rotation)
        {
            Def = def;
            Rotation = rotation;
            tool = new Tool(def, stuff, rotation);
            // Without a chosen material the tool picks the game's default, preferring one the
            // colony has enough of.
            Stuff = def.MadeFromStuff ? tool.StuffDef : null;
        }

        // field prefixes error messages, such as "placements[2].".
        public static BuildPlacement Resolve(string defName, string stuffName, string rotation, string field)
        {
            if (string.IsNullOrWhiteSpace(defName))
            {
                throw new CommandException(400, "Missing required field '" + field + "def'.");
            }
            ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName.Trim());
            if (def == null || !def.BuildableByPlayer)
            {
                throw new CommandException(400, "'" + defName + "' in " + field + "def is not a building the player can build. Use a defName from search_defs.");
            }

            ThingDef stuff = null;
            if (!string.IsNullOrWhiteSpace(stuffName))
            {
                if (!def.MadeFromStuff)
                {
                    throw new CommandException(400, def.LabelCap + " is not made from a material; leave out " + field + "stuff.");
                }
                List<ThingDef> allowed = GenStuff.AllowedStuffsFor(def).ToList();
                stuff = allowed.FirstOrDefault(candidate => string.Equals(candidate.defName, stuffName.Trim(), StringComparison.OrdinalIgnoreCase));
                if (stuff == null)
                {
                    throw new CommandException(400, def.LabelCap + " cannot be made from '" + stuffName + "'. Materials: " + string.Join(", ", allowed.Select(candidate => candidate.defName).ToArray()) + ".");
                }
            }

            Rot4 rot = def.defaultPlacingRot;
            if (!string.IsNullOrWhiteSpace(rotation))
            {
                Rot4? parsed = ParseRotation(rotation);
                if (!parsed.HasValue)
                {
                    throw new CommandException(400, "Field '" + field + "rotation' must be north, east, south, or west.");
                }
                if (!def.rotatable && parsed.Value != def.defaultPlacingRot)
                {
                    throw new CommandException(400, def.LabelCap + " cannot be rotated; leave out " + field + "rotation.");
                }
                rot = parsed.Value;
            }
            return new BuildPlacement(def, stuff, rot);
        }

        public static Rot4? ParseRotation(string text)
        {
            int index = Array.FindIndex(RotationNames, name => string.Equals(name, text == null ? null : text.Trim(), StringComparison.OrdinalIgnoreCase));
            return index < 0 ? (Rot4?)null : new Rot4(index);
        }

        public static string RotationName(Rot4 rotation)
        {
            return RotationNames[rotation.AsInt];
        }

        // Why the architect menu would not offer this building now; null when it would.
        public string Unavailable(Map map)
        {
            if (tool.Visible)
            {
                return null;
            }
            string[] research = (Def.researchPrerequisites ?? new List<ResearchProjectDef>())
                .Where(project => !project.IsFinished)
                .Select(project => project.LabelCap.ToString())
                .ToArray();
            if (research.Length > 0)
            {
                return Def.LabelCap + " needs research first: " + string.Join(", ", research) + ".";
            }
            string[] buildings = (Def.buildingPrerequisites ?? new List<ThingDef>())
                .Where(building => !map.listerBuildings.ColonistsHaveBuilding(building))
                .Select(building => building.label)
                .ToArray();
            if (buildings.Length > 0)
            {
                return Def.LabelCap + " needs a " + string.Join(" and a ", buildings) + " built first.";
            }
            if (!Find.Storyteller.difficulty.AllowedToBuild(Def))
            {
                return "The difficulty settings do not allow building " + Def.label + ".";
            }
            return "The architect menu does not offer " + Def.label + " now.";
        }

        // Why the building cannot go at this position; null when it can.
        public string CannotPlaceAt(IntVec3 cell)
        {
            AcceptanceReport report = tool.CanDesignateCell(cell);
            if (report.Accepted)
            {
                return null;
            }
            return string.IsNullOrWhiteSpace(report.Reason) ? "Cannot be placed here." : report.Reason.StripTags();
        }

        // A blueprint, frame or building of this def, material and rotation already at the position.
        public Thing Existing(IntVec3 cell, Map map)
        {
            foreach (Thing thing in cell.GetThingList(map))
            {
                bool sameDef = thing.def == Def || ((thing is Blueprint || thing is Frame) && thing.def.entityDefToBuild == Def);
                if (sameDef && thing.Position == cell && thing.Rotation == Rotation && MaterialOf(thing) == Stuff)
                {
                    return thing;
                }
            }
            return null;
        }

        // Places a blueprint, or the finished building when it takes no work, and returns it.
        public Thing Place(IntVec3 cell, Map map)
        {
            tool.DesignateSingleCell(cell);
            return Existing(cell, map);
        }

        public static string StateOf(Thing thing)
        {
            return thing is Blueprint ? "blueprint" : thing is Frame ? "frame" : "built";
        }

        public object Occupies(IntVec3 cell)
        {
            return Rect(GenAdj.OccupiedRect(cell, Rotation, Def.Size));
        }

        public List<ThingDefCountClass> Cost()
        {
            return Def.CostListAdjusted(Stuff, false);
        }

        public object Describe()
        {
            CellRect footprint = GenAdj.OccupiedRect(IntVec3.Zero, Rotation, Def.Size);
            return Dto.Obj(
                Dto.Field("def", ReadUtil.Def(Def)),
                Dto.Field("stuff", ReadUtil.Def(Stuff)),
                Dto.Field("rotation", RotationName(Rotation)),
                Dto.Field("footprintOffset", Rect(footprint)),
                Dto.Field("interactionCellOffset", Def.hasInteractionCell ? Offset(ThingUtility.InteractionCell(Def.interactionCellOffset, IntVec3.Zero, Rotation)) : null),
                Dto.Field("cost", Cost().Select(entry => (object)Dto.Obj(Dto.Field("defName", entry.thingDef.defName), Dto.Field("count", entry.count))).ToArray()),
                Dto.Field("workToBuild", Math.Round(Def.GetStatValueAbstract(StatDefOf.WorkToBuild, Stuff))));
        }

        // What would stop these buildings being built once placed: nobody able to build them, or
        // too little material that is explored and not forbidden. Accuracy: material already
        // promised to other blueprints and frames still counts as available.
        public static List<object> BuildBlockers(Map map, IEnumerable<BuildPlacement> placed)
        {
            List<object> blockers = new List<object>();
            List<BuildPlacement> list = placed.ToList();
            foreach (ThingDef def in list.Select(placement => placement.Def).Distinct())
            {
                AddWorkerBlocker(blockers, map, def, SkillDefOf.Construction, def.constructionSkillPrerequisite);
                AddWorkerBlocker(blockers, map, def, SkillDefOf.Artistic, def.artisticSkillPrerequisite);
            }
            Dictionary<ThingDef, int> needed = new Dictionary<ThingDef, int>();
            foreach (ThingDefCountClass entry in list.SelectMany(placement => placement.Cost()))
            {
                int count;
                needed.TryGetValue(entry.thingDef, out count);
                needed[entry.thingDef] = count + entry.count;
            }
            foreach (KeyValuePair<ThingDef, int> need in needed)
            {
                int available = 0, forbidden = 0;
                foreach (Thing thing in map.listerThings.ThingsOfDef(need.Key))
                {
                    if (!thing.Spawned || thing.Position.Fogged(map))
                    {
                        continue;
                    }
                    if (thing.IsForbidden(Faction.OfPlayer))
                    {
                        forbidden += thing.stackCount;
                    }
                    else
                    {
                        available += thing.stackCount;
                    }
                }
                if (available < need.Value)
                {
                    blockers.Add(Blockers.Of("missingMaterial",
                        "Needs " + need.Value + " " + need.Key.label + "; " + available + " available" + (forbidden > 0 ? " and " + forbidden + " forbidden" : "") + ".",
                        Dto.Field("defName", need.Key.defName),
                        Dto.Field("needed", need.Value),
                        Dto.Field("available", available),
                        Dto.Field("forbidden", forbidden > 0 ? (object)forbidden : null)));
                }
            }
            return blockers;
        }

        private static void AddWorkerBlocker(List<object> blockers, Map map, ThingDef def, SkillDef skill, int minSkill)
        {
            if ((minSkill > 0 || skill == SkillDefOf.Construction) && !Blockers.AnyWorker(map, WorkTypeDefOf.Construction, skill, minSkill))
            {
                blockers.Add(Blockers.Of("noConstructor",
                    "No colonist has Construction on" + (minSkill > 0 ? " with " + skill.label + " skill " + minSkill + " or more" : "") + ", so " + def.label + " will not be built.",
                    Dto.Field("defName", def.defName),
                    Dto.Field("skill", skill.defName),
                    Dto.Field("minSkill", minSkill)));
            }
        }

        private static ThingDef MaterialOf(Thing thing)
        {
            IConstructible constructible = thing as IConstructible;
            return constructible != null ? constructible.EntityToBuildStuff() : thing.Stuff;
        }

        private static object Rect(CellRect rect)
        {
            return Dto.Obj(
                Dto.Field("x", rect.minX),
                Dto.Field("z", rect.minZ),
                Dto.Field("width", rect.Width),
                Dto.Field("height", rect.Height));
        }

        private static object Offset(IntVec3 offset)
        {
            return Dto.Obj(Dto.Field("x", offset.x), Dto.Field("z", offset.z));
        }

        // The game's build tool at a chosen rotation, which otherwise only the player's rotate keys set.
        private sealed class Tool : Designator_Build
        {
            public Tool(ThingDef def, ThingDef stuff, Rot4 rotation) : base(def)
            {
                placingRot = rotation;
                if (stuff != null)
                {
                    SetStuffDef(stuff);
                }
            }
        }
    }
}
