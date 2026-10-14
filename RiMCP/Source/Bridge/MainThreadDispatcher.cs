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
                item.Cancel();
                return BridgeResponse.Error(504, "Timed out waiting for RimWorld main thread.");
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

                if (item.IsCancelled)
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
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            private int cancelled;
            public BridgeResponse Response;

            public WorkItem(Func<BridgeResponse> action)
            {
                Action = action;
            }

            public bool IsCancelled
            {
                get { return Interlocked.CompareExchange(ref cancelled, 0, 0) == 1; }
            }

            public void Cancel()
            {
                Interlocked.Exchange(ref cancelled, 1);
            }
        }
    }
}
