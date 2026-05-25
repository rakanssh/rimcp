using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;

namespace RiMCP.Read
{
    internal static class ProductionReadService
    {
        public static BridgeResponse ListProduction(ReadContext context)
        {
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

        public static BridgeResponse GetBill(ReadContext context, string id)
        {
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

        private static object SerializeBill(BillRecord record, ReadDetail detail)
        {
            Bill_Production production = record.Bill as Bill_Production;
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", record.Bill.GetUniqueLoadID()),
                Dto.Field("label", record.Bill.Label),
                Dto.Field("recipe", record.Bill.recipe == null ? null : Dto.Obj(
                    Dto.Field("defName", record.Bill.recipe.defName),
                    Dto.Field("label", record.Bill.recipe.LabelCap))),
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
                dto["products"] = record.Bill.recipe == null || record.Bill.recipe.products == null
                    ? new object[0]
                    : record.Bill.recipe.products.Select(product => Dto.Obj(
                        Dto.Field("defName", product.thingDef == null ? null : product.thingDef.defName),
                        Dto.Field("label", product.thingDef == null ? null : product.thingDef.LabelCap),
                        Dto.Field("count", product.count))).ToArray();
                dto["ingredients"] = record.Bill.recipe == null || record.Bill.recipe.ingredients == null
                    ? new object[0]
                    : record.Bill.recipe.ingredients.Select(ingredient => Dto.Obj(
                        Dto.Field("count", IngredientCountValue(ingredient)),
                        Dto.Field("filterSummary", ingredient.filter == null ? null : ingredient.filter.Summary))).ToArray();
            }
            return dto;
        }

        private static object IngredientCountValue(IngredientCount ingredient)
        {
            return ingredient.GetBaseCount();
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
