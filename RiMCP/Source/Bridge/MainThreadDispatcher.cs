using System;
using System.Collections.Concurrent;
using System.Threading;

namespace RiMCP.Bridge
{
    internal sealed class MainThreadDispatcher
    {
        private readonly ConcurrentQueue<WorkItem> queue = new ConcurrentQueue<WorkItem>();

        public BridgeResponse Invoke(Func<BridgeResponse> action, int timeoutMs)
        {
            WorkItem item = new WorkItem(action);
            queue.Enqueue(item);
            if (!item.Done.Wait(timeoutMs))
            {
                item.Cancel();
                return BridgeResponse.Error(504, "Timed out waiting for RimWorld main thread.");
            }
            return item.Response;
        }

        public void ProcessQueued(int maxItems)
        {
            for (int i = 0; i < maxItems; i++)
            {
                WorkItem item;
                if (!queue.TryDequeue(out item))
                {
                    return;
                }

                if (item.IsCancelled)
                {
                    item.Done.Set();
                    continue;
                }

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
                    item.Done.Set();
                }
            }
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
