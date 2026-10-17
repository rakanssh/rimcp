using UnityEngine;
using Verse;

namespace RiMCP.UI
{
    // Shown under the game's question while a colony saved first waits to be replaced. It keeps the
    // game paused and blocks input to the map and the windows below, as the game's own message boxes
    // do; the question above it still takes input. Some of the game's questions, such as the mod
    // list warning, let input through on their own.
    internal sealed class HeldGameNotice : Window
    {
        private readonly string text;

        public HeldGameNotice(string text)
        {
            this.text = text;
            forcePause = true;
            absorbInputAroundWindow = true;
            preventCameraMotion = true;
            closeOnCancel = false;
            closeOnAccept = false;
            closeOnClickedOutside = false;
            doCloseX = false;
            doCloseButton = false;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(520f, 90f); }
        }

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect((Verse.UI.screenWidth - InitialSize.x) / 2f, 24f, InitialSize.x, InitialSize.y);
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(inRect, text);
        }
    }
}
