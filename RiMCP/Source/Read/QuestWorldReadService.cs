using System;
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
        private static readonly string[] QuestTabs = { "available", "active", "historical" };

        public static BridgeResponse ListQuests(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            string tab = context.Request.Get("tab");
            if (!string.IsNullOrEmpty(tab) && !QuestTabs.Contains(tab))
            {
                throw new BridgeException(400, "Field 'tab' must be available, active, or historical.");
            }

            List<Quest> quests = Current.Game == null || Find.QuestManager == null ? new List<Quest>() : Find.QuestManager.QuestsListForReading.ToList();
            IEnumerable<Quest> source = QuestTabs
                .Where(each => string.IsNullOrEmpty(tab) || each == tab)
                .SelectMany(each => InTabOrder(quests.Where(quest => TabOf(quest) == each), each));
            Page<Quest> page = new Page<Quest>(source, context.Request);
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("quests", page.Items.Select(quest => SerializeQuest(quest, context.Request.Detail)).ToArray())),
                page.Truncated,
                page.NextCursor);
        }

        // Dismissed quests are listed under Historical, from where they can be restored.
        private static string TabOf(Quest quest)
        {
            if (quest.hidden || quest.hiddenInUI)
            {
                return null;
            }
            if (quest.Historical || quest.dismissed)
            {
                return "historical";
            }
            return quest.State == QuestState.NotYetAccepted ? "available" : quest.State == QuestState.Ongoing ? "active" : null;
        }

        private static IEnumerable<Quest> InTabOrder(IEnumerable<Quest> quests, string tab)
        {
            switch (tab)
            {
                case "available":
                    return quests.OrderBy(quest => quest.TicksUntilExpiry);
                case "active":
                    return quests.OrderBy(quest => quest.TicksSinceAccepted);
                default:
                    return quests.OrderBy(quest => quest.TicksSinceCleanup);
            }
        }

        public static BridgeResponse ListWorld(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            FactionManager factions = Find.World == null ? null : Find.FactionManager;

            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("playerFaction", factions == null ? null : SerializeFaction(Faction.OfPlayer)),
                Dto.Field("factions", factions == null ? new object[0] : factions.AllFactionsListForReading.Select(SerializeFaction).ToArray()),
                Dto.Field("worldObjects", SerializeWorldObjects(context.Request))));
        }

        private static object SerializeQuest(Quest quest, ReadDetail detail)
        {
            Dictionary<string, object> dto = Dto.Obj(
                Dto.Field("id", quest.id),
                Dto.Field("name", quest.name),
                Dto.Field("tab", TabOf(quest)),
                Dto.Field("state", quest.State.ToString()),
                Dto.Field("root", quest.root == null ? null : quest.root.defName),
                Dto.Field("accepted", quest.EverAccepted),
                Dto.Field("dismissed", quest.dismissed ? (object)true : null));
            if (quest.State == QuestState.NotYetAccepted)
            {
                AddAcceptance(dto, quest);
            }
            else if (quest.EverAccepted)
            {
                AddAccepted(dto, quest);
            }
            if (detail != ReadDetail.Summary)
            {
                dto["description"] = quest.description.ToString().StripTags();
                dto["parts"] = quest.PartsListForReading
                    .Select(part => Dto.Obj(
                        Dto.Field("kind", part.GetType().Name),
                        Dto.Field("label", part.DescriptionPart)))
                    .ToArray();
            }
            return dto;
        }

        private static void AddAcceptance(Dictionary<string, object> dto, Quest quest)
        {
            AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
            dto["canAccept"] = report.Accepted;
            if (!report.Accepted && !string.IsNullOrEmpty(report.Reason))
            {
                dto["reason"] = report.Reason.StripTags();
            }
            List<string> unmet = quest.PartsListForReading
                .OfType<QuestPart_RequirementsToAccept>()
                .Where(part => part.ShowInRequirementBox)
                .Select(part => part.CanAccept())
                .Where(result => !result.Accepted && !string.IsNullOrEmpty(result.Reason))
                .Select(result => result.Reason.StripTags())
                .Distinct()
                .ToList();
            if (unmet.Count > 0)
            {
                dto["unmetRequirements"] = unmet;
            }
            if (quest.TicksUntilExpiry > 0)
            {
                dto["expiresInTicks"] = quest.TicksUntilExpiry;
            }

            bool needsAccepter;
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            if (choice == null)
            {
                needsAccepter = quest.RequiresAccepter;
                dto["needsAccepter"] = needsAccepter;
            }
            else
            {
                List<object> rewards = new List<object>();
                needsAccepter = false;
                for (int i = 0; i < choice.choices.Count; i++)
                {
                    QuestPart_Choice.Choice option = choice.choices[i];
                    if (option.rewards.Count == 0)
                    {
                        continue;
                    }
                    bool needs = ChoiceNeedsAccepter(quest, choice, option);
                    needsAccepter |= needs;
                    Dictionary<string, object> reward = Rewards(option);
                    reward["index"] = i;
                    reward["needsAccepter"] = needs;
                    rewards.Add(reward);
                }
                dto["rewardChoices"] = rewards;
            }
            if (needsAccepter)
            {
                dto["accepters"] = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
                    .Where(pawn => QuestUtility.CanPawnAcceptQuest(pawn, quest))
                    .Select(pawn => (object)Dto.Obj(Dto.Field("id", ReadUtil.ThingId(pawn)), Dto.Field("name", ReadUtil.PawnName(pawn))))
                    .ToArray();
            }
        }

        // As the Quests tab decides it: the other rewards' parts are dropped before asking.
        internal static bool ChoiceNeedsAccepter(Quest quest, QuestPart_Choice choice, QuestPart_Choice.Choice chosen)
        {
            HashSet<QuestPart> dropped = new HashSet<QuestPart>(choice.choices
                .Where(other => other != chosen)
                .SelectMany(other => other.questParts)
                .Where(part => !chosen.questParts.Contains(part)));
            return quest.PartsListForReading.Any(part => !dropped.Contains(part) && part.RequiresAccepter);
        }

        private static void AddAccepted(Dictionary<string, object> dto, Quest quest)
        {
            Pawn accepter = quest.AccepterPawn;
            if (accepter != null)
            {
                dto["accepter"] = Dto.Obj(Dto.Field("id", ReadUtil.ThingId(accepter)), Dto.Field("name", ReadUtil.PawnName(accepter)));
            }
            else if (!string.IsNullOrEmpty(quest.AccepterPawnLabelCap))
            {
                dto["accepter"] = Dto.Obj(Dto.Field("name", quest.AccepterPawnLabelCap.StripTags()));
            }
            // Accepting for a reward drops the others, leaving the one chosen.
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            if (choice != null && choice.choices.Count == 1 && choice.choices[0].rewards.Count > 0)
            {
                dto["reward"] = Rewards(choice.choices[0]);
            }
        }

        internal static Dictionary<string, object> Rewards(QuestPart_Choice.Choice choice)
        {
            return Dto.Obj(
                Dto.Field("rewards", choice.rewards.Select(RewardLabel).Where(label => !string.IsNullOrEmpty(label)).ToArray()),
                Dto.Field("value", (int)Math.Round(choice.rewards.Sum(reward => reward.TotalMarketValue))));
        }

        private static string RewardLabel(Reward reward)
        {
            try
            {
                Reward_Items items = reward as Reward_Items;
                if (items != null)
                {
                    return string.Join(", ", items.ItemsListForReading.Select(item => item.LabelCap.StripTags()).ToArray());
                }
                Reward_Goodwill goodwill = reward as Reward_Goodwill;
                if (goodwill != null)
                {
                    return goodwill.amount.ToString("+0;-0") + " goodwill with " + (goodwill.faction == null ? "a faction" : goodwill.faction.Name);
                }
                Reward_RoyalFavor favor = reward as Reward_RoyalFavor;
                if (favor != null)
                {
                    return favor.amount + " royal favor with " + (favor.faction == null ? "a faction" : favor.faction.Name);
                }
                Reward_Pawn pawn = reward as Reward_Pawn;
                if (pawn != null && pawn.pawn != null)
                {
                    return pawn.pawn.LabelCap.StripTags();
                }
                string description = reward.GetDescription(default(RewardsGeneratorParams));
                if (!string.IsNullOrEmpty(description))
                {
                    return description.StripTags().Split('\n')[0].Trim();
                }
            }
            catch (Exception)
            {
            }
            string name = reward.GetType().Name;
            return name.StartsWith("Reward_") ? name.Substring("Reward_".Length) : name;
        }

        private static object SerializeFaction(Faction faction)
        {
            return SummaryDto.FactionStatus(faction);
        }

        private static object SerializeWorldObjects(ReadRequest request)
        {
            if (Find.World == null || Find.WorldObjects == null)
            {
                return new object[0];
            }
            IEnumerable<WorldObject> source = Find.WorldObjects.AllWorldObjects
                .OrderBy(obj => obj.def == null ? "" : obj.def.defName, StringComparer.Ordinal)
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
