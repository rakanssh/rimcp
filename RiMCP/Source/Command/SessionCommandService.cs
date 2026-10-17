using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using RimWorld;
using RiMCP.Bridge;
using RiMCP.Read;
using RiMCP.Shared;
using RiMCP.Util;
using Verse;

namespace RiMCP.Command
{
    // Saving, loading, and leaving games. Each is an operation that outlives the game it may replace:
    // the request starts it and returns at once, and the steps run one per frame (OperationRunner).
    internal static class SessionCommandService
    {
        private const double MaxWaitSeconds = 30;
        private const int MaxSaveNameLength = 100;
        // A load or a return to the main menu that takes longer than this is reported as failed.
        private static readonly TimeSpan TransitionTimeout = TimeSpan.FromMinutes(10);
        // RimWorld back at the main menu this long after a load began means the load failed.
        private static readonly TimeSpan ReturnedToMenu = TimeSpan.FromSeconds(3);

        public static BridgeResponse GetOperation(BridgeRequest request, RouteMatch route)
        {
            Operation operation = FindOperation(route["operationId"]);
            return WithWait(request, operation, () => Envelope(false, Serialize(operation)));
        }

        // Permission follows the target: the router admits this as an Observe tool, and stopping any
        // other operation also needs the tool that started it to be on.
        public static BridgeResponse CancelOperation(BridgeRequest request, RouteMatch route)
        {
            Operation operation = FindOperation(route["operationId"]);
            if (operation.Group != ToolGroups.Observe && !BridgeRuntime.Access.AllowsTool(operation.Tool))
            {
                throw new CommandException(403, "Stopping " + operation.Tool + " needs " + operation.Tool + " (" + ToolGroups.Label(operation.Group) + ") to be on in RimMCP settings.");
            }
            bool changed = OperationRunner.Cancel(operation, "Cancelled with cancel_operation.");
            Dictionary<string, object> data = Serialize(operation);
            data["cancel"] = !changed
                ? "The operation had already finished."
                : operation.Done
                    ? "Cancelled before it started."
                    : operation.InNativeStep
                        ? "A native step is running and cannot be interrupted; the operation stops when it finishes."
                        : "The operation stops at its next step.";
            return Envelope(changed, data);
        }

        public static BridgeResponse ListSaves(BridgeRequest request, RouteMatch route)
        {
            ReadContext context = ReadContext.From(request);
            List<FileInfo> files = GenFilePaths.AllSavedGameFiles.OrderByDescending(file => file.LastWriteTimeUtc).ToList();
            Page<FileInfo> page = new Page<FileInfo>(files, context.Request);
            List<string> runningIds = LoadedModManager.RunningMods.Select(mod => mod.PackageId).ToList();
            List<string> runningFolders = LoadedModManager.RunningMods.Select(mod => mod.FolderName).ToList();
            object[] saves = page.Items.Select(file =>
            {
                SaveHeader header = SaveHeader.Read(file.FullName);
                return (object)Dto.Obj(
                    Dto.Field("name", Path.GetFileNameWithoutExtension(file.Name)),
                    Dto.Field("lastWriteUtc", file.LastWriteTimeUtc.ToString("o", CultureInfo.InvariantCulture)),
                    Dto.Field("sizeBytes", file.Length),
                    Dto.Field("gameVersion", header.GameVersion),
                    Dto.Field("modsMatch", header.Problem == null ? (object)header.ModsMatch(runningIds, runningFolders) : null),
                    Dto.Field("commitmentMode", header.CommitmentMode ? (object)true : null),
                    Dto.Field("problem", header.Problem));
            }).ToArray();
            bool colony = ColonyLoaded();
            return ReadEnvelope.Ok(context, Dto.Obj(
                Dto.Field("gameVersion", VersionControl.CurrentVersionStringWithRev),
                Dto.Field("count", files.Count),
                Dto.Field("loadedColony", colony ? Dto.Obj(
                    Dto.Field("commitmentMode", Current.Game.Info.permadeathMode),
                    Dto.Field("commitmentSave", Current.Game.Info.permadeathMode ? Current.Game.Info.permadeathModeUniqueName : null)) : null),
                Dto.Field("saves", saves)), page.Truncated, page.NextCursor);
        }

        public static BridgeResponse SaveGame(BridgeRequest request, RouteMatch route)
        {
            SaveGameBody body = CommandUtil.ReadBody<SaveGameBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            bool overwrite = body.Overwrite == true;
            return Begin(request, "save_game", requestId, () =>
            {
                string name = ValidateSaveName(body.Name, "name");
                RequireSavable();
                if (Current.Game.Info.permadeathMode)
                {
                    throw new CommandException(409, "Saving under another name is not available in commitment mode, as in game. Leaving the game saves it to its own slot.");
                }
                RequireFreeName(name, overwrite, "name");
                string loadId = GameContext.LoadId;
                return operation => SaveOnly(operation, name, overwrite, loadId);
            }, body.Name, overwrite);
        }

        public static BridgeResponse LoadGame(BridgeRequest request, RouteMatch route)
        {
            ReplaceBody body = CommandUtil.ReadBody<ReplaceBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            bool discard = body.DiscardUnsaved == true;
            bool overwrite = body.Overwrite == true;
            return Begin(request, "load_game", requestId, () =>
            {
                string name = ValidateSaveName(body.Name, "name");
                if (!File.Exists(GenFilePaths.FilePathForSavedGame(name)))
                {
                    throw new CommandException(404, "No save named '" + name + "'. list_saves shows the saves.");
                }
                RequireSettled();
                bool colony = ColonyLoaded();
                bool commitment = colony && Current.Game.Info.permadeathMode;
                string saveFirst = body.SaveFirst == null ? null : ValidateSaveName(body.SaveFirst, "saveFirst");
                string refusal = ReplaceRefusal(colony, commitment, discard, saveFirst, "load");
                if (refusal != null)
                {
                    throw new CommandException(409, refusal);
                }
                if (saveFirst != null)
                {
                    RequireSavable();
                    RequireFreeName(saveFirst, overwrite, "saveFirst");
                }
                string loadId = colony ? GameContext.LoadId : null;
                return operation => Load(operation, name, saveFirst, overwrite, loadId);
            }, body.Name, discard, body.SaveFirst, overwrite);
        }

        public static BridgeResponse LeaveGame(BridgeRequest request, RouteMatch route)
        {
            ReplaceBody body = CommandUtil.ReadBody<ReplaceBody>(request);
            string requestId = CommandUtil.ValidateRequestId(body.RequestId);
            bool discard = body.DiscardUnsaved == true;
            bool overwrite = body.Overwrite == true;
            return Begin(request, "leave_game", requestId, () =>
            {
                RequireSettled();
                if (!ColonyLoaded())
                {
                    throw new CommandException(409, "No game is loaded; RimWorld is already at the main menu.");
                }
                bool commitment = Current.Game.Info.permadeathMode;
                string saveFirst = body.SaveFirst == null ? null : ValidateSaveName(body.SaveFirst, "saveFirst");
                string refusal = ReplaceRefusal(true, commitment, discard, saveFirst, "leave");
                if (refusal != null)
                {
                    throw new CommandException(409, refusal);
                }
                if (commitment)
                {
                    // As the game's own menu does: commitment games always save to their slot first.
                    saveFirst = Current.Game.Info.permadeathModeUniqueName;
                    overwrite = true;
                }
                if (saveFirst != null)
                {
                    RequireSavable();
                    RequireFreeName(saveFirst, overwrite, "saveFirst");
                }
                string loadId = GameContext.LoadId;
                string save = saveFirst;
                bool replace = overwrite;
                return operation => Leave(operation, save, replace, loadId);
            }, discard, body.SaveFirst, overwrite);
        }

        // Why a request that would replace the loaded colony cannot go ahead, or null. Unsaved changes
        // cannot be detected reliably (the game's own check misses changes made while paused), so the
        // request has to say what happens to the colony: save it first, or discard it.
        internal static string ReplaceRefusal(bool colonyLoaded, bool commitment, bool discardUnsaved, string saveFirst, string action)
        {
            if (!colonyLoaded)
            {
                return null;
            }
            if (commitment)
            {
                if (action == "load")
                {
                    return "Loading another save is not available in commitment mode, as in game. Leave the game first; leaving saves it to its own slot.";
                }
                if (discardUnsaved || saveFirst != null)
                {
                    return "Commitment mode always saves the colony to its own slot before leaving, as in game; drop discardUnsaved and saveFirst.";
                }
                return null;
            }
            if (discardUnsaved && saveFirst != null)
            {
                return "Give saveFirst or discardUnsaved, not both.";
            }
            if (!discardUnsaved && saveFirst == null)
            {
                return "A colony is loaded. Pass saveFirst with a save name to save it first, or discardUnsaved: true to drop any unsaved changes. Changes made while paused count, so this is always asked.";
            }
            return null;
        }

        // A repeated requestId gets what it did before; a new request is validated and started.
        // prepare throws on invalid requests, so nothing is recorded for them.
        internal static BridgeResponse Begin(BridgeRequest request, string tool, string requestId, Func<Func<Operation, IEnumerable<string>>> prepare, params object[] arguments)
        {
            return Begin(request, tool, null, requestId, prepare, arguments);
        }

        internal static BridgeResponse Begin(BridgeRequest request, string tool, string group, string requestId, Func<Func<Operation, IEnumerable<string>>> prepare, params object[] arguments)
        {
            RequestKey key = RequestKey.For(tool, requestId, null, arguments);
            string fingerprint = key == null ? null : key.Fingerprint;
            Operation known;
            RequestRecord previous = OperationRunner.Recall(tool, requestId, fingerprint, out known);
            if (previous != null)
            {
                if (known == null)
                {
                    return Envelope(false, Dto.Obj(
                        Dto.Field("expired", true),
                        Dto.Field("operationId", previous.OperationId),
                        Dto.Field("outcome", previous.Outcome),
                        Dto.Field("message", "This requestId's operation finished long enough ago that its details are no longer kept. It was not run again.")));
                }
                return WithWait(request, known, () => Envelope(false, Serialize(known)));
            }
            Func<Operation, IEnumerable<string>> steps = prepare();
            Operation operation = OperationRunner.Start(tool, group ?? ToolGroups.GameSetup, requestId, fingerprint, steps);
            return WithWait(request, operation, () => Envelope(true, Serialize(operation)));
        }

        private static IEnumerable<string> SaveOnly(Operation operation, string name, bool overwrite, string loadId)
        {
            object saved = null;
            foreach (string step in Save(operation, name, overwrite, loadId, false, result => saved = result))
            {
                yield return step;
            }
            operation.Result = saved;
        }

        // Saves and verifies the save. Any failure ends the operation, so nothing after it runs. Before
        // a leave or load (replacing), the game is held first and the file is checked in the same
        // frame, so the save is the game as it is replaced: nothing can change in between.
        private static IEnumerable<string> Save(Operation operation, string name, bool overwrite, string loadId, bool replacing, Action<object> done)
        {
            RequireSameGame(loadId);
            if (GameDataSaveLoader.SavingIsTemporarilyDisabled)
            {
                throw new OperationEnded(Operation.Failed, "The game is not allowing saves right now.");
            }
            string path = GenFilePaths.FilePathForSavedGame(name);
            if (!overwrite && File.Exists(path))
            {
                throw new OperationEnded(Operation.Failed, "A save named '" + name + "' appeared before saving started; pass overwrite: true to replace it.");
            }
            if (replacing)
            {
                HoldGame(operation);
            }
            int tick = Find.TickManager.TicksGame;
            SaveAttempt attempt = SaveVerifier.Save(name);
            string fileProblem;
            if (replacing)
            {
                try
                {
                    fileProblem = SaveVerifier.CheckFile(attempt.Path, attempt.PreviousWriteUtc);
                }
                catch (Exception ex)
                {
                    fileProblem = "The save file check failed: " + ex.Message;
                }
            }
            else
            {
                yield return "checkingSave";
                Task<string> check = Task.Run(() => SaveVerifier.CheckFile(attempt.Path, attempt.PreviousWriteUtc));
                while (!check.IsCompleted)
                {
                    yield return "checkingSave";
                }
                fileProblem = check.IsFaulted ? "The save file check failed: " + check.Exception.GetBaseException().Message : check.Result;
            }
            if (attempt.Errors > 0)
            {
                throw new OperationEnded(Operation.Failed, "The game logged " + attempt.Errors + (attempt.Errors == 1 ? " error" : " errors") + " while saving '" + name + "', so the save cannot be relied on: " + attempt.FirstError);
            }
            if (fileProblem != null)
            {
                bool unknownFormat = fileProblem.StartsWith("The save file is not plain XML", StringComparison.Ordinal);
                throw new OperationEnded(unknownFormat ? Operation.Unverified : Operation.Failed, fileProblem);
            }
            if (!attempt.MarkerSeen)
            {
                throw new OperationEnded(Operation.Unverified, "The game's log was not working while '" + name + "' was saved (another mod may have disabled it), so errors could not be ruled out.");
            }
            done(Dto.Obj(
                Dto.Field("name", name),
                Dto.Field("sizeBytes", new FileInfo(path).Length),
                Dto.Field("tick", tick),
                Dto.Field("gameVersion", VersionControl.CurrentVersionStringWithRev),
                Dto.Field("verified", true)));
        }

        // Before a save that a leave or load follows: an advance stops, the game pauses, and agents'
        // changes are refused until the game is replaced, so nothing changes after the save. If it is
        // not replaced after all, the game's speed comes back.
        private static void HoldGame(Operation operation)
        {
            AdvanceRunner.Revoke(operation.Tool + " (" + operation.Id + ") is saving before it replaces the game.");
            TimeSpeed before = Find.TickManager.CurTimeSpeed;
            string loadId = GameContext.LoadId;
            KeepPaused();
            operation.ProtectsGame = true;
            operation.Cleanup = () =>
            {
                if (before != TimeSpeed.Paused && ColonyLoaded() && GameContext.LoadId == loadId && Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
                {
                    Find.TickManager.CurTimeSpeed = before;
                }
            };
        }

        private static void KeepPaused()
        {
            if (ColonyLoaded() && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }
        }

        private static IEnumerable<string> Load(Operation operation, string name, string saveFirst, bool overwrite, string loadId)
        {
            object saved = null;
            if (saveFirst != null)
            {
                foreach (string step in Save(operation, saveFirst, overwrite, loadId, true, result => saved = result))
                {
                    yield return step;
                }
            }
            RequireSameGame(loadId);
            string before = Current.Game == null ? null : GameContext.LoadId;
            AdvanceRunner.Revoke("load_game (" + operation.Id + ") is replacing the game.");
            List<Window> open = Find.WindowStack.Windows.ToList();
            GameDataSaveLoader.CheckVersionAndLoadGame(name);
            if (!LongEventHandler.AnyEventNowOrWaiting)
            {
                // The game asked the player first, about the save's version or mods. Every window it
                // opened for that counts, since a game version may open more than one.
                List<Window> asking = Find.WindowStack.Windows.Where(window => !open.Contains(window)).ToList();
                if (asking.Count == 0)
                {
                    throw new OperationEnded(Operation.Failed, "The game did not start loading '" + name + "'.");
                }
                // The question belongs to this file as it was when asked; while the player answers,
                // a colony saved first stays held as it was saved.
                string file = GenFilePaths.FilePathForSavedGame(name);
                DateTime written = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue;
                string message;
                string kind = LoadQuestion(name, asking, out message);
                Func<string> stale = () => !File.Exists(file) || File.GetLastWriteTimeUtc(file) != written ? "The save '" + name + "' changed while the game was asking, so the question was withdrawn." : null;
                foreach (string step in Confirmations.Await(operation, asking, kind, message, false, () => LongEventHandler.AnyEventNowOrWaiting, stale, saveFirst != null ? (Action)KeepPaused : null, Dto.Field("save", name)))
                {
                    yield return step;
                }
                if (!LongEventHandler.AnyEventNowOrWaiting)
                {
                    throw new OperationEnded(Operation.Declined, "The player chose not to load '" + name + "'.");
                }
            }
            foreach (string step in AwaitTransition(operation, "loading", () => Current.ProgramState == ProgramState.Playing && Current.Game != null && GameContext.LoadId != before))
            {
                yield return step;
            }
            operation.Result = Dto.Obj(
                Dto.Field("loaded", name),
                Dto.Field("loadId", GameContext.LoadId),
                Dto.Field("tick", Find.TickManager.TicksGame),
                Dto.Field("saved", saved));
        }

        internal static IEnumerable<string> Leave(Operation operation, string saveFirst, bool overwrite, string loadId)
        {
            object saved = null;
            if (saveFirst != null)
            {
                foreach (string step in Save(operation, saveFirst, overwrite, loadId, true, result => saved = result))
                {
                    yield return step;
                }
            }
            RequireSameGame(loadId);
            AdvanceRunner.Revoke("leave_game (" + operation.Id + ") is leaving the game.");
            operation.InNativeStep = true;
            operation.ReplacingGame = true;
            GenScene.GoToMainMenu();
            foreach (string step in AwaitTransition(operation, "leaving", () => Current.ProgramState == ProgramState.Entry && Current.Game == null))
            {
                yield return step;
            }
            operation.Result = Dto.Obj(
                Dto.Field("atMainMenu", true),
                Dto.Field("saved", saved));
        }

        // Waits for a native game change to settle. It cannot be interrupted, so cancellation and
        // revocation wait for it; a return to the main menu partway through a load means it failed.
        internal static IEnumerable<string> AwaitTransition(Operation operation, string step, Func<bool> arrived)
        {
            operation.InNativeStep = true;
            operation.ReplacingGame = true;
            DateTime started = DateTime.UtcNow;
            DateTime? atMenuSince = null;
            while (LongEventHandler.AnyEventNowOrWaiting || !arrived())
            {
                if (DateTime.UtcNow - started > TransitionTimeout)
                {
                    throw new OperationEnded(Operation.Failed, "RimWorld did not finish " + step + " within " + TransitionTimeout.TotalMinutes + " minutes.");
                }
                bool idleAtMenu = step == "loading" && !LongEventHandler.AnyEventNowOrWaiting && Current.ProgramState == ProgramState.Entry;
                atMenuSince = idleAtMenu ? atMenuSince ?? DateTime.UtcNow : (DateTime?)null;
                if (atMenuSince.HasValue && DateTime.UtcNow - atMenuSince.Value > ReturnedToMenu)
                {
                    throw new OperationEnded(Operation.Failed, "The save did not load, and RimWorld is at the main menu. get_game_log may say why.");
                }
                yield return step;
            }
            operation.InNativeStep = false;
            operation.ReplacingGame = false;
        }

        // A command planned against one game must not act on another loaded in the meantime.
        private static void RequireSameGame(string loadId)
        {
            string current = Current.Game == null ? null : GameContext.LoadId;
            if (current != loadId)
            {
                throw new OperationEnded(Operation.Failed, "The loaded game changed before this operation acted, so it did nothing.");
            }
        }

        // What the game is asking about, worked out from the save's header with the same checks the
        // game makes, rather than from its dialog classes, which differ between game versions.
        private static string LoadQuestion(string name, List<Window> asking, out string message)
        {
            SaveHeader header = SaveHeader.Read(GenFilePaths.FilePathForSavedGame(name));
            List<string> runningIds = LoadedModManager.RunningMods.Select(mod => mod.PackageId).ToList();
            List<string> runningFolders = LoadedModManager.RunningMods.Select(mod => mod.FolderName).ToList();
            bool modsDiffer = header.Problem == null && !header.ModsMatch(runningIds, runningFolders);
            string text = asking.OfType<Dialog_MessageBox>().Select(box => box.text.ToString().StripTags()).FirstOrDefault(each => !string.IsNullOrWhiteSpace(each));
            message = text ?? (modsDiffer
                ? "The save was made with a different mod list (" + header.ModIds.Count + " mods; " + runningIds.Count + " are running)."
                : "The save was made with another game version (" + (header.GameVersion ?? "unknown") + "; this is " + VersionControl.CurrentVersionStringWithRev + ").");
            return modsDiffer ? "modMismatch" : "versionMismatch";
        }

        internal static bool ColonyLoaded()
        {
            return Current.ProgramState == ProgramState.Playing && Current.Game != null;
        }

        internal static void RequireSettled()
        {
            if (LongEventHandler.AnyEventNowOrWaiting)
            {
                throw new CommandException(409, "RimWorld is busy loading or saving; try again when it finishes.");
            }
            if (Current.ProgramState == ProgramState.MapInitializing)
            {
                throw new CommandException(409, "RimWorld is still setting up a map; try again when it finishes.");
            }
        }

        internal static void RequireSavable()
        {
            RequireSettled();
            if (!ColonyLoaded())
            {
                throw new CommandException(409, "No game is loaded, so there is nothing to save.");
            }
            if (GameDataSaveLoader.SavingIsTemporarilyDisabled)
            {
                throw new CommandException(409, "The game is not allowing saves right now.");
            }
        }

        internal static string ValidateSaveName(string name, string field)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new CommandException(400, "Field '" + field + "' must be a save name.");
            }
            string trimmed = name.Trim();
            if (trimmed.Length > MaxSaveNameLength || !GenText.IsValidFilename(trimmed))
            {
                throw new CommandException(400, "Field '" + field + "' is not a usable save name: at most " + MaxSaveNameLength + " characters, without \\ / : * ? \" < > |.");
            }
            return trimmed;
        }

        internal static void RequireFreeName(string name, bool overwrite, string field)
        {
            if (!overwrite && File.Exists(GenFilePaths.FilePathForSavedGame(name)))
            {
                throw new CommandException(409, "A save named '" + name + "' already exists. Pass overwrite: true to replace it, or choose another " + field + ".");
            }
        }

        private static Operation FindOperation(string id)
        {
            Operation operation = OperationRunner.Find(id);
            if (operation == null)
            {
                throw new CommandException(404, "No operation " + id + ". Every running operation and the last " + OperationRunner.MaxCompleted + " finished ones are kept.");
            }
            return operation;
        }

        internal static Dictionary<string, object> Serialize(Operation operation)
        {
            DateTime end = operation.FinishedUtc ?? DateTime.UtcNow;
            return Dto.Obj(
                Dto.Field("operationId", operation.Id),
                Dto.Field("tool", operation.Tool),
                Dto.Field("state", operation.State),
                Dto.Field("done", operation.Done),
                Dto.Field("step", operation.Done ? null : operation.Step),
                Dto.Field("message", operation.Message),
                Dto.Field("confirmation", operation.Confirmation),
                Dto.Field("result", operation.Result),
                Dto.Field("requestId", operation.RequestId),
                Dto.Field("elapsedSeconds", Math.Round((end - operation.CreatedUtc).TotalSeconds, 1)));
        }

        // A wait changes when the reply arrives, not what it describes. Waiting happens off the main
        // thread, so a load or a scene change keeps running.
        internal static BridgeResponse WithWait(BridgeRequest request, Operation operation, Func<BridgeResponse> reply)
        {
            BridgeResponse response = reply();
            double seconds;
            string text = ReadRequest.FromUri(request.Uri).Get("waitSeconds");
            if (operation != null && !operation.Done && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds) && seconds > 0)
            {
                response.Deferred = new DeferredReply(operation.Finished, Math.Min(MaxWaitSeconds, seconds), reply);
            }
            return response;
        }

        // Session replies avoid reading the game while it is being replaced, when its maps and clock
        // may be half built.
        internal static BridgeResponse Envelope(bool changed, object data)
        {
            bool settled = !LongEventHandler.AnyEventNowOrWaiting && !OperationRunner.ReplacingGame && Current.ProgramState != ProgramState.MapInitializing;
            Game game = settled ? Current.Game : null;
            object envelope = Dto.Obj(
                Dto.Field("schemaVersion", "1"),
                Dto.Field("loadId", game == null ? null : GameContext.LoadId),
                Dto.Field("tick", game != null && Current.ProgramState == ProgramState.Playing ? (object)Find.TickManager.TicksGame : null),
                Dto.Field("programState", Current.ProgramState.ToString()),
                Dto.Field("changed", changed),
                Dto.Field("data", data));
            return BridgeResponse.Json(200, Json.Serialize(envelope));
        }

        [DataContract]
        private sealed class SaveGameBody
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "overwrite")]
            public bool? Overwrite { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }

        [DataContract]
        private sealed class ReplaceBody
        {
            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "discardUnsaved")]
            public bool? DiscardUnsaved { get; set; }

            [DataMember(Name = "saveFirst")]
            public string SaveFirst { get; set; }

            [DataMember(Name = "overwrite")]
            public bool? Overwrite { get; set; }

            [DataMember(Name = "requestId")]
            public string RequestId { get; set; }
        }
    }
}
