using RiMCP.Bridge;
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

            GameObject gameObject = new GameObject("RiMCP Driver");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<RiMCPUnityDriver>();
        }

        private void Update()
        {
            BridgeRuntime.ProcessMainThreadQueue();
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
