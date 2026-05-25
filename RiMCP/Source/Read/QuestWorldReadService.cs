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
        public static BridgeResponse ListQuests(ReadContext context)
        {
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            IEnumerable<object> source = Reflect.ReadEnumerable(Find.QuestManager, "QuestsListForReading")
                .OrderBy(quest => Reflect.ReadString(quest, "name") ?? Reflect.ReadString(quest, "ID") ?? "");
            Page<object> page = new Page<object>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("quests", page.Items.Select(quest => SerializeQuest(quest, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        public static BridgeResponse ListWorld(ReadContext context)
        {
            if (!ReadUtil.ChangedSince(context))
            {
                return ReadEnvelope.NotChanged(context);
            }

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("playerFaction", SerializeFaction(Faction.OfPlayer)),
                Dto.Field("factions", Find.FactionManager == null ? new object[0] : Find.FactionManager.AllFactionsListForReading.Select(SerializeFaction).ToArray()),
                Dto.Field("worldObjects", SerializeWorldObjects(context.Request))));
        }

        private static object SerializeQuest(object quest, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", Reflect.Read(quest, "id") ?? Reflect.Read(quest, "ID")),
                Dto.Field("name", Reflect.ReadString(quest, "name")),
                Dto.Field("state", Reflect.Read(quest, "State") == null ? null : Reflect.Read(quest, "State").ToString()),
                Dto.Field("root", Reflect.ReadString(Reflect.Read(quest, "root"), "defName")),
                Dto.Field("accepted", Reflect.Read(quest, "accepted")));
            if (detail != ReadDetail.Summary)
            {
                dto["description"] = Reflect.ReadString(quest, "description");
                dto["ticksUntilAcceptanceExpiry"] = Reflect.Read(quest, "ticksUntilAcceptanceExpiry");
                dto["hidden"] = Reflect.Read(quest, "hidden");
                dto["parts"] = Reflect.ReadEnumerable(quest, "parts")
                    .Select(part => Dto.Obj(
                        Dto.Field("kind", part.GetType().Name),
                        Dto.Field("label", Reflect.ReadString(part, "Label")),
                        Dto.Field("state", Reflect.Read(part, "State") == null ? null : Reflect.Read(part, "State").ToString())))
                    .ToArray();
            }
            return dto;
        }

        private static object SerializeFaction(Faction faction)
        {
            if (faction == null)
            {
                return null;
            }
            return Dto.Obj(
                Dto.Field("name", faction.Name),
                Dto.Field("defName", faction.def == null ? null : faction.def.defName),
                Dto.Field("isPlayer", faction == Faction.OfPlayer),
                Dto.Field("hostileToPlayer", faction.HostileTo(Faction.OfPlayer)));
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
                Dto.Field("tile", worldObject.Tile),
                Dto.Field("faction", worldObject.Faction == null ? null : worldObject.Faction.Name));
        }
    }
}
