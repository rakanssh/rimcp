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
    // Starting the colony from the last setup screen, with the player's approval.
    internal static partial class SetupCommandService
    {
        public static BridgeResponse StartGame(BridgeRequest request, RouteMatch route)
        {
            RevisionBody body = CommandUtil.ReadBody<RevisionBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            return SessionCommandService.Begin(request, "start_game", requestId, () =>
            {
                Page page = SetupController.OpenPage;
                if (page == null || page.next != null || page.nextAct == null || SetupController.StageOf(page) == SetupController.Unsupported)
                {
                    throw new CommandException(409, page == null ? "No new game is being set up." : "start_game is for the last setup screen; setup is at the " + SetupController.StageOf(page) + " screen.");
                }
                SetupController.RequireRevision(body.ExpectedRevision);
                int own = SetupController.Revision;
                return operation => Begin(operation, page, own);
            }, body.ExpectedRevision);
        }

        private static IEnumerable<string> Begin(Operation operation, Page page, int own)
        {
            SetupController.Continue(operation, own);
            if (!SetupController.CanNext(page))
            {
                throw new OperationEnded(Operation.Failed, "The " + SetupController.StageOf(page) + " screen is not ready to start; see the message in game.");
            }
            if (!BridgeRuntime.UnattendedSetup)
            {
                // The player approves this exact setup. Any change while asking voids the approval.
                bool approved = false;
                Dialog_MessageBox approval = new Dialog_MessageBox(Summary(), "Start colony", () => approved = true, "Not yet", null, "RimMCP is ready to start this colony");
                Find.WindowStack.Add(approval);
                operation.State = Operation.AwaitingConfirmation;
                operation.Confirmation = Dto.Obj(
                    Dto.Field("kind", "startColony"),
                    Dto.Field("message", "The player is asked to approve starting the colony as set up."),
                    Dto.Field("revision", own),
                    Dto.Field("answeredBy", "player"));
                try
                {
                    while (Find.WindowStack.IsOpen(approval))
                    {
                        if (SetupStateChanged(own))
                        {
                            throw new OperationEnded(Operation.Interrupted, "The setup changed while the player was asked, so the approval was withdrawn.");
                        }
                        yield return "awaitingApproval";
                    }
                }
                finally
                {
                    // Withdrawn when the operation stops or the setup changes.
                    if (Find.WindowStack.IsOpen(approval))
                    {
                        approval.Close(false);
                    }
                }
                operation.State = Operation.Running;
                operation.Confirmation = null;
                if (!approved)
                {
                    throw new OperationEnded(Operation.Declined, "The player chose not to start the colony yet.");
                }
                SetupController.Continue(operation, own);
            }
            List<Window> open = Find.WindowStack.Windows.ToList();
            SetupController.DoNext(page);
            if (!LongEventHandler.AnyEventNowOrWaiting)
            {
                List<Window> asking = Find.WindowStack.Windows.Where(window => !open.Contains(window) && !(window is Page)).ToList();
                if (asking.Count == 0)
                {
                    throw new OperationEnded(Operation.Failed, "The game did not start the colony.");
                }
                foreach (string step in AwaitQuestion(operation, asking, "requiredWorkDisabled", () => LongEventHandler.AnyEventNowOrWaiting))
                {
                    yield return step;
                }
                if (!LongEventHandler.AnyEventNowOrWaiting)
                {
                    throw new OperationEnded(Operation.Declined, "Starting without someone for every required job was not confirmed.");
                }
            }
            foreach (string step in SessionCommandService.AwaitTransition(operation, "startingColony", () => Current.ProgramState == ProgramState.Playing))
            {
                yield return step;
            }
            SetupController.Changed();
            operation.Result = Dto.Obj(
                Dto.Field("loadId", GameContext.LoadId),
                Dto.Field("tick", Find.TickManager.TicksGame),
                Dto.Field("colonists", Find.CurrentMap == null ? null : Find.CurrentMap.mapPawns.FreeColonists.Select(pawn => ReadUtil.PawnName(pawn)).ToArray()));
        }

        private static bool SetupStateChanged(int own)
        {
            SetupController.Observe();
            return SetupController.Revision != own;
        }

        private static string Summary()
        {
            GameInitData init = Find.GameInitData;
            List<string> lines = new List<string>
            {
                "Scenario: " + (Find.Scenario == null ? "?" : Find.Scenario.name),
                "Storyteller: " + (Find.Storyteller == null ? "?" : Find.Storyteller.def.LabelCap + ", " + Find.Storyteller.difficultyDef.LabelCap) + (init.permadeath ? " (commitment mode)" : ""),
                "Site: tile " + init.startingTile.tileId + (init.startingTile.Valid ? ", " + Find.WorldGrid[init.startingTile].PrimaryBiome.LabelCap : "") + ", map " + init.mapSize,
                "Colonists: " + string.Join(", ", init.startingAndOptionalPawns.Take(init.startingPawnCount).Select(pawn => ReadUtil.PawnName(pawn)).ToArray())
            };
            return string.Join("\n", lines.ToArray());
        }
    }
}
