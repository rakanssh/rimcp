using System;
using System.Collections.Generic;
using RiMCP.Settings;

namespace RiMCP.Bridge
{
    internal static class BridgeRuntime
    {
        private const int MainThreadWaitMs = 1200;
        private const int MaxQueuedRequestsPerFrame = 16;

        private static readonly MainThreadDispatcher Dispatcher = new MainThreadDispatcher();
        private static readonly RecentLog RecentLog = new RecentLog(12);
        private static BridgeServer server;
        private static RiMCPSettings settings;

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

        public static BridgeResponse DispatchRead(string path)
        {
            return Dispatcher.Invoke(() => BridgeRouter.Handle(path), MainThreadWaitMs);
        }

        public static void ProcessMainThreadQueue()
        {
            Dispatcher.ProcessQueued(MaxQueuedRequestsPerFrame);
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
                return;
            }

            Stop();
            BridgeServer newServer = new BridgeServer(settings.Port, settings.Token, DispatchRead, RecentLog);
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
