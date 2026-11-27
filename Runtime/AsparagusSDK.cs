using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Network;
using Asparagus.Sdk.Internal.Runtime;

namespace Asparagus.Sdk
{
    /// <summary>
    /// Fault-contained public static facade for the Asparagus Unity SDK (Schema v3).
    /// Lazily auto-bootstraps its hidden <c>DontDestroyOnLoad</c> runtime host on first session start
    /// (zero scene/prefab wiring required). No method ever throws an exception into host game code.
    /// </summary>
    public static class AsparagusSDK
    {
        public static readonly SdkVersion SdkVersion = new SdkVersion(1, 0, 0);

        private sealed class StaticEventSink : IEventSink
        {
            public void SendEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
            {
                AsparagusSDK.SendEvent(eventName, eventData, span);
            }

            public void SendOutcomeEvent(string eventName, string outcome, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
            {
                AsparagusSDK.SendOutcomeEvent(eventName, outcome, eventData, span);
            }
        }

        private static readonly object Sync = new object();
        private static readonly CoreEventSender StaticCoreEventSender = new CoreEventSender(new StaticEventSink());

        private static AsparagusRuntime _runtime;
#if UNITY_5_3_OR_NEWER
        private static AsparagusBehaviour _behaviour;
#endif

        public static CoreEventSender SendCoreEvent
        {
            get { return StaticCoreEventSender; }
        }

        public static bool StartSession(
            string controlPlaneBaseUrl,
            string apiKey,
            IReadOnlyDictionary<string, Value> sessionProperties = null,
            string appVersion = null,
            string buildNumber = null,
            bool demo = false)
        {
            try
            {
                AsparagusRuntime rt = EnsureRuntime();
                return rt != null && rt.StartSession(controlPlaneBaseUrl, apiKey, sessionProperties, appVersion, buildNumber, demo);
            }
            catch
            {
                return false;
            }
        }

        public static bool StartSessionWithStageOverride(StageBootstrapConfig stageConfig)
        {
            try
            {
                AsparagusRuntime rt = EnsureRuntime();
                return rt != null && rt.StartSessionWithStageOverride(stageConfig);
            }
            catch
            {
                return false;
            }
        }

        public static void UpdateStageToken(string newToken)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.UpdateStageToken(newToken);
                }
            }
            catch
            {
            }
        }

        public static void StopSession()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.StopSession();
                }
            }
            catch
            {
            }
        }

        public static void StartTelemetry()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.StartTelemetry();
                }
            }
            catch
            {
            }
        }

        public static void StopTelemetry()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.StopTelemetry();
                }
            }
            catch
            {
            }
        }

        public static void SetTouchPaused(bool paused)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.SetTouchPaused(paused);
                }
            }
            catch
            {
            }
        }

        public static void SendEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.SendEvent(eventName, eventData, span);
                }
            }
            catch
            {
            }
        }

        public static void SendOutcomeEvent(string eventName, string outcome, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.SendOutcomeEvent(eventName, outcome, eventData, span);
                }
            }
            catch
            {
            }
        }

        public static void SetAttributes(IReadOnlyDictionary<string, Value> attributes)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.SetAttributes(attributes);
                }
            }
            catch
            {
            }
        }

        public static void SetAttribute(string key, Value value)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.SetAttribute(key, value);
                }
            }
            catch
            {
            }
        }

        public static void SetAttribute(string key, string value)
        {
            SetAttribute(key, Value.FromText(value));
        }

        public static void SetAttribute(string key, double value)
        {
            SetAttribute(key, Value.FromNumber(value));
        }

        public static void SetAttribute(string key, bool value)
        {
            SetAttribute(key, Value.FromFlag(value));
        }

        public static void RemoveAttribute(string key)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.RemoveAttribute(key);
                }
            }
            catch
            {
            }
        }

        public static void ClearAttributes()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.ClearAttributes();
                }
            }
            catch
            {
            }
        }

        public static void PersonalDataRequested(string jsonBody, Action<bool, long> callback)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.PersonalDataRequested(jsonBody, callback);
                }
                else
                {
                    callback?.Invoke(false, 0L);
                }
            }
            catch
            {
            }
        }

        public static void PersonalDataDeletionRequested(string jsonBody, Action<bool, long> callback)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.PersonalDataDeletionRequested(jsonBody, callback);
                }
                else
                {
                    callback?.Invoke(false, 0L);
                }
            }
            catch
            {
            }
        }

        [Obsolete("Turnip activity prediction is not supported in Asparagus v3. Always returns not available with zero network calls.")]
        public static void RequestActivity(Action<bool, string> callback = null)
        {
            try
            {
                callback?.Invoke(false, "not available");
            }
            catch
            {
            }
        }

        [Obsolete("Turnip affect prediction is not supported in Asparagus v3. Always returns not available with zero network calls.")]
        public static void RequestAffect(Action<bool, float, string> callback = null)
        {
            try
            {
                callback?.Invoke(false, 0f, "not available");
            }
            catch
            {
            }
        }

        [Obsolete("Use SendEvent or SendCoreEvent instead. Forwards directly into the Asparagus event pipeline with zero Turnip network calls.")]
        public static void FireEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string source = null)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.FireEvent(eventName, eventData, source);
                }
            }
            catch
            {
            }
        }

        public static bool ForceCloseCurrentWindow(CloseReason reason = CloseReason.WindowFull)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                return rt != null && rt.ForceCloseCurrentWindow(reason);
            }
            catch
            {
                return false;
            }
        }

        public static bool TryExportCurrentBatch(
            string outputDirectory,
            out string rawFilePath,
            out string gzipFilePath,
            out string error)
        {
            rawFilePath = string.Empty;
            gzipFilePath = string.Empty;
            error = null;

            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt == null)
                {
                    error = "SDK is not initialized.";
                    return false;
                }

                return rt.TryExportClosedBatchFiles(outputDirectory, out rawFilePath, out gzipFilePath, out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void InjectSimulatedMotionSample(in RawMotionSample sample)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.InjectSimulatedMotionSample(in sample);
                }
            }
            catch
            {
            }
        }

        public static void InjectSimulatedTouchSample(in RawTouchSample sample)
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                if (rt != null)
                {
                    rt.InjectSimulatedTouchSample(in sample);
                }
            }
            catch
            {
            }
        }

        public static AsparagusStatusSnapshot GetStatusSnapshot()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                return rt != null ? rt.GetStatusSnapshot() : default;
            }
            catch
            {
                return default;
            }
        }

        public static long GetCurrentUnityTimeUs()
        {
            try
            {
                AsparagusRuntime rt = GetRuntimeOrNull();
                return rt != null ? rt.CurrentUnityTimeUs : 0L;
            }
            catch
            {
                return 0L;
            }
        }

        public static void Shutdown()
        {
            try
            {
                AsparagusRuntime rtToShutdown;
#if UNITY_5_3_OR_NEWER
                AsparagusBehaviour hostToDestroy;
#endif
                lock (Sync)
                {
                    rtToShutdown = _runtime;
                    _runtime = null;
#if UNITY_5_3_OR_NEWER
                    hostToDestroy = _behaviour;
                    _behaviour = null;
#endif
                }

                if (rtToShutdown != null)
                {
                    rtToShutdown.Shutdown();
                }

#if UNITY_5_3_OR_NEWER
                if (hostToDestroy != null)
                {
                    try
                    {
                        AsparagusBehaviour.ClearInstanceIfMatches(hostToDestroy);
                        UnityEngine.Object.Destroy(hostToDestroy.gameObject);
                    }
                    catch
                    {
                    }
                }
#endif
            }
            catch
            {
            }
        }

        private static AsparagusRuntime GetRuntimeOrNull()
        {
            lock (Sync)
            {
                return _runtime != null && !_runtime.IsShutdown ? _runtime : null;
            }
        }

        private static AsparagusRuntime EnsureRuntime()
        {
            lock (Sync)
            {
                if (_runtime == null || _runtime.IsShutdown)
                {
                    _runtime = new AsparagusRuntime(new AsparagusRuntimeOptions
                    {
                        SdkVersion = SdkVersion
                    });
                }

#if UNITY_5_3_OR_NEWER
                if (_behaviour == null)
                {
                    _behaviour = AsparagusBehaviour.EnsureCreated(_runtime);
                }
#endif
                return _runtime;
            }
        }
    }
}
