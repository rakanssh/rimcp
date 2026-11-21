using Verse;

namespace RiMCP.Command
{
    // Once another game is loaded, or the player leaves to the main menu, drops what RimMCP kept
    // from the old one, so the old game is not held in memory. Main thread only.
    internal static class OldGames
    {
        private static readonly System.WeakReference<Game> seen = new System.WeakReference<Game>(null);

        public static void Update()
        {
            Game known;
            if (seen.TryGetTarget(out known) ? ReferenceEquals(known, Current.Game) : Current.Game == null)
            {
                return;
            }
            seen.SetTarget(Current.Game);
            CommandReceipts.ForgetOtherGames();
            AdvanceRunner.ForgetOtherGames();
            Interactions.ForgetOtherGames();
        }
    }
}
