using System;
using System.Threading;

namespace RiMCP.Bridge
{
    // The bridge waits off the main thread until Done is set or Seconds pass, then sends Reply().
    internal sealed class DeferredReply
    {
        public readonly ManualResetEventSlim Done;
        public readonly double Seconds;
        public readonly Func<BridgeResponse> Reply;

        public DeferredReply(ManualResetEventSlim done, double seconds, Func<BridgeResponse> reply)
        {
            Done = done;
            Seconds = seconds;
            Reply = reply;
        }
    }

    internal sealed class BridgeResponse
    {
        public int StatusCode;
        public string Body;
        public string ContentType;
        // Time the handler spent on RimWorld's main thread; null when it never ran there.
        public double? MainThreadMs;
        // Set by handlers that may wait for game time to pass before answering.
        public DeferredReply Deferred;

        public BridgeResponse(int statusCode, string body, string contentType = "application/json")
        {
            StatusCode = statusCode;
            Body = body;
            ContentType = contentType;
        }

        public static BridgeResponse Json(int statusCode, string body)
        {
            return new BridgeResponse(statusCode, body);
        }

        public static BridgeResponse Error(int statusCode, string message)
        {
            return Json(statusCode, "{\"error\":" + RiMCP.Util.Json.String(message) + "}");
        }
    }
}
