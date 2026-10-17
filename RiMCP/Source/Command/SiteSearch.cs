using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // What a starting site must have. Temperatures are the tile's yearly average in °C, rainfall is
    // in mm a year, and the growing period is in days of the 60-day year, as the world inspect pane
    // shows. Only the surface layer is searched, by each tile's primary biome.
    internal sealed class SiteCriteria
    {
        public HashSet<string> Biomes;
        public HashSet<string> Hilliness;
        public bool? River;
        public bool? Road;
        public bool? Coastal;
        public float? MinTemperature;
        public float? MaxTemperature;
        public float? MinRainfall;
        public float? MaxRainfall;
        public int? MinGrowingDays;
        // In world tiles, to the nearest settlement of another faction whose relation to the player is
        // one of SettlementRelations (any, when null).
        public float? MinSettlementDistance;
        public float? MaxSettlementDistance;
        public HashSet<FactionRelationKind> SettlementRelations;
        // No settlement that would lose goodwill over the colony, as the game's settle warning checks.
        public bool AvoidEncroaching;
        public int Limit = 10;

        public bool NeedsSettlements
        {
            get { return MinSettlementDistance.HasValue || MaxSettlementDistance.HasValue; }
        }
    }

    // The cheap facts about a tile, read straight from its fields.
    internal sealed class SiteFacts
    {
        public int Tile;
        public string Biome;
        public string Hilliness;
        public float Temperature;
        public float Rainfall;
        public bool River;
        public bool Road;
        public bool WaterCovered;
    }

    // Finds starting sites in a bounded number of milliseconds per frame. Cheap filters on the tile's
    // own fields run first; coast, growing period, and settlement validity only for what is left.
    internal static class SiteSearch
    {
        public const double BudgetMsPerFrame = 4;
        private const float MinGrowTemperature = 6f;
        private const float MaxGrowTemperature = 42f;
        private const int DaysPerTwelfth = 5;

        public static bool PassesCheap(SiteCriteria criteria, SiteFacts facts)
        {
            return !facts.WaterCovered
                && (criteria.Biomes == null || criteria.Biomes.Contains(facts.Biome))
                && (criteria.Hilliness == null || criteria.Hilliness.Contains(facts.Hilliness))
                && (!criteria.MinTemperature.HasValue || facts.Temperature >= criteria.MinTemperature.Value)
                && (!criteria.MaxTemperature.HasValue || facts.Temperature <= criteria.MaxTemperature.Value)
                && (!criteria.MinRainfall.HasValue || facts.Rainfall >= criteria.MinRainfall.Value)
                && (!criteria.MaxRainfall.HasValue || facts.Rainfall <= criteria.MaxRainfall.Value)
                && (!criteria.River.HasValue || facts.River == criteria.River.Value)
                && (!criteria.Road.HasValue || facts.Road == criteria.Road.Value);
        }

        // The distance bounds for the nearest counted settlement, or null when there is none.
        public static bool PassesDistance(SiteCriteria criteria, float? nearest)
        {
            if (criteria.MaxSettlementDistance.HasValue && (!nearest.HasValue || nearest.Value > criteria.MaxSettlementDistance.Value))
            {
                return false;
            }
            return !criteria.MinSettlementDistance.HasValue || !nearest.HasValue || nearest.Value >= criteria.MinSettlementDistance.Value;
        }

        // Longer growing periods first, then temperatures nearer the middle of the range asked for
        // (20 °C when none was).
        public static double Score(SiteCriteria criteria, float temperature, int growingDays)
        {
            float middle = criteria.MinTemperature.HasValue && criteria.MaxTemperature.HasValue
                ? (criteria.MinTemperature.Value + criteria.MaxTemperature.Value) / 2f
                : criteria.MinTemperature ?? criteria.MaxTemperature ?? 20f;
            return growingDays * 10.0 - Math.Abs(temperature - middle);
        }

        // Identifies the generated world, so results from one world are not used on another.
        public static string WorldId()
        {
            return Find.World == null || Find.World.info == null ? null : Find.World.info.seedString + "#" + Find.WorldGrid.TilesCount + "#" + RuntimeHelpers.GetHashCode(Find.World);
        }

        public static int GrowingDays(PlanetTile tile)
        {
            return GenTemperature.TwelfthsInAverageTemperatureRange(tile, MinGrowTemperature, MaxGrowTemperature).Count * DaysPerTwelfth;
        }

        public static IEnumerable<string> Run(Operation operation, SiteCriteria criteria, string worldId)
        {
            int count = Find.WorldGrid.TilesCount;
            List<KeyValuePair<double, object>> best = new List<KeyValuePair<double, object>>();
            int matched = 0;
            int invalid = 0;
            List<Settlement> settlements = Find.WorldObjects.Settlements
                .Where(settlement => settlement.Faction != null && !settlement.Faction.IsPlayer && settlement.Tile.Layer == Find.WorldGrid.Surface
                    && (criteria.SettlementRelations == null || criteria.SettlementRelations.Contains(settlement.Faction.RelationKindWith(Faction.OfPlayer))))
                .ToList();
            List<Pair<Settlement, int>> encroached = new List<Pair<Settlement, int>>();
            Stopwatch frame = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                if (frame.Elapsed.TotalMilliseconds >= BudgetMsPerFrame)
                {
                    operation.Result = Progress(worldId, i, count, matched, invalid, best, false);
                    yield return "searching";
                    if (WorldId() != worldId)
                    {
                        throw new OperationEnded(Operation.Interrupted, "The world was generated again during the search, so its results no longer apply.");
                    }
                    frame.Restart();
                }
                SurfaceTile tile = Find.WorldGrid[i];
                SiteFacts facts = Facts(i, tile);
                if (!PassesCheap(criteria, facts))
                {
                    continue;
                }
                PlanetTile planet = new PlanetTile(i, Find.WorldGrid.Surface);
                bool coastal = tile.IsCoastal;
                if (criteria.Coastal.HasValue && coastal != criteria.Coastal.Value)
                {
                    continue;
                }
                int days = GrowingDays(planet);
                if (criteria.MinGrowingDays.HasValue && days < criteria.MinGrowingDays.Value)
                {
                    continue;
                }
                Settlement nearest = null;
                float? distance = null;
                foreach (Settlement settlement in settlements)
                {
                    float apart = Find.WorldGrid.ApproxDistanceInTiles(planet, settlement.Tile);
                    if (!distance.HasValue || apart < distance.Value)
                    {
                        distance = apart;
                        nearest = settlement;
                    }
                }
                if (!PassesDistance(criteria, distance))
                {
                    continue;
                }
                encroached.Clear();
                SettlementProximityGoodwillUtility.AppendProximityGoodwillOffsets(planet, encroached, false, true);
                if (criteria.AvoidEncroaching && encroached.Count > 0)
                {
                    continue;
                }
                matched++;
                if (!TileFinder.IsValidTileForNewSettlement(planet, null))
                {
                    invalid++;
                    continue;
                }
                double score = Score(criteria, facts.Temperature, days);
                if (best.Count < criteria.Limit || score > best[best.Count - 1].Key)
                {
                    best.Add(new KeyValuePair<double, object>(score, Describe(i, tile, facts, coastal, days, nearest, distance, encroached)));
                    best.Sort((a, b) => b.Key.CompareTo(a.Key));
                    if (best.Count > criteria.Limit)
                    {
                        best.RemoveAt(best.Count - 1);
                    }
                }
            }
            operation.Result = Progress(worldId, count, count, matched, invalid, best, true);
        }

        public static SiteFacts Facts(int id, SurfaceTile tile)
        {
            return new SiteFacts
            {
                Tile = id,
                Biome = tile.PrimaryBiome == null ? null : tile.PrimaryBiome.defName,
                Hilliness = tile.hilliness.ToString(),
                Temperature = tile.temperature,
                Rainfall = tile.rainfall,
                River = tile.Rivers != null && tile.Rivers.Count > 0,
                Road = tile.Roads != null && tile.Roads.Count > 0,
                WaterCovered = tile.WaterCovered
            };
        }

        public static object Describe(int id, SurfaceTile tile, SiteFacts facts, bool coastal, int growingDays, Settlement nearest, float? distance, List<Pair<Settlement, int>> encroached)
        {
            return Dto.Obj(
                Dto.Field("tileId", id),
                Dto.Field("biome", tile.PrimaryBiome == null ? null : ReadUtil.Def(tile.PrimaryBiome)),
                Dto.Field("hilliness", facts.Hilliness),
                Dto.Field("temperatureC", Math.Round(facts.Temperature, 1)),
                Dto.Field("rainfallMm", Math.Round(facts.Rainfall)),
                Dto.Field("growingDays", growingDays),
                Dto.Field("river", facts.River),
                Dto.Field("road", facts.Road),
                Dto.Field("coastal", coastal),
                Dto.Field("landmark", tile.Landmark == null ? null : tile.Landmark.name),
                Dto.Field("nearestSettlement", nearest == null ? null : Dto.Obj(
                    Dto.Field("name", nearest.LabelCap.ToString()),
                    Dto.Field("faction", nearest.Faction.Name),
                    Dto.Field("relation", nearest.Faction.RelationKindWith(Faction.OfPlayer).ToString()),
                    Dto.Field("distanceTiles", Math.Round(distance.Value, 1)))),
                Dto.Field("encroaches", encroached.Count == 0 ? null : encroached.Select(pair => (object)Dto.Obj(
                    Dto.Field("settlement", pair.First.LabelCap.ToString()),
                    Dto.Field("faction", pair.First.Faction.Name),
                    Dto.Field("goodwillPerQuadrum", pair.Second))).ToArray()));
        }

        private static object Progress(string worldId, int scanned, int total, int matched, int invalid, List<KeyValuePair<double, object>> best, bool done)
        {
            return Dto.Obj(
                Dto.Field("worldId", worldId),
                Dto.Field("scanned", scanned),
                Dto.Field("tiles", total),
                Dto.Field("matching", matched),
                Dto.Field("matchingButNotSettleable", invalid > 0 ? (object)invalid : null),
                Dto.Field("complete", done),
                Dto.Field("sites", best.Select(pair => pair.Value).ToArray()));
        }
    }
}
