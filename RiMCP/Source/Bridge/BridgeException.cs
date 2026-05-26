using System;

namespace RiMCP.Bridge
{
    internal class BridgeException : Exception
    {
        public readonly int StatusCode;

        public BridgeException(int statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
