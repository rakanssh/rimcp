using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RiMCP.Command
{
    // One session transition, such as saving, loading, or leaving a game. It belongs to the RimWorld
    // process, not the game, so its status stays readable after the game it started in is gone.
    // Main thread only, apart from Finished.
    internal sealed class Operation
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string AwaitingConfirmation = "awaitingConfirmation";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Unverified = "unverified";
        public const string Declined = "declined";
        public const string Cancelled = "cancelled";
        public const string Revoked = "revoked";

        public readonly string Id;
        public readonly string Tool;
        public readonly string Group;
        public readonly string RequestId;
        public readonly DateTime CreatedUtc = DateTime.UtcNow;
        public readonly ManualResetEventSlim Finished = new ManualResetEventSlim(false);

        public string State = Queued;
        public string Step;
        public string Message;
        public object Confirmation;
        public object Result;
        public DateTime? FinishedUtc;

        // A native step, such as a scene change or the game's own save, is running and cannot be
        // interrupted; cancellation and revocation wait for it to finish.
        public bool InNativeStep;
        // The step replaces or clears the loaded game, so colony requests must wait.
        public bool ReplacingGame;

        internal IEnumerator<string> Steps;
        internal string CancelRequested;
        internal string RevokeRequested;

        public Operation(string id, string tool, string group, string requestId)
        {
            Id = id;
            Tool = tool;
            Group = group;
            RequestId = requestId;
        }

        public bool Started
        {
            get { return State != Queued; }
        }

        public bool Done
        {
            get { return IsTerminal(State); }
        }

        public static bool IsTerminal(string state)
        {
            return state == Succeeded || state == Failed || state == Unverified || state == Declined || state == Cancelled || state == Revoked;
        }
    }

    // Ends an operation from inside its steps with an outcome other than success.
    internal sealed class OperationEnded : Exception
    {
        public readonly string State;

        public OperationEnded(string state, string message) : base(message)
        {
            State = state;
        }
    }

    // What a requestId was used for, kept after the operation's detailed record is evicted, so an
    // old retry gets an explicit answer instead of running again.
    internal sealed class RequestRecord
    {
        public string RequestId;
        public string Tool;
        public string Fingerprint;
        public string OperationId;
        public string Outcome;
    }

    // Runs session operations one at a time, one step per frame. Detailed records are kept for every
    // running operation and the last MaxCompleted finished ones; compact request records for the last
    // MaxRequestRecords requests. Both live for the RimWorld process and are not saved. Main thread only.
    internal static class OperationRunner
    {
        public const int MaxCompleted = 32;
        public const int MaxRequestRecords = 1000;

        private static readonly List<Operation> operations = new List<Operation>();
        private static readonly Dictionary<string, RequestRecord> requests = new Dictionary<string, RequestRecord>(StringComparer.Ordinal);
        private static readonly Queue<string> requestOrder = new Queue<string>();
        private static Operation active;
        private static int nextId;

        public static Operation Active
        {
            get { return active != null && !active.Done ? active : null; }
        }

        // An operation is replacing or clearing the loaded game right now.
        public static bool ReplacingGame
        {
            get { return active != null && !active.Done && active.ReplacingGame; }
        }

        public static Operation Find(string id)
        {
            return operations.FirstOrDefault(operation => operation.Id == id);
        }

        public static RequestRecord FindRequest(string requestId)
        {
            RequestRecord record;
            return requestId != null && requests.TryGetValue(requestId, out record) ? record : null;
        }

        // What a repeated requestId did before: its record, and its operation while the detailed
        // record is still kept (null once evicted, when the record alone answers). A requestId reused
        // with other arguments is refused. Null record for a new requestId.
        public static RequestRecord Recall(string tool, string requestId, string fingerprint, out Operation operation)
        {
            operation = null;
            RequestRecord record = FindRequest(requestId);
            if (record == null)
            {
                return null;
            }
            if (record.Tool != tool || record.Fingerprint != fingerprint)
            {
                throw new CommandException(409, "requestId '" + requestId + "' was already used for a different " + record.Tool + " call (operation " + record.OperationId + ").");
            }
            operation = Find(record.OperationId);
            return record;
        }

        // Starts an operation for a request Recall did not recognise. A new operation is refused while
        // another runs.
        public static Operation Start(string tool, string group, string requestId, string fingerprint, Func<Operation, IEnumerable<string>> steps)
        {
            Operation known;
            if (Recall(tool, requestId, fingerprint, out known) != null)
            {
                throw new InvalidOperationException("Start called for a requestId that is already recorded; call Recall first.");
            }
            Operation running = Active;
            if (running != null)
            {
                throw new CommandException(409, "Operation " + running.Id + " (" + running.Tool + ") is still " + running.State + ". Wait for it with get_operation, or stop it with cancel_operation.");
            }

            Operation operation = new Operation("op-" + (++nextId), tool, group, requestId);
            operation.Steps = steps(operation).GetEnumerator();
            active = operation;
            operations.Add(operation);
            if (requestId != null)
            {
                Remember(new RequestRecord { RequestId = requestId, Tool = tool, Fingerprint = fingerprint, OperationId = operation.Id, Outcome = Operation.Queued });
            }
            return operation;
        }

        // Advances the running operation by one step. A requested cancellation or revocation takes
        // effect here, between steps, unless a native step is still running.
        public static void Update()
        {
            Operation operation = Active;
            if (operation == null)
            {
                return;
            }
            if (!operation.InNativeStep)
            {
                if (operation.RevokeRequested != null)
                {
                    Finish(operation, Operation.Revoked, operation.RevokeRequested);
                    return;
                }
                if (operation.CancelRequested != null)
                {
                    Finish(operation, Operation.Cancelled, operation.CancelRequested);
                    return;
                }
            }
            try
            {
                if (operation.State == Operation.Queued)
                {
                    operation.State = Operation.Running;
                }
                if (operation.Steps.MoveNext())
                {
                    operation.Step = operation.Steps.Current;
                    return;
                }
                if (!operation.Done)
                {
                    Finish(operation, Operation.Succeeded, operation.Message);
                }
            }
            catch (OperationEnded ended)
            {
                Finish(operation, ended.State, ended.Message);
            }
            catch (CommandException ex)
            {
                Finish(operation, Operation.Failed, ex.Message);
            }
            catch (Exception ex)
            {
                Finish(operation, Operation.Failed, "Unexpected " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Stops an operation at its next safe point. Returns false when it had already finished.
        public static bool Cancel(Operation operation, string reason)
        {
            if (operation.Done)
            {
                return false;
            }
            if (!operation.Started)
            {
                Finish(operation, Operation.Cancelled, reason);
                return true;
            }
            operation.CancelRequested = reason;
            return true;
        }

        // Access was taken away: an operation not yet started never starts, and a running one stops at
        // its next safe point. Only operations of the given group are affected.
        public static void Revoke(string group, string reason)
        {
            Operation operation = Active;
            if (operation == null || operation.Group != group)
            {
                return;
            }
            if (!operation.Started)
            {
                Finish(operation, Operation.Revoked, reason);
                return;
            }
            operation.RevokeRequested = reason;
        }

        private static void Finish(Operation operation, string state, string message)
        {
            operation.State = state;
            operation.Message = message;
            operation.Confirmation = null;
            operation.InNativeStep = false;
            operation.ReplacingGame = false;
            operation.FinishedUtc = DateTime.UtcNow;
            if (operation.Steps != null)
            {
                operation.Steps.Dispose();
            }
            RequestRecord record = FindRequest(operation.RequestId);
            if (record != null && record.OperationId == operation.Id)
            {
                record.Outcome = state + (string.IsNullOrEmpty(message) ? "" : ": " + message);
            }
            // Only finished operations are evicted, oldest first.
            List<Operation> finished = operations.Where(item => item.Done).ToList();
            for (int i = 0; i < finished.Count - MaxCompleted; i++)
            {
                operations.Remove(finished[i]);
            }
            operation.Finished.Set();
        }

        private static void Remember(RequestRecord record)
        {
            requests[record.RequestId] = record;
            requestOrder.Enqueue(record.RequestId);
            while (requestOrder.Count > MaxRequestRecords)
            {
                requests.Remove(requestOrder.Dequeue());
            }
        }

        // For tests: forget every operation and request.
        internal static void Reset()
        {
            operations.Clear();
            requests.Clear();
            requestOrder.Clear();
            active = null;
        }
    }
}
