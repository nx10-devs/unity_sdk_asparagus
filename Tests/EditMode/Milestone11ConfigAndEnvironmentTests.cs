using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Asparagus.Sdk;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Network;
using Asparagus.Sdk.Internal.Runtime;
using Asparagus.Sdk.Internal.Storage;
using Asparagus.Sdk.Internal.Time;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone11ConfigAndEnvironmentTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "asparagus_m11_test_" + Guid.NewGuid().ToString("N"));
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

            public bool TryGetSensorTimeUs(out long sensorUs)
            {
                sensorUs = SteadyTimeUs;
                return true;
            }
        }

        private sealed class FakeMotionSource : IMotionCaptureSource
        {
            public CaptureSource SourceKind { get { return CaptureSource.NativeAndroid; } }
            public bool IsRunning { get; private set; }
            public bool HasGyroscope { get { return true; } }

            public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified) { IsRunning = true; }
            public void StopCapture() { IsRunning = false; }
            public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs) { }
            public bool TryConsumeOverflowGap(out GapRecord gap) { gap = default; return false; }
        }

        private sealed class FakeTouchSource : ITouchCaptureSource
        {
            public TouchMode CurrentTouchMode { get { return TouchMode.Events; } }
            public bool IsRunning { get; private set; }
            public bool IsTouchPaused { get; private set; }

            public void StartCapture(SessionSettings settings) { IsRunning = true; }
            public void StopCapture() { IsRunning = false; }
            public void SetTouchPaused(bool paused) { IsTouchPaused = paused; }
            public void DrainSamples(ITouchSampleConsumer consumer, long currentUnityTimeUs) { }
            public bool TryConsumeOverflowGap(out GapRecord gap) { gap = default; return false; }
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
            public bool TryGetScreenBrightness(out double brightness01) { brightness01 = 0.5; return true; }
            public bool TryGetScreenLocked(out bool isLocked) { isLocked = false; return true; }
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
                    DeviceIdentifier = "m11-hw-id",
                    AppVersion = appVersionOverride ?? "1.2.0",
                    BuildNumber = buildNumberOverride ?? "120",
                    DeviceProperties = new DeviceProperties
                    {
                        Maker = "Pixel",
                        Model = "Pixel 9",
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
        }

        private sealed class MockHttpTransport : IHttpTransport
        {
            private readonly Queue<HttpResponseResult> _responses = new Queue<HttpResponseResult>();
            public readonly List<RecordedHttpCall> Calls = new List<RecordedHttpCall>();

            public void EnqueueResponse(long statusCode, byte[] body = null, uint rttMs = 15u)
            {
                _responses.Enqueue(new HttpResponseResult(statusCode, body, body != null ? body.Length : 0, rttMs));
            }

            public void EnqueueJson(long statusCode, string json, uint rttMs = 15u)
            {
                byte[] bytes = string.IsNullOrEmpty(json) ? Array.Empty<byte>() : System.Text.Encoding.UTF8.GetBytes(json);
                EnqueueResponse(statusCode, bytes, rttMs);
            }

            public void StartPost(in HttpRequestSpec spec, Action<HttpResponseResult> onCompleted)
            {
                byte[] copy = null;
                if (spec.Body != null && spec.BodyLength > 0)
                {
                    copy = new byte[spec.BodyLength];
                    Buffer.BlockCopy(spec.Body, 0, copy, 0, spec.BodyLength);
                }

                Calls.Add(new RecordedHttpCall { Spec = spec, BodyCopy = copy });

                if (_responses.Count > 0)
                {
                    onCompleted?.Invoke(_responses.Dequeue());
                }
            }

            public void Tick() { }
            public void Dispose() { }
        }

        private AsparagusRuntime CreateRuntime(
            string storageDir,
            FakeClock clock,
            MockHttpTransport http,
            AsparagusConfiguration config,
            bool? isEditorOrDevOverride,
            bool synchronousWorker = true)
        {
            return new AsparagusRuntime(new AsparagusRuntimeOptions
            {
                SdkVersion = new SdkVersion(1, 2, 0),
                StorageBaseDirectory = storageDir,
                ClockSource = clock,
                HttpTransport = http,
                MotionSource = new FakeMotionSource(),
                TouchSource = new FakeTouchSource(),
                ScreenInfoProvider = new FakeScreenProvider(),
                SystemStateProvider = new FakeSystemStateProvider(),
                DeviceMetadataProvider = new FakeDeviceMetadataProvider(),
                RetryPolicy = new RetryBackoffPolicy(() => 0.0),
                Configuration = config,
                IsEditorOrDevelopmentBuildOverride = isEditorOrDevOverride,
                RunWorkerSynchronouslyForTests = synchronousWorker,
                AutoLoadDiskQueueOnStart = true
            });
        }

        private static string MakeSessionStartSuccessJson(string sessionId, string token, long serverUtcMs, string batchEndpoint)
        {
            return @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-env"",
                    ""id_deployment"": ""dep-env"",
                    ""id_source"": ""src-env"",
                    ""id_session"": """ + sessionId + @""",
                    ""token"": """ + token + @""",
                    ""token_expires_utc_ms"": """ + (serverUtcMs + 86400000L) + @""",
                    ""server_utc_ms"": """ + serverUtcMs + @""",
                    ""batch_endpoint"": """ + batchEndpoint + @""",
                    ""settings_label"": ""env-test"",
                    ""settings"": {
                        ""version"": ""v3-env"",
                        ""batch_length_s"": 10,
                        ""compress"": true
                    }
                }
            }";
        }

        // ====================================================================
        // 1. Environment & Endpoint Resolution
        // ====================================================================

        [Test]
        public void Configuration_EnvironmentResolution_Automatic_Staging_And_Production()
        {
            AsparagusConfiguration cfg = new AsparagusConfiguration
            {
                EnvironmentMode = AsparagusEnvironmentMode.Automatic
            };

            Assert.AreEqual(AsparagusEnvironment.Staging, cfg.ResolveEnvironment(isEditorOrDevelopmentBuild: true));
            Assert.AreEqual("https://control-plane.affectstack-stage.com", cfg.ResolveControlPlaneBaseUrl(isEditorOrDevelopmentBuild: true));

            Assert.AreEqual(AsparagusEnvironment.Production, cfg.ResolveEnvironment(isEditorOrDevelopmentBuild: false));
            Assert.AreEqual("https://control-plane.affectstack.com", cfg.ResolveControlPlaneBaseUrl(isEditorOrDevelopmentBuild: false));

            cfg.EnvironmentMode = AsparagusEnvironmentMode.Staging;
            Assert.AreEqual(AsparagusEnvironment.Staging, cfg.ResolveEnvironment(isEditorOrDevelopmentBuild: false));
            Assert.AreEqual("https://control-plane.affectstack-stage.com", cfg.ResolveControlPlaneBaseUrl(isEditorOrDevelopmentBuild: false));

            cfg.EnvironmentMode = AsparagusEnvironmentMode.Production;
            Assert.AreEqual(AsparagusEnvironment.Production, cfg.ResolveEnvironment(isEditorOrDevelopmentBuild: true));
            Assert.AreEqual("https://control-plane.affectstack.com", cfg.ResolveControlPlaneBaseUrl(isEditorOrDevelopmentBuild: true));

            Assert.AreEqual("https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start", AsparagusEndpoints.GetSessionStartUrl(AsparagusEnvironment.Staging));
            Assert.AreEqual("https://control-plane.affectstack.com/routes/asparagus/sessions/start", AsparagusEndpoints.GetSessionStartUrl(AsparagusEnvironment.Production));
            Assert.AreEqual(AsparagusEnvironment.Staging, AsparagusEndpoints.ClassifyEnvironment("https://CONTROL-PLANE.AFFECTSTACK-STAGE.COM"));
            Assert.AreEqual(AsparagusEnvironment.Production, AsparagusEndpoints.ClassifyEnvironment("https://control-plane.affectstack.com"));
        }

        // ====================================================================
        // 2. API Key Resolution & Environment Variable Fallbacks
        // ====================================================================

        [Test]
        public void Configuration_ApiKeyResolution_RespectsConfiguredKeysAndEnvFallbacks_WithoutThrowing()
        {
            Dictionary<string, string> env = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "ASPARAGUS_STAGING_API_KEY", "  env-stage-key  " },
                { "ASPARAGUS_PRODUCTION_API_KEY", "  env-prod-key  " },
                { "ASPARAGUS_API_KEY", "  env-generic-key  " }
            };
            Func<string, string> reader = name => env.TryGetValue(name, out string v) ? v : null;

            // Configured keys take priority and are trimmed
            AsparagusConfiguration cfg = new AsparagusConfiguration
            {
                EnvironmentMode = AsparagusEnvironmentMode.Automatic,
                StagingApiKey = "  cfg-stage-key  ",
                ProductionApiKey = "  cfg-prod-key  "
            };
            Assert.AreEqual("cfg-stage-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: true, reader));
            Assert.AreEqual("cfg-prod-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: false, reader));

            // Empty configured keys fall back to environment-specific env vars
            cfg.StagingApiKey = "   ";
            cfg.ProductionApiKey = string.Empty;
            Assert.AreEqual("env-stage-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: true, reader));
            Assert.AreEqual("env-prod-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: false, reader));

            // Missing environment-specific env vars fall back to generic ASPARAGUS_API_KEY
            env.Remove("ASPARAGUS_STAGING_API_KEY");
            env.Remove("ASPARAGUS_PRODUCTION_API_KEY");
            Assert.AreEqual("env-generic-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: true, reader));
            Assert.AreEqual("env-generic-key", cfg.ResolveApiKey(isEditorOrDevelopmentBuild: false, reader));

            // Throwing envReader is fault-contained and returns empty string
            Func<string, string> throwingReader = _ => throw new InvalidOperationException("env denied");
            Assert.AreEqual(string.Empty, cfg.ResolveApiKey(isEditorOrDevelopmentBuild: true, throwingReader));
        }

        // ====================================================================
        // 3. Zero-Argument StartSession() & AsparagusSDK.Configure(...)
        // ====================================================================

        [Test]
        public void RuntimeAndFacade_ZeroArgStartSession_RoutesStagingVsProduction_AndAppliesDemoMode()
        {
            AsparagusConfiguration sharedCfg = new AsparagusConfiguration
            {
                EnvironmentMode = AsparagusEnvironmentMode.Automatic,
                StagingApiKey = "stage-deployment-key",
                ProductionApiKey = "prod-deployment-key",
                DemoMode = true
            };

            // (a) Editor/Dev build -> Staging URL + StagingApiKey + DemoMode=true
            FakeClock stageClock = new FakeClock();
            MockHttpTransport stageHttp = new MockHttpTransport();
            string stageDir = Path.Combine(_tempDir, "zero_arg_stage");
            using (AsparagusRuntime rtStage = CreateRuntime(stageDir, stageClock, stageHttp, sharedCfg, isEditorOrDevOverride: true))
            {
                stageHttp.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-st-1", "tok-st", stageClock.WallUtcMs, "https://intake.stage/batches"));
                Assert.IsTrue(rtStage.StartSession());

                Assert.AreEqual(1, stageHttp.Calls.Count);
                Assert.AreEqual("https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start", stageHttp.Calls[0].Spec.Url);
                string body = System.Text.Encoding.UTF8.GetString(stageHttp.Calls[0].BodyCopy);
                StringAssert.Contains("\"api_key\":\"stage-deployment-key\"", body);
                StringAssert.Contains("\"demo\":true", body);

                AsparagusStatusSnapshot snap = rtStage.GetStatusSnapshot();
                Assert.AreEqual(AsparagusEnvironment.Staging, snap.ActiveEnvironment);
                Assert.AreEqual("https://control-plane.affectstack-stage.com", snap.ControlPlaneBaseUrl);
                Assert.IsFalse(snap.IsCollecting, "DemoMode=true must keep sensors/collecting off");
            }

            // (b) Release build -> Production URL + ProductionApiKey + explicit StartSession(demo: false) override
            FakeClock prodClock = new FakeClock();
            MockHttpTransport prodHttp = new MockHttpTransport();
            string prodDir = Path.Combine(_tempDir, "zero_arg_prod");
            using (AsparagusRuntime rtProd = CreateRuntime(prodDir, prodClock, prodHttp, sharedCfg, isEditorOrDevOverride: false))
            {
                prodHttp.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-pr-1", "tok-pr", prodClock.WallUtcMs, "https://intake.prod/batches"));
                Assert.IsTrue(rtProd.StartSession(demo: false, appVersion: "3.4.5", buildNumber: "345"));

                Assert.AreEqual(1, prodHttp.Calls.Count);
                Assert.AreEqual("https://control-plane.affectstack.com/routes/asparagus/sessions/start", prodHttp.Calls[0].Spec.Url);
                string body = System.Text.Encoding.UTF8.GetString(prodHttp.Calls[0].BodyCopy);
                StringAssert.Contains("\"api_key\":\"prod-deployment-key\"", body);
                StringAssert.DoesNotContain("\"demo\":true", body);
                StringAssert.Contains("\"app_version\":\"3.4.5\"", body);
                StringAssert.Contains("\"build_number\":\"345\"", body);

                AsparagusStatusSnapshot snap = rtProd.GetStatusSnapshot();
                Assert.AreEqual(AsparagusEnvironment.Production, snap.ActiveEnvironment);
                Assert.AreEqual("https://control-plane.affectstack.com", snap.ControlPlaneBaseUrl);
                Assert.IsTrue(snap.IsCollecting);
            }

            // (c) Static AsparagusSDK.Configure / GetActiveConfiguration / StartSession()
            AsparagusSDK.Configure(new AsparagusConfiguration
            {
                EnvironmentMode = AsparagusEnvironmentMode.Production,
                StagingApiKey = string.Empty,
                ProductionApiKey = string.Empty,
                DemoMode = true
            });
            AsparagusConfiguration active = AsparagusSDK.GetActiveConfiguration();
            Assert.AreEqual(AsparagusEnvironmentMode.Production, active.EnvironmentMode);
            Assert.IsTrue(active.DemoMode);

            // Empty key -> returns false cleanly (no network call) and status reflects Production environment
            Assert.IsFalse(AsparagusSDK.StartSession());
            AsparagusStatusSnapshot facadeSnap = AsparagusSDK.GetStatusSnapshot();
            Assert.AreEqual(AsparagusEnvironment.Production, facadeSnap.ActiveEnvironment);
            Assert.AreEqual("https://control-plane.affectstack.com", facadeSnap.ControlPlaneBaseUrl);
        }

        // ====================================================================
        // 4. Environment-Isolated CollectionHistory (In-Process & Across Restarts)
        // ====================================================================

        [Test]
        public void Runtime_CollectionHistory_IsolatedBetweenStagingAndProduction_InProcessAndAcrossRestarts()
        {
            long baseUtcMs = 1794736860000L;
            string sharedDir = Path.Combine(_tempDir, "env_isolated_history");
            Directory.CreateDirectory(sharedDir);

            // --- Launch #1: Collect 20s on Staging, switch directly to Production (collect 10s), switch back to Staging ---
            FakeClock clock1 = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs };
            MockHttpTransport http1 = new MockHttpTransport();
            using (AsparagusRuntime rt1 = CreateRuntime(sharedDir, clock1, http1, new AsparagusConfiguration(), isEditorOrDevOverride: true))
            {
                // 1. Start Staging session (first launch -> zero history)
                http1.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-st-1", "tok-st-1", clock1.WallUtcMs, "https://intake.stage/batches"));
                Assert.IsTrue(rt1.StartSession(AsparagusEnvironment.Staging, "stage-key"));
                Assert.AreEqual("https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start", http1.Calls[0].Spec.Url);
                StringAssert.DoesNotContain("\"history\"", System.Text.Encoding.UTF8.GetString(http1.Calls[0].BodyCopy));

                // Collect 20s (2 x 10s batches) on Staging
                for (int i = 0; i < 2; i++)
                {
                    http1.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                    clock1.SteadyTimeUs += 10000000L;
                    clock1.UnityTimeUs += 10000000L;
                    clock1.WallUtcMs += 10000L;
                    rt1.Tick();
                }

                // Staging file exists; Production file MUST NOT exist yet!
                Assert.IsTrue(File.Exists(Path.Combine(sharedDir, CollectionHistoryStore.StagingFileName)));
                Assert.IsFalse(File.Exists(Path.Combine(sharedDir, CollectionHistoryStore.FileName)));

                // 2. Switch IN-PROCESS directly to Production -> MUST send zero history (Staging MUST NOT leak!)
                http1.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-pr-1", "tok-pr-1", clock1.WallUtcMs, "https://intake.prod/batches"));
                Assert.IsTrue(rt1.StartSession(AsparagusEnvironment.Production, "prod-key"));
                RecordedHttpCall prodStartCall = http1.Calls[http1.Calls.Count - 1];
                Assert.AreEqual("https://control-plane.affectstack.com/routes/asparagus/sessions/start", prodStartCall.Spec.Url);
                StringAssert.DoesNotContain("\"history\"", System.Text.Encoding.UTF8.GetString(prodStartCall.BodyCopy), "Staging history must NEVER leak into Production");

                // Collect 10s on Production
                http1.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                clock1.SteadyTimeUs += 10000000L;
                clock1.UnityTimeUs += 10000000L;
                clock1.WallUtcMs += 10000L;
                rt1.Tick();
                Assert.IsTrue(File.Exists(Path.Combine(sharedDir, CollectionHistoryStore.FileName)));

                // 3. Switch back IN-PROCESS to Staging -> MUST send Staging's (1, 20, 20), NOT Production's 10s!
                http1.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-st-2", "tok-st-2", clock1.WallUtcMs, "https://intake.stage/batches"));
                Assert.IsTrue(rt1.StartSession(AsparagusEnvironment.Staging, "stage-key"));
                RecordedHttpCall stageStart2Call = http1.Calls[http1.Calls.Count - 1];
                Assert.AreEqual("https://control-plane.affectstack-stage.com/routes/asparagus/sessions/start", stageStart2Call.Spec.Url);
                StringAssert.Contains(
                    "\"history\":{\"sessions_before\":1,\"collected_seconds_before\":\"20\",\"longest_session_seconds_before\":20}",
                    System.Text.Encoding.UTF8.GetString(stageStart2Call.BodyCopy));
            }

            // --- Launch #2 (real background worker thread): verifies separate persisted files reload cleanly ---
            FakeClock clock2 = new FakeClock { SteadyTimeUs = 0L, UnityTimeUs = 100000L, WallUtcMs = baseUtcMs + 600000L };
            MockHttpTransport http2 = new MockHttpTransport();
            using (AsparagusRuntime rt2 = CreateRuntime(
                sharedDir,
                clock2,
                http2,
                new AsparagusConfiguration(),
                isEditorOrDevOverride: false,
                synchronousWorker: false))
            {
                // Start Production -> must report Production's (1, 10, 10)
                http2.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-pr-2", "tok-pr-2", clock2.WallUtcMs, "https://intake.prod/batches"));
                Assert.IsTrue(rt2.StartSession(AsparagusEnvironment.Production, "prod-key"));

                long deadline = Environment.TickCount + 3000;
                while (http2.Calls.Count == 0 && Environment.TickCount < deadline)
                {
                    rt2.Tick();
                    Thread.Sleep(10);
                }

                Assert.AreEqual(1, http2.Calls.Count);
                StringAssert.Contains(
                    "\"history\":{\"sessions_before\":1,\"collected_seconds_before\":\"10\",\"longest_session_seconds_before\":10}",
                    System.Text.Encoding.UTF8.GetString(http2.Calls[0].BodyCopy));

                // Start Staging -> must report Staging's (2, 20, 20)
                http2.EnqueueJson(200L, MakeSessionStartSuccessJson("ses-st-3", "tok-st-3", clock2.WallUtcMs + 1000L, "https://intake.stage/batches"));
                Assert.IsTrue(rt2.StartSession(AsparagusEnvironment.Staging, "stage-key"));
                Assert.AreEqual(2, http2.Calls.Count);
                StringAssert.Contains(
                    "\"history\":{\"sessions_before\":2,\"collected_seconds_before\":\"20\",\"longest_session_seconds_before\":20}",
                    System.Text.Encoding.UTF8.GetString(http2.Calls[1].BodyCopy));
            }
        }

        // ====================================================================
        // 5. Literal StartSession(null) Compiles With Zero CS0121 Ambiguity
        // ====================================================================

        [Test]
        public void StartSession_LiteralNull_CompilesWithoutOverloadAmbiguity_AndReturnsFalse()
        {
            FakeClock clock = new FakeClock();
            MockHttpTransport http = new MockHttpTransport();
            using (AsparagusRuntime rt = CreateRuntime(_tempDir, clock, http, new AsparagusConfiguration(), isEditorOrDevOverride: true))
            {
                Assert.IsFalse(rt.StartSession(null));
                Assert.IsFalse(rt.StartSession("   "));
                Assert.AreEqual(0, http.Calls.Count);
            }

            Assert.IsFalse(AsparagusSDK.StartSession(null));
            Assert.IsFalse(AsparagusSDK.StartSession("   "));
        }
    }
}
