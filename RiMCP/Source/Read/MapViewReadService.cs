using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    // A compact picture of part of a map: one character per cell, north at the top. Each cell shows
    // the most important thing on it: a pawn, then a building or blueprint, a tree, an item, a zone,
    // and the ground. Capital letters are colonists, named in the legend. Layers are extra grids over
    // the same cells, each with its own symbols. Unexplored cells reveal nothing.
    internal static class MapViewReadService
    {
        private const int DefaultSize = 32;
        private const int MaxSize = 40;

        private static readonly Dictionary<char, string> Meanings = new Dictionary<char, string>
        {
            { '@', "colonist past Z" },
            { 'h', "hostile" },
            { 'a', "animal" },
            { 'p', "other pawn, such as a prisoner, visitor, or mechanoid" },
            { '^', "natural rock" },
            { '#', "wall or impassable building" },
            { '+', "door" },
            { 'b', "other building" },
            { 'x', "blueprint or unfinished building" },
            { 't', "tree" },
            { '*', "item" },
            { '=', "stockpile zone" },
            { '%', "growing zone" },
            { 'w', "deep water" },
            { '~', "shallow water" },
            { ':', "rich soil" },
            { '.', "soil" },
            { ',', "poor soil" },
            { '_', "ground too poor to farm, or floor" }
        };

        private static readonly Layer[] Layers =
        {
            new Layer("roof", RoofSymbol, '-', new Dictionary<char, string>
            {
                { 'R', "thick rock roof" },
                { 'r', "thin rock roof" },
                { 'b', "built roof" },
                { '-', "no roof" }
            }),
            new Layer("designations", DesignationSymbol, '-', new Dictionary<char, string>
            {
                { 'd', "deconstruct" },
                { 'm', "mine" },
                { 'c', "cut plant" },
                { 'h', "harvest or chop wood" },
                { 'u', "haul" },
                { 'o', "other order" },
                { '-', "no order" }
            })
        };

        // When a cell has several orders, the first in this list is shown.
        private const string DesignationPrecedence = "dmchuo-";

        public static BridgeResponse GetMapView(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.FromMap(request);
            ReadRequest query = context.Request;
            Map map = context.Map;

            int width = Math.Min(Size(query, "width"), map.Size.x);
            int height = Math.Min(Size(query, "height"), map.Size.z);
            List<Layer> layers = new List<Layer>();
            foreach (string name in query.List("layers"))
            {
                Layer layer = Layers.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                if (layer == null)
                {
                    throw new BridgeException(400, "Unknown layer '" + name + "'. Supported layers: " + string.Join(", ", Layers.Select(candidate => candidate.Name).ToArray()) + ".");
                }
                if (!layers.Contains(layer))
                {
                    layers.Add(layer);
                }
            }

            // Without a position, centre the view on the colonists.
            IntVec3 centre = ColonistCentre(map);
            int x0 = query.OptionalInt("x") ?? centre.x - width / 2;
            int z0 = query.OptionalInt("z") ?? centre.z - height / 2;
            x0 = Math.Max(0, Math.Min(x0, map.Size.x - width));
            z0 = Math.Max(0, Math.Min(z0, map.Size.z - height));

            Dictionary<char, string> meanings = new Dictionary<char, string>();
            Dictionary<IntVec3, char> pawns = PawnSymbols(map, new CellRect(x0, z0, width, height), meanings);
            foreach (KeyValuePair<char, string> pair in Meanings)
            {
                meanings[pair.Key] = pair.Value;
            }
            char pawn;
            float farmable = GrowingSummary.ZoneMinFertility();
            Grid view = Grid.Draw(x0, z0, width, height, cell => cell.Fogged(map) ? '?' : pawns.TryGetValue(cell, out pawn) ? pawn : CellSymbol(cell, map, farmable));
            Dictionary<string, object> layerDtos = new Dictionary<string, object>();
            foreach (Layer layer in layers)
            {
                layerDtos[layer.Name] = LayerDto(layer, Grid.Draw(x0, z0, width, height, cell => cell.Fogged(map) ? '?' : layer.Symbol(cell, map)));
            }

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("origin", Dto.Obj(Dto.Field("x", x0), Dto.Field("z", z0))),
                Dto.Field("width", width),
                Dto.Field("height", height),
                Dto.Field("rows", view.Rows),
                Dto.Field("legend", Legend(meanings, view)),
                Dto.Field("layers", layerDtos.Count == 0 ? null : layerDtos)));
        }

        // A layer reports how many explored cells it marks; its rows are left out when it marks none,
        // so asking for a layer with nothing in view costs one line.
        private static object LayerDto(Layer layer, Grid grid)
        {
            int marked = grid.Counts.Where(pair => pair.Key != layer.Blank && pair.Key != '?').Sum(pair => pair.Value);
            return Dto.Obj(
                Dto.Field("markedCells", marked),
                Dto.Field("rows", marked == 0 ? null : grid.Rows),
                Dto.Field("legend", marked == 0 ? null : Legend(layer.Meanings, grid)));
        }

        private static Dictionary<string, object> Legend(Dictionary<char, string> meanings, Grid grid)
        {
            Dictionary<string, object> legend = new Dictionary<string, object>();
            if (grid.Counts.ContainsKey('?'))
            {
                legend["?"] = "unexplored";
            }
            foreach (KeyValuePair<char, string> pair in meanings)
            {
                if (grid.Counts.ContainsKey(pair.Key))
                {
                    legend[pair.Key.ToString()] = pair.Value;
                }
            }
            return legend;
        }

        // Colonists get letters by when they arrived (A is the earliest ThingID), so a colonist keeps
        // its letter from one view to the next; the legend gives each one's name, id, and job. A
        // colonist sharing a cell with another pawn wins the cell.
        private static Dictionary<IntVec3, char> PawnSymbols(Map map, CellRect view, Dictionary<char, string> meanings)
        {
            Dictionary<IntVec3, char> symbols = new Dictionary<IntVec3, char>();
            List<Pawn> colonists = map.mapPawns.AllPawnsSpawned
                .Where(pawn => pawn.Faction == Faction.OfPlayer && pawn.RaceProps.Humanlike)
                .OrderBy(pawn => pawn.thingIDNumber)
                .ToList();
            for (int i = 0; i < colonists.Count; i++)
            {
                Pawn colonist = colonists[i];
                if (!view.Contains(colonist.Position) || colonist.Position.Fogged(map))
                {
                    continue;
                }
                char letter = i < 26 ? (char)('A' + i) : '@';
                symbols[colonist.Position] = letter;
                if (letter != '@')
                {
                    meanings[letter] = Describe(colonist);
                }
            }
            foreach (Pawn other in map.mapPawns.AllPawnsSpawned)
            {
                if (colonists.Contains(other) || !view.Contains(other.Position) || other.Position.Fogged(map) || symbols.ContainsKey(other.Position))
                {
                    continue;
                }
                symbols[other.Position] = other.HostileTo(Faction.OfPlayer) ? 'h' : other.RaceProps.Animal ? 'a' : 'p';
            }
            return symbols;
        }

        private static string Describe(Pawn colonist)
        {
            string job = null;
            try
            {
                job = colonist.CurJob == null ? null : colonist.CurJob.GetReport(colonist);
            }
            catch
            {
            }
            string state = colonist.Downed ? "downed" : colonist.Drafted ? "drafted" : job;
            return ReadUtil.PawnName(colonist) + " (" + ReadUtil.ThingId(colonist) + ")" + (string.IsNullOrWhiteSpace(state) ? "" : ": " + state.StripTags());
        }

        // Ground below farmable fertility, such as sand, cannot hold a growing zone, so it is not soil.
        private static char CellSymbol(IntVec3 cell, Map map, float farmable)
        {
            Building edifice = cell.GetEdifice(map);
            if (edifice != null)
            {
                if (edifice.def.building != null && edifice.def.building.isNaturalRock)
                {
                    return '^';
                }
                if (edifice is Frame)
                {
                    return 'x';
                }
                if (edifice is Building_Door)
                {
                    return '+';
                }
                return edifice.def.passability == Traversability.Impassable ? '#' : 'b';
            }
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Blueprint || things[i] is Frame)
                {
                    return 'x';
                }
            }
            Plant plant = cell.GetPlant(map);
            if (plant != null && plant.def.plant != null && plant.def.plant.IsTree)
            {
                return 't';
            }
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i].def.EverHaulable)
                {
                    return '*';
                }
                if (things[i].def.category == ThingCategory.Building)
                {
                    return 'b';
                }
            }
            Zone zone = map.zoneManager.ZoneAt(cell);
            if (zone is Zone_Stockpile)
            {
                return '=';
            }
            if (zone is Zone_Growing)
            {
                return '%';
            }
            TerrainDef terrain = cell.GetTerrain(map);
            if (terrain.IsWater)
            {
                return terrain.passability == Traversability.Impassable ? 'w' : '~';
            }
            float fertility = cell.GetFertility(map);
            return fertility >= 1.4f ? ':' : fertility >= 0.7f ? '.' : fertility >= farmable ? ',' : '_';
        }

        // Orders on the cell or on things in it: AllDesignationsAt covers both.
        private static char DesignationSymbol(IntVec3 cell, Map map)
        {
            char shown = '-';
            foreach (Designation designation in map.designationManager.AllDesignationsAt(cell))
            {
                char symbol = DesignationChar(designation.def);
                if (DesignationPrecedence.IndexOf(symbol) < DesignationPrecedence.IndexOf(shown))
                {
                    shown = symbol;
                }
            }
            return shown;
        }

        private static char DesignationChar(DesignationDef def)
        {
            if (def == DesignationDefOf.Deconstruct || def == DesignationDefOf.Uninstall)
            {
                return 'd';
            }
            if (def == DesignationDefOf.Mine || def == DesignationDefOf.MineVein)
            {
                return 'm';
            }
            if (def == DesignationDefOf.CutPlant)
            {
                return 'c';
            }
            if (def == DesignationDefOf.HarvestPlant)
            {
                return 'h';
            }
            return def == DesignationDefOf.Haul ? 'u' : 'o';
        }

        private static char RoofSymbol(IntVec3 cell, Map map)
        {
            RoofDef roof = map.roofGrid.RoofAt(cell);
            return roof == null ? '-' : roof.isThickRoof ? 'R' : roof.isNatural ? 'r' : 'b';
        }

        private static int Size(ReadRequest query, string field)
        {
            int size = query.OptionalInt(field) ?? DefaultSize;
            if (size < 1 || size > MaxSize)
            {
                throw new BridgeException(400, "Field '" + field + "' must be 1 to " + MaxSize + ".");
            }
            return size;
        }

        private static IntVec3 ColonistCentre(Map map)
        {
            List<Pawn> colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            if (colonists.Count == 0)
            {
                return map.Center;
            }
            return new IntVec3(
                (int)Math.Round(colonists.Average(pawn => pawn.Position.x)),
                0,
                (int)Math.Round(colonists.Average(pawn => pawn.Position.z)));
        }

        // An extra grid over the view's cells. Blank is the symbol for a cell the layer does not mark.
        private sealed class Layer
        {
            public readonly string Name;
            public readonly Func<IntVec3, Map, char> Symbol;
            public readonly char Blank;
            public readonly Dictionary<char, string> Meanings;

            public Layer(string name, Func<IntVec3, Map, char> symbol, char blank, Dictionary<char, string> meanings)
            {
                Name = name;
                Symbol = symbol;
                Blank = blank;
                Meanings = meanings;
            }
        }

        // Rows of symbols, north first, with how often each symbol appears.
        private sealed class Grid
        {
            public string[] Rows;
            public readonly Dictionary<char, int> Counts = new Dictionary<char, int>();

            public static Grid Draw(int x0, int z0, int width, int height, Func<IntVec3, char> symbol)
            {
                Grid grid = new Grid { Rows = new string[height] };
                for (int z = z0 + height - 1; z >= z0; z--)
                {
                    StringBuilder row = new StringBuilder(width);
                    for (int x = x0; x < x0 + width; x++)
                    {
                        char cell = symbol(new IntVec3(x, 0, z));
                        row.Append(cell);
                        int count;
                        grid.Counts.TryGetValue(cell, out count);
                        grid.Counts[cell] = count + 1;
                    }
                    grid.Rows[z0 + height - 1 - z] = row.ToString();
                }
                return grid;
            }
        }
    }
}
