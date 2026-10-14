namespace RiMCP.Bridge
{
    internal sealed class BridgeResponse
    {
        public int StatusCode;
        public string Body;
        public string ContentType;
        // Time the handler spent on RimWorld's main thread; null when it never ran there.
        public double? MainThreadMs;

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
