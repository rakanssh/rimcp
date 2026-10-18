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
    // Buildings: checking where one fits, which changes nothing, and placing blueprints for
    // colonists to build. Both use the game's build tool through BuildPlacement, which works on the
    // map shown in game.
    internal static class BuildService
    {
        private const int MaxPlacements = 50;
        private const int MaxCheckSide = 20;
        private const int MaxListedFits = 50;
        private const int MaxListedPerReason = 10;

        public static BridgeResponse CheckPlacement(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;
            if (map != Find.CurrentMap)
            {
                throw new BridgeException(409, "check_placement uses the game's build tool, which works on the map shown in game (map " + Find.CurrentMap.uniqueID + "). Switch to map " + map.uniqueID + " in game first.");
            }
            BuildPlacement building = BuildPlacement.Resolve(query.Get("def"), query.Get("stuff"), query.Get("rotation"), "");
            int? x = query.OptionalInt("x");
            int? z = query.OptionalInt("z");
            if (!x.HasValue || !z.HasValue)
            {
                throw new BridgeException(400, "Give x and z: the position to check, or the south-west corner of an area of positions.");
            }
            int width = Side(query, "width");
            int height = Side(query, "height");

            // Positions nearest the middle of the area come first, so the first fit is the most central.
            IntVec3 centre = new IntVec3(x.Value + width / 2, 0, z.Value + height / 2);
            List<IntVec3> positions = new List<IntVec3>();
            int outside = 0;
            for (int px = x.Value; px < x.Value + width; px++)
            {
                for (int pz = z.Value; pz < z.Value + height; pz++)
                {
                    IntVec3 cell = new IntVec3(px, 0, pz);
                    if (cell.InBounds(map))
                    {
                        positions.Add(cell);
                    }
                    else
                    {
                        outside++;
                    }
                }
            }
            positions = positions.OrderBy(cell => cell.DistanceToSquared(centre)).ThenBy(cell => cell.x).ThenBy(cell => cell.z).ToList();

            string unavailable = building.Unavailable(map);
            List<IntVec3> fits = new List<IntVec3>();
            Dictionary<string, List<IntVec3>> rejected = new Dictionary<string, List<IntVec3>>();
            if (unavailable == null)
            {
                foreach (IntVec3 cell in positions)
                {
                    string reason = building.CannotPlaceAt(cell);
                    if (reason == null)
                    {
                        fits.Add(cell);
                        continue;
                    }
                    List<IntVec3> cells;
                    if (!rejected.TryGetValue(reason, out cells))
                    {
                        rejected[reason] = cells = new List<IntVec3>();
                    }
                    cells.Add(cell);
                }
            }

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("building", building.Describe()),
                Dto.Field("available", unavailable == null),
                Dto.Field("reason", unavailable),
                Dto.Field("fitsCount", fits.Count),
                Dto.Field("fits", fits.Take(MaxListedFits).Select(Position).ToArray()),
                Dto.Field("rejected", rejected
                    .OrderByDescending(pair => pair.Value.Count)
                    .Select(pair => (object)Dto.Obj(
                        Dto.Field("reason", pair.Key),
                        Dto.Field("count", pair.Value.Count),
                        Dto.Field("positions", pair.Value.Take(MaxListedPerReason).Select(Position).ToArray())))
                    .ToArray()),
                Dto.Field("outsideMap", outside > 0 ? (object)outside : null),
                Dto.Field("blockers", unavailable == null ? Blockers.ToArray(BuildPlacement.BuildBlockers(map, new[] { building })) : null)));
        }

        // Each placement is checked and placed in order, so a later one cannot overlap an earlier
        // one. A placement that is already there, as a blueprint, frame or building, is satisfied.
        public static BridgeResponse PlaceBlueprint(BridgeRequest request, RouteMatch route)
        {
            PlaceBlueprintBody body = CommandUtil.ReadBody<PlaceBlueprintBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            if (body.Placements == null || body.Placements.Length == 0)
            {
                throw new CommandException(400, "Missing required field 'placements'.");
            }
            if (body.Placements.Length > MaxPlacements)
            {
                throw new CommandException(400, "At most " + MaxPlacements + " placements per call.");
            }
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;

            // Every placement is read before any is placed, so a malformed one changes nothing.
            BuildPlacement[] buildings = new BuildPlacement[body.Placements.Length];
            for (int i = 0; i < buildings.Length; i++)
            {
                PlacementBody placement = body.Placements[i];
                string field = "placements[" + i + "].";
                if (placement == null || !placement.X.HasValue || !placement.Z.HasValue)
                {
                    throw new CommandException(400, "Each placement needs def, x, and z (" + field.TrimEnd('.') + ").");
                }
                buildings[i] = BuildPlacement.Resolve(placement.Def, placement.Stuff, placement.Rotation, field);
            }
            // The material counts only when given: the game's default depends on what the colony has.
            RequestKey key = RequestKey.For("place_blueprint", requestId, map, string.Join(";", buildings.Select((building, i) =>
                building.Def.defName + "," + (string.IsNullOrWhiteSpace(body.Placements[i].Stuff) ? "" : building.Stuff.defName) + "," + BuildPlacement.RotationName(building.Rotation) + "," + body.Placements[i].X + "," + body.Placements[i].Z).ToArray()));
            return CommandReceipts.Once(context, key, () => Place(buildings, body.Placements, map));
        }

        private static CommandResult Place(BuildPlacement[] buildings, PlacementBody[] placements, Map map)
        {
            List<object> results = new List<object>();
            List<BuildPlacement> placed = new List<BuildPlacement>();
            int applied = 0, satisfied = 0, rejectedCount = 0;
            for (int i = 0; i < buildings.Length; i++)
            {
                BuildPlacement building = buildings[i];
                IntVec3 cell = new IntVec3(placements[i].X.Value, 0, placements[i].Z.Value);
                string outcome;
                string reason = null;
                Thing thing = null;
                if (!cell.InBounds(map))
                {
                    outcome = "rejected";
                    reason = "Outside the map.";
                }
                else if (!cell.Fogged(map) && (thing = building.Existing(cell, map)) != null)
                {
                    outcome = "alreadySatisfied";
                }
                else if ((reason = building.Unavailable(map) ?? building.CannotPlaceAt(cell)) != null)
                {
                    outcome = "rejected";
                }
                else if ((thing = building.Place(cell, map)) != null)
                {
                    outcome = "applied";
                    // In god mode the build tool builds at once, and nothing blocks a finished building.
                    if (BuildPlacement.StateOf(thing) != "built")
                    {
                        placed.Add(building);
                    }
                }
                else
                {
                    outcome = "rejected";
                    reason = "The game did not place it.";
                }
                applied += outcome == "applied" ? 1 : 0;
                satisfied += outcome == "alreadySatisfied" ? 1 : 0;
                rejectedCount += outcome == "rejected" ? 1 : 0;
                results.Add(Dto.Obj(
                    Dto.Field("def", building.Def.defName),
                    Dto.Field("stuff", building.Stuff == null ? null : building.Stuff.defName),
                    Dto.Field("x", cell.x),
                    Dto.Field("z", cell.z),
                    Dto.Field("rotation", BuildPlacement.RotationName(building.Rotation)),
                    Dto.Field("outcome", outcome),
                    Dto.Field("reason", reason),
                    Dto.Field("id", thing == null ? null : ReadUtil.ThingId(thing)),
                    Dto.Field("state", thing == null ? null : BuildPlacement.StateOf(thing)),
                    Dto.Field("occupies", outcome == "rejected" ? null : building.Occupies(cell))));
            }

            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("applied", applied),
                Dto.Field("alreadySatisfied", satisfied),
                Dto.Field("rejectedCount", rejectedCount),
                Dto.Field("results", results.ToArray()),
                Dto.Field("blockers", Blockers.ToArray(BuildPlacement.BuildBlockers(map, placed))));
            return new CommandResult(applied > 0, data);
        }

        private static int Side(ReadRequest query, string field)
        {
            int side = query.OptionalInt(field) ?? 1;
            if (side < 1 || side > MaxCheckSide)
            {
                throw new BridgeException(400, "Field '" + field + "' must be 1 to " + MaxCheckSide + ".");
            }
            return side;
        }

        private static object Position(IntVec3 cell)
        {
            return Dto.Obj(Dto.Field("x", cell.x), Dto.Field("z", cell.z));
        }

        [DataContract]
        private sealed class PlaceBlueprintBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "placements")]
            public PlacementBody[] Placements { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class PlacementBody
        {
            [DataMember(Name = "def")]
            public string Def { get; set; }

            [DataMember(Name = "stuff")]
            public string Stuff { get; set; }

            [DataMember(Name = "x")]
            public int? X { get; set; }

            [DataMember(Name = "z")]
            public int? Z { get; set; }

            [DataMember(Name = "rotation")]
            public string Rotation { get; set; }
        }
    }
}
