#if UNITY_5_3_OR_NEWER
using Asparagus.Sdk.Internal.Runtime;
using UnityEngine;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Auto-bootstrapped hidden <see cref="MonoBehaviour"/> host attached to a <c>DontDestroyOnLoad</c>
    /// GameObject (<c>"[AsparagusSDK]"</c>). Studios never need to add this component to scenes or prefabs.
    /// Every Unity lifecycle callback is strictly wrapped in <c>try/catch</c>.
    /// </summary>
    [DisallowMultipleComponent]
    internal sealed class AsparagusBehaviour : MonoBehaviour
    {
        private const string HostObjectName = "[AsparagusSDK]";
        private static AsparagusBehaviour _instance;

        internal AsparagusRuntime Runtime { get; private set; }

        internal static AsparagusBehaviour EnsureCreated(AsparagusRuntime runtime)
        {
            try
            {
                if (_instance != null)
                {
                    if (_instance.Runtime == null || _instance.Runtime.IsShutdown)
                    {
                        _instance.Runtime = runtime;
                    }
                    return _instance;
                }

                GameObject go = new GameObject(HostObjectName);
                go.hideFlags = HideFlags.HideInHierarchy;
                DontDestroyOnLoad(go);
                AsparagusBehaviour host = go.AddComponent<AsparagusBehaviour>();
                host.Runtime = runtime;
                _instance = host;
                return host;
            }
            catch
            {
                return null;
            }
        }

        internal static void ClearInstanceIfMatches(AsparagusBehaviour host)
        {
            if (_instance == host)
            {
                _instance = null;
            }
        }

        private void Update()
        {
            try
            {
                AsparagusRuntime rt = Runtime;
                if (rt != null && !rt.IsShutdown)
                {
                    rt.Tick();
                }
            }
            catch
            {
                // Total fault containment: never throw into Unity frame loop.
            }
        }

        private void OnApplicationPause(bool pauseStatus)
        {
            try
            {
                AsparagusRuntime rt = Runtime;
                if (rt != null && !rt.IsShutdown)
                {
                    rt.OnApplicationPause(pauseStatus);
                }
            }
            catch
            {
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            try
            {
                AsparagusRuntime rt = Runtime;
                if (rt != null && !rt.IsShutdown)
                {
                    rt.OnApplicationFocus(hasFocus);
                }
            }
            catch
            {
            }
        }

        private void OnApplicationQuit()
        {
            try
            {
                AsparagusRuntime rt = Runtime;
                if (rt != null && !rt.IsShutdown)
                {
                    rt.OnApplicationQuit();
                }
            }
            catch
            {
            }
        }

        private void OnDestroy()
        {
            try
            {
                AsparagusRuntime rt = Runtime;
                Runtime = null;
                ClearInstanceIfMatches(this);
                if (rt != null && !rt.IsShutdown)
                {
                    rt.Shutdown();
                }
            }
            catch
            {
            }
        }
    }
}
#endif
