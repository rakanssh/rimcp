using System;
using System.Linq;
using System.Reflection;
using RimWorld;
using Verse;

namespace RiMCP.Bridge
{
    internal static class GameContext
    {
        // Distinguishes RimWorld sessions, so a load id cannot repeat after a restart.
        public static readonly string SessionId = Guid.NewGuid().ToString("N").Substring(0, 8);
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
                return SessionId + "-" + loads;
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

        // Whether this is a Multiplayer mod session, read from the mod's public API
        // (Multiplayer.API.MP.IsInMultiplayer). Having the mod loaded for single-player games is
        // not enough.
        public static bool InMultiplayer
        {
            get
            {
                if (multiplayerCheck == null)
                {
                    Type api = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(assembly => assembly.GetType("Multiplayer.API.MP", false))
                        .FirstOrDefault(type => type != null);
                    PropertyInfo flag = api == null ? null : api.GetProperty("IsInMultiplayer", BindingFlags.Public | BindingFlags.Static);
                    multiplayerCheck = flag == null ? (Func<bool>)(() => false) : () => (bool)flag.GetValue(null, null);
                }
                return multiplayerCheck();
            }
        }

        private static Func<bool> multiplayerCheck;
    }
}
