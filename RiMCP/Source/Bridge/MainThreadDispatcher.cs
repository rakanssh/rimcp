using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RiMCP.Bridge
{
    internal sealed class MainThreadDispatcher
    {
        private const int MaxQueuedWorkItems = 64;

        private readonly ConcurrentQueue<WorkItem> queue = new ConcurrentQueue<WorkItem>();
        private int queuedWorkItems;

        public int QueuedCount
        {
            get { return Interlocked.CompareExchange(ref queuedWorkItems, 0, 0); }
        }

        public BridgeResponse Invoke(Func<BridgeResponse> action, int timeoutMs)
        {
            if (!TryReserveQueueSlot())
            {
                return BridgeResponse.Error(429, "RimMCP is busy; too many requests are waiting for the RimWorld main thread. Try again shortly.");
            }

            WorkItem item = new WorkItem(action);
            queue.Enqueue(item);
            if (!item.Done.Wait(timeoutMs))
            {
                // Work the main thread has started runs to the end, so its reply is the one to return.
                if (item.TryCancel())
                {
                    return BridgeResponse.Error(504, "Timed out waiting for the RimWorld main thread; the request did not run.");
                }
                item.Done.Wait();
            }
            return item.Response;
        }

        // Runs at least one item per frame, then stops once the frame budget is spent so a burst
        // of requests spreads over several frames instead of stalling one.
        public void ProcessQueued(int maxItems, double frameBudgetMs)
        {
            Stopwatch frame = Stopwatch.StartNew();
            for (int i = 0; i < maxItems; i++)
            {
                if (i > 0 && frame.Elapsed.TotalMilliseconds >= frameBudgetMs)
                {
                    return;
                }

                WorkItem item;
                if (!queue.TryDequeue(out item))
                {
                    return;
                }

                if (!item.TryStart())
                {
                    Complete(item);
                    continue;
                }

                Stopwatch work = Stopwatch.StartNew();
                try
                {
                    item.Response = item.Action();
                }
                catch (Exception ex)
                {
                    item.Response = BridgeResponse.Error(500, ex.GetType().Name + ": " + ex.Message);
                    BridgeRouter.LogFailure(ex);
                }
                finally
                {
                    if (item.Response != null)
                    {
                        item.Response.MainThreadMs = work.Elapsed.TotalMilliseconds;
                    }
                    Complete(item);
                }
            }
        }

        private bool TryReserveQueueSlot()
        {
            while (true)
            {
                int current = QueuedCount;
                if (current >= MaxQueuedWorkItems)
                {
                    return false;
                }
                if (Interlocked.CompareExchange(ref queuedWorkItems, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        private void Complete(WorkItem item)
        {
            item.Done.Set();
            Interlocked.Decrement(ref queuedWorkItems);
        }

        private sealed class WorkItem
        {
            public readonly Func<BridgeResponse> Action;
            private const int Queued = 0;
            private const int Started = 1;
            private const int Cancelled = 2;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            private int state;
            public BridgeResponse Response;

            public WorkItem(Func<BridgeResponse> action)
            {
                Action = action;
            }

            // Starting and cancelling race; only one of them wins.
            public bool TryStart()
            {
                return Interlocked.CompareExchange(ref state, Started, Queued) == Queued;
            }

            public bool TryCancel()
            {
                return Interlocked.CompareExchange(ref state, Cancelled, Queued) == Queued;
            }
        }
    }
}
