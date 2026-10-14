using RiMCP.Bridge;
using RiMCP.Command;
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
            BridgeRuntime.SetRunInBackground(Application.runInBackground);
            AdvanceRunner.Update();
            BridgeRuntime.ProcessMainThreadQueue();
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
