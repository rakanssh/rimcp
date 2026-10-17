using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RimWorld.Planet;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using Verse;

namespace RiMCP.Command
{
    // The world and starting site screens: generating the world, searching it, and choosing a site.
    internal static partial class SetupCommandService
    {
        private static readonly int[] Coverages = { 30, 50, 100 };

        public static BridgeResponse GenerateWorld(BridgeRequest request, RouteMatch route)
        {
            WorldBody body = CommandUtil.ReadBody<WorldBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "generate_world", requestId, () =>
            {
                Page_CreateWorldParams page = SetupController.Require<Page_CreateWorldParams>(SetupController.World);
                SetupController.RequireRevision(body.ExpectedRevision);
                if (body.Coverage.HasValue && !Coverages.Contains(body.Coverage.Value))
                {
                    throw new CommandException(400, "Field 'coverage' must be 30, 50, or 100 (percent of the planet).");
                }
                if (body.Pollution.HasValue && (!ModsConfig.BiotechActive || body.Pollution.Value < 0 || body.Pollution.Value > 100))
                {
                    throw new CommandException(400, ModsConfig.BiotechActive ? "Field 'pollution' must be 0 to 100 (percent)." : "Pollution needs Biotech.");
                }
                object rainfall = ParseEnum<OverallRainfall>(body.Rainfall, "rainfall");
                object temperature = ParseEnum<OverallTemperature>(body.Temperature, "temperature");
                object population = ParseEnum<OverallPopulation>(body.Population, "population");
                object landmarks = ParseEnum<LandmarkDensity>(body.LandmarkDensity, "landmarkDensity");
                int own = SetupController.Revision;
                return operation =>
                {
                    Action apply = () =>
                    {
                        if (!string.IsNullOrWhiteSpace(body.Seed))
                        {
                            SetupController.Set(page, "seedString", body.Seed.Trim());
                        }
                        if (body.Coverage.HasValue)
                        {
                            SetupController.Set(page, "planetCoverage", body.Coverage.Value / 100f);
                        }
                        if (rainfall != null)
                        {
                            SetupController.Set(page, "rainfall", rainfall);
                        }
                        if (temperature != null)
                        {
                            SetupController.Set(page, "temperature", temperature);
                        }
                        if (population != null)
                        {
                            SetupController.Set(page, "population", population);
                        }
                        if (landmarks != null)
                        {
                            SetupController.Set(page, "landmarkDensity", landmarks);
                        }
                        if (body.Pollution.HasValue)
                        {
                            page.pollution = body.Pollution.Value / 100f;
                        }
                    };
                    return Generate(operation, page, own, apply);
                };
            }, body.Seed, body.Coverage, body.Rainfall, body.Temperature, body.Population, body.LandmarkDensity, body.Pollution, body.ExpectedRevision);
        }

        // Choices are written once the operation runs, so a refused request changes nothing. The world
        // page starts generating from its own can-proceed check, then opens the next screen.
        private static IEnumerable<string> Generate(Operation operation, Page_CreateWorldParams page, int own, Action apply)
        {
            SetupController.Continue(operation, own);
            apply();
            SetupController.Changed();
            SetupController.CanNext(page);
            if (!LongEventHandler.AnyEventNowOrWaiting)
            {
                throw new OperationEnded(Operation.Failed, "The world screen did not start generating.");
            }
            operation.InNativeStep = true;
            operation.ReplacingGame = true;
            while (LongEventHandler.AnyEventNowOrWaiting)
            {
                yield return "generatingWorld";
            }
            operation.InNativeStep = false;
            operation.ReplacingGame = false;
            // When generation finishes, the game opens the next screen and closes this one.
            for (int frame = 0; frame < 60 && SetupController.OpenPage == page; frame++)
            {
                yield return "generatingWorld";
            }
            if (SetupController.OpenPage == page || SetupController.OpenPage == null || Find.World == null)
            {
                throw new OperationEnded(Operation.Failed, "World generation did not finish; get_game_log may say why.");
            }
            SetupController.Changed();
            operation.Result = Dto.Obj(
                Dto.Field("seed", Find.World.info.seedString),
                Dto.Field("coverage", Math.Round(Find.World.info.planetCoverage * 100)),
                Dto.Field("tiles", Find.WorldGrid.TilesCount),
                Dto.Field("setup", State()));
        }

        public static BridgeResponse FindStartingSites(BridgeRequest request, RouteMatch route)
        {
            SitesBody body = CommandUtil.ReadBody<SitesBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "find_starting_sites", ToolGroups.Observe, requestId, () =>
            {
                if (Find.World == null || Current.ProgramState != ProgramState.Entry || !(SetupController.OpenPage is Page_SelectStartingSite))
                {
                    throw new CommandException(409, "Site search needs the starting site screen of a new game: generate the world first.");
                }
                SiteCriteria criteria = new SiteCriteria
                {
                    Biomes = Defs<BiomeDef>(body.Biomes, "biomes"),
                    Hilliness = body.Hilliness == null ? null : new HashSet<string>(body.Hilliness.Select(name => RequiredEnum<Hilliness>(name, "hilliness").ToString())),
                    River = body.River,
                    Road = body.Road,
                    Coastal = body.Coastal,
                    MinTemperature = body.MinTemperature,
                    MaxTemperature = body.MaxTemperature,
                    MinRainfall = body.MinRainfall,
                    MaxRainfall = body.MaxRainfall,
                    MinGrowingDays = body.MinGrowingDays,
                    MinSettlementDistance = body.MinSettlementDistance,
                    MaxSettlementDistance = body.MaxSettlementDistance,
                    SettlementRelations = body.SettlementRelations == null ? null : new HashSet<FactionRelationKind>(body.SettlementRelations.Select(name => RequiredEnum<FactionRelationKind>(name, "settlementRelations"))),
                    AvoidEncroaching = body.AvoidEncroaching == true,
                    Limit = Math.Max(1, Math.Min(50, body.Limit ?? 10))
                };
                if (criteria.MinSettlementDistance < 0 || criteria.MaxSettlementDistance < 0 || criteria.MinSettlementDistance > criteria.MaxSettlementDistance)
                {
                    throw new CommandException(400, "Settlement distances are world tiles from 0, with minSettlementDistance no more than maxSettlementDistance.");
                }
                string worldId = SiteSearch.WorldId();
                return operation => SiteSearch.Run(operation, criteria, worldId);
            }, Join(body.Biomes), Join(body.Hilliness), body.River, body.Road, body.Coastal, body.MinTemperature, body.MaxTemperature, body.MinRainfall, body.MaxRainfall, body.MinGrowingDays, body.MinSettlementDistance, body.MaxSettlementDistance, Join(body.SettlementRelations), body.AvoidEncroaching, body.Limit, SiteSearch.WorldId());
        }

        public static BridgeResponse ChooseStartingSite(BridgeRequest request, RouteMatch route)
        {
            SiteBody body = CommandUtil.ReadBody<SiteBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "choose_starting_site", requestId, () =>
            {
                Page_SelectStartingSite page = SetupController.Require<Page_SelectStartingSite>(SetupController.Site);
                SetupController.RequireRevision(body.ExpectedRevision);
                if (page.next == null)
                {
                    throw new CommandException(409, "This scenario has no screens after the starting site, so choosing it starts the colony; choose the site in game.");
                }
                if (!body.TileId.HasValue || body.TileId.Value < 0 || body.TileId.Value >= Find.WorldGrid.TilesCount)
                {
                    throw new CommandException(400, "Field 'tileId' must be a tile of this world, 0 to " + (Find.WorldGrid.TilesCount - 1) + ".");
                }
                PlanetTile tile = new PlanetTile(body.TileId.Value, Find.WorldGrid.Surface);
                System.Text.StringBuilder reason = new System.Text.StringBuilder();
                if (!TileFinder.IsValidTileForNewSettlement(tile, reason))
                {
                    throw new CommandException(409, "The game does not allow settling tile " + body.TileId.Value + ": " + reason.ToString().StripTags());
                }
                if (body.MapSize.HasValue && !Dialog_AdvancedGameConfig.MapSizes.Contains(body.MapSize.Value))
                {
                    throw new CommandException(400, "Field 'mapSize' must be one of: " + string.Join(", ", Dialog_AdvancedGameConfig.MapSizes.Select(size => size.ToString()).ToArray()) + ".");
                }
                object season = ParseEnum<Season>(body.StartingSeason, "startingSeason");
                int own = SetupController.Revision;
                return operation =>
                {
                    Action apply = () =>
                    {
                        Find.WorldInterface.SelectedTile = tile;
                        if (body.MapSize.HasValue)
                        {
                            Find.GameInitData.mapSize = body.MapSize.Value;
                        }
                        if (season != null)
                        {
                            Find.GameInitData.startingSeason = (Season)season;
                        }
                    };
                    return Settle(operation, page, own, apply);
                };
            }, body.TileId, body.MapSize, body.StartingSeason, body.ExpectedRevision);
        }

        private static IEnumerable<string> Settle(Operation operation, Page_SelectStartingSite page, int own, Action apply)
        {
            SetupController.Continue(operation, own);
            apply();
            SetupController.Changed();
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The starting site screen did not accept the tile.");
            }
            List<Window> open = Find.WindowStack.Windows.ToList();
            SetupController.DoNext(page);
            if (SetupController.OpenPage == page)
            {
                // Settling near a faction asks first, as it does for a click.
                List<Window> asking = Find.WindowStack.Windows.Where(window => !open.Contains(window) && !(window is Page)).ToList();
                if (asking.Count == 0)
                {
                    throw new OperationEnded(Operation.Failed, "The starting site screen did not move on.");
                }
                foreach (string step in AwaitQuestion(operation, asking, "settleNearFaction", () => SetupController.OpenPage != page))
                {
                    yield return step;
                }
                if (SetupController.OpenPage == page)
                {
                    SetupController.Changed();
                    throw new OperationEnded(Operation.Declined, "Settling there was not confirmed.");
                }
            }
            if (SetupController.OpenPage == null)
            {
                throw new OperationEnded(Operation.Failed, "The starting site screen did not open the next setup screen.");
            }
            SetupController.Changed();
            operation.Result = State();
        }

        [DataContract]
        private sealed class WorldBody
        {
            [DataMember(Name = "seed")] public string Seed { get; set; }
            [DataMember(Name = "coverage")] public int? Coverage { get; set; }
            [DataMember(Name = "rainfall")] public string Rainfall { get; set; }
            [DataMember(Name = "temperature")] public string Temperature { get; set; }
            [DataMember(Name = "population")] public string Population { get; set; }
            [DataMember(Name = "landmarkDensity")] public string LandmarkDensity { get; set; }
            [DataMember(Name = "pollution")] public float? Pollution { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SitesBody
        {
            [DataMember(Name = "biomes")] public string[] Biomes { get; set; }
            [DataMember(Name = "hilliness")] public string[] Hilliness { get; set; }
            [DataMember(Name = "river")] public bool? River { get; set; }
            [DataMember(Name = "road")] public bool? Road { get; set; }
            [DataMember(Name = "coastal")] public bool? Coastal { get; set; }
            [DataMember(Name = "minTemperature")] public float? MinTemperature { get; set; }
            [DataMember(Name = "maxTemperature")] public float? MaxTemperature { get; set; }
            [DataMember(Name = "minRainfall")] public float? MinRainfall { get; set; }
            [DataMember(Name = "maxRainfall")] public float? MaxRainfall { get; set; }
            [DataMember(Name = "minGrowingDays")] public int? MinGrowingDays { get; set; }
            [DataMember(Name = "minSettlementDistance")] public float? MinSettlementDistance { get; set; }
            [DataMember(Name = "maxSettlementDistance")] public float? MaxSettlementDistance { get; set; }
            [DataMember(Name = "settlementRelations")] public string[] SettlementRelations { get; set; }
            [DataMember(Name = "avoidEncroaching")] public bool? AvoidEncroaching { get; set; }
            [DataMember(Name = "limit")] public int? Limit { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SiteBody
        {
            [DataMember(Name = "tileId")] public int? TileId { get; set; }
            [DataMember(Name = "mapSize")] public int? MapSize { get; set; }
            [DataMember(Name = "startingSeason")] public string StartingSeason { get; set; }
            [DataMember(Name = "expectedRevision")] public int? ExpectedRevision { get; set; }
            [DataMember(Name = "requestId")] public string RequestId { get; set; }
        }
    }
}
