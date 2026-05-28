using System;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    internal static class ProductionCommandService
    {
        public static BridgeResponse SetBill(BridgeRequest request, RouteMatch route)
        {
            string billId = route["id"];
            SetBillBody body = CommandUtil.ReadBody<SetBillBody>(request);
            string mode = NormalizeMode(body.Mode);

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            ProductionReadService.BillRecord record = ProductionReadService.FindBill(context.Map, billId);
            if (record == null)
            {
                throw new CommandException(404, "Bill not found.");
            }

            if (mode == "delete")
            {
                RejectUpdateFieldsForDelete(body);
                object deletedBill = ProductionReadService.SerializeBill(record, ReadDetail.Full);
                record.Giver.BillStack.Delete(record.Bill);
                return CommandEnvelope.Ok(context, true, Dto.Obj(
                    Dto.Field("deleted", true),
                    Dto.Field("bill", deletedBill),
                    Dto.Field("workshop", WorkshopSummary(record.Workbench))));
            }

            if (!HasUpdateFields(body))
            {
                throw new CommandException(400, "Update mode requires at least one bill field to change.");
            }

            object previousBill = ProductionReadService.SerializeBill(record, ReadDetail.Full);
            BillSnapshot previous = BillSnapshot.From(record.Bill);
            ResolvedBillSettings settings = ResolveBillSettings(record.Bill, body, context.Map);
            ApplyBillSettings(record.Bill, settings);
            BillSnapshot current = BillSnapshot.From(record.Bill);
            bool changed = !previous.Equals(current) || settings.HasSpecificStockpileTarget;

            return CommandEnvelope.Ok(context, changed, Dto.Obj(
                Dto.Field("previousBill", previousBill),
                Dto.Field("bill", ProductionReadService.SerializeBill(record, ReadDetail.Full)),
                Dto.Field("workshop", WorkshopSummary(record.Workbench))));
        }

        public static BridgeResponse AddBillToWorkshop(BridgeRequest request, RouteMatch route)
        {
            string workshopId = route["workshopId"];
            AddBillToWorkshopBody body = CommandUtil.ReadBody<AddBillToWorkshopBody>(request);
            if (string.IsNullOrWhiteSpace(body.RecipeDefName))
            {
                throw new CommandException(400, "Missing required field 'recipeDefName'.");
            }

            CommandContext context = CommandUtil.ContextFor(body.MapId);
            CommandUtil.RequireMap(context);

            ProductionReadService.WorkshopRecord workshop = ProductionReadService.FindWorkshop(context.Map, workshopId);
            if (workshop == null)
            {
                throw new CommandException(404, "Workshop not found.");
            }

            RecipeDef recipe = DefDatabase<RecipeDef>.GetNamedSilentFail(body.RecipeDefName);
            if (recipe == null)
            {
                throw new CommandException(404, "Recipe not found.");
            }
            if (!RecipeAvailableAtWorkshop(workshop.Workbench.def, recipe))
            {
                throw new CommandException(409, "Recipe is not available at this workshop.");
            }

            Bill bill = BillUtility.MakeNewBill(recipe, null);
            ResolvedBillSettings settings = ResolveBillSettings(bill, body, context.Map);
            workshop.Giver.BillStack.AddBill(bill);
            ApplyBillSettings(bill, settings);

            ProductionReadService.BillRecord record = new ProductionReadService.BillRecord(workshop.Workbench, workshop.Giver, bill);
            return CommandEnvelope.Ok(context, true, Dto.Obj(
                Dto.Field("bill", ProductionReadService.SerializeBill(record, ReadDetail.Full)),
                Dto.Field("workshop", WorkshopSummary(workshop.Workbench))));
        }

        private static ResolvedBillSettings ResolveBillSettings(Bill bill, BillSettingsBody body, Map map)
        {
            ResolvedBillSettings settings = new ResolvedBillSettings();
            if (body.Suspended.HasValue)
            {
                settings.HasSuspended = true;
                settings.Suspended = body.Suspended.Value;
            }
            if (body.IngredientSearchRadius.HasValue)
            {
                float radius = body.IngredientSearchRadius.Value;
                if (radius < 0f || radius > Bill.MaxIngredientSearchRadius)
                {
                    throw new CommandException(400, "Ingredient search radius must be between 0 and " + Bill.MaxIngredientSearchRadius + ".");
                }
                settings.HasIngredientSearchRadius = true;
                settings.IngredientSearchRadius = radius;
            }
            if (body.AllowedSkillMin.HasValue || body.AllowedSkillMax.HasValue)
            {
                if (!body.AllowedSkillMin.HasValue || !body.AllowedSkillMax.HasValue)
                {
                    throw new CommandException(400, "Fields 'allowedSkillMin' and 'allowedSkillMax' must be supplied together.");
                }
                int min = body.AllowedSkillMin.Value;
                int max = body.AllowedSkillMax.Value;
                if (min < 0 || min > 20 || max < 0 || max > 20 || min > max)
                {
                    throw new CommandException(400, "Allowed skill range must be within 0..20 and min must be less than or equal to max.");
                }
                settings.HasAllowedSkillRange = true;
                settings.AllowedSkillMin = min;
                settings.AllowedSkillMax = max;
            }
            if (!string.IsNullOrWhiteSpace(body.StoreModeDefName))
            {
                settings.StoreMode = ResolveStoreMode(body.StoreModeDefName);
                settings.StoreZone = ResolveStoreZone(map, settings.StoreMode, body.StoreZoneId);
                settings.HasSpecificStockpileTarget = settings.StoreMode == BillStoreModeDefOf.SpecificStockpile;
            }
            else if (!string.IsNullOrWhiteSpace(body.StoreZoneId))
            {
                throw new CommandException(400, "Field 'storeZoneId' requires storeModeDefName SpecificStockpile.");
            }

            if (HasProductionFields(body))
            {
                if (!(bill is Bill_Production))
                {
                    throw new CommandException(409, "Production-only fields require a production bill.");
                }
                ResolveProductionSettings(body, settings);
            }
            return settings;
        }

        private static void ResolveProductionSettings(BillSettingsBody body, ResolvedBillSettings settings)
        {
            if (!string.IsNullOrWhiteSpace(body.RepeatModeDefName))
            {
                settings.RepeatMode = ResolveRepeatMode(body.RepeatModeDefName);
            }
            if (body.RepeatCount.HasValue)
            {
                settings.HasRepeatCount = true;
                settings.RepeatCount = RequirePositive(body.RepeatCount.Value, "repeatCount");
            }
            if (body.TargetCount.HasValue)
            {
                settings.HasTargetCount = true;
                settings.TargetCount = RequirePositive(body.TargetCount.Value, "targetCount");
            }
            if (body.PauseWhenSatisfied.HasValue)
            {
                settings.HasPauseWhenSatisfied = true;
                settings.PauseWhenSatisfied = body.PauseWhenSatisfied.Value;
            }
            if (body.UnpauseWhenYouHave.HasValue)
            {
                settings.HasUnpauseWhenYouHave = true;
                settings.UnpauseWhenYouHave = RequirePositive(body.UnpauseWhenYouHave.Value, "unpauseWhenYouHave");
            }
        }

        private static void ApplyBillSettings(Bill bill, ResolvedBillSettings settings)
        {
            if (settings.HasSuspended)
            {
                bill.suspended = settings.Suspended;
            }
            if (settings.HasIngredientSearchRadius)
            {
                bill.ingredientSearchRadius = settings.IngredientSearchRadius;
            }
            if (settings.HasAllowedSkillRange)
            {
                bill.allowedSkillRange = new IntRange(settings.AllowedSkillMin, settings.AllowedSkillMax);
            }
            if (settings.StoreMode != null)
            {
                bill.SetStoreMode(settings.StoreMode, settings.StoreZone);
            }

            Bill_Production production = bill as Bill_Production;
            if (production != null)
            {
                ApplyProductionSettings(production, settings);
            }
        }

        private static void ApplyProductionSettings(Bill_Production bill, ResolvedBillSettings settings)
        {
            if (settings.RepeatMode != null)
            {
                bill.repeatMode = settings.RepeatMode;
            }
            if (settings.HasRepeatCount)
            {
                bill.repeatCount = settings.RepeatCount;
            }
            if (settings.HasTargetCount)
            {
                bill.targetCount = settings.TargetCount;
            }
            if (settings.HasPauseWhenSatisfied)
            {
                bill.pauseWhenSatisfied = settings.PauseWhenSatisfied;
            }
            if (settings.HasUnpauseWhenYouHave)
            {
                bill.unpauseWhenYouHave = settings.UnpauseWhenYouHave;
            }
        }

        private static BillRepeatModeDef ResolveRepeatMode(string defName)
        {
            BillRepeatModeDef def = DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                throw new CommandException(404, "Bill repeat mode not found.");
            }
            return def;
        }

        private static BillStoreModeDef ResolveStoreMode(string defName)
        {
            BillStoreModeDef def = DefDatabase<BillStoreModeDef>.GetNamedSilentFail(defName);
            if (def == null)
            {
                throw new CommandException(404, "Bill store mode not found.");
            }
            return def;
        }

        private static ISlotGroup ResolveStoreZone(Map map, BillStoreModeDef storeMode, string storeZoneId)
        {
            if (storeMode == BillStoreModeDefOf.SpecificStockpile)
            {
                if (string.IsNullOrWhiteSpace(storeZoneId))
                {
                    throw new CommandException(400, "Field 'storeZoneId' is required when storeModeDefName is SpecificStockpile.");
                }

                Zone_Stockpile stockpile = ZoneReadService.FindStockpile(map, storeZoneId);
                if (stockpile == null)
                {
                    throw new CommandException(404, "Stockpile zone not found.");
                }
                ISlotGroupParent parent = stockpile as ISlotGroupParent;
                ISlotGroup slotGroup = parent == null ? null : parent.GetSlotGroup();
                if (slotGroup == null)
                {
                    throw new CommandException(409, "Stockpile zone has no slot group.");
                }
                return slotGroup;
            }

            if (!string.IsNullOrWhiteSpace(storeZoneId))
            {
                throw new CommandException(400, "Field 'storeZoneId' can only be used with storeModeDefName SpecificStockpile.");
            }
            return null;
        }

        private static int RequirePositive(int value, string fieldName)
        {
            if (value <= 0)
            {
                throw new CommandException(400, "Field '" + fieldName + "' must be positive.");
            }
            return value;
        }

        private static bool RecipeAvailableAtWorkshop(ThingDef workbenchDef, RecipeDef recipe)
        {
            foreach (RecipeDef available in ProductionReadService.AvailableRecipes(workbenchDef))
            {
                if (available == recipe)
                {
                    return true;
                }
            }
            return false;
        }

        private static string NormalizeMode(string mode)
        {
            if (string.IsNullOrWhiteSpace(mode))
            {
                throw new CommandException(400, "Missing required field 'mode'.");
            }
            string normalized = mode.Trim().ToLowerInvariant();
            if (normalized != "update" && normalized != "delete")
            {
                throw new CommandException(400, "Mode must be update or delete.");
            }
            return normalized;
        }

        private static bool HasUpdateFields(BillSettingsBody body)
        {
            return body.Suspended.HasValue ||
                   !string.IsNullOrWhiteSpace(body.RepeatModeDefName) ||
                   body.RepeatCount.HasValue ||
                   body.TargetCount.HasValue ||
                   body.PauseWhenSatisfied.HasValue ||
                   body.UnpauseWhenYouHave.HasValue ||
                   body.IngredientSearchRadius.HasValue ||
                   body.AllowedSkillMin.HasValue ||
                   body.AllowedSkillMax.HasValue ||
                   !string.IsNullOrWhiteSpace(body.StoreModeDefName) ||
                   !string.IsNullOrWhiteSpace(body.StoreZoneId);
        }

        private static bool HasProductionFields(BillSettingsBody body)
        {
            return !string.IsNullOrWhiteSpace(body.RepeatModeDefName) ||
                   body.RepeatCount.HasValue ||
                   body.TargetCount.HasValue ||
                   body.PauseWhenSatisfied.HasValue ||
                   body.UnpauseWhenYouHave.HasValue;
        }

        private static void RejectUpdateFieldsForDelete(BillSettingsBody body)
        {
            if (HasUpdateFields(body))
            {
                throw new CommandException(400, "Delete mode cannot include update fields.");
            }
        }

        private static object WorkshopSummary(Building workbench)
        {
            return Dto.Obj(
                Dto.Field("ids", ReadUtil.ThingIds(workbench)),
                Dto.Field("def", ReadUtil.Def(workbench.def)),
                Dto.Field("label", workbench.LabelCap),
                Dto.Field("position", ReadUtil.Cell(workbench.Position)));
        }

        private sealed class BillSnapshot
        {
            private bool suspended;
            private float ingredientSearchRadius;
            private int allowedSkillMin;
            private int allowedSkillMax;
            private string storeMode;
            private string repeatMode;
            private int repeatCount;
            private int targetCount;
            private bool pauseWhenSatisfied;
            private int unpauseWhenYouHave;

            public static BillSnapshot From(Bill bill)
            {
                Bill_Production production = bill as Bill_Production;
                BillStoreModeDef storeMode = bill.GetStoreMode();
                return new BillSnapshot
                {
                    suspended = bill.suspended,
                    ingredientSearchRadius = bill.ingredientSearchRadius,
                    allowedSkillMin = bill.allowedSkillRange.min,
                    allowedSkillMax = bill.allowedSkillRange.max,
                    storeMode = storeMode == null ? null : storeMode.defName,
                    repeatMode = production == null || production.repeatMode == null ? null : production.repeatMode.defName,
                    repeatCount = production == null ? 0 : production.repeatCount,
                    targetCount = production == null ? 0 : production.targetCount,
                    pauseWhenSatisfied = production != null && production.pauseWhenSatisfied,
                    unpauseWhenYouHave = production == null ? 0 : production.unpauseWhenYouHave
                };
            }

            public bool Equals(BillSnapshot other)
            {
                return other != null &&
                       suspended == other.suspended &&
                       ingredientSearchRadius == other.ingredientSearchRadius &&
                       allowedSkillMin == other.allowedSkillMin &&
                       allowedSkillMax == other.allowedSkillMax &&
                       storeMode == other.storeMode &&
                       repeatMode == other.repeatMode &&
                       repeatCount == other.repeatCount &&
                       targetCount == other.targetCount &&
                       pauseWhenSatisfied == other.pauseWhenSatisfied &&
                       unpauseWhenYouHave == other.unpauseWhenYouHave;
            }
        }

        private sealed class ResolvedBillSettings
        {
            public bool HasSuspended;
            public bool Suspended;
            public bool HasIngredientSearchRadius;
            public float IngredientSearchRadius;
            public bool HasAllowedSkillRange;
            public int AllowedSkillMin;
            public int AllowedSkillMax;
            public BillStoreModeDef StoreMode;
            public ISlotGroup StoreZone;
            public bool HasSpecificStockpileTarget;
            public BillRepeatModeDef RepeatMode;
            public bool HasRepeatCount;
            public int RepeatCount;
            public bool HasTargetCount;
            public int TargetCount;
            public bool HasPauseWhenSatisfied;
            public bool PauseWhenSatisfied;
            public bool HasUnpauseWhenYouHave;
            public int UnpauseWhenYouHave;
        }

        [DataContract]
        private class BillSettingsBody
        {
            [DataMember(Name = "mapId")]
            public string MapId { get; set; }

            [DataMember(Name = "suspended")]
            public bool? Suspended { get; set; }

            [DataMember(Name = "repeatModeDefName")]
            public string RepeatModeDefName { get; set; }

            [DataMember(Name = "repeatCount")]
            public int? RepeatCount { get; set; }

            [DataMember(Name = "targetCount")]
            public int? TargetCount { get; set; }

            [DataMember(Name = "pauseWhenSatisfied")]
            public bool? PauseWhenSatisfied { get; set; }

            [DataMember(Name = "unpauseWhenYouHave")]
            public int? UnpauseWhenYouHave { get; set; }

            [DataMember(Name = "ingredientSearchRadius")]
            public float? IngredientSearchRadius { get; set; }

            [DataMember(Name = "allowedSkillMin")]
            public int? AllowedSkillMin { get; set; }

            [DataMember(Name = "allowedSkillMax")]
            public int? AllowedSkillMax { get; set; }

            [DataMember(Name = "storeModeDefName")]
            public string StoreModeDefName { get; set; }

            [DataMember(Name = "storeZoneId")]
            public string StoreZoneId { get; set; }
        }

        [DataContract]
        private sealed class SetBillBody : BillSettingsBody
        {
            [DataMember(Name = "mode")]
            public string Mode { get; set; }
        }

        [DataContract]
        private sealed class AddBillToWorkshopBody : BillSettingsBody
        {
            [DataMember(Name = "recipeDefName")]
            public string RecipeDefName { get; set; }
        }
    }
}
