using System;
using System.Security.Cryptography;
using Verse;

namespace RiMCP.Settings
{
    public sealed class RiMCPSettings : ModSettings
    {
        public bool BridgeEnabled = true;
        public int Port = 39571;
        public string Token;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref BridgeEnabled, "bridgeEnabled", true);
            Scribe_Values.Look(ref Port, "port", 39571);
            Scribe_Values.Look(ref Token, "token");
        }

        public void EnsureToken()
        {
            if (string.IsNullOrEmpty(Token))
            {
                RegenerateToken();
            }
        }

        public void RegenerateToken()
        {
            byte[] bytes = new byte[32];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            Token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
