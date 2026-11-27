using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Network;
using Asparagus.Sdk.Internal.Pipeline;
using Asparagus.Sdk.Internal.Storage;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone5WindowQueueAndTransportTests
    {
        private string _tempDir;

        [SetUp]
        public void SetUp()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "asparagus_m5_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        [TearDown]
        public void TearDown()
        {
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

        private sealed class FakeClockSource : IClockSource
        {
            public long SteadyTimeUs { get; set; }
            public long UnityTimeUs { get; set; }
            public long WallUtcMs { get; set; } = 1794736860000L;
            public int UtcOffsetMinutes { get; set; } = 60;
            public bool HasSensorClock { get; set; }
            public long SensorUs { get; set; }

            public bool TryGetSensorTimeUs(out long sensorUs)
            {
                if (HasSensorClock)
                {
                    sensorUs = SensorUs;
                    return true;
                }
                sensorUs = 0L;
                return false;
            }
        }

        private sealed class RecordedRequest
        {
            public HttpRequestSpec Spec;
            public Action<HttpResponseResult> Callback;
            public byte[] BodyCopy;
        }

        private sealed class MockHttpTransport : IHttpTransport
        {
            private readonly Queue<HttpResponseResult> _autoResponses = new Queue<HttpResponseResult>();
            public readonly List<RecordedRequest> Requests = new List<RecordedRequest>();

            public void EnqueueResponse(long statusCode, byte[] body = null, uint rttMs = 20u)
            {
                _autoResponses.Enqueue(new HttpResponseResult(statusCode, body, body != null ? body.Length : 0, rttMs));
            }

            public void EnqueueJsonResponse(long statusCode, string json, uint rttMs = 20u)
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

                RecordedRequest rec = new RecordedRequest
                {
                    Spec = spec,
                    Callback = onCompleted,
                    BodyCopy = copy
                };
                Requests.Add(rec);

                if (_autoResponses.Count > 0)
                {
                    HttpResponseResult resp = _autoResponses.Dequeue();
                    onCompleted?.Invoke(resp);
                }
            }

            public void Tick() { }
            public void Dispose() { }
        }

        // ====================================================================
        // 1. Proactive W8-26 Early Batch Splitting & Six Header Rules
        // ====================================================================

        [Test]
        public void BatchWindowCoordinator_ProactivelySplitsAt750Events_And7500BoundedMotionSamplesExcludingMag()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 1000000L, UnityTimeUs = 1000000L };
            SessionClock sessionClock = new SessionClock(clock);
            WindowCallOrder order = new WindowCallOrder();
            StateTracker stateTracker = new StateTracker(order);
            EventAndSpanTracker eventTracker = new EventAndSpanTracker(order);
            MotionWindowAggregator motionAgg = new MotionWindowAggregator();
            TouchWindowAggregator touchAgg = new TouchWindowAggregator();
            ScreenTracker screenTracker = new ScreenTracker();
            DiagnosticRecorder diags = new DiagnosticRecorder();

            BatchWindowCoordinator coordinator = new BatchWindowCoordinator(
                clock, sessionClock, order, stateTracker, eventTracker, motionAgg, touchAgg, screenTracker, diags);

            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.HoldRunLength = 0u;

            coordinator.StartNewSession(new SessionIdentity
            {
                IdOrg = new string('a', 1200), // > 1024 bytes -> must truncate cleanly!
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-1",
                SdkVersion = new SdkVersion(1, 0, 0),
                Platform = Platform.Android,
                OsVersion = "15",
                DeviceModel = "TestPhone",
                HasGyroscope = true,
                Settings = settings
            }, 1794736860250L, 40u);

            List<Batch> closedBatches = new List<Batch>();

            // Push 751 events -> Batch 1 closes at exactly 750 events, #751 goes into Batch 2 (Order = 1)
            for (int i = 0; i < 751; i++)
            {
                clock.UnityTimeUs += 100L;
                coordinator.RecordEvent("ev", null, false, null, null, b => closedBatches.Add(b));
            }

            Assert.AreEqual(1, closedBatches.Count, "751st event must trigger proactive early split");
            Batch b1 = closedBatches[0];
            Assert.AreEqual(1UL, b1.Header.Sequence);
            Assert.AreEqual(BatchConstants.SchemaVersion, b1.Header.SchemaVersion);
            Assert.AreEqual(BatchConstants.MaxHeaderStringBytes, Utf8StringBounds.GetUtf8ByteCount(b1.Header.IdOrg));
            Assert.AreEqual(0L, b1.Header.WindowStartUs % 1000000L, "WindowStartUs must be on a whole second");
            Assert.GreaterOrEqual(b1.Header.WindowLengthS, 1u);
            Assert.AreEqual(CloseReason.WindowFull, b1.Header.CloseReason);
            Assert.AreEqual(750, b1.Events.Count);
            Assert.AreEqual(1u, b1.Events[0].Order);
            Assert.AreEqual(750u, b1.Events[749].Order);

            // Event #751 sits in active window (sequence 2) with Order = 1
            Assert.AreEqual(1, eventTracker.CurrentWindowEventCount);
            Assert.AreEqual(1u, eventTracker.WindowEvents[0].Order);

            // Close window 2 to verify sequence 2 & Carried.LastEventTimeUs
            Assert.IsTrue(coordinator.CloseCurrentWindow(CloseReason.WindowFull, out Batch b2));
            Assert.AreEqual(2UL, b2.Header.Sequence);
            Assert.AreEqual(1, b2.Events.Count);
            Assert.IsTrue(b2.Carried.HasLastEventTimeUs);
            Assert.AreEqual(b1.Events[749].TimeUs, b2.Carried.LastEventTimeUs);

            // Motion bound test: 7500 Accel/Gyro + 500 Magnetometer (W0-10 excluded!) must NOT split;
            // 7501st Accel triggers proactive split at 7500 bounded samples AND populates Header.Clocks.HasSensorUs!
            closedBatches.Clear();
            clock.HasSensorClock = false; // even if window opened without sensor clock, safety check must populate it!

            for (int i = 0; i < 7500; i++)
            {
                Sensor s = (i % 2 == 0) ? Sensor.Accelerometer : Sensor.Gyroscope;
                ValueUnit u = s == Sensor.Accelerometer ? ValueUnit.MetresPerSecondSquared : ValueUnit.RadiansPerSecond;
                coordinator.IngestMotionSample(
                    new RawMotionSample(s, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, u, 9007199254740000L + i * 100L, 0.1f, 0.2f, 9.8f),
                    b => closedBatches.Add(b));
            }
            for (int i = 0; i < 500; i++)
            {
                coordinator.IngestMotionSample(
                    new RawMotionSample(Sensor.Magnetometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.Microtesla, 9007199254740000L + i * 1000L, 20f, -5f, -40f),
                    b => closedBatches.Add(b));
            }

            Assert.AreEqual(0, closedBatches.Count, "7500 bounded + 500 Magnetometer must NOT trigger split (W0-10)");

            // 7501st bounded motion sample triggers early split!
            coordinator.IngestMotionSample(
                new RawMotionSample(Sensor.Accelerometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 9007199256000000L, 0.3f, 0.4f, 9.8f),
                b => closedBatches.Add(b));

            Assert.AreEqual(1, closedBatches.Count);
            Batch motionBatch = closedBatches[0];
            Assert.AreEqual(3UL, motionBatch.Header.Sequence);
            Assert.IsTrue(motionBatch.Header.Clocks.HasSensorUs, "Safety check must guarantee Header.Clocks.HasSensorUs when Clock.Sensor blocks exist");

            coordinator.RecycleBatch(b1);
            coordinator.RecycleBatch(b2);
            coordinator.RecycleBatch(motionBatch);
        }

        // ====================================================================
        // 2. DiskBatchQueue Persistence, Eviction, Zero-Rule & Restart Recovery
        // ====================================================================

        [Test]
        public void DiskBatchQueue_EnforcesSizeAndAgeEviction_ZeroRule_AndCrossSessionRestartRecovery()
        {
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            byte[] payload = new byte[1000];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);

            // Size cap ~3200 bytes -> fits at most 2 envelopes (~1090 bytes each); enqueuing 5 evicts oldest 3!
            queue.ConfigureLimits(queueMaxBytes: 3200u, queueMaxAgeS: 900u);
            long t0Ms = 1794736860000L;

            for (ulong seq = 1; seq <= 5; seq++)
            {
                long winStart = (t0Ms + (long)(seq - 1) * 10000L) * 1000L;
                long closed = winStart + 10000000L;
                Assert.IsTrue(queue.Enqueue(seq, "org-1", "dep-1", "src-1", "session-1", winStart, closed, t0Ms + (long)seq * 1000L, true, payload, 0, payload.Length, out _));
            }

            Assert.AreEqual(2, queue.QueuedCount);
            Assert.LessOrEqual(queue.TotalQueuedBytes, 3200L);
            Assert.AreEqual(3u, queue.PendingBatchesDroppedCount);

            Assert.IsTrue(queue.TryClaimDroppedBatchReport(out GapRecord dropGap, out uint headerDropped));
            Assert.AreEqual(3u, headerDropped);
            Assert.AreEqual(0u, queue.PendingBatchesDroppedCount, "Atomic claim must clear pending count");
            Assert.AreEqual(GapSignal.Everything, dropGap.Signal);
            Assert.AreEqual(Clock.Session, dropGap.Clock);
            Assert.AreEqual(GapCause.BatchesDropped, dropGap.Cause);
            Assert.AreEqual(3u, dropGap.BatchesDropped);
            Assert.AreEqual(1UL, dropGap.FirstDroppedSequence);
            Assert.AreEqual(t0Ms * 1000L, dropGap.StartTimeUs);
            Assert.AreEqual((t0Ms + 30000L) * 1000L, dropGap.EndTimeUs);

            // Simulate app restart under a NEW session ("session-2") with same org/dep/src:
            // sequences 4 & 5 from "session-1" must recover intact!
            DiskBatchQueue recovered = new DiskBatchQueue(_tempDir);
            recovered.ConfigureLimits(queueMaxBytes: 50000000u, queueMaxAgeS: 900u);
            recovered.LoadFromDisk(t0Ms + 60000L, "org-1", "dep-1", "src-1");

            Assert.AreEqual(2, recovered.QueuedCount);
            Assert.IsTrue(recovered.TryPeekOldest(t0Ms + 60000L, out QueuedBatchMetadata first));
            Assert.AreEqual(4UL, first.Sequence);
            Assert.AreEqual("session-1", first.IdSession);
            Assert.IsTrue(recovered.TryReadPayload(in first, out byte[] readBytes));
            Assert.AreEqual(payload, readBytes);

            // Age eviction (> 900s = 900000ms) prunes both remaining batches
            Assert.IsFalse(recovered.TryPeekOldest(t0Ms + 910000L, out _));
            Assert.AreEqual(0, recovered.QueuedCount);
            Assert.AreEqual(2u, recovered.PendingBatchesDroppedCount);

            // Zero rule: queue_max_bytes == 0 or queue_max_age_s == 0 disables disk persistence
            recovered.ConfigureLimits(queueMaxBytes: 0u, queueMaxAgeS: 900u);
            Assert.IsTrue(recovered.IsDiskQueueDisabled);
            Assert.IsFalse(recovered.Enqueue(10UL, "org-1", "dep-1", "src-1", "session-2", 0L, 1000000L, t0Ms, true, payload, 0, payload.Length, out _));
            Assert.AreEqual(0, recovered.QueuedCount);
            Assert.AreEqual(3u, recovered.PendingBatchesDroppedCount);
        }

        // ====================================================================
        // 3. RetryBackoffPolicy & Jitter Math
        // ====================================================================

        [Test]
        public void RetryBackoffPolicy_DoublesWithCapAndAppliesUpTo50PercentJitter()
        {
            // Zero jitter (r = 0.0 -> 1.0x)
            RetryBackoffPolicy noJitter = new RetryBackoffPolicy(() => 0.0);
            Assert.AreEqual(2000L, noJitter.ComputeDelayMs(0, 2000u, 300000u));
            Assert.AreEqual(4000L, noJitter.ComputeDelayMs(1, 2000u, 300000u));
            Assert.AreEqual(8000L, noJitter.ComputeDelayMs(2, 2000u, 300000u));
            Assert.AreEqual(300000L, noJitter.ComputeDelayMs(20, 2000u, 300000u));

            // Clamping when firstWait > longestWait
            Assert.AreEqual(10000L, noJitter.ComputeDelayMs(0, 50000u, 10000u));

            // Max jitter (r = 1.0 -> 0.5x)
            RetryBackoffPolicy maxJitter = new RetryBackoffPolicy(() => 1.0);
            Assert.AreEqual(1000L, maxJitter.ComputeDelayMs(0, 2000u, 300000u));
            Assert.AreEqual(2000L, maxJitter.ComputeDelayMs(1, 2000u, 300000u));
            Assert.AreEqual(150000L, maxJitter.ComputeDelayMs(20, 2000u, 300000u));

            // Mid jitter (r = 0.5 -> 0.75x)
            RetryBackoffPolicy midJitter = new RetryBackoffPolicy(() => 0.5);
            Assert.AreEqual(1500L, midJitter.ComputeDelayMs(0, 2000u, 300000u));
        }

        // ====================================================================
        // 4. AsparagusTransportCoordinator Full HTTP Status Matrix
        // ====================================================================

        [Test]
        public void TransportCoordinator_SessionStartMatrix_RefusalRetryAndEmptyEndpoint()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: true);
            MockHttpTransport http = new MockHttpTransport();
            RetryBackoffPolicy backoff = new RetryBackoffPolicy(() => 0.0); // deterministic: 2000ms

            AsparagusTransportCoordinator transport = new AsparagusTransportCoordinator(
                http, worker, sessionClock, clock, backoff);

            // 1. 503 -> waits 2000ms (collecting disabled) -> 200 with valid batch_endpoint -> active & collecting!
            http.EnqueueResponse(503L);
            http.EnqueueJsonResponse(200L, @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-1"",
                    ""id_deployment"": ""dep-1"",
                    ""id_source"": ""src-1"",
                    ""id_session"": ""ses-1"",
                    ""token"": ""tok-1"",
                    ""token_expires_utc_ms"": ""1794823260000"",
                    ""server_utc_ms"": ""1794736860000"",
                    ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches"",
                    ""access_request_endpoint"": ""https://cp.stage/access"",
                    ""forget_request_endpoint"": ""https://cp.stage/forget"",
                    ""settings"": { ""compress"": true }
                }
            }");

            SessionStartRequest req = new SessionStartRequest { ApiKey = "k", DeviceIdentifier = "d", Platform = Platform.Android };
            Assert.IsTrue(transport.StartSessionViaControlPlane("https://cp.stage", req));
            Assert.AreEqual(TransportState.WaitingSessionRetry, transport.State);
            Assert.IsFalse(transport.IsCollectingEnabled);

            transport.Tick(1999L); // still waiting
            Assert.AreEqual(1, http.Requests.Count);
            transport.Tick(2000L); // retries and receives 200!
            Assert.AreEqual(2, http.Requests.Count);
            Assert.AreEqual(TransportState.Active, transport.State);
            Assert.IsTrue(transport.IsCollectingEnabled);
            Assert.AreEqual("tok-1", transport.Token);

            // Compliance POSTs send Bearer token and invoke callbacks cleanly
            http.EnqueueResponse(202L);
            http.EnqueueResponse(204L);
            bool accessOk = false;
            long accessCode = 0L;
            transport.SendPersonalDataAccessRequest("{\"reason\":\"user\"}", (ok, code) => { accessOk = ok; accessCode = code; });
            Assert.IsTrue(accessOk);
            Assert.AreEqual(202L, accessCode);
            Assert.AreEqual("tok-1", http.Requests[2].Spec.BearerToken);

            bool forgetOk = false;
            long forgetCode = 0L;
            transport.SendPersonalDataForgetRequest("{}", (ok, code) => { forgetOk = ok; forgetCode = code; });
            Assert.IsTrue(forgetOk);
            Assert.AreEqual(204L, forgetCode);

            // 2. Empty batch_endpoint -> Active but collects nothing
            MockHttpTransport httpEmptyEp = new MockHttpTransport();
            httpEmptyEp.EnqueueJsonResponse(200L, @"{""status"":""success"",""data"":{""id_org"":""o"",""id_deployment"":""d"",""id_source"":""s"",""id_session"":""x"",""token"":""t"",""server_utc_ms"":""1794736860000"",""batch_endpoint"":""""}}");
            AsparagusTransportCoordinator emptyEpTransport = new AsparagusTransportCoordinator(httpEmptyEp, worker, new SessionClock(clock), clock, backoff);
            emptyEpTransport.StartSessionViaControlPlane("https://cp.stage", req);
            Assert.AreEqual(TransportState.Active, emptyEpTransport.State);
            Assert.IsFalse(emptyEpTransport.IsCollectingEnabled);

            // 3. 403 -> refused for app run (never retries)
            MockHttpTransport httpRefused = new MockHttpTransport();
            httpRefused.EnqueueResponse(403L);
            AsparagusTransportCoordinator refusedTransport = new AsparagusTransportCoordinator(httpRefused, worker, new SessionClock(clock), clock, backoff);
            refusedTransport.StartSessionViaControlPlane("https://cp.stage", req);
            Assert.IsTrue(refusedTransport.IsRefusedForAppRun);
            Assert.AreEqual(TransportState.RefusedForAppRun, refusedTransport.State);
            refusedTransport.Tick(999999L);
            Assert.AreEqual(1, httpRefused.Requests.Count);
        }

        [Test]
        public void TransportCoordinator_UploadMatrix_200_503_400_And401ReauthCrossSession()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: true);
            MockHttpTransport http = new MockHttpTransport();
            RetryBackoffPolicy backoff = new RetryBackoffPolicy(() => 0.0);
            DiagnosticRecorder diags = new DiagnosticRecorder();

            AsparagusTransportCoordinator transport = new AsparagusTransportCoordinator(
                http, worker, sessionClock, clock, backoff, diags);

            // Session 1 start
            http.EnqueueJsonResponse(200L, @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-1"", ""id_deployment"": ""dep-1"", ""id_source"": ""src-1"", ""id_session"": ""session-1"",
                    ""token"": ""token-session-1"", ""token_expires_utc_ms"": ""1794823260000"", ""server_utc_ms"": ""1794736860000"",
                    ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches"",
                    ""settings"": { ""compress"": true, ""retry_first_wait_ms"": 1000, ""retry_longest_wait_ms"": 10000 }
                }
            }");
            transport.StartSessionViaControlPlane("https://cp.stage", new SessionStartRequest { ApiKey = "k", DeviceIdentifier = "d" });

            // Queue 3 batches from session-1:
            // Batch 1: 503 (keep) -> retry -> 200 (delete)
            // Batch 2: 400 (permanent delete + diagnostic)
            // Batch 3: 401 -> backoff -> start session-2 -> re-upload session-1 batch under token-session-2 -> 200!
            worker.EnqueueClosedBatch(MakeMinimalBatch("session-1", 1UL), true, clock.WallUtcMs);
            worker.EnqueueClosedBatch(MakeMinimalBatch("session-1", 2UL), false, clock.WallUtcMs + 1000L);
            worker.EnqueueClosedBatch(MakeMinimalBatch("session-1", 3UL), true, clock.WallUtcMs + 2000L);
            Assert.AreEqual(3, queue.QueuedCount);

            // Batch 1: 503 then 200 (08 01)
            http.EnqueueResponse(503L);
            transport.Tick(1000L);
            Assert.AreEqual(3, queue.QueuedCount, "503 must keep batch on disk");
            Assert.AreEqual("gzip", http.Requests[1].Spec.ContentEncoding);
            Assert.AreEqual(0x1f, http.Requests[1].BodyCopy[0]);
            Assert.AreEqual(0x8b, http.Requests[1].BodyCopy[1]);

            http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
            transport.Tick(1999L); // still within 1000ms backoff
            Assert.AreEqual(2, http.Requests.Count);
            transport.Tick(2000L); // retries Batch 1 -> 200 -> deleted!
            Assert.AreEqual(2, queue.QueuedCount);

            // Batch 2 (uncompressed -> content-encoding MUST be omitted): 400 -> permanent drop + diagnostic
            http.EnqueueResponse(400L);
            transport.Tick(2100L);
            Assert.AreEqual(1, queue.QueuedCount, "400 must permanently delete batch from disk");
            Assert.IsNull(http.Requests[3].Spec.ContentEncoding, "Uncompressed upload must omit content-encoding");
            Assert.Greater(diags.ActiveCount, 0);

            // Batch 3: 401 -> keeps batch on disk, backs off 1000ms, starts session-2, uploads session-1 batch with session-2 token!
            http.EnqueueResponse(401L);
            transport.Tick(2200L);
            Assert.AreEqual(1, queue.QueuedCount, "401 must keep batch in queue");
            Assert.AreEqual(TransportState.WaitingReauth401, transport.State);
            Assert.AreEqual(1, transport.Consecutive401Count);

            http.EnqueueJsonResponse(200L, @"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-1"", ""id_deployment"": ""dep-1"", ""id_source"": ""src-1"", ""id_session"": ""session-2"",
                    ""token"": ""token-session-2"", ""token_expires_utc_ms"": ""1794823260000"", ""server_utc_ms"": ""1794736870000"",
                    ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches"",
                    ""settings"": { ""compress"": true }
                }
            }");
            transport.Tick(3200L); // re-auth fires -> session-2 active!
            Assert.AreEqual(TransportState.Active, transport.State);
            Assert.AreEqual("token-session-2", transport.Token);

            http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
            transport.Tick(3300L); // uploads leftover session-1 batch #3 under token-session-2!
            Assert.AreEqual(0, queue.QueuedCount);
            Assert.AreEqual(0, transport.Consecutive401Count, "200 must reset consecutive 401 count");

            RecordedRequest reuploaded = http.Requests[http.Requests.Count - 1];
            Assert.AreEqual("token-session-2", reuploaded.Spec.BearerToken);
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(reuploaded.BodyCopy, 0, reuploaded.BodyCopy.Length, out Batch decoded, out _));
            Assert.AreEqual("session-1", decoded.Header.IdSession, "Leftover batch keeps its original IdSession inside Header");
            Assert.AreEqual(3UL, decoded.Header.Sequence);
        }

        // ====================================================================
        // 5. Real Background Worker Thread + Stage Override Test
        // ====================================================================

        [Test]
        public void AsparagusWorker_RealBackgroundThread_EncodesPersistsRecyclesAndShutsDownCleanly()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: false);
            MockHttpTransport http = new MockHttpTransport();

            try
            {
                AsparagusTransportCoordinator transport = new AsparagusTransportCoordinator(
                    http, worker, sessionClock, clock, new RetryBackoffPolicy(() => 0.0));

                Assert.IsTrue(transport.StartSessionWithStageOverride(new StageBootstrapConfig
                {
                    Token = "stage-manual-token",
                    IdOrg = "org-1",
                    IdDeployment = "dep-1",
                    IdSource = "src-1",
                    IdSession = "ses-stage",
                    BatchEndpoint = "https://intake.stage/asparagus/v1/batches"
                }));
                Assert.IsTrue(transport.IsCollectingEnabled);
                Assert.AreEqual(0, http.Requests.Count, "Stage override must not call Control Plane");

                // Submit 3 batches to the real background thread
                for (ulong seq = 1; seq <= 3; seq++)
                {
                    http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
                    worker.EnqueueClosedBatch(MakeMinimalBatch("ses-stage", seq), true, clock.WallUtcMs + (long)seq * 1000L);
                }

                int recycledCount = 0;
                long deadline = Environment.TickCount + 3000;
                while ((http.Requests.Count < 3 || recycledCount < 3 || queue.QueuedCount > 0) && Environment.TickCount < deadline)
                {
                    worker.DrainRecycledBatches(_ => recycledCount++);
                    transport.Tick(1000L);
                    Thread.Sleep(10);
                }

                worker.DrainRecycledBatches(_ => recycledCount++);
                Assert.AreEqual(3, recycledCount, "All 3 closed batches must be recycled after disk persistence");
                Assert.AreEqual(3, http.Requests.Count, "All 3 batches must upload to Stage endpoint");
                Assert.AreEqual(0, queue.QueuedCount, "All 3 batches must be deleted from disk after 200");
                Assert.AreEqual("stage-manual-token", http.Requests[0].Spec.BearerToken);
            }
            finally
            {
                worker.Shutdown(500);
            }
        }

        // ====================================================================
        // 6. Dropped-Batch Rollback on Offline Eviction & Strict MaxGaps Cap
        // ====================================================================

        [Test]
        public void DroppedBatchReport_RollsBackWhenCarryingBatchIsEvictedOffline_AndGapsNeverExceed750()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, UnityTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: true);

            BatchWindowCoordinator coordinator = new BatchWindowCoordinator(
                clock, sessionClock, new WindowCallOrder(), new StateTracker(new WindowCallOrder()),
                new EventAndSpanTracker(new WindowCallOrder()), new MotionWindowAggregator(),
                new TouchWindowAggregator(), new ScreenTracker(), new DiagnosticRecorder(),
                null, queue);

            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.QueueMaxBytes = 50000000u;
            settings.QueueMaxAgeS = 900u;

            coordinator.StartNewSession(new SessionIdentity
            {
                IdOrg = "org-1",
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-offline",
                SdkVersion = new SdkVersion(1, 0, 0),
                Platform = Platform.Android,
                Settings = settings
            }, clock.WallUtcMs, 20u);

            // Simulate Batches 1 & 2 evicted while offline (age eviction at t0 + 950s)
            long t1Start = 1794736860000000L;
            long t1End = 1794736870000000L;
            long t2Start = 1794736870000000L;
            long t2End = 1794736880000000L;
            long t3Start = 1794736880000000L;
            long t3End = 1794736890000000L;

            queue.ConfigureLimits(queueMaxBytes: 50000000u, queueMaxAgeS: 60u);
            byte[] dummy = new byte[64];
            Assert.IsTrue(queue.Enqueue(1UL, "org-1", "dep-1", "src-1", "ses-offline", t1Start, t1End, 1000L, false, dummy, 0, dummy.Length, out _));
            Assert.IsTrue(queue.Enqueue(2UL, "org-1", "dep-1", "src-1", "ses-offline", t2Start, t2End, 2000L, false, dummy, 0, dummy.Length, out _));
            Assert.AreEqual(2, queue.PruneExpired(100000L));
            Assert.AreEqual(2u, queue.PendingBatchesDroppedCount);

            // Batch 3 closes and atomically claims the report for Batches 1 & 2
            Batch batch3 = MakeMinimalBatch("ses-offline", 3UL);
            batch3.Header.WindowStartUs = t3Start;
            batch3.Header.ClosedUs = t3End;
            Assert.IsTrue(queue.TryClaimDroppedBatchReport(out GapRecord b3Gap, out uint b3Dropped));
            Assert.AreEqual(2u, b3Dropped);
            batch3.Header.BatchesDropped = b3Dropped;
            batch3.Gaps.Add(b3Gap);

            // Pending drops are now 0, so another window closing while Batch 3 is queued does NOT emit orphaned BatchesDropped!
            Assert.AreEqual(0u, queue.PendingBatchesDroppedCount);
            Assert.IsFalse(queue.HasPendingDroppedBatchReport);

            // Persist Batch 3 through worker (persists CarriedDroppedCount=2, FirstSeq=1, [t1Start..t2End] in ABQ1 v2 header)
            worker.EnqueueClosedBatch(batch3, false, 100000L);
            Assert.AreEqual(1, queue.QueuedCount);

            // Simulate restart (LoadFromDisk) and then evict Batch 3 itself by age while still offline!
            DiskBatchQueue reloaded = new DiskBatchQueue(_tempDir);
            reloaded.ConfigureLimits(queueMaxBytes: 50000000u, queueMaxAgeS: 60u);
            reloaded.LoadFromDisk(200000L, "org-1", "dep-1", "src-1"); // > 60s after 100000ms -> Batch 3 evicted!

            Assert.AreEqual(0, reloaded.QueuedCount);
            Assert.AreEqual(3u, reloaded.PendingBatchesDroppedCount, "Evicting Batch 3 must roll back Batches 1..2 plus Batch 3");

            // Batch 4 claims the rolled-back cumulative drop report
            Assert.IsTrue(reloaded.TryClaimDroppedBatchReport(out GapRecord b4Gap, out uint b4Dropped));
            Assert.AreEqual(3u, b4Dropped);
            Assert.AreEqual(GapCause.BatchesDropped, b4Gap.Cause);
            Assert.AreEqual(GapSignal.Everything, b4Gap.Signal);
            Assert.AreEqual(Clock.Session, b4Gap.Clock);
            Assert.AreEqual(3u, b4Gap.BatchesDropped);
            Assert.AreEqual(1UL, b4Gap.FirstDroppedSequence);
            Assert.AreEqual(t1Start, b4Gap.StartTimeUs);
            Assert.AreEqual(t3End, b4Gap.EndTimeUs);

            // Verify BatchWindowCoordinator:
            // (a) includes HasPendingDroppedBatchReport in TotalWindowGapCount for proactive splitting, AND
            // (b) strictly caps batch.Gaps at 750 on flush even if simultaneous motion/extra gaps exceed 750,
            //     reserving 1 slot for dropGap and spilling excess gaps into the next window!
            reloaded.RestoreCarriedDropReport(3u, 1UL, t1Start, t3End);
            MotionWindowAggregator capMotionAgg = new MotionWindowAggregator();
            BatchWindowCoordinator cappedCoord = new BatchWindowCoordinator(
                clock, new SessionClock(clock), new WindowCallOrder(), new StateTracker(new WindowCallOrder()),
                new EventAndSpanTracker(new WindowCallOrder()), capMotionAgg,
                new TouchWindowAggregator(), new ScreenTracker(), new DiagnosticRecorder(),
                null, reloaded);
            cappedCoord.StartNewSession(new SessionIdentity
            {
                IdOrg = "org-1",
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-cap",
                Settings = settings
            }, clock.WallUtcMs, 20u);

            // Add 748 extra gaps (TotalWindowGapCount == 749 including pending drop report -> no proactive split yet)
            List<Batch> proactiveSplits = new List<Batch>();
            for (int i = 0; i < 748; i++)
            {
                cappedCoord.RecordGap(new GapRecord
                {
                    Signal = GapSignal.Touch,
                    Clock = Clock.Unity,
                    StartTimeUs = i * 100L,
                    EndTimeUs = i * 100L + 50L,
                    Cause = GapCause.BufferOverflowed
                }, b => proactiveSplits.Add(b));
            }
            Assert.AreEqual(0, proactiveSplits.Count, "748 extra + 1 pending drop = 749 (< 750)");
            Assert.AreEqual(749, cappedCoord.TotalWindowGapCount);

            // Now inject 4 simultaneous motion gaps directly into capMotionAgg (simulating multi-sensor flush burst -> 752 + 1 = 753 total!)
            capMotionAgg.RecordExternalGap(new GapRecord { Signal = GapSignal.Accelerometer, Clock = Clock.Unity, StartTimeUs = 1000L, EndTimeUs = 2000L, Cause = GapCause.BufferOverflowed });
            capMotionAgg.RecordExternalGap(new GapRecord { Signal = GapSignal.Gyroscope, Clock = Clock.Unity, StartTimeUs = 1000L, EndTimeUs = 2000L, Cause = GapCause.BufferOverflowed });
            capMotionAgg.RecordExternalGap(new GapRecord { Signal = GapSignal.Gravity, Clock = Clock.Unity, StartTimeUs = 1000L, EndTimeUs = 2000L, Cause = GapCause.BufferOverflowed });
            capMotionAgg.RecordExternalGap(new GapRecord { Signal = GapSignal.Magnetometer, Clock = Clock.Unity, StartTimeUs = 1000L, EndTimeUs = 2000L, Cause = GapCause.BufferOverflowed });

            Assert.IsTrue(cappedCoord.CloseCurrentWindow(CloseReason.WindowFull, out Batch cappedBatch));
            Assert.AreEqual(BatchConstants.MaxGaps, cappedBatch.Gaps.Count, "batch.Gaps must never exceed 750");
            Assert.AreEqual(3u, cappedBatch.Header.BatchesDropped, "Reserved drop slot must emit BatchesDropped + GapRecord together");
            Assert.AreEqual(GapCause.BatchesDropped, cappedBatch.Gaps[BatchConstants.MaxGaps - 1].Cause);

            // Next window receives the 3 spilled non-drop gaps (752 - 749 = 3)
            Assert.IsTrue(cappedCoord.CloseCurrentWindow(CloseReason.WindowFull, out Batch nextBatch));
            Assert.AreEqual(3, nextBatch.Gaps.Count, "Excess gaps must spill cleanly into next window");
            Assert.AreEqual(0u, nextBatch.Header.BatchesDropped);

            // Also verify 400 permanent refusal rolls back a batch's carried drop report!
            AsparagusWorker refuseWorker = new AsparagusWorker(reloaded, runSynchronouslyForTests: true);
            refuseWorker.EnqueueClosedBatch(cappedBatch, false, 205000L);
            refuseWorker.RequestNextUploadPayload(205000L);
            Assert.IsTrue(refuseWorker.TryTakePreparedUpload(out PreparedUpload refusedPrep));
            Assert.AreEqual(3u, refusedPrep.Metadata.CarriedDroppedCount);
            refuseWorker.AcknowledgePermanentRefusal(in refusedPrep);
            Assert.AreEqual(3u, reloaded.PendingBatchesDroppedCount, "Permanent 4xx refusal must roll back carried drop report");
            Assert.IsTrue(reloaded.TryClaimDroppedBatchReport(out GapRecord afterRefusalGap, out uint afterRefusalCount));
            Assert.AreEqual(3u, afterRefusalCount);
            Assert.AreEqual(1UL, afterRefusalGap.FirstDroppedSequence);
            Assert.AreEqual(t1Start, afterRefusalGap.StartTimeUs);
            Assert.AreEqual(t3End, afterRefusalGap.EndTimeUs);

            cappedCoord.RecycleBatch(nextBatch);
        }

        // ====================================================================
        // 7. StopSession Lifecycle, Stale In-Flight Start & OnBeforeSessionActivated
        // ====================================================================

        [Test]
        public void TransportCoordinator_StopSession_IgnoresStaleStart_DrainsQueuedBatches_AndFiresBeforeHook()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: true);
            MockHttpTransport http = new MockHttpTransport(); // No auto-response yet -> manual deferred callback!

            AsparagusTransportCoordinator transport = new AsparagusTransportCoordinator(
                http, worker, sessionClock, clock, new RetryBackoffPolicy(() => 0.0));

            SessionStartRequest req = new SessionStartRequest { ApiKey = "k", DeviceIdentifier = "d" };
            Assert.IsTrue(transport.StartSessionViaControlPlane("https://cp.stage", req));
            Assert.AreEqual(1, http.Requests.Count);
            Assert.AreEqual(TransportState.StartingSession, transport.State);

            // Call StopSession while SessionStart POST is still in-flight!
            transport.StopSession();
            Assert.AreEqual(TransportState.Idle, transport.State);
            Assert.IsFalse(transport.IsCollectingEnabled);

            // Now deliver the stale 200 response -> MUST be ignored!
            byte[] validSessionJson = System.Text.Encoding.UTF8.GetBytes(@"{
                ""status"": ""success"",
                ""data"": {
                    ""id_org"": ""org-1"", ""id_deployment"": ""dep-1"", ""id_source"": ""src-1"", ""id_session"": ""stale-ses"",
                    ""token"": ""stale-tok"", ""token_expires_utc_ms"": ""1794823260000"", ""server_utc_ms"": ""1794736860000"",
                    ""batch_endpoint"": ""https://intake.stage/asparagus/v1/batches""
                }
            }");
            http.Requests[0].Callback(new HttpResponseResult(200L, validSessionJson, validSessionJson.Length, 25u));
            Assert.AreEqual(TransportState.Idle, transport.State, "Stale SessionStart callback must be ignored after StopSession");
            Assert.IsFalse(transport.IsCollectingEnabled);
            Assert.IsNull(transport.ActiveSessionResponse);

            // Now verify OnBeforeSessionActivated fires BEFORE overwriting active session/clock
            string seenSessionBeforeActivate = null;
            int beforeCount = 0;
            transport.OnBeforeSessionActivated = () =>
            {
                beforeCount++;
                seenSessionBeforeActivate = transport.ActiveSessionResponse != null
                    ? transport.ActiveSessionResponse.IdSession
                    : "<none>";
            };

            Assert.IsTrue(transport.StartSessionWithStageOverride(new StageBootstrapConfig
            {
                Token = "tok-active",
                IdOrg = "org-1",
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-active",
                BatchEndpoint = "https://intake.stage/asparagus/v1/batches",
                TokenExpiresUtcMs = clock.WallUtcMs + 600000L
            }));
            Assert.AreEqual(1, beforeCount);
            Assert.AreEqual("<none>", seenSessionBeforeActivate);
            Assert.AreEqual("ses-active", transport.ActiveSessionResponse.IdSession);

            // Second activation sees the outgoing session intact inside OnBeforeSessionActivated!
            Assert.IsTrue(transport.StartSessionWithStageOverride(new StageBootstrapConfig
            {
                Token = "tok-active-2",
                IdOrg = "org-1",
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-active-2",
                BatchEndpoint = "https://intake.stage/asparagus/v1/batches",
                TokenExpiresUtcMs = clock.WallUtcMs + 600000L
            }));
            Assert.AreEqual(2, beforeCount);
            Assert.AreEqual("ses-active", seenSessionBeforeActivate, "OnBeforeSessionActivated must observe outgoing session before overwrite");

            // Queue a final SessionEnded batch, call StopSession(), and verify Tick() still drains it!
            Batch finalBatch = MakeMinimalBatch("ses-active-2", 9UL);
            finalBatch.Header.CloseReason = CloseReason.SessionEnded;
            worker.EnqueueClosedBatch(finalBatch, true, clock.WallUtcMs);
            Assert.AreEqual(1, queue.QueuedCount);

            transport.StopSession();
            Assert.IsFalse(transport.IsCollectingEnabled);
            Assert.AreEqual(TransportState.Idle, transport.State);

            http.EnqueueResponse(200L, new byte[] { 0x08, 0x01 });
            transport.Tick(1000L);
            Assert.AreEqual(0, queue.QueuedCount, "StopSession must still allow Tick() to drain final queued batches");
            Assert.AreEqual(2, http.Requests.Count);
            Assert.AreEqual("tok-active-2", http.Requests[1].Spec.BearerToken);
        }

        // ====================================================================
        // 8. Zero-Queue Mode Drops Immediately During Backoff & Shutdown
        // ====================================================================

        [Test]
        public void ZeroQueueMode_DropsImmediatelyDuringBackoffAndShutdown_WithoutRetainingRam()
        {
            FakeClockSource clock = new FakeClockSource { SteadyTimeUs = 0L, WallUtcMs = 1794736860000L };
            SessionClock sessionClock = new SessionClock(clock);
            DiskBatchQueue queue = new DiskBatchQueue(_tempDir);
            AsparagusWorker worker = new AsparagusWorker(queue, runSynchronouslyForTests: true);
            MockHttpTransport http = new MockHttpTransport();

            AsparagusTransportCoordinator transport = new AsparagusTransportCoordinator(
                http, worker, sessionClock, clock, new RetryBackoffPolicy(() => 0.0));

            SessionSettings zeroSettings = SessionSettings.CreateDefaults();
            zeroSettings.QueueMaxBytes = 0u;
            zeroSettings.RetryFirstWaitMs = 5000u;

            Assert.IsTrue(transport.StartSessionWithStageOverride(new StageBootstrapConfig
            {
                Token = "tok-zero",
                IdOrg = "org-1",
                IdDeployment = "dep-1",
                IdSource = "src-1",
                IdSession = "ses-zero",
                BatchEndpoint = "https://intake.stage/asparagus/v1/batches",
                Settings = zeroSettings,
                TokenExpiresUtcMs = clock.WallUtcMs + 600000L
            }));
            Assert.IsTrue(queue.IsDiskQueueDisabled);
            Assert.IsTrue(worker.CanAcceptImmediateUpload);

            // Batch 1 sent immediately -> receives 503 -> enters 5000ms backoff and drops Batch 1
            worker.EnqueueClosedBatch(MakeMinimalBatch("ses-zero", 1UL), false, clock.WallUtcMs);
            Assert.IsTrue(worker.HasPreparedUpload);
            http.EnqueueResponse(503L);
            transport.Tick(1000L);
            Assert.IsFalse(worker.CanAcceptImmediateUpload, "During retry backoff, zero-queue mode must refuse ephemeral RAM buffering");
            Assert.AreEqual(1u, queue.PendingBatchesDroppedCount);

            // Batch 2 closed during backoff -> MUST be dropped immediately without storing in _preparedUpload!
            worker.EnqueueClosedBatch(MakeMinimalBatch("ses-zero", 2UL), false, clock.WallUtcMs + 1000L);
            Assert.IsFalse(worker.HasPreparedUpload, "Zero-queue mode must never hold unsent batch bytes in RAM during backoff");
            Assert.AreEqual(2u, queue.PendingBatchesDroppedCount);

            // Advance past backoff so immediate upload is allowed again, enqueue Batch 3 without uploading, then Shutdown()
            transport.Tick(7000L);
            Assert.IsTrue(worker.CanAcceptImmediateUpload);
            worker.EnqueueClosedBatch(MakeMinimalBatch("ses-zero", 3UL), false, clock.WallUtcMs + 7000L);
            Assert.IsTrue(worker.HasPreparedUpload);

            worker.Shutdown();
            Assert.IsFalse(worker.HasPreparedUpload, "Shutdown must release any unsent ephemeral upload");
            Assert.AreEqual(3u, queue.PendingBatchesDroppedCount, "Shutdown must count unsent ephemeral batch as dropped");
        }

        private static Batch MakeMinimalBatch(string sessionId, ulong sequence)
        {
            Batch b = new Batch
            {
                Header = new Header
                {
                    IdOrg = "org-1",
                    IdDeployment = "dep-1",
                    IdSource = "src-1",
                    IdSession = sessionId,
                    Sequence = sequence,
                    SchemaVersion = BatchConstants.SchemaVersion,
                    WindowStartUs = 1794736860000000L + (long)(sequence - 1UL) * 10000000L,
                    WindowLengthS = 10u,
                    ClosedUs = 1794736870000000L + (long)(sequence - 1UL) * 10000000L,
                    CloseReason = CloseReason.WindowFull
                }
            };
            b.Header.SetUtcOffsetMinutes(0);
            return b;
        }
    }
}
