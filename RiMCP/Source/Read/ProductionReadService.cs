using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ProductionReadService
    {
        public static BridgeResponse ListProduction(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<BillRecord> records = BillRecords(context.Map)
                .OrderBy(record => record.Workbench.def.defName)
                .ThenBy(record => record.Bill.Label);
            Page<BillRecord> page = new Page<BillRecord>(records, context.Request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("bills", page.Items.Select(record => SerializeBill(record, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse ListWorkshops(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<WorkshopRecord> records = WorkshopRecords(context.Map)
                .OrderBy(record => record.Workbench.def.defName)
                .ThenBy(record => record.Workbench.ThingID);
            Page<WorkshopRecord> page = new Page<WorkshopRecord>(records, context.Request);
            bool includeRecipes = context.Request.Wants("recipes");

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("workshops", page.Items.Select(record => SerializeWorkshop(record, context.Request.Detail, includeRecipes)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse GetWorkshop(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string id = route["id"];
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            WorkshopRecord record = WorkshopRecords(context.Map)
                .FirstOrDefault(item => item.Workbench.ThingID == id || item.Workbench.GetUniqueLoadID() == id);
            if (record == null)
            {
                return BridgeResponse.Error(404, "Workshop not found.");
            }
            return ReadEnvelope.Ok(context, SerializeWorkshop(record, ReadDetail.Full, true));
        }

        public static BridgeResponse GetBill(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string id = route["id"];
            if (context.Map == null)
            {
                return BridgeResponse.Error(409, "No active map is loaded.");
            }

            BillRecord record = BillRecords(context.Map).FirstOrDefault(item => item.Bill.GetUniqueLoadID() == id);
            if (record == null)
            {
                return BridgeResponse.Error(404, "Bill not found.");
            }
            return ReadEnvelope.Ok(context, SerializeBill(record, ReadDetail.Full));
        }

        private static IEnumerable<BillRecord> BillRecords(Map map)
        {
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                IBillGiver giver = building as IBillGiver;
                if (giver == null || giver.BillStack == null)
                {
                    continue;
                }
                foreach (Bill bill in giver.BillStack.Bills)
                {
                    yield return new BillRecord(building, giver, bill);
                }
            }
        }

        private static IEnumerable<WorkshopRecord> WorkshopRecords(Map map)
        {
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                IBillGiver giver = building as IBillGiver;
                if (giver == null || giver.BillStack == null)
                {
                    continue;
                }
                yield return new WorkshopRecord(building, giver);
            }
        }

        private static object SerializeWorkshop(WorkshopRecord record, ReadDetail detail, bool includeRecipes)
        {
            List<RecipeDef> recipes = AvailableRecipes(record.Workbench.def)
                .OrderBy(recipe => recipe.defName)
                .ToList();
            List<Bill> bills = record.Giver.BillStack == null
                ? new List<Bill>()
                : record.Giver.BillStack.Bills.ToList();

            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("ids", ReadUtil.ThingIds(record.Workbench)),
                Dto.Field("def", ReadUtil.Def(record.Workbench.def)),
                Dto.Field("label", record.Workbench.LabelCap),
                Dto.Field("position", ReadUtil.Cell(record.Workbench.Position)),
                Dto.Field("usableForBills", CurrentlyUsableForBills(record.Giver)),
                Dto.Field("usableAfterFueling", UsableForBillsAfterFueling(record.Giver)),
                Dto.Field("currentBillCount", bills.Count),
                Dto.Field("availableRecipeCount", recipes.Count));

            if (detail != ReadDetail.Summary)
            {
                dto["currentBills"] = bills
                    .Select(bill => SerializeBill(new BillRecord(record.Workbench, record.Giver, bill), detail))
                    .ToArray();
            }
            if (includeRecipes)
            {
                dto["availableRecipes"] = recipes
                    .Select(recipe => SerializeRecipe(recipe, detail))
                    .ToArray();
            }
            return dto;
        }

        private static object SerializeBill(BillRecord record, ReadDetail detail)
        {
            Bill_Production production = record.Bill as Bill_Production;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", record.Bill.GetUniqueLoadID()),
                Dto.Field("label", record.Bill.Label),
                Dto.Field("recipe", record.Bill.recipe == null ? null : Dto.Obj(
                    Dto.Field("defName", record.Bill.recipe.defName),
                    Dto.Field("label", ReadUtil.DefLabel(record.Bill.recipe)))),
                Dto.Field("suspended", record.Bill.suspended),
                Dto.Field("workbench", Dto.Obj(
                    Dto.Field("ids", ReadUtil.ThingIds(record.Workbench)),
                    Dto.Field("def", ReadUtil.Def(record.Workbench.def)),
                    Dto.Field("label", record.Workbench.LabelCap),
                    Dto.Field("position", ReadUtil.Cell(record.Workbench.Position)))),
                Dto.Field("repeatMode", production == null ? null : production.repeatMode.ToString()),
                Dto.Field("targetCount", production == null ? null : (object)production.targetCount));

            if (detail != ReadDetail.Summary)
            {
                BillStoreModeDef storeMode = record.Bill.GetStoreMode();
                dto["pauseWhenSatisfied"] = production == null ? null : (object)production.pauseWhenSatisfied;
                dto["unpauseWhenYouHave"] = production == null ? null : (object)production.unpauseWhenYouHave;
                dto["ingredientSearchRadius"] = record.Bill.ingredientSearchRadius;
                dto["storeMode"] = storeMode == null ? null : storeMode.defName;
                dto["allowedSkillRange"] = Dto.Obj(
                    Dto.Field("min", record.Bill.allowedSkillRange.min),
                    Dto.Field("max", record.Bill.allowedSkillRange.max));
                dto["products"] = SerializeRecipeProducts(record.Bill.recipe);
                dto["ingredients"] = SerializeRecipeIngredients(record.Bill.recipe);
            }
            return dto;
        }

        private static IEnumerable<RecipeDef> AvailableRecipes(ThingDef workbenchDef)
        {
            IEnumerable<RecipeDef> recipes = null;
            try
            {
                recipes = workbenchDef == null ? null : workbenchDef.AllRecipes;
            }
            catch
            {
            }
            if (recipes == null)
            {
                yield break;
            }

            foreach (RecipeDef recipe in recipes)
            {
                if (recipe == null)
                {
                    continue;
                }
                bool available = false;
                try
                {
                    available = recipe.AvailableNow;
                }
                catch
                {
                }
                if (available)
                {
                    yield return recipe;
                }
            }
        }

        private static object SerializeRecipe(RecipeDef recipe, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("defName", recipe.defName),
                Dto.Field("label", ReadUtil.DefLabel(recipe)),
                Dto.Field("workAmount", recipe.WorkAmountTotal(null)),
                Dto.Field("workSkill", recipe.workSkill == null ? null : recipe.workSkill.defName),
                Dto.Field("workSpeedStat", recipe.workSpeedStat == null ? null : recipe.workSpeedStat.defName),
                Dto.Field("products", SerializeRecipeProducts(recipe)));

            if (detail != ReadDetail.Summary)
            {
                dto["ingredients"] = SerializeRecipeIngredients(recipe);
            }
            return dto;
        }

        private static object SerializeRecipeProducts(RecipeDef recipe)
        {
            return recipe == null || recipe.products == null
                ? new object[0]
                : recipe.products.Select(product => Dto.Obj(
                    Dto.Field("defName", product.thingDef == null ? null : product.thingDef.defName),
                    Dto.Field("label", ReadUtil.DefLabel(product.thingDef)),
                    Dto.Field("count", product.count))).ToArray();
        }

        private static object SerializeRecipeIngredients(RecipeDef recipe)
        {
            return recipe == null || recipe.ingredients == null
                ? new object[0]
                : recipe.ingredients.Select(ingredient => Dto.Obj(
                    Dto.Field("count", IngredientCountValue(ingredient)),
                    Dto.Field("filterSummary", ingredient.filter == null ? null : ingredient.filter.Summary))).ToArray();
        }

        private static object CurrentlyUsableForBills(IBillGiver giver)
        {
            try
            {
                return giver.CurrentlyUsableForBills();
            }
            catch
            {
                return null;
            }
        }

        private static object UsableForBillsAfterFueling(IBillGiver giver)
        {
            try
            {
                return giver.UsableForBillsAfterFueling();
            }
            catch
            {
                return null;
            }
        }

        private static object IngredientCountValue(IngredientCount ingredient)
        {
            return ingredient.GetBaseCount();
        }

        private sealed class WorkshopRecord
        {
            public readonly Building Workbench;
            public readonly IBillGiver Giver;

            public WorkshopRecord(Building workbench, IBillGiver giver)
            {
                Workbench = workbench;
                Giver = giver;
            }
        }

        private sealed class BillRecord
        {
            public readonly Building Workbench;
            public readonly IBillGiver Giver;
            public readonly Bill Bill;

            public BillRecord(Building workbench, IBillGiver giver, Bill bill)
            {
                Workbench = workbench;
                Giver = giver;
                Bill = bill;
            }
        }
    }
}
