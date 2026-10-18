using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using Verse;

namespace RiMCP.Command
{
    // Answers are ordinary play, so they need no approval.
    internal static class DecisionCommandService
    {
        public static BridgeResponse AnswerLetter(BridgeRequest request, RouteMatch route)
        {
            AnswerLetterBody body = CommandUtil.ReadBody<AnswerLetterBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            RequireGame();
            int letterId = ParseId(route["letterId"], "letterId");
            RequestKey key = RequestKey.For("answer_letter", requestId, null, letterId, body.Option, body.Revision, body.Dismiss);
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () => AnswerLetter(letterId, body));
        }

        public static BridgeResponse RespondToQuest(BridgeRequest request, RouteMatch route)
        {
            RespondToQuestBody body = CommandUtil.ReadBody<RespondToQuestBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            string action = body.Action == null ? null : body.Action.Trim();
            if (action != "accept" && action != "dismiss" && action != "restore")
            {
                throw new CommandException(400, "Field 'action' must be accept, dismiss, or restore.");
            }
            RequireGame();
            int questId = ParseId(route["questId"], "questId");
            RequestKey key = RequestKey.For("respond_to_quest", requestId, null, questId, action, body.Reward, body.Accepter);
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () => RespondToQuest(questId, action, body));
        }

        public static BridgeResponse AnswerDialog(BridgeRequest request, RouteMatch route)
        {
            AnswerDialogBody body = CommandUtil.ReadBody<AnswerDialogBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            bool cancel = body.Cancel == true;
            if (cancel == !string.IsNullOrEmpty(body.Option))
            {
                throw new CommandException(400, "Give option (an id from the dialog's page) or cancel: true.");
            }
            if (!body.Page.HasValue)
            {
                throw new CommandException(400, "Field 'page' is required: the page the options were read from.");
            }
            int page = body.Page.Value;
            string interactionId = route["interactionId"];
            // Receipt first, so a retry is answered even after the dialog is forgotten.
            RequestKey key = RequestKey.For("answer_dialog", requestId, null, interactionId, page, body.Option, body.Value, cancel, body.AimId, body.AimX, body.AimZ);
            return CommandReceipts.Once(CommandUtil.ContextFor(null), key, () =>
            {
                Interaction interaction = Interactions.Find(interactionId);
                if (interaction == null)
                {
                    throw new CommandException(404, "No dialog '" + interactionId + "'. get_events lists the dialogs agents have open.");
                }
                if (!BridgeRuntime.Access.AllowsTool(interaction.Tool))
                {
                    throw new CommandException(403, "Answering this dialog needs " + interaction.Tool + " to be on in RimMCP settings.");
                }
                Dictionary<string, object> data = Dto.Obj(Dto.Field("interactionId", interaction.Id));
                if (cancel)
                {
                    Interactions.Cancel(interaction, page);
                    data["cancelled"] = true;
                    return new CommandResult(true, data);
                }
                Map map = Current.Game == null ? null : Find.CurrentMap;
                LocalTargetInfo aim = map == null ? LocalTargetInfo.Invalid : Targets.Parse(map, body.AimId, body.AimX, body.AimZ, "aim");
                Action choose;
                DialogOption chosen = Interactions.Prepare(interaction, page, body.Option, body.Value, out choose);
                ActionOrigin origin = Interactions.OriginOf(interaction, chosen);
                data["answered"] = chosen.Text;
                data["origin"] = origin.Mod;
                Press.Execute(data, origin, interaction.Tool, interaction.Subject, interaction.Selected, map, aim, choose, () => Interactions.Settle(interaction), interaction);
                return new CommandResult(true, data);
            });
        }

        private static CommandResult AnswerLetter(int letterId, AnswerLetterBody body)
        {
            Letter letter = Find.LetterStack.LettersListForReading.FirstOrDefault(each => each.ID == letterId && each.CanShowInLetterStack);
            if (letter == null)
            {
                throw new CommandException(404, "Letter " + letterId + " is not waiting: it was answered, dismissed, or expired. get_events lists the letters waiting.");
            }
            string label = letter.Label.ToString().StripTags();
            if (body.Dismiss == true)
            {
                if (!string.IsNullOrEmpty(body.Option))
                {
                    throw new CommandException(400, "Give option or dismiss, not both.");
                }
                if (!letter.CanDismissWithRightClick)
                {
                    throw new CommandException(409, "'" + label + "' cannot be dismissed; it needs one of its options.");
                }
                Find.LetterStack.RemoveLetter(letter);
                FollowUps.Announce("dismissed the letter '" + label + "'.", letter.lookTargets);
                return new CommandResult(true, Dto.Obj(Dto.Field("letterId", letterId), Dto.Field("dismissed", true)));
            }
            if (string.IsNullOrEmpty(body.Option))
            {
                throw new CommandException(400, "Give option (an id from the letter's options in get_events), or dismiss: true.");
            }
            ChoiceLetter choiceLetter = letter as ChoiceLetter;
            if (choiceLetter == null)
            {
                throw new CommandException(409, "'" + label + "' " + EventReadService.LeftToPlayer(letter));
            }
            List<LetterChoice> choices = LetterChoices.Of(choiceLetter);
            if (!LetterChoices.Answerable(choices))
            {
                throw new CommandException(409, "'" + label + "' has no choices to answer; dismiss it instead.");
            }
            if (string.IsNullOrEmpty(body.Revision))
            {
                throw new CommandException(400, "Field 'revision' is required: the letter's revision from get_events.");
            }
            if (body.Revision != LetterChoices.Revision(GameContext.LoadId, letter, choices))
            {
                throw new CommandException(409, "The options of '" + label + "' changed since they were read; read them again with get_events.");
            }
            LetterChoice chosen = choices.FirstOrDefault(choice => choice.Id == body.Option);
            if (chosen == null)
            {
                throw new CommandException(400, "'" + label + "' has no option '" + body.Option + "'; its options are " + string.Join(", ", choices.Select(choice => choice.Id).ToArray()) + ".");
            }
            if (chosen.DisabledReason != null)
            {
                throw new CommandException(409, "'" + chosen.Text + "' is unavailable: " + chosen.DisabledReason);
            }

            Press.Check(GameActions.Of(new[] { letter.GetType() }));
            List<Window> before = FollowUps.Snapshot();
            letter.OpenLetter();
            List<Window> opened = FollowUps.OpenedSince(before);
            Interaction interaction;
            ActionOrigin origin;
            Action choose;
            try
            {
                DialogAdapter dialog = opened.OfType<Dialog_NodeTree>().Select(DialogAdapter.For).FirstOrDefault(each => each != null);
                if (dialog == null)
                {
                    throw new CommandException(409, "'" + label + "' opens its own window, which agents cannot answer yet; it is left to the player.");
                }
                DialogOption option = dialog.Options().FirstOrDefault(each => each.Index == chosen.Index);
                if (option == null || option.Text != chosen.Text || option.DisabledReason != chosen.DisabledReason)
                {
                    throw new CommandException(409, "The options of '" + label + "' changed as it opened; read them again with get_events.");
                }
                origin = GameActions.Of(new[] { letter.GetType(), GameActions.OriginType(dialog.Callback(option)) });
                Press.Check(origin);
                interaction = Interactions.Start("answer_letter", label, dialog, null, origin);
                Interactions.Prepare(interaction, interaction.Page, option.Id, null, out choose);
            }
            catch
            {
                foreach (Window window in opened)
                {
                    window.Close(false);
                }
                throw;
            }
            Dictionary<string, object> data = Dto.Obj(
                Dto.Field("letterId", letterId),
                Dto.Field("answered", chosen.Text),
                Dto.Field("origin", origin.Mod));
            Press.Execute(data, origin, "answer_letter", label, null, Find.CurrentMap, LocalTargetInfo.Invalid, choose, () => Interactions.Settle(interaction), interaction);
            data["letterWaiting"] = Find.LetterStack.LettersListForReading.Contains(letter);
            FollowUps.Announce("chose \"" + chosen.Text + "\" for '" + label + "'.", letter.lookTargets);
            return new CommandResult(true, data);
        }

        private static CommandResult RespondToQuest(int questId, string action, RespondToQuestBody body)
        {
            Quest quest = Find.QuestManager.QuestsListForReading.FirstOrDefault(each => each.id == questId && !each.hidden && !each.hiddenInUI);
            if (quest == null)
            {
                throw new CommandException(404, "Quest " + questId + " is not in the Quests tab. list_quests lists them.");
            }
            if (action == "accept")
            {
                return AcceptQuest(quest, body);
            }
            if (body.Reward.HasValue || !string.IsNullOrEmpty(body.Accepter))
            {
                throw new CommandException(400, "reward and accepter are only for accepting a quest.");
            }
            bool dismiss = action == "dismiss";
            if (quest.Historical)
            {
                throw new CommandException(409, "'" + quest.name + "' has ended; only quests on offer or active can be dismissed or restored.");
            }
            if (quest.dismissed == dismiss)
            {
                return new CommandResult(false, Dto.Obj(Dto.Field("questId", quest.id), Dto.Field("dismissed", dismiss), Dto.Field("alreadySatisfied", true)));
            }
            // Subquests too, as the Quests tab does.
            quest.dismissed = dismiss;
            foreach (Quest subquest in quest.GetSubquests())
            {
                subquest.dismissed = dismiss;
            }
            FollowUps.Announce((dismiss ? "dismissed" : "restored") + " the quest '" + quest.name + "'.");
            return new CommandResult(true, Dto.Obj(Dto.Field("questId", quest.id), Dto.Field("dismissed", dismiss)));
        }

        // The reward is chosen only once the quest is accepted.
        private static CommandResult AcceptQuest(Quest quest, RespondToQuestBody body)
        {
            if (quest.State != QuestState.NotYetAccepted)
            {
                throw new CommandException(409, "'" + quest.name + "' is not on offer: it is " + quest.State + ".");
            }
            AcceptanceReport report = QuestUtility.CanAcceptQuest(quest);
            if (!report.Accepted)
            {
                throw new CommandException(409, "'" + quest.name + "' cannot be accepted" + (string.IsNullOrEmpty(report.Reason) ? "." : ": " + report.Reason.StripTags()));
            }
            QuestPart_Choice choice = quest.PartsListForReading.OfType<QuestPart_Choice>().FirstOrDefault();
            QuestPart_Choice.Choice reward = null;
            bool needsAccepter;
            if (choice != null)
            {
                if (!body.Reward.HasValue)
                {
                    throw new CommandException(400, "'" + quest.name + "' is accepted for one of its rewardChoices; give reward, its index from list_quests.");
                }
                int index = body.Reward.Value;
                if (index < 0 || index >= choice.choices.Count || choice.choices[index].rewards.Count == 0)
                {
                    throw new CommandException(400, "'" + quest.name + "' has no reward choice " + index + ". list_quests lists its rewardChoices.");
                }
                reward = choice.choices[index];
                needsAccepter = QuestWorldReadService.ChoiceNeedsAccepter(quest, choice, reward);
            }
            else
            {
                if (body.Reward.HasValue)
                {
                    throw new CommandException(400, "'" + quest.name + "' has no reward choices; leave out reward.");
                }
                needsAccepter = quest.RequiresAccepter;
            }
            Pawn accepter = null;
            if (needsAccepter)
            {
                if (string.IsNullOrEmpty(body.Accepter))
                {
                    throw new CommandException(400, "'" + quest.name + "' needs a colonist to accept it; give accepter, one of its accepters in list_quests.");
                }
                accepter = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoSuspended
                    .FirstOrDefault(pawn => pawn.ThingID == body.Accepter || pawn.GetUniqueLoadID() == body.Accepter);
                if (accepter == null)
                {
                    throw new CommandException(404, "No free colonist '" + body.Accepter + "'.");
                }
                if (!QuestUtility.CanPawnAcceptQuest(accepter, quest))
                {
                    throw new CommandException(409, ReadUtil.PawnName(accepter) + " cannot accept '" + quest.name + "': they do not meet its requirements.");
                }
            }
            else if (!string.IsNullOrEmpty(body.Accepter))
            {
                throw new CommandException(400, "'" + quest.name + "' needs no accepter; leave out accepter.");
            }

            Pawn by = accepter;
            Func<bool> accept = () =>
            {
                // Checked again when the player confirms the game's question, in case the offer moved on.
                if (quest.State != QuestState.NotYetAccepted || !QuestUtility.CanAcceptQuest(quest) || by != null && !QuestUtility.CanPawnAcceptQuest(by, quest))
                {
                    Messages.Message("MessageCannotAcceptQuest".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                if (reward != null)
                {
                    choice.Choose(reward);
                }
                quest.Accept(by);
                if (by != null)
                {
                    Messages.Message("MessageQuestAccepted".Translate(by, quest.name), by, MessageTypeDefOf.TaskCompletion, false);
                }
                FollowUps.Announce("accepted the quest '" + quest.name + "'" + (by == null ? "" : " with " + ReadUtil.PawnName(by)) + ".");
                return true;
            };
            Dictionary<string, object> data = Dto.Obj(Dto.Field("questId", quest.id));
            string warning = needsAccepter ? RoyalFavorWarning(quest, accepter) : null;
            if (warning != null)
            {
                List<Window> before = FollowUps.Snapshot();
                Find.WindowStack.Add(new Dialog_MessageBox(warning, "Confirm".Translate(), () => accept(), "GoBack".Translate()));
                data["accepted"] = false;
                data["asking"] = "The game asks before giving royal favor to " + ReadUtil.PawnName(accepter) + "; answer with answer_dialog.";
                FollowUps.Report(data, "respond_to_quest", quest.name, FollowUps.OpenedSince(before));
                return new CommandResult(false, data);
            }
            accept();
            data["accepted"] = true;
            data["state"] = quest.State.ToString();
            if (accepter != null)
            {
                data["accepter"] = Dto.Obj(Dto.Field("id", ReadUtil.ThingId(accepter)), Dto.Field("name", ReadUtil.PawnName(accepter)));
            }
            if (reward != null)
            {
                data["reward"] = QuestWorldReadService.Rewards(reward);
            }
            return new CommandResult(true, data);
        }

        private static string RoyalFavorWarning(Quest quest, Pawn pawn)
        {
            QuestPart_GiveRoyalFavor favor = quest.PartsListForReading.OfType<QuestPart_GiveRoyalFavor>().FirstOrDefault();
            if (favor == null || !favor.giveToAccepter || pawn == null || pawn.skills == null)
            {
                return null;
            }
            List<Trait> conceited = RoyalTitleUtility.GetConceitedTraits(pawn).ToList();
            List<Trait> psylink = RoyalTitleUtility.GetTraitsAffectingPsylinkNegatively(pawn).ToList();
            bool noSocial = pawn.skills.GetSkill(SkillDefOf.Social).TotallyDisabled;
            bool noPsylink = !pawn.HasPsylink && psylink.Count > 0;
            if (!noSocial && conceited.Count == 0 && !noPsylink)
            {
                return null;
            }
            NamedArgument who = pawn.Named("PAWN");
            NamedArgument faction = favor.faction.Named("FACTION");
            TaggedString text = "QuestGivesRoyalFavor".Translate(who, faction);
            if (noSocial)
            {
                text += "\n\n" + "RoyalIncapableOfSocial".Translate(who, faction);
            }
            if (conceited.Count > 0)
            {
                text += "\n\n" + "RoyalWithConceitedTrait".Translate(who, faction, conceited.Select(trait => trait.Label).ToCommaList(true));
            }
            if (noPsylink)
            {
                text += "\n\n" + "RoyalWithTraitAffectingPsylinkNegatively".Translate(who, faction, psylink.Select(trait => trait.Label).ToCommaList(true));
            }
            text += "\n\n" + "WantToContinue".Translate();
            return text;
        }

        private static void RequireGame()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                throw new CommandException(409, "No game is loaded.");
            }
        }

        private static int ParseId(string text, string field)
        {
            int id;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                throw new CommandException(400, "Field '" + field + "' must be a whole number.");
            }
            return id;
        }

        [DataContract]
        private sealed class AnswerLetterBody
        {
            [DataMember(Name = "option")]
            public string Option { get; set; }

            [DataMember(Name = "revision")]
            public string Revision { get; set; }

            [DataMember(Name = "dismiss")]
            public bool? Dismiss { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class RespondToQuestBody
        {
            [DataMember(Name = "action")]
            public string Action { get; set; }

            [DataMember(Name = "reward")]
            public int? Reward { get; set; }

            [DataMember(Name = "accepter")]
            public string Accepter { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class AnswerDialogBody
        {
            [DataMember(Name = "page")]
            public int? Page { get; set; }

            [DataMember(Name = "option")]
            public string Option { get; set; }

            [DataMember(Name = "value")]
            public int? Value { get; set; }

            [DataMember(Name = "cancel")]
            public bool? Cancel { get; set; }

            [DataMember(Name = "aimId")]
            public string AimId { get; set; }

            [DataMember(Name = "aimX")]
            public int? AimX { get; set; }

            [DataMember(Name = "aimZ")]
            public int? AimZ { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
