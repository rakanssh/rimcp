using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using Verse;
using RimWorld.Planet;

namespace RiMCP.Read
{
    internal static class QuestWorldReadService
    {
        public static BridgeResponse ListQuests(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);

            IEnumerable<Quest> source = (Find.QuestManager == null ? Enumerable.Empty<Quest>() : Find.QuestManager.QuestsListForReading)
                .OrderBy(quest => quest.name ?? quest.id.ToString());
            Page<Quest> page = new Page<Quest>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("quests", page.Items.Select(quest => SerializeQuest(quest, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse ListWorld(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("playerFaction", SerializeFaction(Faction.OfPlayer)),
                Dto.Field("factions", Find.FactionManager == null ? new object[0] : Find.FactionManager.AllFactionsListForReading.Select(SerializeFaction).ToArray()),
                Dto.Field("worldObjects", SerializeWorldObjects(context.Request))));
        }

        private static object SerializeQuest(Quest quest, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", quest.id),
                Dto.Field("name", quest.name),
                Dto.Field("state", quest.State.ToString()),
                Dto.Field("root", quest.root == null ? null : quest.root.defName),
                Dto.Field("accepted", quest.EverAccepted));
            if (detail != ReadDetail.Summary)
            {
                dto["description"] = quest.description.ToString();
                dto["ticksUntilAcceptanceExpiry"] = quest.TicksUntilExpiry;
                dto["hidden"] = quest.hidden;
                dto["parts"] = quest.PartsListForReading
                    .Select(part => Dto.Obj(
                        Dto.Field("kind", part.GetType().Name),
                        Dto.Field("label", part.DescriptionPart)))
                    .ToArray();
            }
            return dto;
        }

        private static object SerializeFaction(Faction faction)
        {
            return SummaryDto.FactionStatus(faction);
        }

        private static object SerializeWorldObjects(ReadRequest request)
        {
            if (Find.WorldObjects == null)
            {
                return new object[0];
            }
            IEnumerable<WorldObject> source = Find.WorldObjects.AllWorldObjects
                .OrderBy(obj => obj.def == null ? "" : obj.def.defName)
                .ThenBy(obj => obj.ID);
            Page<WorldObject> page = new Page<WorldObject>(source, request);
            return Dto.Obj(
                Dto.Field("truncated", page.Truncated),
                Dto.Field("nextCursor", page.NextCursor),
                Dto.Field("items", page.Items.Select(SerializeWorldObject).ToArray()));
        }

        private static object SerializeWorldObject(WorldObject worldObject)
        {
            return Dto.Obj(
                Dto.Field("id", worldObject.ID),
                Dto.Field("def", ReadUtil.Def(worldObject.def)),
                Dto.Field("label", worldObject.LabelCap),
                Dto.Field("tile", ReadUtil.TileId(worldObject.Tile)),
                Dto.Field("faction", worldObject.Faction == null ? null : worldObject.Faction.Name));
        }
    }
}
