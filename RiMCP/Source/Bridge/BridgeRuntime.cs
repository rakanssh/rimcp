using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RiMCP.Command;
using RiMCP.Settings;
using RiMCP.Shared;

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
        private static volatile AccessState access = AccessState.None;
        private static volatile bool applicationFocused = true;
        private static volatile bool runInBackground;

        public static AccessState Access
        {
            get { return access; }
        }

        public static bool UnattendedSetup
        {
            get { return settings != null && settings.UnattendedSetup; }
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
            // Polled by the proxy to keep the agent's tool list current. It reads only the immutable
            // access state, so it answers off the main thread, even with RimWorld in the background.
            if (request.Method == "GET" && request.Uri.AbsolutePath.TrimEnd('/') == "/v1/access")
            {
                return RiMCP.Read.ToolReadService.GetAccess(access);
            }
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
            settings.EnsureAccess();

            if (!settings.BridgeEnabled)
            {
                Disable();
                Stop();
                return;
            }

            ApplyAccess(settings.AccessPreset, settings.EnabledGroups, settings.EnabledToolsOn, settings.EnabledToolsOff);
            if (server != null && server.IsRunning && server.Port == settings.Port)
            {
                server.UpdateToken(settings.Token);
                return;
            }

            Stop();
            BridgeServer newServer = new BridgeServer(settings.Port, settings.Token, Dispatch, RecentLog, () => access);
            try
            {
                newServer.Start();
                server = newServer;
            }
            catch (Exception ex)
            {
                newServer.Stop();
                server = null;
                RecentLog.Add("Bridge failed to start: " + ex.Message);
            }
        }

        // Main thread only. Requests still waiting for the main thread are refused when they reach
        // the router, and an advance the agent owns ends and pauses. Saved settings are untouched, so
        // enabling RimMCP again restores the same access through ApplyAccess.
        internal static void Disable()
        {
            access = access.Disabled();
            AdvanceRunner.Revoke("RimMCP was disabled in its settings.");
            OperationRunner.Revoke(ToolGroups.GameSetup, "RimMCP was disabled in its settings.");
        }

        // Main thread only. Turning off Time ends and pauses an advance the agent owns, and turning off
        // Game setup stops its operations; other groups only affect new requests, because their
        // commands finish within a frame.
        internal static void ApplyAccess(string preset, IEnumerable<string> groups)
        {
            ApplyAccess(preset, groups, new string[0], new string[0]);
        }

        // Turning off advance_time ends and pauses an advance the agent owns, and turning off the tool
        // that started an operation stops it.
        internal static void ApplyAccess(string preset, IEnumerable<string> groups, IEnumerable<string> toolsOn, IEnumerable<string> toolsOff)
        {
            string[] enabled = groups.ToArray();
            string[] on = toolsOn.ToArray();
            string[] off = toolsOff.ToArray();
            AccessState current = access;
            if (!current.SameAs(preset, enabled, on, off))
            {
                access = new AccessState(preset, enabled, on, off, current.Revision + 1);
            }
            if (!access.AllowsTool("advance_time"))
            {
                AdvanceRunner.Revoke(access.Allows(ToolGroups.Time) ? "advance_time was turned off in RimMCP settings." : "Time access was turned off in RimMCP settings.");
            }
            // A queued operation never starts; a running one stops at its next step.
            Operation running = OperationRunner.Active;
            if (running != null && !access.AllowsTool(running.Tool))
            {
                OperationRunner.Revoke(running.Group, access.Allows(running.Group) ? running.Tool + " was turned off in RimMCP settings." : ToolGroups.Label(running.Group) + " access was turned off in RimMCP settings.");
            }
        }

        // Calls per tool since RimWorld started, for the settings' Activity tab.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> toolCalls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        private static DateTime? lastToolCallUtc;

        public static void CountCall(string tool)
        {
            toolCalls.AddOrUpdate(tool, 1, (name, count) => count + 1);
            lastToolCallUtc = DateTime.UtcNow;
        }

        // An agent counts as working while it called a tool in the last few seconds, or while an
        // advance or an operation it started is running.
        public static bool AgentWorking
        {
            get
            {
                return (lastToolCallUtc.HasValue && (DateTime.UtcNow - lastToolCallUtc.Value).TotalSeconds < 5) || AdvanceRunner.Running || OperationRunner.Active != null;
            }
        }

        public static IEnumerable<KeyValuePair<string, int>> ToolCalls
        {
            get { return toolCalls.ToArray(); }
        }

        public static DateTime? LastRequestUtc
        {
            get { return server == null ? null : server.LastRequestUtc; }
        }

        public static void Stop()
        {
            if (server != null)
            {
                server.Stop();
                server = null;
            }
        }
    }
}
