using System;
using System.Collections.Generic;
using System.Diagnostics;
using RiMCP.Settings;

namespace RiMCP.Bridge
{
    internal static class BridgeRuntime
    {
        private const int MainThreadWaitMs = 10000;
        private const int MaxQueuedRequestsPerFrame = 16;
        private const double MainThreadBudgetMsPerFrame = 4;

        private static readonly MainThreadDispatcher Dispatcher = new MainThreadDispatcher();
        private static readonly RecentLog RecentLog = new RecentLog(12);
        private static BridgeServer server;
        private static RiMCPSettings settings;
        private static volatile bool allowColonyChanges;
        private static volatile bool applicationFocused = true;
        private static volatile bool runInBackground;

        public static bool AllowColonyChanges
        {
            get { return allowColonyChanges; }
        }

        public static bool IsRunning
        {
            get { return server != null && server.IsRunning; }
        }

        public static int RecentClientCount
        {
            get { return server == null ? 0 : server.RecentClientCount; }
        }

        public static string LastRequestDescription
        {
            get
            {
                if (server == null || !server.LastRequestUtc.HasValue)
                {
                    return "never";
                }

                TimeSpan age = DateTime.UtcNow - server.LastRequestUtc.Value;
                return age.TotalSeconds < 2 ? "just now" : ((int)age.TotalSeconds) + "s ago";
            }
        }

        public static IEnumerable<string> RecentLogLines
        {
            get { return RecentLog.Lines; }
        }

        public static void Configure(RiMCPSettings newSettings)
        {
            settings = newSettings;
        }

        // Unity stops calling Update while RimWorld is unfocused unless it runs in the background.
        public static bool MainThreadAvailable
        {
            get { return applicationFocused || runInBackground; }
        }

        public static void SetApplicationFocused(bool focused)
        {
            applicationFocused = focused;
        }

        public static void SetRunInBackground(bool enabled)
        {
            runInBackground = enabled;
        }

        public static BridgeResponse Dispatch(BridgeRequest request)
        {
            BridgeResponse response = RunOnMainThread(() => BridgeRouter.Handle(request));
            DeferredReply deferred = response.Deferred;
            if (deferred == null)
            {
                return response;
            }

            // Wait off the main thread so the game keeps running, then rebuild the same reply.
            Stopwatch waited = Stopwatch.StartNew();
            while (waited.Elapsed.TotalSeconds < deferred.Seconds && MainThreadAvailable && !deferred.Done.Wait(250))
            {
            }
            return RunOnMainThread(deferred.Reply);
        }

        private static BridgeResponse RunOnMainThread(Func<BridgeResponse> action)
        {
            if (!MainThreadAvailable)
            {
                return BridgeResponse.Error(503, "RimWorld is in the background with Run in background off, so it is not processing requests. Bring RimWorld to the front, or enable Run in background in its options.");
            }
            return Dispatcher.Invoke(action, MainThreadWaitMs);
        }

        public static void ProcessMainThreadQueue()
        {
            Dispatcher.ProcessQueued(MaxQueuedRequestsPerFrame, MainThreadBudgetMsPerFrame);
        }

        public static void ApplySettings()
        {
            if (settings == null)
            {
                return;
            }

            settings.EnsureToken();

            if (!settings.BridgeEnabled)
            {
                Stop();
                return;
            }

            if (server != null && server.IsRunning && server.Port == settings.Port)
            {
                server.UpdateToken(settings.Token);
                allowColonyChanges = settings.AllowColonyChanges;
                return;
            }

            Stop();
            BridgeServer newServer = new BridgeServer(settings.Port, settings.Token, Dispatch, RecentLog, () => AllowColonyChanges);
            try
            {
                newServer.Start();
                server = newServer;
                allowColonyChanges = settings.AllowColonyChanges;
            }
            catch (Exception ex)
            {
                newServer.Stop();
                server = null;
                RecentLog.Add("Bridge failed to start: " + ex.Message);
            }
        }

        public static void Stop()
        {
            allowColonyChanges = false;
            if (server != null)
            {
                server.Stop();
                server = null;
            }
        }
    }
}
