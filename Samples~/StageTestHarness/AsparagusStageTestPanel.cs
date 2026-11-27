#if UNITY_5_3_OR_NEWER
using System;
using System.Collections.Generic;
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Network;
using UnityEngine;

namespace Asparagus.Sdk.Samples
{
    /// <summary>
    /// Self-contained DPI-scaled IMGUI Stage Test &amp; Diagnostics Panel.
    /// Drop this component onto any GameObject in a scene and press Play (Editor, Android, or iOS).
    /// Reads optional runtime credentials from environment variables only; contains zero hardcoded secrets.
    /// </summary>
    public sealed class AsparagusStageTestPanel : MonoBehaviour
    {
        private const int MaxLogLines = 12;

        private bool _useStageOverride = true;
        private string _stageEndpoint = string.Empty;
        private string _stageToken = string.Empty;
        private string _idOrg = string.Empty;
        private string _idDeployment = string.Empty;
        private string _idSource = string.Empty;
        private string _idSession = string.Empty;

        private string _controlPlaneUrl = string.Empty;
        private string _apiKey = string.Empty;
        private bool _demoFlag;

        private bool _touchPaused;
        private bool _popupSpanOpen;
        private int _levelCounter = 1;
        private long _simTimestampUs = 1000000L;
        private string _lastExportSummary = string.Empty;

        private Vector2 _scrollPos;
        private readonly List<string> _logLines = new List<string>(MaxLogLines);
        private readonly Dictionary<string, Value> _popupData = new Dictionary<string, Value>(2, StringComparer.Ordinal);

        private void Awake()
        {
            _stageEndpoint = ReadEnv("ASPARAGUS_ENDPOINT");
            _stageToken = ReadEnv("ASPARAGUS_TOKEN");
            _idOrg = ReadEnv("ASPARAGUS_ID_ORG");
            _idDeployment = ReadEnv("ASPARAGUS_ID_DEPLOYMENT");
            _idSource = ReadEnv("ASPARAGUS_ID_SOURCE");
            _idSession = ReadEnv("ASPARAGUS_ID_SESSION");

            _controlPlaneUrl = ReadEnv("ASPARAGUS_CONTROL_PLANE");
            _apiKey = ReadEnv("ASPARAGUS_API_KEY");

            if (string.IsNullOrEmpty(_stageEndpoint) && !string.IsNullOrEmpty(_controlPlaneUrl))
            {
                _useStageOverride = false;
            }

            AppendLog("Ready. Configure Stage Override or Control Plane and click Start Session.");
        }

        private void OnGUI()
        {
            try
            {
                float scale = ComputeUiScale();
                Matrix4x4 prevMatrix = GUI.matrix;
                GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

                float virtualWidth = Screen.width / scale;
                float virtualHeight = Screen.height / scale;
                float pad = 12f;

                GUILayout.BeginArea(new Rect(pad, pad, virtualWidth - pad * 2f, virtualHeight - pad * 2f));
                _scrollPos = GUILayout.BeginScrollView(_scrollPos);

                GUILayout.Label("Asparagus Unity SDK — Stage Test & Diagnostics Panel");

                DrawConnectionSection();
                GUILayout.Space(8f);
                DrawSessionControls();
                GUILayout.Space(8f);
                DrawEventsAndStateSection();
                GUILayout.Space(8f);
                DrawSimulatorAndExportSection();
                GUILayout.Space(8f);
                DrawStatusDashboard();
                GUILayout.Space(8f);
                DrawLogSection();

                GUILayout.EndScrollView();
                GUILayout.EndArea();

                GUI.matrix = prevMatrix;
            }
            catch
            {
                // Demo UI never throws into Unity OnGUI loop.
            }
        }

        private void DrawConnectionSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("1. Connection Mode (R40-safe: env vars or runtime input, no hardcoded secrets)");

            GUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_useStageOverride, "Stage Override Mode"))
            {
                _useStageOverride = true;
            }
            if (GUILayout.Toggle(!_useStageOverride, "Control Plane Mode"))
            {
                _useStageOverride = false;
            }
            GUILayout.EndHorizontal();

            if (_useStageOverride)
            {
                _stageEndpoint = LabeledTextField("Batch Endpoint:", _stageEndpoint);
                _stageToken = LabeledPasswordField("Bearer Token:", _stageToken);
                _idOrg = LabeledTextField("id_org:", _idOrg);
                _idDeployment = LabeledTextField("id_deployment:", _idDeployment);
                _idSource = LabeledTextField("id_source:", _idSource);
                _idSession = LabeledTextField("id_session:", _idSession);
            }
            else
            {
                _controlPlaneUrl = LabeledTextField("Control Plane URL:", _controlPlaneUrl);
                _apiKey = LabeledPasswordField("API Key:", _apiKey);
                _demoFlag = GUILayout.Toggle(_demoFlag, "Demo Mode (collect nothing)");
            }

            GUILayout.EndVertical();
        }

        private void DrawSessionControls()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("2. Session & Telemetry Controls");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start Session"))
            {
                OnClickStartSession();
            }
            if (GUILayout.Button("Stop Session"))
            {
                AsparagusSDK.StopSession();
                AppendLog("StopSession() called (final batch queued for upload).");
            }
            if (GUILayout.Button("Force Close Batch"))
            {
                bool closed = AsparagusSDK.ForceCloseCurrentWindow(CloseReason.WindowFull);
                AppendLog(closed ? "ForceCloseCurrentWindow() closed & queued batch." : "ForceCloseCurrentWindow() skipped (no active session).");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Stop Telemetry"))
            {
                AsparagusSDK.StopTelemetry();
                AppendLog("StopTelemetry() called (sensors off, batch closed).");
            }
            if (GUILayout.Button("Start Telemetry"))
            {
                AsparagusSDK.StartTelemetry();
                AppendLog("StartTelemetry() called (capture gap recorded, sensors restarted).");
            }
            if (GUILayout.Button(_touchPaused ? "Resume Touch" : "Pause Touch"))
            {
                _touchPaused = !_touchPaused;
                AsparagusSDK.SetTouchPaused(_touchPaused);
                AppendLog("SetTouchPaused(" + _touchPaused + ").");
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawEventsAndStateSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("3. Core Events, Spans & State Exerciser");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("LevelStarted"))
            {
                AsparagusSDK.SendCoreEvent.LevelStarted(_levelCounter.ToString(), attemptNumber: 1, difficulty: 0.5);
                AppendLog("SendCoreEvent.LevelStarted(" + _levelCounter + ").");
            }
            if (GUILayout.Button("LevelSucceeded"))
            {
                AsparagusSDK.SendCoreEvent.LevelSucceeded(_levelCounter.ToString(), score: 1250, duration: 42.5, attemptNumber: 1);
                AppendLog("SendCoreEvent.LevelSucceeded(" + _levelCounter + ").");
                _levelCounter++;
            }
            if (GUILayout.Button("LevelFailed"))
            {
                AsparagusSDK.SendCoreEvent.LevelFailed(_levelCounter.ToString(), score: 300, duration: 18.0, attemptNumber: 1);
                AppendLog("SendCoreEvent.LevelFailed(" + _levelCounter + ").");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("AdStarted (rewarded)"))
            {
                AsparagusSDK.SendCoreEvent.AdStarted("rewarded", placement: "revive");
                AppendLog("SendCoreEvent.AdStarted(rewarded).");
            }
            if (GUILayout.Button("AdFinished (rewarded)"))
            {
                AsparagusSDK.SendCoreEvent.AdFinished("rewarded", placement: "revive", completed: true);
                AppendLog("SendCoreEvent.AdFinished(rewarded, completed=true).");
            }
            if (GUILayout.Button("AdRevenue"))
            {
                AsparagusSDK.SendCoreEvent.AdRevenue("rewarded", 0.015, "USD", Outcomes.Converted, network: "demo_net");
                AppendLog("SendCoreEvent.AdRevenue(0.015 USD).");
            }
            if (GUILayout.Button("Purchase"))
            {
                AsparagusSDK.SendCoreEvent.Purchase("starter_pack", 4.99, "USD", Outcomes.Converted, quantity: 1);
                AppendLog("SendCoreEvent.Purchase(starter_pack, 4.99 USD).");
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(_popupSpanOpen ? "Close CoreSpans.Popup" : "Open CoreSpans.Popup"))
            {
                _popupSpanOpen = !_popupSpanOpen;
                _popupData.Clear();
                _popupData["popup_id"] = Value.FromText("daily_bonus");
                AsparagusSDK.SendEvent(_popupSpanOpen ? "popup_opened" : "popup_closed", _popupData, CoreSpans.Popup);
                AppendLog("Toggled CoreSpans.Popup -> " + (_popupSpanOpen ? "OPEN" : "CLOSED") + ".");
            }
            if (GUILayout.Button("SetAttribute(game_level=3)"))
            {
                AsparagusSDK.SetAttribute("game_level", "3");
                AppendLog("SetAttribute(\"game_level\", \"3\").");
            }
            if (GUILayout.Button("ClearAttributes()"))
            {
                AsparagusSDK.ClearAttributes();
                AppendLog("ClearAttributes() called.");
            }
            if (GUILayout.Button("Test Reserved nx10_ Key"))
            {
                AsparagusStatusSnapshot before = AsparagusSDK.GetStatusSnapshot();
                AsparagusSDK.SetAttribute("nx10_forbidden_test", "blocked");
                AsparagusStatusSnapshot after = AsparagusSDK.GetStatusSnapshot();
                AppendLog("nx10_ rejected: version unchanged (" + before.CurrentStateVersion + " -> " + after.CurrentStateVersion + ").");
            }
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
        }

        private void DrawSimulatorAndExportSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("4. Desktop Sensor/Touch Simulator & Step 5 Offline Exporter");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Simulate 1s Hand Motion + Swipe"))
            {
                SimulateOneSecondMotionAndSwipe();
            }
            if (GUILayout.Button("Export batch.binpb & batch.binpb.gz"))
            {
                bool ok = AsparagusSDK.TryExportCurrentBatch(
                    Application.persistentDataPath,
                    out string rawPath,
                    out string gzPath,
                    out string err);
                if (ok)
                {
                    _lastExportSummary = "Exported:\n  " + rawPath + "\n  " + gzPath;
                    AppendLog("Exported batch.binpb & batch.binpb.gz to persistentDataPath.");
                }
                else
                {
                    _lastExportSummary = "Export failed: " + (err ?? "unknown error");
                    AppendLog(_lastExportSummary);
                }
            }
            GUILayout.EndHorizontal();

            if (!string.IsNullOrEmpty(_lastExportSummary))
            {
                GUILayout.Label(_lastExportSummary);
            }

            GUILayout.EndVertical();
        }

        private void DrawStatusDashboard()
        {
            AsparagusStatusSnapshot s = AsparagusSDK.GetStatusSnapshot();

            GUILayout.BeginVertical("box");
            GUILayout.Label("5. Live SDK Status Dashboard");
            GUILayout.Label(
                "Initialized: " + s.IsInitialized
                + " | Active: " + s.IsSessionActive
                + " | Collecting: " + s.IsCollecting
                + " | StoppedByGame: " + s.IsTelemetryStoppedByGame
                + " | Paused: " + s.IsPaused);
            GUILayout.Label(
                "StageOverride: " + s.IsStageOverrideMode
                + " | Refused: " + s.IsRefusedForAppRun
                + " | SessionId: " + (string.IsNullOrEmpty(s.ActiveSessionId) ? "<none>" : s.ActiveSessionId)
                + " | SettingsVer: " + (string.IsNullOrEmpty(s.SettingsVersion) ? "<default>" : s.SettingsVersion));
            GUILayout.Label(
                "NextSeq: " + s.NextSequence
                + " | MotionSrc: " + s.ActiveMotionSource
                + " | TouchMode: " + s.ActiveTouchMode);
            GUILayout.Label(
                "Window Motion (Bounded/Total): " + s.WindowBoundedMotionSamples + " / " + s.WindowTotalMotionSamples
                + " | Touch (Contacts/Samples): " + s.WindowTouchContacts + " / " + s.WindowTouchSamples);
            GUILayout.Label(
                "Events: " + s.WindowEvents
                + " | StateVer: " + s.CurrentStateVersion
                + " | OpenSpans: " + s.OpenSpansCount
                + " | Gaps: " + s.WindowGapsCount
                + " | Diagnostics: " + s.ActiveDiagnosticsCount);
            GUILayout.Label(
                "Disk Queue: " + s.DiskQueuedBatches + " batch(es), " + s.DiskQueuedBytes + " bytes"
                + " | Retries (Sess/Up/401): " + s.SessionRetryAttempt + " / " + s.UploadRetryAttempt + " / " + s.Consecutive401Count);
            GUILayout.EndVertical();
        }

        private void DrawLogSection()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("6. Recent Panel Activity");
            for (int i = 0; i < _logLines.Count; i++)
            {
                GUILayout.Label(_logLines[i]);
            }
            GUILayout.EndVertical();
        }

        private void OnClickStartSession()
        {
            if (_useStageOverride)
            {
                string sessionId = string.IsNullOrEmpty(_idSession) ? ("stage-ses-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds()) : _idSession;
                StageBootstrapConfig cfg = new StageBootstrapConfig
                {
                    BatchEndpoint = _stageEndpoint,
                    Token = _stageToken,
                    IdOrg = _idOrg,
                    IdDeployment = _idDeployment,
                    IdSource = _idSource,
                    IdSession = sessionId
                };

                bool ok = AsparagusSDK.StartSessionWithStageOverride(cfg);
                AppendLog(ok ? "Stage Override session started (" + sessionId + ")." : "Stage Override start failed.");
            }
            else
            {
                bool ok = AsparagusSDK.StartSession(_controlPlaneUrl, _apiKey, demo: _demoFlag);
                AppendLog(ok ? "Control Plane SessionStart dispatched." : "Control Plane start failed (check URL & API key).");
            }
        }

        private void SimulateOneSecondMotionAndSwipe()
        {
            long baseUs = Math.Max(AsparagusSDK.GetCurrentUnityTimeUs(), _simTimestampUs);

            // 100 Hz Accelerometer (ValueUnit.G per Unity Input System W1-09) + Gyroscope (rad/s) over 1s
            for (int i = 0; i < 100; i++)
            {
                long tsUs = baseUs + i * 10000L;
                float t = i * 0.01f;
                float ax = 0.02f * Mathf.Sin(t * 6.283185f);
                float ay = 0.015f * Mathf.Cos(t * 6.283185f);
                float az = 1.0f + 0.005f * Mathf.Sin(t * 12.56637f);

                AsparagusSDK.InjectSimulatedMotionSample(new RawMotionSample(
                    Sensor.Accelerometer,
                    CaptureSource.UnityEvents,
                    FallbackReason.Unspecified,
                    Clock.Unity,
                    ValueUnit.G,
                    tsUs,
                    ax,
                    ay,
                    az));

                AsparagusSDK.InjectSimulatedMotionSample(new RawMotionSample(
                    Sensor.Gyroscope,
                    CaptureSource.UnityEvents,
                    FallbackReason.Unspecified,
                    Clock.Unity,
                    ValueUnit.RadiansPerSecond,
                    tsUs,
                    0.05f * Mathf.Cos(t * 3.14159f),
                    -0.04f * Mathf.Sin(t * 3.14159f),
                    0.02f));
            }

            // 10 Hz Gravity (ValueUnit.G per Unity Input System W1-09) + Magnetometer (microtesla) over 1s
            for (int i = 0; i < 10; i++)
            {
                long tsUs = baseUs + i * 100000L;
                AsparagusSDK.InjectSimulatedMotionSample(new RawMotionSample(
                    Sensor.Gravity,
                    CaptureSource.UnityEvents,
                    FallbackReason.Unspecified,
                    Clock.Unity,
                    ValueUnit.G,
                    tsUs,
                    0.005f,
                    0.002f,
                    1.0f));

                AsparagusSDK.InjectSimulatedMotionSample(new RawMotionSample(
                    Sensor.Magnetometer,
                    CaptureSource.UnityEvents,
                    FallbackReason.Unspecified,
                    Clock.Unity,
                    ValueUnit.Microtesla,
                    tsUs,
                    22.5f + i * 0.1f,
                    -4.2f,
                    -41.8f));
            }

            // 60 Hz Touch swipe across screen (60 samples over 1s)
            for (int i = 0; i < 60; i++)
            {
                long tsUs = baseUs + (long)Math.Round(i * (1000000.0 / 60.0));
                TouchPhaseKind phase = i == 0
                    ? TouchPhaseKind.Began
                    : (i == 59 ? TouchPhaseKind.Ended : TouchPhaseKind.Moved);

                float px = 120f + i * 8f;
                float py = 320f + Mathf.Sin(i * 0.1f) * 40f;
                AsparagusSDK.InjectSimulatedTouchSample(new RawTouchSample(
                    fingerId: 1u,
                    phase: phase,
                    timeUs: tsUs,
                    x: px,
                    y: py,
                    sizeMajor: 42f,
                    sizeMinor: 38f,
                    pressure: 0.45f,
                    hasPressure: true));
            }

            _simTimestampUs = baseUs + 1050000L;
            AppendLog("Injected 1s simulated motion (100Hz Acc/Gyro, 10Hz Grav/Mag) + 60Hz swipe.");
        }

        private void AppendLog(string message)
        {
            if (_logLines.Count >= MaxLogLines)
            {
                _logLines.RemoveAt(0);
            }
            _logLines.Add("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message);
        }

        private static string LabeledTextField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(140f));
            string updated = GUILayout.TextField(value ?? string.Empty);
            GUILayout.EndHorizontal();
            return updated;
        }

        private static string LabeledPasswordField(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(140f));
            string updated = GUILayout.PasswordField(value ?? string.Empty, '*');
            GUILayout.EndHorizontal();
            return updated;
        }

        private static float ComputeUiScale()
        {
            float dpi = Screen.dpi;
            if (dpi > 160f)
            {
                return Mathf.Clamp(dpi / 160f, 1f, 3f);
            }
            return Screen.width >= 1600 ? 1.25f : 1f;
        }

        private static string ReadEnv(string name)
        {
            try
            {
                return Environment.GetEnvironmentVariable(name) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
#endif
