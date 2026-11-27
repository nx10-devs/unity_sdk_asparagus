using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Network;
using Asparagus.Sdk.Internal.Runtime;
using Asparagus.Sdk.Internal.Time;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone6RuntimeAndFacadeTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "asparagus_m6_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            AsparagusSDK.Shutdown();
        }

        [TearDown]
        public void TearDown()
        {
            AsparagusSDK.Shutdown();
            try
            {
                if (!string.IsNullOrEmpty(_tempDir) && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
            }
        }

        private sealed class FakeClock : IClockSource
        {
            public long SteadyTimeUs { get; set; }
            public long UnityTimeUs { get; set; }
            public long WallUtcMs { get; set; } = 1794736860000L;
            public int UtcOffsetMinutes { get; set; } = 0;
            public bool HasSensor { get; set; } = true;
            public long SensorUs { get; set; } = 5000000L;

            public bool TryGetSensorTimeUs(out long sensorUs)
            {
                if (HasSensor)
                {
                    sensorUs = SensorUs;
                    return true;
                }
                sensorUs = 0L;
                return false;
            }
        }

        private sealed class FakeMotionSource : IMotionCaptureSource
        {
            private readonly Queue<RawMotionSample> _queued = new Queue<RawMotionSample>();
            public CaptureSource SourceKind { get { return CaptureSource.NativeAndroid; } }
            public bool IsRunning { get; private set; }
            public bool HasGyroscope { get { return true; } }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }

            public void QueueSample(in RawMotionSample sample)
            {
                _queued.Enqueue(sample);
            }

            public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified)
            {
                IsRunning = true;
                StartCount++;
            }

            public void StopCapture()
            {
                IsRunning = false;
                StopCount++;
            }

            public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs)
            {
                if (!IsRunning || consumer == null) return;
                while (_queued.Count > 0)
                {
                    RawMotionSample s = _queued.Dequeue();
                    consumer.OnMotionSample(in s);
                }
            }

            public bool TryConsumeOverflowGap(out GapRecord gap)
            {
                gap = default;
                return false;
            }
        }

        private sealed class FakeTouchSource : ITouchCaptureSource
        {
            private readonly Queue<RawTouchSample> _queued = new Queue<RawTouchSample>();
            public TouchMode CurrentTouchMode { get { return TouchMode.Events; } }
            public bool IsRunning { get; private set; }
            public bool IsTouchPaused { get; private set; }
            public int StartCount { get; private set; }
            public int StopCount { get; private set; }

            public void QueueSample(in RawTouchSample sample)
            {
                _queued.Enqueue(sample);
            }

            public void StartCapture(SessionSettings settings)
            {
                IsRunning = true;
                StartCount++;
            }

            public void StopCapture()
            {
                IsRunning = false;
                StopCount++;
            }

            public void SetTouchPaused(bool paused)
            {
                IsTouchPaused = paused;
            }

            public void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs)
            {
                if (!IsRunning || IsTouchPaused || consumer == null) return;
                while (_queued.Count > 0)
                {
                    RawTouchSample s = _queued.Dequeue();
                    consumer.OnTouchSample(in s);
                }
            }

            public bool TryConsumeOverflowGap(out GapRecord gap)
            {
                gap = default;
                return false;
            }
        }

        private sealed class FakeScreenProvider : IScreenInfoProvider
        {
            public ScreenRecord ReadCurrentScreen()
            {
                return new ScreenRecord
                {
                    TimeUs = 0L,
                    WidthPx = 1080u,
                    HeightPx = 2400u,
                    DensityDpi = 420f,
                    Orientation = Orientation.Portrait,
                    Origin = CoordinateOrigin.BottomLeft
                };
            }
        }

        private sealed class FakeSystemStateProvider : ISystemStateProvider
        {
            public double Brightness = 0.72;
            public bool Locked;

            public bool TryGetScreenBrightness(out double brightness01)
            {
                brightness01 = Brightness;
                return true;
            }

            public bool TryGetScreenLocked(out bool isLocked)
            {
                isLocked = Locked;
                return true;
            }
        }

        private sealed class FakeDeviceMetadataProvider : IDeviceMetadataProvider
        {
            public DeviceMetadataSnapshot Collect(
                IScreenInfoProvider screenInfoProvider,
                bool hasGyroscope,
                string appVersionOverride = null,
                string buildNumberOverride = null)
            {
                return new DeviceMetadataSnapshot
                {
                    Platform = Platform.Android,
                    DeviceIdentifier = "non-personal-hw-id-123",
                    AppVersion = appVersionOverride ?? "2.1.0",
                    BuildNumber = buildNumberOverride ?? "42",
                    DeviceProperties = new DeviceProperties
                    {
                        Maker = "Pixel",
                        Model = "Pixel 9 Pro",
                        OsVersion = "Android 15",
                        DeviceType = "Handheld",
                        ScreenWidthPx = 1080u,
                        ScreenHeightPx = 2400u,
                        ScreenDensityDpi = 420f,
                        HasGyroscope = hasGyroscope,
                        Timezone = "UTC",
                        Locale = "en-US"
                    }
                };
            }
        }

        private sealed class RecordedHttpCall
        {
            public HttpRequestSpec Spec;
            public byte[] BodyCopy;
            public Action<HttpResponseResult> Callback;
        }

        private sealed class MockHttpTransport : IHttpTransport
        {
            private readonly Queue<HttpResponseResult> _responses = new Queue<HttpResponseResult>();
            public readonly List<RecordedHttpCall> Calls = new List<RecordedHttpCall>();
            public bool ThrowOnStartPost;

            public void EnqueueResponse(long statusCode, byte[] body = null, uint rttMs = 20u)
            {
                _responses.Enqueue(new HttpResponseResult(statusCode, body, body != null ? body.Length : 0, rttMs));
            }

            public void EnqueueJson(long statusCode, string json, uint rttMs = 20u)
            {
                byte[] bytes = string.IsNullOrEmpty(json) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(json);
                EnqueueResponse(statusCode, bytes, rttMs);
            }

            public void StartPost(in HttpRequestSpec spec, Action<HttpResponseResult> onCompleted)
            {
                if (ThrowOnStartPost)
                {
                    throw new InvalidOperationException("Simulated transport explosion");
                }

                byte[] copy = null;
                if (spec.Body != null && spec.BodyLength > 0)
                {
                    copy = new byte[spec.BodyLength];
                    Buffer.BlockCopy(spec.Body, 0, copy, 0, spec.BodyLength);
                }

                Calls.Add(new RecordedHttpCall { Spec = spec, BodyCopy = copy, Callback = onCompleted });

                if (_responses.Count > 0)
                {
                    onCompleted?.Invoke(_responses.Dequeue());
                }
            }

            public void Tick() { }
            public void Dispose() { }
        }

        private AsparagusRuntime CreateRuntime(
            FakeClock clock,
            MockHttpTransport http,
            FakeMotionSource motion,
            FakeTouchSource touch,
            FakeSystemStateProvider sysState = null)
        {
            return new AsparagusRuntime(new AsparagusRuntimeOptions
            {
                SdkVersion = new SdkVersion(1, 0, 0),
                StorageBaseDirectory = _tempDir,
                ClockSource = clock,
                HttpTransport = http,
                MotionSource = motion,
                TouchSource = touch,
                ScreenInfoProvider = new FakeScreenProvider(),
                SystemStateProvider = sysState ?? new FakeSystemStateProvider(),
                DeviceMetadataProvider = new FakeDeviceMetadataProvider(),
                RetryPolicy = new RetryBackoffPolicy(() => 0.0),
                RunWorkerSynchronouslyForTests = true,
                AutoLoadDiskQueueOnStart = true
            });
        }

        private static string MakeSessionStartSuccessJson(string sessionId, string token, long serverUtcMs, uint newSessionAfterS = 1800u)
        {
            return @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-e2e"",
                    ""id_deployment"": ""dep-e2e"",
                    ""id_source"": ""src-e2e"",
                    ""id_session"": """ + sessionId + @""",
                    ""token"": """ + token + @""",
                    ""token_expires_utc_ms"": """ + (serverUtcMs + 86400000L) + @""",
                    ""server_utc_ms"": """ + serverUtcMs + @""",
                    ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches"",
                    ""access_request_endpoint"": ""https://cp.stage/access"",
                    ""forget_request_endpoint"": ""https://cp.stage/forget"",
                    ""settings"": {
                        ""version"": ""v3-test"",
                        ""batch_length_s"": 10,
                        ""compress"": true,
                        ""hold_run_length"": 0,
                        ""new_session_after_s"": " + newSessionAfterS + @"
                    }
                }
            }";
        }

        // ====================================================================
        // 1. Full Session Lifecycle End-to-End Test
        // ====================================================================

        [Test]
        public void Runtime_FullSessionLifecycle_ControlPlane_Capture_WindowClose_GzipUpload_AndStopSession()
        {
            FakeClock clock = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = 1794736860000L };
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();
            FakeSystemStateProvider sysState = new FakeSystemStateProvider { Brightness = 0.80, Locked = false };

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch, sysState))
            {
                // Before 200 Session Start: sensors MUST remain OFF and events/state MUST be ignored!
                Assert.IsFalse(motion.IsRunning);
                Assert.IsFalse(touch.IsRunning);
                rt.SendCoreEvent.LevelStarted("ignored_before_start");
                rt.SetAttribute("ignored_key", Value.FromText("ignored"));
                Assert.AreEqual(0, rt.GetStatusSnapshot().WindowEvents);

                // Start session via Control Plane (deferred response -> sensors still OFF until 200 arrives)
                Assert.IsTrue(rt.StartSession("https://cp.stage", "test-api-key"));
                Assert.AreEqual(1, http.Calls.Count);
                Assert.IsFalse(motion.IsRunning);
                string reqJson = System.Text.Encoding.UTF8.GetString(http.Calls[0].BodyCopy);
                StringAssert.Contains("non-personal-hw-id-123", reqJson);
                StringAssert.Contains("Pixel 9 Pro", reqJson);

                // Deliver 200 SessionStartResponse -> sensors automatically start!
                byte[] respBytes = System.Text.Encoding.UTF8.GetBytes(MakeSessionStartSuccessJson("ses-e2e-1", "tok-e2e-1", clock.WallUtcMs));
                http.Calls[0].Callback(new HttpResponseResult(200L, respBytes, respBytes.Length, 30u));

                Assert.IsTrue(motion.IsRunning, "Sensors must start after Session Start 200 with non-empty batch_endpoint");
                Assert.IsTrue(touch.IsRunning);

                // Record core event, state attribute, motion (accel + mag), touch swipe, and tick to poll system state
                rt.SendCoreEvent.LevelStarted("1", attemptNumber: 1);
                rt.SetAttribute("game_level", Value.FromText("1"));

                motion.QueueSample(new RawMotionSample(Sensor.Accelerometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 5000000L, 0.1f, 0.2f, 9.8f));
                motion.QueueSample(new RawMotionSample(Sensor.Magnetometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.Microtesla, 5000000L, 21f, -4f, -40f));
                touch.QueueSample(new RawTouchSample(1u, TouchPhaseKind.Began, 200000L, 100f, 200f, 30f, 30f, 0.5f, true));
                touch.QueueSample(new RawTouchSample(1u, TouchPhaseKind.Ended, 250000L, 150f, 250f, 30f, 30f, 0.5f, true));

                rt.Tick();
                AsparagusStatusSnapshot midSnap = rt.GetStatusSnapshot();
                Assert.IsTrue(midSnap.IsCollecting);
                Assert.AreEqual(1, midSnap.WindowEvents);
                Assert.AreEqual(1, midSnap.WindowBoundedMotionSamples);
                Assert.AreEqual(2, midSnap.WindowTotalMotionSamples);
                Assert.AreEqual(1, midSnap.WindowTouchContacts);

                // Advance clock by 10s -> closes Sequence 1 (WindowFull) and uploads GZip Protobuf to BatchEndpoint!
                clock.SteadyTimeUs += 10000000L;
                clock.UnityTimeUs += 10000000L;
                clock.WallUtcMs += 10000L;

                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();

                Assert.AreEqual(2, http.Calls.Count, "Closed batch must upload to BatchEndpoint");
                RecordedHttpCall upload = http.Calls[1];
                Assert.AreEqual("https://intake.stage/asparagus/v1/batches", upload.Spec.Url);
                Assert.AreEqual("tok-e2e-1", upload.Spec.BearerToken);
                Assert.AreEqual("gzip", upload.Spec.ContentEncoding);
                Assert.AreEqual(0x1f, upload.BodyCopy[0]);
                Assert.AreEqual(0x8b, upload.BodyCopy[1]);

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(upload.BodyCopy, 0, upload.BodyCopy.Length, out Batch decoded, out string err), err);
                Assert.AreEqual(1UL, decoded.Header.Sequence);
                Assert.AreEqual(BatchConstants.SchemaVersion, decoded.Header.SchemaVersion);
                Assert.AreEqual("Pixel 9 Pro", decoded.Header.DeviceModel);
                Assert.AreEqual("Android 15", decoded.Header.OsVersion);
                Assert.AreEqual("v3-test", decoded.Header.SettingsVersion);
                Assert.AreEqual(CloseReason.WindowFull, decoded.Header.CloseReason);
                Assert.AreEqual(2, decoded.SensorBlocks.Count);
                Assert.IsNotNull(decoded.Touch);
                Assert.AreEqual(1, decoded.Touch.Contacts.Count);
                Assert.AreEqual(1, decoded.Events.Count);
                Assert.AreEqual(CoreEventNames.GameLevelStarted, decoded.Events[0].Name);
                Assert.GreaterOrEqual(decoded.StateRecords.Count, 2, "Must contain game_level plus SDK nx10_screen_brightness/locked");

                // StopSession -> stops sensors, closes final SessionEnded batch, and uploads it on Tick()!
                rt.StopSession();
                Assert.IsFalse(motion.IsRunning);
                Assert.IsFalse(touch.IsRunning);
                Assert.AreEqual(1, rt.DiskQueue.QueuedCount);

                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(0, rt.DiskQueue.QueuedCount);
                Assert.AreEqual(3, http.Calls.Count);
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[2].BodyCopy, 0, http.Calls[2].BodyCopy.Length, out Batch finalDecoded, out _));
                Assert.AreEqual(CloseReason.SessionEnded, finalDecoded.Header.CloseReason);
                Assert.AreEqual(2UL, finalDecoded.Header.Sequence);
            }
        }

        // ====================================================================
        // 2. StopTelemetry / StartTelemetry Power Gating & CaptureStopped Gap
        // ====================================================================

        [Test]
        public void Runtime_StopAndStartTelemetry_GatesSensors_EmitsLifecycleAndCaptureStoppedGap()
        {
            FakeClock clock = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = 1794736860000L };
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch))
            {
                http.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-tel", "tok-tel", clock.WallUtcMs));
                rt.StartSession("https://cp.stage", "key");
                Assert.IsTrue(motion.IsRunning);
                Assert.IsTrue(touch.IsRunning);

                // StopTelemetry -> immediately turns sensors OFF, emits CaptureStoppedByGame, closes batch (CaptureStopped)
                clock.SteadyTimeUs = 2000000L;
                clock.UnityTimeUs = 2100000L;
                rt.StopTelemetry();

                Assert.IsFalse(motion.IsRunning, "StopTelemetry must turn motion sensors off immediately");
                Assert.IsFalse(touch.IsRunning, "StopTelemetry must turn touch capture off immediately");
                Assert.IsTrue(rt.GetStatusSnapshot().IsTelemetryStoppedByGame);

                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[1].BodyCopy, 0, http.Calls[1].BodyCopy.Length, out Batch stoppedBatch, out _));
                Assert.AreEqual(CloseReason.CaptureStopped, stoppedBatch.Header.CloseReason);
                Assert.AreEqual(1, stoppedBatch.Lifecycle.Count);
                Assert.AreEqual(LifecycleKind.CaptureStoppedByGame, stoppedBatch.Lifecycle[0].Kind);

                // Advance 5s and call StartTelemetry -> restarts capture, records CaptureStopped gap + CaptureStartedByGame!
                clock.SteadyTimeUs += 5000000L;
                clock.UnityTimeUs += 5000000L;
                rt.StartTelemetry();

                Assert.IsTrue(motion.IsRunning);
                Assert.IsTrue(touch.IsRunning);
                Assert.IsFalse(rt.GetStatusSnapshot().IsTelemetryStoppedByGame);

                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[2].BodyCopy, 0, http.Calls[2].BodyCopy.Length, out Batch resumedBatch, out _));
                Assert.AreEqual(1, resumedBatch.Lifecycle.Count);
                Assert.AreEqual(LifecycleKind.CaptureStartedByGame, resumedBatch.Lifecycle[0].Kind);
                Assert.AreEqual(1, resumedBatch.Gaps.Count);
                Assert.AreEqual(GapCause.CaptureStopped, resumedBatch.Gaps[0].Cause);
                Assert.AreEqual(GapSignal.Everything, resumedBatch.Gaps[0].Signal);
                Assert.AreEqual(Clock.Session, resumedBatch.Gaps[0].Clock);
                Assert.AreEqual(5000000L, resumedBatch.Gaps[0].EndTimeUs - resumedBatch.Gaps[0].StartTimeUs);
            }
        }

        // ====================================================================
        // 3. Pause / Resume (CLOCK_STEPPED vs new_session_after_s Expiry)
        // ====================================================================

        [Test]
        public void Runtime_PauseResume_ShortPauseEmitsClockStepped_LongPauseStartsNewSession()
        {
            FakeClock clock = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = 1794736860000L };
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch))
            {
                http.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-1", "tok-1", clock.WallUtcMs, newSessionAfterS: 1800u));
                rt.StartSession("https://cp.stage", "key");

                // Open a level span before pausing
                rt.SendCoreEvent.LevelStarted("level-7");
                Assert.AreEqual(1, rt.GetStatusSnapshot().OpenSpansCount);

                // Short pause (10s wall, 7s steady -> 3s lag -> CLOCK_STEPPED = 3s, same session, span kept!)
                clock.SteadyTimeUs += 1000000L;
                clock.UnityTimeUs += 1000000L;
                clock.WallUtcMs += 1000L;
                rt.OnApplicationPause(true);

                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[1].BodyCopy, 0, http.Calls[1].BodyCopy.Length, out Batch pausedBatch, out _));
                Assert.AreEqual(CloseReason.Paused, pausedBatch.Header.CloseReason);
                Assert.AreEqual(LifecycleKind.Paused, pausedBatch.Lifecycle[0].Kind);

                clock.WallUtcMs += 10000L;     // 10s wall time elapsed
                clock.SteadyTimeUs += 7000000L; // 7s steady elapsed -> 3s deep-sleep lag
                clock.UnityTimeUs += 7000000L;
                rt.OnApplicationPause(false);

                Assert.AreEqual(1, rt.GetStatusSnapshot().OpenSpansCount, "Short pause must preserve open spans");
                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[2].BodyCopy, 0, http.Calls[2].BodyCopy.Length, out Batch resumedBatch, out _));
                Assert.AreEqual("ses-1", resumedBatch.Header.IdSession);
                Assert.AreEqual(2, resumedBatch.Lifecycle.Count);
                Assert.AreEqual(LifecycleKind.ClockStepped, resumedBatch.Lifecycle[0].Kind);
                Assert.AreEqual(3L, resumedBatch.Lifecycle[0].ClockStepS);
                Assert.AreEqual(LifecycleKind.Resumed, resumedBatch.Lifecycle[1].Kind);
                Assert.AreEqual(1, resumedBatch.Carried.OpenSpans.Spans.Count);

                // Long pause (1805s > new_session_after_s = 1800s) -> starts fresh session-2 without carrying old spans/state!
                rt.OnApplicationPause(true);
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();

                clock.WallUtcMs += 1805000L;
                clock.SteadyTimeUs += 1805000000L;
                clock.UnityTimeUs += 1805000000L;

                http.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-2", "tok-2", clock.WallUtcMs, newSessionAfterS: 1800u));
                rt.OnApplicationPause(false);

                Assert.AreEqual("ses-2", rt.GetStatusSnapshot().ActiveSessionId);
                Assert.AreEqual(0, rt.GetStatusSnapshot().OpenSpansCount, "New session after long pause must NOT carry old open spans");
                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();

                RecordedHttpCall lastUpload = http.Calls[http.Calls.Count - 1];
                Assert.AreEqual("tok-2", lastUpload.Spec.BearerToken);
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(lastUpload.BodyCopy, 0, lastUpload.BodyCopy.Length, out Batch ses2Batch, out _));
                Assert.AreEqual("ses-2", ses2Batch.Header.IdSession);
                Assert.AreEqual(1UL, ses2Batch.Header.Sequence);
                Assert.AreEqual(0, ses2Batch.Carried.OpenSpans.Spans.Count);
                Assert.AreEqual(0, ses2Batch.Carried.State.Values.Count);
            }
        }

        // ====================================================================
        // 4. Obsolete Compatibility Shims & Total Fault Containment
        // ====================================================================

        [Test]
        public void ObsoleteShims_AndFaultContainment_NeverThrowOrCallTurnip()
        {
            // Calling every AsparagusSDK static method before initialization or with nulls must NEVER throw!
            Assert.DoesNotThrow(() =>
            {
                AsparagusSDK.StopSession();
                AsparagusSDK.StartTelemetry();
                AsparagusSDK.StopTelemetry();
                AsparagusSDK.SetTouchPaused(true);
                AsparagusSDK.SendEvent(null, null, null);
                AsparagusSDK.SendOutcomeEvent(null, null, null, null);
                AsparagusSDK.SendCoreEvent.LevelStarted(null);
                AsparagusSDK.SetAttributes(null);
                AsparagusSDK.SetAttribute(null, Value.FromText(null));
                AsparagusSDK.RemoveAttribute(null);
                AsparagusSDK.ClearAttributes();
                AsparagusSDK.PersonalDataRequested(null, (ok, code) => { throw new Exception("bad user callback"); });
                AsparagusSDK.PersonalDataDeletionRequested(null, null);
                AsparagusSDK.ForceCloseCurrentWindow();
                AsparagusSDK.TryExportCurrentBatch(null, out _, out _, out _);
                AsparagusSDK.GetStatusSnapshot();
            });

            // Obsolete shims: RequestActivity / RequestAffect make 0 HTTP calls and return (false, ..., "not available")
            FakeClock clock = new FakeClock();
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch))
            {
                bool actOk = true;
                string actMsg = null;
#pragma warning disable CS0618
                AsparagusSDK.RequestActivity((ok, msg) => { actOk = ok; actMsg = msg; });
                Assert.IsFalse(actOk);
                Assert.AreEqual("not available", actMsg);
                rt.RequestActivity((ok, msg) => { actOk = ok; actMsg = msg; });
                Assert.IsFalse(actOk);
                Assert.AreEqual("not available", actMsg);

                bool affOk = true;
                float affScore = 99f;
                string affMsg = null;
                AsparagusSDK.RequestAffect((ok, score, msg) => { affOk = ok; affScore = score; affMsg = msg; });
                Assert.IsFalse(affOk);
                Assert.AreEqual(0f, affScore);
                Assert.AreEqual("not available", affMsg);
                rt.RequestAffect((ok, score, msg) => { affOk = ok; affScore = score; affMsg = msg; });
                Assert.IsFalse(affOk);
                Assert.AreEqual(0f, affScore);
                Assert.AreEqual("not available", affMsg);
                Assert.AreEqual(0, http.Calls.Count, "Obsolete prediction shims must make zero network calls");

                // Start session via Stage Override and test FireEvent forwarding
                Assert.IsTrue(rt.StartSessionWithStageOverride(new StageBootstrapConfig
                {
                    Token = "stage-tok",
                    IdOrg = "org-1",
                    IdDeployment = "dep-1",
                    IdSource = "src-1",
                    IdSession = "ses-shim",
                    BatchEndpoint = "https://intake.stage/asparagus/v1/batches"
                }));

                Dictionary<string, Value> legacyData = new Dictionary<string, Value> { { "item", Value.FromText("sword") } };
                rt.FireEvent("legacy_evt", legacyData, source: "menu");
                AsparagusSDK.FireEvent(null, null, null);
#pragma warning restore CS0618

                Assert.AreEqual(0, http.Calls.Count, "FireEvent must not call any Turnip HTTP route");
                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));

                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(1, http.Calls.Count);
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[0].BodyCopy, 0, http.Calls[0].BodyCopy.Length, out Batch decoded, out _));
                Assert.AreEqual(1, decoded.Events.Count);
                Assert.AreEqual("legacy_evt", decoded.Events[0].Name);
                Assert.AreEqual("sword", decoded.Events[0].Fields["item"].TextValue);
                Assert.AreEqual("menu", decoded.Events[0].Fields["source"].TextValue);

                // Throwing transport must NEVER throw out of runtime/facade
                http.ThrowOnStartPost = true;
                Assert.DoesNotThrow(() => rt.StartSession("https://cp.stage", "key"));
                Assert.DoesNotThrow(() => rt.Tick());
                Assert.DoesNotThrow(() => AsparagusSDK.Shutdown());
            }
        }

        // ====================================================================
        // 5. Step 5 Offline .binpb & .binpb.gz Export Test
        // ====================================================================

        [Test]
        public void Runtime_TryExportClosedBatchFiles_WritesValidBinpbAndGzip_AndRecyclesCleanly()
        {
            FakeClock clock = new FakeClock();
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch))
            {
                Assert.IsTrue(rt.StartSessionWithStageOverride(new StageBootstrapConfig
                {
                    Token = "stage-tok",
                    IdOrg = "org-export",
                    IdDeployment = "dep-export",
                    IdSource = "src-export",
                    IdSession = "ses-export",
                    BatchEndpoint = "https://intake.stage/asparagus/v1/batches"
                }));

                rt.SendCoreEvent.Purchase("gem_pack", 9.99, "USD", Outcomes.Converted, quantity: 1);
                rt.InjectSimulatedMotionSample(new RawMotionSample(
                    Sensor.Accelerometer, CaptureSource.UnityEvents, FallbackReason.Unspecified,
                    Clock.Unity, ValueUnit.MetresPerSecondSquared, 100000L, 0.1f, 0.2f, 9.81f));

                string exportDir = Path.Combine(_tempDir, "step5_export");
                Assert.IsTrue(rt.TryExportClosedBatchFiles(exportDir, out string rawPath, out string gzPath, out string err), err);
                Assert.IsTrue(File.Exists(rawPath));
                Assert.IsTrue(File.Exists(gzPath));

                byte[] rawBytes = File.ReadAllBytes(rawPath);
                byte[] gzBytes = File.ReadAllBytes(gzPath);
                Assert.Greater(rawBytes.Length, 0);
                Assert.Greater(gzBytes.Length, 2);
                Assert.AreEqual(0x1f, gzBytes[0]);
                Assert.AreEqual(0x8b, gzBytes[1]);

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(rawBytes, 0, rawBytes.Length, out Batch rawDecoded, out _));
                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(gzBytes, 0, gzBytes.Length, out Batch gzDecoded, out _));

                Assert.AreEqual("ses-export", rawDecoded.Header.IdSession);
                Assert.AreEqual("ses-export", gzDecoded.Header.IdSession);
                Assert.AreEqual(1UL, rawDecoded.Header.Sequence);
                Assert.AreEqual(1, gzDecoded.Events.Count);
                Assert.AreEqual(CoreEventNames.InAppPurchase, gzDecoded.Events[0].Name);
                Assert.AreEqual(1, gzDecoded.SensorBlocks.Count);

                // Verify batch was also queued and recycled cleanly without leaking
                Assert.AreEqual(1, rt.DiskQueue.QueuedCount);
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(0, rt.DiskQueue.QueuedCount);
            }
        }

        // ====================================================================
        // 6. Post-Pause 600s Snap (Zero Catch-Up Spam) & 25s Ad-Buffered Drain
        // ====================================================================

        [Test]
        public void Runtime_PostPause_600sSnapProducesZeroCatchUpBatches_And25sAdPauseFlushesBufferedSamples()
        {
            long baseUtcMs = 1794736860000L; // exact whole second
            long baseUs = baseUtcMs * 1000L;
            FakeClock clock = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs };
            MockHttpTransport http = new MockHttpTransport();
            FakeMotionSource motion = new FakeMotionSource();
            FakeTouchSource touch = new FakeTouchSource();

            using (AsparagusRuntime rt = CreateRuntime(clock, http, motion, touch))
            {
                http.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-pause-snap", "tok-snap", baseUtcMs, newSessionAfterS: 1800u));
                rt.StartSession("https://cp.stage", "key");
                Assert.AreEqual(100000L, rt.CurrentUnityTimeUs);

                // (a) Advance to t = 10s, pause for 600s (SteadyTimeUs +600s, WallUtcMs +600s, 0 buffered samples)
                clock.SteadyTimeUs = 10000000L;
                clock.UnityTimeUs = 10100000L;
                clock.WallUtcMs = baseUtcMs + 10000L;
                rt.OnApplicationPause(true);

                // Drain the CloseReason.Paused batch (#1)
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(2, http.Calls.Count);
                Assert.AreEqual(0, rt.DiskQueue.QueuedCount);

                // Return 600s later (t = 610s) with 0 buffered native samples
                clock.SteadyTimeUs += 600000000L;
                clock.UnityTimeUs += 600000000L;
                clock.WallUtcMs += 600000L;
                rt.OnApplicationPause(false);

                // Pump 10 frames right after resume -> MUST produce ZERO empty catch-up batches!
                for (int f = 0; f < 10; f++)
                {
                    clock.SteadyTimeUs += 16000L;
                    clock.UnityTimeUs += 16000L;
                    rt.Tick();
                }

                Assert.AreEqual(0, rt.DiskQueue.QueuedCount, "600s pause with 0 samples must produce zero catch-up batches");
                Assert.AreEqual(2, http.Calls.Count, "Zero catch-up uploads must be dispatched");

                // Close the resumed foreground window -> WindowStartUs and Header.Clocks.SessionUs both snap to +610s!
                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(3, http.Calls.Count);

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[2].BodyCopy, 0, http.Calls[2].BodyCopy.Length, out Batch resumedFg, out _));
                Assert.AreEqual(baseUs + 610000000L, resumedFg.Header.WindowStartUs, "Resumed window must snap to +610s");
                Assert.AreEqual(baseUs + 610000000L, resumedFg.Header.Clocks.SessionUs, "Header.Clocks.SessionUs must match snapped window start");
                Assert.LessOrEqual(resumedFg.Header.WindowLengthS, BatchConstants.MaxWindowLengthS);
                Assert.AreEqual(1, resumedFg.Lifecycle.Count);
                Assert.AreEqual(LifecycleKind.Resumed, resumedFg.Lifecycle[0].Kind);

                // (b) 25s full-screen ad pause with native buffered samples during [610s..635s]
                // Anchor next pause cleanly at exact t = 620s
                clock.SteadyTimeUs = 620000000L;
                clock.UnityTimeUs = 620100000L;
                clock.WallUtcMs = baseUtcMs + 620000L;
                rt.OnApplicationPause(true);
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(4, http.Calls.Count);

                // Native ring buffer captures samples during 25s ad pause
                motion.QueueSample(new RawMotionSample(
                    Sensor.Accelerometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified,
                    Clock.Sensor, ValueUnit.MetresPerSecondSquared, 625000000L, 0.1f, 0.2f, 9.81f));
                motion.QueueSample(new RawMotionSample(
                    Sensor.Accelerometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified,
                    Clock.Sensor, ValueUnit.MetresPerSecondSquared, 635000000L, 0.2f, 0.1f, 9.80f));

                clock.SteadyTimeUs += 25000000L; // +25s -> t = 645s
                clock.UnityTimeUs += 25000000L;
                clock.WallUtcMs += 25000L;
                rt.OnApplicationPause(false);

                // Resume must have flushed the 25s ad-buffered samples into a single 25s batch!
                Assert.AreEqual(1, rt.DiskQueue.QueuedCount, "Ad-buffered native samples must flush into one pause-buffered batch on resume");
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(5, http.Calls.Count);

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[4].BodyCopy, 0, http.Calls[4].BodyCopy.Length, out Batch adBatch, out _));
                Assert.AreEqual(baseUs + 620000000L, adBatch.Header.WindowStartUs);
                Assert.AreEqual(25u, adBatch.Header.WindowLengthS, "25s ad-buffered batch must span 25s (<= 150s)");
                Assert.AreEqual(baseUs + 645000000L, adBatch.Header.ClosedUs);
                Assert.AreEqual(1, adBatch.SensorBlocks.Count);
                Assert.AreEqual(2u, adBatch.SensorBlocks[0].SampleCount);

                // Additional ticks right after ad resume must NOT produce catch-up spam; foreground window starts cleanly at +645s!
                for (int f = 0; f < 5; f++)
                {
                    clock.SteadyTimeUs += 16000L;
                    clock.UnityTimeUs += 16000L;
                    rt.Tick();
                }
                Assert.AreEqual(0, rt.DiskQueue.QueuedCount);
                Assert.AreEqual(5, http.Calls.Count);

                Assert.IsTrue(rt.ForceCloseCurrentWindow(CloseReason.WindowFull));
                http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                rt.Tick();
                Assert.AreEqual(6, http.Calls.Count);

                Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(http.Calls[5].BodyCopy, 0, http.Calls[5].BodyCopy.Length, out Batch postAdFg, out _));
                Assert.AreEqual(baseUs + 645000000L, postAdFg.Header.WindowStartUs, "Post-ad foreground window must start cleanly at +645s");
                Assert.AreEqual(baseUs + 645000000L, postAdFg.Header.Clocks.SessionUs);
                Assert.AreEqual(1, postAdFg.Lifecycle.Count);
                Assert.AreEqual(LifecycleKind.Resumed, postAdFg.Lifecycle[0].Kind);
            }
        }

        // ====================================================================
        // 7. v7 CollectionHistory Cross-Launch Persistence, Retries, SettingsLabel & 1-Arg StartSession
        // ====================================================================

        [Test]
        public void Runtime_CollectionHistory_PersistsAcrossLaunches_IgnoresRetries_AndUsesHardcodedEndpoint()
        {
            long baseUtcMs = 1794736860000L;

            // --- Launch #1: 503 retry does NOT increment history; collects 20s across 2 batches ---
            FakeClock clock1 = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs };
            MockHttpTransport http1 = new MockHttpTransport();
            using (AsparagusRuntime rt1 = CreateRuntime(clock1, http1, new FakeMotionSource(), new FakeTouchSource()))
            {
                http1.EnqueueResponse(503L);
                http1.EnqueueJson(200L, @"{
                    ""status"": ""success"",
                    ""data"": {
                        ""id_org"": ""org-h"", ""id_deployment"": ""dep-h"", ""id_source"": ""src-h"", ""id_session"": ""ses-h1"",
                        ""token"": ""tok-h1"", ""token_expires_utc_ms"": """ + (baseUtcMs + 86400000L) + @""", ""server_utc_ms"": """ + baseUtcMs + @""",
                        ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches"",
                        ""settings_label"": ""share-alpha"",
                        ""settings"": { ""batch_length_s"": 10, ""compress"": true }
                    }
                }");

                // 1-argument StartSession(apiKey) MUST hit the hardcoded stage Control Plane URL!
                Assert.IsTrue(rt1.StartSession("test-key-v7"));
                Assert.AreEqual(1, http1.Calls.Count);
                Assert.AreEqual(
                    "https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start",
                    http1.Calls[0].Spec.Url);
                string req1Json = System.Text.Encoding.UTF8.GetString(http1.Calls[0].BodyCopy);
                StringAssert.DoesNotContain("\"history\"", req1Json, "First launch must omit zero history");

                // Retry after 503 backoff -> still zero history (503 never started a session!)
                clock1.SteadyTimeUs += 2500000L;
                clock1.UnityTimeUs += 2500000L;
                rt1.Tick();
                Assert.AreEqual(2, http1.Calls.Count);
                string retryJson = System.Text.Encoding.UTF8.GetString(http1.Calls[1].BodyCopy);
                StringAssert.DoesNotContain("\"history\"", retryJson, "Retry after 503 must keep history unchanged");

                AsparagusStatusSnapshot snap1 = rt1.GetStatusSnapshot();
                Assert.AreEqual("share-alpha", snap1.SettingsLabel);
                Assert.AreEqual(0u, snap1.HistorySessionsBefore);

                // Collect 2 full 10s batches (20s total)
                http1.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                clock1.SteadyTimeUs += 10000000L;
                clock1.UnityTimeUs += 10000000L;
                rt1.Tick();

                http1.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                clock1.SteadyTimeUs += 10000000L;
                clock1.UnityTimeUs += 10000000L;
                rt1.Tick();
            }

            // --- Launch #2: loads (1, 20, 20) from disk, collects 12s, StopSession->StartSession, then long pause -> non-collecting share ---
            FakeClock clock2 = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs + 60000L };
            MockHttpTransport http2 = new MockHttpTransport();
            using (AsparagusRuntime rt2 = CreateRuntime(clock2, http2, new FakeMotionSource(), new FakeTouchSource()))
            {
                http2.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-h2", "tok-h2", clock2.WallUtcMs, newSessionAfterS: 1800u));
                Assert.IsTrue(rt2.StartSession("test-key-v7"));

                string req2Json = System.Text.Encoding.UTF8.GetString(http2.Calls[0].BodyCopy);
                StringAssert.Contains("\"history\":{\"sessions_before\":1,\"collected_seconds_before\":\"20\",\"longest_session_seconds_before\":20}", req2Json);

                // Collect 12s (10s window + 2s final window on StopSession -> 12s < longest 20s)
                http2.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                clock2.SteadyTimeUs += 10000000L;
                clock2.UnityTimeUs += 10000000L;
                rt2.Tick();

                clock2.SteadyTimeUs += 2000000L;
                clock2.UnityTimeUs += 2000000L;
                rt2.StopSession();

                // In-process StartSession -> sends (2, 32, 20)
                http2.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-h3", "tok-h3", clock2.WallUtcMs + 12000L, newSessionAfterS: 1800u));
                Assert.IsTrue(rt2.StartSession("test-key-v7"));
                string req3Json = System.Text.Encoding.UTF8.GetString(http2.Calls[http2.Calls.Count - 1].BodyCopy);
                StringAssert.Contains("\"history\":{\"sessions_before\":2,\"collected_seconds_before\":\"32\",\"longest_session_seconds_before\":20}", req3Json);

                // Collect 30s (3 x 10s -> new longest = 30s!) then pause > 1800s -> auto-starts non-collecting holdout share
                for (int w = 0; w < 3; w++)
                {
                    http2.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                    clock2.SteadyTimeUs += 10000000L;
                    clock2.UnityTimeUs += 10000000L;
                    rt2.Tick();
                }

                rt2.OnApplicationPause(true);
                clock2.WallUtcMs += 1805000L;
                clock2.SteadyTimeUs += 1805000000L;
                clock2.UnityTimeUs += 1805000000L;

                // Non-collecting holdout share with omitted "settings" and settings_label = "holdout-zero"
                http2.EnqueueJson(200L, @"{
                    ""status"": ""success"",
                    ""data"": {
                        ""id_org"": ""org-h"", ""id_deployment"": ""dep-h"", ""id_source"": ""src-h"", ""id_session"": ""ses-h4"",
                        ""token"": ""tok-h4"", ""server_utc_ms"": """ + clock2.WallUtcMs + @""",
                        ""batch_endpoint"": """",
                        ""settings_label"": ""holdout-zero""
                    }
                }");
                rt2.OnApplicationPause(false);

                string req4Json = System.Text.Encoding.UTF8.GetString(http2.Calls[http2.Calls.Count - 1].BodyCopy);
                StringAssert.Contains("\"history\":{\"sessions_before\":3,\"collected_seconds_before\":\"62\",\"longest_session_seconds_before\":30}", req4Json);
                AsparagusStatusSnapshot holdoutSnap = rt2.GetStatusSnapshot();
                Assert.IsFalse(holdoutSnap.IsCollecting);
                Assert.AreEqual("holdout-zero", holdoutSnap.SettingsLabel);
                Assert.AreEqual(3u, holdoutSnap.HistorySessionsBefore);
                Assert.AreEqual(62UL, holdoutSnap.HistoryCollectedSecondsBefore);
                Assert.AreEqual(30u, holdoutSnap.HistoryLongestSessionSecondsBefore);
            }

            // --- Launch #3 (real background worker thread!): verifies deferred StartSession loads (4, 62, 30) off main thread ---
            FakeClock clock3 = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs + 2000000L };
            MockHttpTransport http3 = new MockHttpTransport();
            using (AsparagusRuntime rt3 = new AsparagusRuntime(new AsparagusRuntimeOptions
            {
                SdkVersion = new SdkVersion(1, 1, 0),
                StorageBaseDirectory = _tempDir,
                ClockSource = clock3,
                HttpTransport = http3,
                MotionSource = new FakeMotionSource(),
                TouchSource = new FakeTouchSource(),
                ScreenInfoProvider = new FakeScreenProvider(),
                SystemStateProvider = new FakeSystemStateProvider(),
                DeviceMetadataProvider = new FakeDeviceMetadataProvider(),
                RetryPolicy = new RetryBackoffPolicy(() => 0.0),
                RunWorkerSynchronouslyForTests = false,
                AutoLoadDiskQueueOnStart = true
            }))
            {
                http3.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-h5", "tok-h5", clock3.WallUtcMs));
                Assert.IsTrue(rt3.StartSession("test-key-v7"));

                long deadline = Environment.TickCount + 3000;
                while (http3.Calls.Count == 0 && Environment.TickCount < deadline)
                {
                    rt3.Tick();
                    System.Threading.Thread.Sleep(10);
                }

                Assert.AreEqual(1, http3.Calls.Count, "Deferred StartSession must dispatch once background worker finishes startup load");
                string req5Json = System.Text.Encoding.UTF8.GetString(http3.Calls[0].BodyCopy);
                StringAssert.Contains("\"history\":{\"sessions_before\":4,\"collected_seconds_before\":\"62\",\"longest_session_seconds_before\":30}", req5Json);
            }
        }
    }
}
