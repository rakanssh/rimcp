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
    // Stockpiles, growing zones, storage and crop settings, and areas.
    internal static class ZoneCommandService
    {
        private static readonly string[] ZoneKinds = { "stockpile", "growing" };

        public static BridgeResponse SetZone(BridgeRequest request, RouteMatch route)
        {
            SetZoneBody body = CommandUtil.ReadBody<SetZoneBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireCurrentMap(context);
            Map map = context.Map;

            string kind = string.IsNullOrWhiteSpace(body.Kind) ? null : ZoneKinds.FirstOrDefault(candidate => string.Equals(candidate, body.Kind.Trim(), StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(body.Kind) && kind == null)
            {
                throw new CommandException(400, "Field 'kind' must be stockpile or growing.");
            }
            // Every rectangle is checked before anything changes, so a malformed request changes nothing.
            int addOutside, removeOutside;
            List<IntVec3> addCells = CommandCells.FromRects(body.Add, map, "add", out addOutside);
            List<IntVec3> removeCells = CommandCells.FromRects(body.Remove, map, "remove", out removeOutside);

            // Through a receipt, so a retried delete gets its first result back, not "Zone not found".
            if (body.Delete == true && !string.IsNullOrWhiteSpace(body.ZoneId))
            {
                if (body.Add != null || body.Remove != null)
                {
                    throw new CommandException(400, "Delete cannot be combined with add or remove.");
                }
                return CommandReceipts.Once(context, RequestKey.For("set_zone", requestId, map, "delete", body.ZoneId), () => DeleteZone(map, body.ZoneId, kind));
            }

            Zone zone = null;
            RequestKey key = null;
            if (!string.IsNullOrWhiteSpace(body.ZoneId))
            {
                zone = ZoneReadService.FindZone(map, body.ZoneId);
                if (zone == null)
                {
                    throw new CommandException(404, "Zone not found.");
                }
                if (KindOf(zone) == null)
                {
                    throw new CommandException(409, "Only stockpile and growing zones can be edited.");
                }
                if (kind != null && kind != KindOf(zone))
                {
                    throw new CommandException(400, "Zone " + body.ZoneId + " is a " + KindOf(zone) + " zone, not " + kind + ".");
                }
                kind = KindOf(zone);
                AgentLog.About("Change '" + zone.label + "'", AgentLog.Cells(map, zone.Cells));
            }
            else
            {
                if (body.Delete == true)
                {
                    throw new CommandException(400, "Field 'zoneId' is required to delete a zone.");
                }
                if (kind == null || body.Add == null || body.Add.Length == 0)
                {
                    throw new CommandException(400, "To create a zone, give kind (stockpile or growing) and add rectangles; to change one, give zoneId.");
                }
                if (body.Remove != null)
                {
                    throw new CommandException(400, "A new zone has no cells to remove.");
                }
                key = RequestKey.For("set_zone", requestId, map, kind, body.Label, body.Add);
                AgentLog.About("Create a " + kind + " zone", AgentLog.Cells(map, addCells));
            }

            return CommandReceipts.Once(context, key, () =>
            {
                string label = string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim();
                // Dialog_RenameZone rejects a name another zone already uses.
                if (label != null && map.zoneManager.AllZones.Any(other => other != zone && other.label == label))
                {
                    throw new CommandException(409, "Another zone is already called '" + label + "'.");
                }

                Designator_ZoneAdd designator = kind == "stockpile" ? (Designator_ZoneAdd)new Designator_ZoneAddStockpile_Resources() : new Designator_ZoneAdd_Growing();
                float farmable = kind == "growing" ? GrowingSummary.ZoneMinFertility() : 0f;
                CellOutcomes added = new CellOutcomes { Outside = addOutside };
                List<IntVec3> accepted = new List<IntVec3>();
                foreach (IntVec3 cell in addCells)
                {
                    Zone existing = map.zoneManager.ZoneAt(cell);
                    if (zone != null && existing == zone)
                    {
                        added.AlreadySatisfied++;
                        continue;
                    }
                    if (existing != null)
                    {
                        added.Reject(cell, "Part of another zone (" + ZoneReadService.ZoneId(map, existing) + ").");
                        continue;
                    }
                    AcceptanceReport report = designator.CanDesignateCell(cell);
                    if (!report.Accepted)
                    {
                        added.Reject(cell, string.IsNullOrWhiteSpace(report.Reason) ? CellRejection(kind, cell, map, farmable) : report.Reason.StripTags());
                        continue;
                    }
                    accepted.Add(cell);
                }

                bool created = false;
                if (zone == null && accepted.Count > 0)
                {
                    zone = kind == "stockpile" ? (Zone)new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager) : new Zone_Growing(map.zoneManager);
                    map.zoneManager.RegisterZone(zone);
                    zone.AddCell(accepted[0]);
                    accepted.RemoveAt(0);
                    added.Applied++;
                    created = true;
                }
                if (zone != null)
                {
                    GrowConnected(zone, accepted, map, added);
                }

                CellOutcomes removed = new CellOutcomes { Outside = removeOutside };
                int dropped = 0;
                if (zone != null && body.Remove != null)
                {
                    foreach (IntVec3 cell in removeCells)
                    {
                        if (map.zoneManager.ZoneAt(cell) == zone)
                        {
                            zone.RemoveCell(cell);
                            removed.Applied++;
                        }
                        else
                        {
                            removed.AlreadySatisfied++;
                        }
                    }
                    // Like the game's delete tool, a split zone keeps only the part connected to its first cell.
                    int before = zone.Cells.Count;
                    zone.CheckContiguous();
                    dropped = before - zone.Cells.Count;
                }

                bool labelChanged = false;
                if (zone != null && zone.Cells.Count > 0 && label != null && zone.label != label)
                {
                    zone.label = label;
                    labelChanged = true;
                }
                Zone_Stockpile stockpile = zone as Zone_Stockpile;
                if (stockpile != null && added.Applied > 0 && stockpile.slotGroup != null)
                {
                    stockpile.slotGroup.RemoveHaulDesignationOnStoredThings();
                }

                ReportCells(zone == null ? null : "'" + zone.label + "'", kind + " zone", created, added, removed, dropped, labelChanged,
                    zone == null ? AgentLog.Cells(map, addCells) : AgentLog.Cells(map, zone.Cells));
                Dictionary<string, object> data = Dto.Obj(
                    Dto.Field("zone", zone == null || zone.Cells.Count == 0 ? null : SerializeZone(map, zone)),
                    Dto.Field("created", created),
                    Dto.Field("added", body.Add == null ? null : added.ToDto()),
                    Dto.Field("removed", body.Remove == null ? null : removed.ToDto()),
                    Dto.Field("droppedDisconnected", dropped > 0 ? (object)dropped : null));
                return new CommandResult(created || added.Applied > 0 || removed.Applied > 0 || labelChanged, data);
            });
        }

        public static BridgeResponse SetStorage(BridgeRequest request, RouteMatch route)
        {
            SetStorageBody body = CommandUtil.ReadBody<SetStorageBody>(request);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Map map = context.Map;

            IStoreSettingsParent parent;
            object owner;
            if (!string.IsNullOrWhiteSpace(body.ZoneId) == !string.IsNullOrWhiteSpace(body.BuildingId))
            {
                throw new CommandException(400, "Give either zoneId (a stockpile) or buildingId (a storage building such as a shelf).");
            }
            if (!string.IsNullOrWhiteSpace(body.ZoneId))
            {
                Zone_Stockpile stockpile = ZoneReadService.FindStockpile(map, body.ZoneId);
                if (stockpile == null)
                {
                    throw new CommandException(404, "Stockpile not found.");
                }
                parent = stockpile;
                owner = SerializeZone(map, stockpile);
                AgentLog.About("Change the storage of '" + stockpile.label + "'", AgentLog.Cells(map, stockpile.Cells));
            }
            else
            {
                Building building = map.listerBuildings.allBuildingsColonist
                    .FirstOrDefault(candidate => candidate is IStoreSettingsParent && (candidate.ThingID == body.BuildingId || candidate.GetUniqueLoadID() == body.BuildingId));
                if (building == null)
                {
                    throw new CommandException(404, "Storage building not found.");
                }
                parent = (IStoreSettingsParent)building;
                owner = SummaryDto.ThingWithPosition(building);
                AgentLog.About("Change the storage of " + AgentLog.Name(building), building);
            }
            if (body.AllowAll == true && body.DisallowAll == true)
            {
                throw new CommandException(400, "allowAll and disallowAll cannot both be set.");
            }

            StorageSettings settings = parent.GetStoreSettings();
            StorageSettings parentSettings = parent.GetParentStoreSettings();
            ThingFilter limit = parentSettings == null ? null : parentSettings.filter;
            StoragePriority previous = settings.Priority;
            if (!string.IsNullOrWhiteSpace(body.Priority))
            {
                string name = new[] { "Low", "Normal", "Preferred", "Important", "Critical" }
                    .FirstOrDefault(candidate => string.Equals(candidate, body.Priority.Trim(), StringComparison.OrdinalIgnoreCase));
                if (name == null)
                {
                    throw new CommandException(400, "Field 'priority' must be Low, Normal, Preferred, Important, or Critical.");
                }
                settings.Priority = (StoragePriority)Enum.Parse(typeof(StoragePriority), name);
            }

            bool bulkChanged = false;
            if (body.AllowAll == true)
            {
                bulkChanged = ChangeItemsOnly(settings.filter, filter => filter.SetAllowAll(limit));
            }
            if (body.DisallowAll == true)
            {
                bulkChanged = ChangeItemsOnly(settings.filter, filter => filter.SetDisallowAll());
            }

            List<object> entries = new List<object>();
            bool entryChanged = false;
            foreach (string name in body.Allow ?? new string[0])
            {
                entryChanged |= ApplyEntry(settings.filter, limit, name, true, entries);
            }
            foreach (string name in body.Disallow ?? new string[0])
            {
                entryChanged |= ApplyEntry(settings.filter, limit, name, false, entries);
            }

            ReportStorage(previous != settings.Priority ? settings.Priority.ToString() : null, body.AllowAll == true && bulkChanged, body.DisallowAll == true && bulkChanged, entries);
            return CommandEnvelope.Ok(context, previous != settings.Priority || bulkChanged || entryChanged, Dto.Obj(
                Dto.Field("storage", owner),
                Dto.Field("previousPriority", previous.ToString()),
                Dto.Field("priority", settings.Priority.ToString()),
                Dto.Field("entries", entries.ToArray()),
                Dto.Field("accepts", StorageSummary.Accepts(parent))));
        }

        // Changes which items a storage filter accepts, leaving its special filters (such as rotten
        // food or tainted apparel) as they were: the game's category, allow-all, and disallow-all
        // setters also reset those, which would undo what a player chose. A special filter changes
        // only when an entry names it. Returns whether the accepted items changed.
        internal static bool ChangeItemsOnly(ThingFilter filter, Action<ThingFilter> change)
        {
            HashSet<ThingDef> before = new HashSet<ThingDef>(filter.AllowedThingDefs);
            List<SpecialThingFilterDef> special = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
            bool[] allowed = special.Select(filter.Allows).ToArray();
            change(filter);
            for (int i = 0; i < special.Count; i++)
            {
                if (filter.Allows(special[i]) != allowed[i])
                {
                    filter.SetAllow(special[i], allowed[i]);
                }
            }
            return !before.SetEquals(filter.AllowedThingDefs);
        }

        // A name is a thing category (such as FoodMeals), a special filter (such as AllowRotten), or
        // a single thing (such as MealSimple). An allowed category or thing also lists the special
        // filters that are off and still refuse some of it: the storage's own, which can be allowed
        // by name, and the building's, which cannot and so are not listed among the storage's.
        internal static bool ApplyEntry(ThingFilter filter, ThingFilter limit, string name, bool allow, List<object> entries)
        {
            string trimmed = name == null ? "" : name.Trim();
            ThingCategoryDef category = DefDatabase<ThingCategoryDef>.GetNamedSilentFail(trimmed);
            SpecialThingFilterDef special = category == null ? DefDatabase<SpecialThingFilterDef>.GetNamedSilentFail(trimmed) : null;
            ThingDef thing = category == null && special == null ? DefDatabase<ThingDef>.GetNamedSilentFail(trimmed) : null;
            string outcome;
            string reason = null;
            List<ThingDef> defs = null;
            if (category == null && special == null && thing == null)
            {
                outcome = "rejected";
                reason = "Unknown thing, category, or special filter. Use a ThingDef, ThingCategoryDef, or SpecialThingFilterDef defName from search_defs.";
            }
            else if (special != null)
            {
                if (!special.configurable)
                {
                    outcome = "rejected";
                    reason = "The game sets " + trimmed + " itself; it cannot be changed.";
                }
                else if (allow && limit != null && !limit.Allows(special))
                {
                    outcome = "rejected";
                    reason = "This storage refuses what " + trimmed + " covers, whatever its settings.";
                }
                else if (filter.Allows(special) == allow)
                {
                    outcome = "alreadySatisfied";
                }
                else
                {
                    filter.SetAllow(special, allow);
                    outcome = "applied";
                }
            }
            else
            {
                defs = (category != null ? category.DescendantThingDefs : new[] { thing })
                    .Where(def => limit == null || limit.Allows(def))
                    .ToList();
                if (defs.Count == 0)
                {
                    outcome = "rejected";
                    reason = "This storage cannot hold " + trimmed + ".";
                }
                else if (defs.All(def => filter.Allows(def) == allow))
                {
                    outcome = "alreadySatisfied";
                }
                else
                {
                    ChangeItemsOnly(filter, edited =>
                    {
                        if (category != null)
                        {
                            edited.SetAllow(category, allow);
                        }
                        else
                        {
                            edited.SetAllow(thing, allow);
                        }
                    });
                    outcome = "applied";
                }
            }
            bool accepted = allow && defs != null && outcome != "rejected";
            string[] fixedOff = accepted && limit != null ? StorageSummary.OffFor(limit, defs) : null;
            string[] ownOff = accepted ? StorageSummary.OffFor(filter, defs) : null;
            ownOff = ownOff == null || fixedOff == null ? ownOff : ownOff.Except(fixedOff).ToArray();
            entries.Add(Dto.Obj(
                Dto.Field("name", trimmed),
                Dto.Field("kind", category != null ? "category" : special != null ? "specialFilter" : thing != null ? "thing" : null),
                Dto.Field("allow", allow),
                Dto.Field("outcome", outcome),
                Dto.Field("reason", reason),
                Dto.Field("specialFiltersOff", ownOff == null || ownOff.Length == 0 ? null : ownOff),
                Dto.Field("fixedSpecialFiltersOff", fixedOff)));
            return outcome == "applied";
        }

        // A growing zone's crop and whether to sow and cut. The crop must be one the game's crop
        // menu (Command_SetPlantToGrow) offers for the zone; what then stops it growing comes back
        // as blockers.
        public static BridgeResponse SetGrowingZone(BridgeRequest request, RouteMatch route)
        {
            SetGrowingZoneBody body = CommandUtil.ReadBody<SetGrowingZoneBody>(request);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Map map = context.Map;
            if (string.IsNullOrWhiteSpace(body.ZoneId))
            {
                throw new CommandException(400, "Missing required field 'zoneId'.");
            }
            Zone_Growing zone = ZoneReadService.FindZone(map, body.ZoneId) as Zone_Growing;
            if (zone == null)
            {
                throw new CommandException(404, "Growing zone not found.");
            }
            AgentLog.About("Change '" + zone.label + "'", AgentLog.Cells(map, zone.Cells));

            ThingDef crop = null;
            if (!string.IsNullOrWhiteSpace(body.Plant))
            {
                crop = CommandUtil.ResolveDef<ThingDef>(body.Plant.Trim(), "plant", "Plant");
                if (crop.plant == null)
                {
                    throw new CommandException(400, crop.defName + " is not a plant.");
                }
                string refusal = CropRefusal(crop, zone, map);
                if (refusal != null)
                {
                    throw new CommandException(409, refusal);
                }
            }

            ThingDef previous = zone.GetPlantDefToGrow();
            bool changed = false;
            if (crop != null && crop != previous)
            {
                zone.SetPlantDefToGrow(crop);
                changed = true;
            }
            if (body.AllowSow.HasValue && body.AllowSow.Value != zone.allowSow)
            {
                zone.allowSow = body.AllowSow.Value;
                changed = true;
            }
            if (body.AllowCut.HasValue && body.AllowCut.Value != zone.allowCut)
            {
                zone.allowCut = body.AllowCut.Value;
                changed = true;
            }

            List<string> growing = new List<string>();
            if (crop != null && crop != previous)
            {
                growing.Add("grow " + crop.label);
            }
            if (body.AllowSow.HasValue)
            {
                growing.Add("sowing " + (zone.allowSow ? "on" : "off"));
            }
            if (body.AllowCut.HasValue)
            {
                growing.Add("cutting " + (zone.allowCut ? "on" : "off"));
            }
            if (changed)
            {
                AgentLog.Done("Set '" + zone.label + "' to " + string.Join(", ", growing.ToArray()), AgentLog.Cells(map, zone.Cells));
            }
            else
            {
                AgentLog.NoChange("'" + zone.label + "' was already set so", AgentLog.Cells(map, zone.Cells));
            }
            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("zone", SerializeZone(map, zone)),
                Dto.Field("previousPlant", ReadUtil.Def(previous)),
                Dto.Field("growing", GrowingSummary.Of(zone))));
        }

        // Why the crop menu would not offer this crop for the zone; null when it would.
        private static string CropRefusal(ThingDef crop, Zone_Growing zone, Map map)
        {
            if (!PlantUtility.CanSowOnGrower(crop, zone))
            {
                return crop.LabelCap + " cannot be sown in a growing zone.";
            }
            if (Command_SetPlantToGrow.IsPlantAvailable(crop, map))
            {
                return null;
            }
            string[] research = (crop.plant.sowResearchPrerequisites ?? new List<ResearchProjectDef>())
                .Where(project => !project.IsFinished)
                .Select(project => project.LabelCap.ToString())
                .ToArray();
            return research.Length > 0
                ? crop.LabelCap + " needs research first: " + string.Join(", ", research) + "."
                : crop.LabelCap + " cannot be sown on this map.";
        }

        // The Home area and allowed areas. Other areas, such as the roof areas, have rules of their
        // own that only their designators apply, so they are not editable here.
        public static BridgeResponse SetArea(BridgeRequest request, RouteMatch route)
        {
            SetAreaBody body = CommandUtil.ReadBody<SetAreaBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);
            Map map = context.Map;
            string label = string.IsNullOrWhiteSpace(body.Label) ? null : body.Label.Trim();

            // Everything is checked before anything changes, so a refused request changes nothing.
            int addOutside, removeOutside;
            List<IntVec3> addCells = CommandCells.FromRects(body.Add, map, "add", out addOutside);
            List<IntVec3> removeCells = CommandCells.FromRects(body.Remove, map, "remove", out removeOutside);
            // Through a receipt, so a retried delete gets its first result back, not "Area not found".
            if (body.Delete == true && !string.IsNullOrWhiteSpace(body.AreaId))
            {
                if (body.Add != null || body.Remove != null || label != null)
                {
                    throw new CommandException(400, "Delete cannot be combined with other changes.");
                }
                string deleteId = body.AreaId.Trim();
                return CommandReceipts.Once(context, RequestKey.For("set_area", requestId, map, "delete", deleteId), () => DeleteArea(map, deleteId));
            }

            Area area = null;
            RequestKey key = null;
            if (string.IsNullOrWhiteSpace(body.AreaId))
            {
                if (body.Delete == true || body.Remove != null)
                {
                    throw new CommandException(400, "Field 'areaId' is required to delete an area or remove cells.");
                }
                key = RequestKey.For("set_area", requestId, map, label, body.Add);
                AgentLog.About("Create an allowed area", AgentLog.Cells(map, addCells));
            }
            else
            {
                area = FindArea(map, body.AreaId.Trim());
                if (label != null && !(area is Area_Allowed))
                {
                    throw new CommandException(409, "Only allowed areas can be renamed.");
                }
                AgentLog.About("Change " + AreaName(area), AgentLog.Cells(map, area.ActiveCells));
            }
            return CommandReceipts.Once(context, key, () =>
            {
                if (area == null && !map.areaManager.CanMakeNewAllowed())
                {
                    throw new CommandException(409, "The map already has the maximum number of allowed areas.");
                }
                // Dialog_RenameArea refuses a name another area already uses.
                if (label != null && map.areaManager.AllAreas.Any(other => other != area && other.Label == label))
                {
                    throw new CommandException(409, "Another area is already called '" + label + "'.");
                }

                bool created = false;
                if (area == null)
                {
                    Area_Allowed allowed;
                    map.areaManager.TryMakeNewAllowed(out allowed);
                    area = allowed;
                    created = true;
                }
                bool labelChanged = false;
                if (label != null)
                {
                    labelChanged = area.Label != label;
                    area.RenamableLabel = label;
                }

                CellOutcomes added = SetCells(area, addCells, addOutside, true);
                CellOutcomes removed = SetCells(area, removeCells, removeOutside, false);
                ReportCells(AreaName(area), "allowed area", created, added, removed, 0, labelChanged && !created, AgentLog.Cells(map, area.ActiveCells));
                Dictionary<string, object> data = Dto.Obj(
                    Dto.Field("area", SerializeArea(map, area)),
                    Dto.Field("created", created),
                    Dto.Field("added", body.Add == null ? null : added.ToDto()),
                    Dto.Field("removed", body.Remove == null ? null : removed.ToDto()));
                return new CommandResult(created || labelChanged || added.Applied > 0 || removed.Applied > 0, data);
            });
        }

        private static Area FindArea(Map map, string areaId)
        {
            Area area = string.Equals(areaId, "home", StringComparison.OrdinalIgnoreCase)
                ? map.areaManager.Home
                : map.areaManager.AllAreas.FirstOrDefault(candidate => PawnReadService.AllowedAreaId(candidate, map) == areaId || candidate.ID.ToString() == areaId);
            if (area == null)
            {
                throw new CommandException(404, "Area not found. Use \"home\" or an area id from list_zones.");
            }
            if (!(area is Area_Home) && !(area is Area_Allowed))
            {
                throw new CommandException(409, area.Label + " cannot be edited here: set_area edits only the Home area and allowed areas.");
            }
            return area;
        }

        private static CommandResult DeleteArea(Map map, string areaId)
        {
            Area area = FindArea(map, areaId);
            AgentLog.About("Delete " + AreaName(area), AgentLog.Cells(map, area.ActiveCells));
            if (!(area is Area_Allowed))
            {
                throw new CommandException(409, "Only allowed areas can be deleted.");
            }
            object deleted = SerializeArea(map, area);
            List<AgentTarget> where = AgentLog.Cells(map, area.ActiveCells);
            string name = AreaName(area);
            area.Delete();
            AgentLog.Done("Deleted " + name, where);
            return new CommandResult(true, Dto.Obj(Dto.Field("deleted", true), Dto.Field("area", deleted)));
        }

        private static CommandResult DeleteZone(Map map, string zoneId, string kind)
        {
            Zone zone = ZoneReadService.FindZone(map, zoneId);
            if (zone == null)
            {
                throw new CommandException(404, "Zone not found.");
            }
            if (KindOf(zone) == null)
            {
                throw new CommandException(409, "Only stockpile and growing zones can be edited.");
            }
            if (kind != null && kind != KindOf(zone))
            {
                throw new CommandException(400, "Zone " + zoneId + " is a " + KindOf(zone) + " zone, not " + kind + ".");
            }
            List<AgentTarget> where = AgentLog.Cells(map, zone.Cells);
            AgentLog.About("Delete '" + zone.label + "'", where);
            object deleted = SerializeZone(map, zone);
            string name = zone.label;
            zone.Delete(false);
            AgentLog.Done("Deleted '" + name + "'", where);
            return new CommandResult(true, Dto.Obj(Dto.Field("deleted", true), Dto.Field("zone", deleted)));
        }

        private static string AreaName(Area area)
        {
            return area is Area_Home ? "the Home area" : "'" + area.Label + "'";
        }

        // name is how the zone or area is called, such as 'Stockpile 1' or the Home area; null when
        // it was not created.
        private static void ReportCells(string name, string kind, bool created, CellOutcomes added, CellOutcomes removed, int dropped, bool renamed, List<AgentTarget> where)
        {
            List<string> parts = new List<string>();
            if (added.Applied > 0)
            {
                parts.Add("added " + AgentLog.Count(added.Applied, "cell"));
            }
            if (removed.Applied > 0)
            {
                parts.Add("removed " + AgentLog.Count(removed.Applied, "cell"));
            }
            if (dropped > 0)
            {
                parts.Add("dropped " + AgentLog.Count(dropped, "cell") + " cut off from it");
            }
            if (renamed)
            {
                parts.Add("renamed it");
            }
            string done = created
                ? "Created " + kind + " " + name + " with " + AgentLog.Count(added.Applied, "cell")
                : "Changed " + name + ": " + string.Join(", ", parts.ToArray());
            string asked = name == null ? "Create a " + kind : "Change " + name;
            int applied = (created ? 1 : 0) + (renamed ? 1 : 0) + added.Applied + removed.Applied;
            AgentLog.Counted(done, asked, applied, added.AlreadySatisfied + removed.AlreadySatisfied, added.RejectedCount, added.Reasons, where);
        }

        private static void ReportStorage(string priority, bool allowedAll, bool disallowedAll, List<object> entries)
        {
            List<Dictionary<string, object>> results = entries.OfType<Dictionary<string, object>>().ToList();
            Func<string, bool, int> count = (outcome, allow) => results.Count(entry => (string)entry["outcome"] == outcome && (bool)entry["allow"] == allow);
            List<string> parts = new List<string>();
            if (priority != null)
            {
                parts.Add("priority " + priority);
            }
            if (allowedAll)
            {
                parts.Add("allowed everything");
            }
            if (disallowedAll)
            {
                parts.Add("disallowed everything");
            }
            if (count("applied", true) > 0)
            {
                parts.Add("allowed " + string.Join(", ", results.Where(entry => (string)entry["outcome"] == "applied" && (bool)entry["allow"]).Select(entry => (string)entry["name"]).ToArray()));
            }
            if (count("applied", false) > 0)
            {
                parts.Add("disallowed " + string.Join(", ", results.Where(entry => (string)entry["outcome"] == "applied" && !(bool)entry["allow"]).Select(entry => (string)entry["name"]).ToArray()));
            }
            int rejected = results.Count(entry => (string)entry["outcome"] == "rejected");
            string reasons = string.Join("; ", results.Where(entry => (string)entry["outcome"] == "rejected" && entry.ContainsKey("reason")).Select(entry => (string)entry["name"] + ": " + (string)entry["reason"]).Take(3).ToArray());
            AgentLog.Counted("Changed the storage: " + string.Join(", ", parts.ToArray()), "Change the storage", parts.Count, results.Count(entry => (string)entry["outcome"] == "alreadySatisfied"), rejected, reasons);
        }

        private static CellOutcomes SetCells(Area area, List<IntVec3> cells, int outside, bool value)
        {
            CellOutcomes outcomes = new CellOutcomes { Outside = outside };
            foreach (IntVec3 cell in cells)
            {
                if (area[cell] == value)
                {
                    outcomes.AlreadySatisfied++;
                }
                else
                {
                    area[cell] = value;
                    outcomes.Applied++;
                }
            }
            return outcomes;
        }

        // Like the game's zone tool, a zone only grows into cells that touch it.
        private static void GrowConnected(Zone zone, List<IntVec3> pending, Map map, CellOutcomes outcomes)
        {
            bool grew = true;
            while (grew && pending.Count > 0)
            {
                grew = false;
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    IntVec3 cell = pending[i];
                    if (GenAdj.CardinalDirections.Any(direction => (cell + direction).InBounds(map) && map.zoneManager.ZoneAt(cell + direction) == zone))
                    {
                        zone.AddCell(cell);
                        pending.RemoveAt(i);
                        outcomes.Applied++;
                        grew = true;
                    }
                }
            }
            foreach (IntVec3 cell in pending)
            {
                outcomes.Reject(cell, "Not connected to the zone; create a separate zone for it.");
            }
        }

        private static string KindOf(Zone zone)
        {
            return zone is Zone_Stockpile ? "stockpile" : zone is Zone_Growing ? "growing" : null;
        }

        // The zone designators refuse without a reason; the commonest cause for crops is fertility.
        private static string CellRejection(string kind, IntVec3 cell, Map map, float farmable)
        {
            float fertility = cell.GetFertility(map);
            if (kind == "growing" && fertility < farmable)
            {
                return "Not fertile enough for a growing zone: fertility " + fertility.ToStringPercent() + ", needs " + farmable.ToStringPercent() + ".";
            }
            return kind == "stockpile"
                ? "Cannot hold a stockpile here: unexplored, a wall or building, impassable terrain, or too close to the map edge."
                : "Cannot grow here: not fertile enough, unexplored, a building, or too close to the map edge.";
        }

        private static object SerializeZone(Map map, Zone zone)
        {
            return Dto.Obj(
                Dto.Field("id", ZoneReadService.ZoneId(map, zone)),
                Dto.Field("kind", KindOf(zone)),
                Dto.Field("label", zone.label),
                Dto.Field("cellCount", zone.Cells.Count));
        }

        private static object SerializeArea(Map map, Area area)
        {
            return Dto.Obj(
                Dto.Field("id", area is Area_Home ? "home" : PawnReadService.AllowedAreaId(area, map)),
                Dto.Field("label", area.Label),
                Dto.Field("cellCount", area.TrueCount));
        }

        [DataContract]
        private sealed class SetZoneBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "zoneId")]
            public string ZoneId { get; set; }

            [DataMember(Name = "kind")]
            public string Kind { get; set; }

            [DataMember(Name = "add")]
            public CellRectBody[] Add { get; set; }

            [DataMember(Name = "remove")]
            public CellRectBody[] Remove { get; set; }

            [DataMember(Name = "delete")]
            public bool? Delete { get; set; }

            [DataMember(Name = "label")]
            public string Label { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class SetGrowingZoneBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "zoneId")]
            public string ZoneId { get; set; }

            [DataMember(Name = "plant")]
            public string Plant { get; set; }

            [DataMember(Name = "allowSow")]
            public bool? AllowSow { get; set; }

            [DataMember(Name = "allowCut")]
            public bool? AllowCut { get; set; }
        }

        [DataContract]
        private sealed class SetStorageBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "zoneId")]
            public string ZoneId { get; set; }

            [DataMember(Name = "buildingId")]
            public string BuildingId { get; set; }

            [DataMember(Name = "priority")]
            public string Priority { get; set; }

            [DataMember(Name = "allowAll")]
            public bool? AllowAll { get; set; }

            [DataMember(Name = "disallowAll")]
            public bool? DisallowAll { get; set; }

            [DataMember(Name = "allow")]
            public string[] Allow { get; set; }

            [DataMember(Name = "disallow")]
            public string[] Disallow { get; set; }
        }

        [DataContract]
        private sealed class SetAreaBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "areaId")]
            public string AreaId { get; set; }

            [DataMember(Name = "label")]
            public string Label { get; set; }

            [DataMember(Name = "add")]
            public CellRectBody[] Add { get; set; }

            [DataMember(Name = "remove")]
            public CellRectBody[] Remove { get; set; }

            [DataMember(Name = "delete")]
            public bool? Delete { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
