using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Bridge
{
    internal static class GameContext
    {
        public static Map ResolveMap(string mapId)
        {
            if (Find.Maps == null || Find.Maps.Count == 0)
            {
                return null;
            }
            if (!string.IsNullOrWhiteSpace(mapId))
            {
                return Find.Maps.FirstOrDefault(map => map.uniqueID.ToString() == mapId);
            }
            return Find.CurrentMap ?? Find.Maps.FirstOrDefault();
        }
    }
}
