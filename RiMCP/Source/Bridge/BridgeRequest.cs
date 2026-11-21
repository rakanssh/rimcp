using System;

namespace RiMCP.Bridge
{
    internal sealed class BridgeRequest
    {
        public readonly string Method;
        public readonly Uri Uri;
        public readonly string Body;
        // Who sent it, as the helper names its client and session; null for other callers.
        public readonly string Client;

        public BridgeRequest(string method, Uri uri, string body) : this(method, uri, body, null)
        {
        }

        public BridgeRequest(string method, Uri uri, string body, string client)
        {
            Method = method;
            Uri = uri;
            Body = body;
            Client = client;
        }
    }
}
