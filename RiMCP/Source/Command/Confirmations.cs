using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.UI;
using Verse;

namespace RiMCP.Command
{
    // An operation waiting while the game asks something, with the dialogs it opened. The operation
    // owns them: if it stops, or the question goes stale (the setup or the file changed), the
    // dialogs close, so an old question can no longer load a save or move a setup on. Where agents
    // may answer (unattended setup), answer_confirmation presses the button a click would, and is
    // refused once the question is stale. Main thread only.
    internal static class Confirmations
    {
        private static int asked;

        public sealed class Refusal
        {
            public readonly string Reason;
            private readonly TextKey key;
            private readonly NamedArgument[] arguments;

            public Refusal(string reason, TextKey key, params NamedArgument[] arguments)
            {
                Reason = reason;
                this.key = key;
                this.arguments = arguments;
            }

            public DisplayText Display() => key.Display(arguments);
        }

        // kind and message describe the question; stale returns why it no longer applies, or null;
        // hold runs every frame while waiting, such as keeping the game paused.
        public static IEnumerable<string> Await(Operation operation, List<Window> asking, string kind, string message, bool agentMayAnswer, Func<bool> proceeded, Func<Refusal> stale, Action hold = null, params Dto.JsonField[] details)
        {
            string id = "confirm-" + (++asked);
            Dialog_MessageBox box = asking.OfType<Dialog_MessageBox>().FirstOrDefault();
            Dialog_ModMismatch mods = asking.OfType<Dialog_ModMismatch>().FirstOrDefault();
            bool answerable = agentMayAnswer && BridgeRuntime.UnattendedSetup && (box != null || mods != null);
            Dictionary<string, object> confirmation = Dto.Obj(
                Dto.Field("confirmationId", id),
                Dto.Field("kind", kind),
                Dto.Field("message", message ?? (box != null ? box.text.ToString().StripTags() : mods != null ? "The file was made with a different mod list. Accepting loads it anyway; declining goes back." : "The game is asking before it goes on.")),
                Dto.Field("answeredBy", answerable ? "agent" : "player"),
                Dto.Field("dialogs", asking.Select(window => window.GetType().Name).ToArray()));
            foreach (Dto.JsonField field in details.Where(field => field.Value != null))
            {
                confirmation[field.Name] = field.Value;
            }
            // Every way of accepting checks first, right before it acts: the dialog's buttons and Enter
            // key, the mod list dialog's Load anyway, and answer_confirmation. A stop asked for, or a
            // stale question, refuses it, even before the operation's next step.
            Func<Refusal> refusal = () => CheckRefusal(operation, stale);
            foreach (Dialog_MessageBox each in asking.OfType<Dialog_MessageBox>())
            {
                each.buttonAAction = Guard(each.buttonAAction, refusal);
                each.acceptAction = Guard(each.acceptAction, refusal);
            }
            foreach (Dialog_ModMismatch each in asking.OfType<Dialog_ModMismatch>())
            {
                SetupController.Set(each, "loadAction", Guard((Action)SetupController.Get(each, "loadAction"), refusal));
            }
            operation.State = Operation.AwaitingConfirmation;
            operation.ConfirmationId = id;
            operation.Confirmation = confirmation;
            operation.Answer = !answerable ? (Func<bool, string>)null : accept =>
            {
                Refusal rejected = refusal();
                if (rejected != null)
                {
                    return rejected.Reason;
                }
                // As the button does: its action, then the dialog closes. The mod list dialog's
                // accept is Load anyway, and its decline Go back.
                Action action = box != null ? (accept ? box.buttonAAction : box.buttonBAction) : accept ? (Action)SetupController.Get(mods, "loadAction") : null;
                if (action != null)
                {
                    action();
                }
                (box ?? (Window)mods).Close();
                return null;
            };
            try
            {
                while (asking.Any(Find.WindowStack.IsOpen) && !proceeded())
                {
                    Refusal rejected = stale();
                    if (rejected != null)
                    {
                        throw new OperationEnded(Operation.Interrupted, rejected.Reason);
                    }
                    if (hold != null)
                    {
                        hold();
                    }
                    yield return "awaitingConfirmation";
                }
            }
            finally
            {
                // Still open when the operation stops or the question goes stale: withdrawn.
                foreach (Window window in asking.Where(Find.WindowStack.IsOpen).ToList())
                {
                    window.Close(false);
                }
                if (!operation.Done)
                {
                    operation.State = Operation.Running;
                    operation.Confirmation = null;
                    operation.ConfirmationId = null;
                    operation.Answer = null;
                }
            }
        }

        private static Refusal CheckRefusal(Operation operation, Func<Refusal> stale)
        {
            if (operation.Done)
            {
                return new Refusal("The operation has ended, so its question no longer applies.", Keys.ConfirmationEnded);
            }
            if (operation.CancelRequested != null || operation.RevokeRequested != null)
            {
                return new Refusal("The operation is stopping, so its question no longer applies.", Keys.ConfirmationStopping);
            }
            return stale();
        }

        private static Action Guard(Action action, Func<Refusal> refusal)
        {
            if (action == null)
            {
                return null;
            }
            return () =>
            {
                Refusal rejected = refusal();
                if (rejected != null)
                {
                    UiDisplay.Message(rejected.Display(), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                action();
            };
        }

        // A setup question goes stale once the setup changes from the revision it was asked at.
        public static Func<Refusal> SetupUnchanged(int revision)
        {
            return () =>
            {
                SetupController.Observe();
                return SetupController.Revision != revision
                    ? new Refusal("The setup changed while the game was asking, so the question was withdrawn.", Keys.ConfirmationSetupChanged)
                    : null;
            };
        }

        public static Refusal SaveChanged(string name) =>
            new Refusal("The save '" + name + "' changed while the game was asking, so the question was withdrawn.", Keys.ConfirmationSaveChanged, name);
    }
}
