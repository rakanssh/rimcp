using System;

namespace RiMCP.Bridge
{
    internal sealed class BridgeRequest
    {
        public readonly string Method;
        public readonly Uri Uri;
        public readonly string Body;

        public BridgeRequest(string method, Uri uri, string body)
        {
            Method = method;
            Uri = uri;
            Body = body;
        }
    }
}
