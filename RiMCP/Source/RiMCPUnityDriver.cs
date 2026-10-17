using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Read;
using UnityEngine;

namespace RiMCP
{
    internal sealed class RiMCPUnityDriver : MonoBehaviour
    {
        private static RiMCPUnityDriver instance;

        public static void EnsureStarted()
        {
            if (instance != null)
            {
                return;
            }

            BridgeRuntime.SetApplicationFocused(Application.isFocused);
            BridgeRuntime.SetRunInBackground(Application.runInBackground);
            GameObject gameObject = new GameObject("RiMCP Driver");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<RiMCPUnityDriver>();
        }

        private void Update()
        {
            AdvanceRunner.Update();
            OperationRunner.Update();
            FoodHistory.Update();
            BridgeRuntime.ProcessMainThreadQueue();
            // Read after handling requests: set_game_settings may have just turned this off, and an
            // unfocused game stops calling Update before the next frame.
            BridgeRuntime.SetRunInBackground(Application.runInBackground);
        }

        private void OnApplicationFocus(bool focused)
        {
            BridgeRuntime.SetApplicationFocused(focused);
        }

        private void OnApplicationQuit()
        {
            BridgeRuntime.Stop();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }
    }
}
