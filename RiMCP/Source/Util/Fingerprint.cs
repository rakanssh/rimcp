namespace RiMCP.Util
{
    internal static class Fingerprint
    {
        // 64-bit FNV-1a, stable across sessions unlike string.GetHashCode.
        public static string Of(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text ?? "")
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }
}
