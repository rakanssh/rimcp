using System;
using System.Linq;
using RimWorld;
using Verse;

namespace RiMCP.Bridge
{
    internal static class GameContext
    {
        // Distinguishes RimWorld sessions, so a load id cannot repeat after a restart.
        private static readonly string SessionToken = Guid.NewGuid().ToString("N").Substring(0, 8);
        private static readonly System.WeakReference<Game> loadedGame = new System.WeakReference<Game>(null);
        private static int loads;

        // Identifies the loaded game. It changes on every load, including reloading the same save,
        // so a command planned against one load can be refused after another. Main thread only.
        public static string LoadId
        {
            get
            {
                Game game = Current.Game;
                if (game == null)
                {
                    return null;
                }
                Game known;
                if (!loadedGame.TryGetTarget(out known) || !ReferenceEquals(known, game))
                {
                    loadedGame.SetTarget(game);
                    loads++;
                }
                return SessionToken + "-" + loads;
            }
        }

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
