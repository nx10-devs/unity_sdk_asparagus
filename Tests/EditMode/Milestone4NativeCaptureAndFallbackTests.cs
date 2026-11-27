using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Tracking;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone4NativeCaptureAndFallbackTests
    {
        private sealed class MockNativeMotionBridge : INativeMotionBridge
        {
            private readonly Queue<long[]> _packedQueue = new Queue<long[]>();
            private readonly Dictionary<Sensor, bool> _hasSensor = new Dictionary<Sensor, bool>();
            private readonly Dictionary<Sensor, NativeSensorStartResult> _startResults = new Dictionary<Sensor, NativeSensorStartResult>();
            private readonly Dictionary<Sensor, (long startUs, long endUs)> _overflows = new Dictionary<Sensor, (long, long)>();

            public CaptureSource SourceKind { get; set; } = CaptureSource.NativeAndroid;
            public Platform NativePlatform { get; set; } = Platform.Android;
            public bool IsPluginAvailable { get; set; } = true;
            public long SensorClockUs { get; set; } = 9007199254740993L;
            public int PendingErrorCount { get; set; }
            public bool FailNextDrain { get; set; }
            public bool LastPausedState { get; private set; }
            public uint LastPausedBufferS { get; private set; }
            public readonly List<Sensor> StoppedSensors = new List<Sensor>();
            public int StopAllCalls { get; private set; }

            public MockNativeMotionBridge()
            {
                _hasSensor[Sensor.Accelerometer] = true;
                _hasSensor[Sensor.Gyroscope] = true;
                _hasSensor[Sensor.Gravity] = true;
                _hasSensor[Sensor.Magnetometer] = true;

                _startResults[Sensor.Accelerometer] = NativeSensorStartResult.Started;
                _startResults[Sensor.Gyroscope] = NativeSensorStartResult.Started;
                _startResults[Sensor.Gravity] = NativeSensorStartResult.Started;
                _startResults[Sensor.Magnetometer] = NativeSensorStartResult.Started;
            }

            public void SetHasSensor(Sensor sensor, bool has) { _hasSensor[sensor] = has; }
            public void SetStartResult(Sensor sensor, NativeSensorStartResult res) { _startResults[sensor] = res; }
            public void SetOverflow(Sensor sensor, long startUs, long endUs) { _overflows[sensor] = (startUs, endUs); }

            public void EnqueuePackedSample(Sensor sensor, long timestampUs, float x, float y, float z)
            {
                long[] rec = new long[PackedMotionRecord.LongsPerSample];
                PackedMotionRecord.Pack(rec, 0, sensor, timestampUs, x, y, z);
                _packedQueue.Enqueue(rec);
            }

            public bool HasSensor(Sensor sensor)
            {
                return _hasSensor.TryGetValue(sensor, out bool v) && v;
            }

            public ValueUnit GetSensorUnit(Sensor sensor)
            {
                switch (sensor)
                {
                    case Sensor.Accelerometer:
                        return NativePlatform == Platform.Ios ? ValueUnit.G : ValueUnit.MetresPerSecondSquared;
                    case Sensor.Gyroscope:
                        return ValueUnit.RadiansPerSecond;
                    case Sensor.Gravity:
                        return NativePlatform == Platform.Ios ? ValueUnit.G : ValueUnit.MetresPerSecondSquared;
                    case Sensor.Magnetometer:
                        return ValueUnit.Microtesla;
                    default:
                        return ValueUnit.Unspecified;
                }
            }

            public NativeSensorStartResult StartSensor(Sensor sensor, uint requestedHz, uint bufferSeconds)
            {
                return _startResults.TryGetValue(sensor, out NativeSensorStartResult res)
                    ? res
                    : NativeSensorStartResult.MissingOrRefused;
            }

            public void StopSensor(Sensor sensor)
            {
                StoppedSensors.Add(sensor);
            }

            public void StopAll()
            {
                StopAllCalls++;
            }

            public void SetUnityPaused(bool paused, uint bufferSeconds)
            {
                LastPausedState = paused;
                LastPausedBufferS = bufferSeconds;
            }

            public bool TryGetSensorClockUs(out long sensorTimeUs)
            {
                sensorTimeUs = SensorClockUs;
                return IsPluginAvailable;
            }

            public int DrainPackedSamples(long[] destinationPacked, int maxSamples)
            {
                if (FailNextDrain)
                {
                    FailNextDrain = false;
                    return -1;
                }

                int drained = 0;
                while (drained < maxSamples && _packedQueue.Count > 0)
                {
                    long[] item = _packedQueue.Dequeue();
                    int offset = drained * PackedMotionRecord.LongsPerSample;
                    destinationPacked[offset] = item[0];
                    destinationPacked[offset + 1] = item[1];
                    destinationPacked[offset + 2] = item[2];
                    drained++;
                }

                return drained;
            }

            public bool TryConsumeOverflow(Sensor sensor, out long startTimeUs, out long endTimeUs)
            {
                if (_overflows.TryGetValue(sensor, out (long startUs, long endUs) range))
                {
                    _overflows.Remove(sensor);
                    startTimeUs = range.startUs;
                    endTimeUs = range.endUs;
                    return true;
                }

                startTimeUs = 0L;
                endTimeUs = 0L;
                return false;
            }

            public int ConsumeErrorCount()
            {
                int count = PendingErrorCount;
                PendingErrorCount = 0;
                return count;
            }
        }

        private sealed class MockUnityMotionSource : IMotionCaptureSource
        {
            private readonly Queue<RawMotionSample> _samples = new Queue<RawMotionSample>();

            public CaptureSource SourceKind { get { return CaptureSource.UnityEvents; } }
            public bool IsRunning { get; private set; }
            public bool HasGyroscope { get; set; } = true;
            public SessionSettings LastStartedSettings { get; private set; }
            public FallbackReason LastStartedFallbackReason { get; private set; }

            public void EnqueueSample(Sensor sensor, long unityUs, float x, float y, float z)
            {
                ValueUnit unit = sensor == Sensor.Gyroscope
                    ? ValueUnit.RadiansPerSecond
                    : (sensor == Sensor.Magnetometer ? ValueUnit.Microtesla : ValueUnit.G);
                _samples.Enqueue(new RawMotionSample(sensor, CaptureSource.UnityEvents, FallbackReason.Unspecified, Clock.Unity, unit, unityUs, x, y, z));
            }

            public void StartCapture(SessionSettings settings, FallbackReason fallbackReason = FallbackReason.Unspecified)
            {
                IsRunning = true;
                LastStartedSettings = settings;
                LastStartedFallbackReason = fallbackReason;
            }

            public void StopCapture()
            {
                IsRunning = false;
                _samples.Clear();
            }

            public void DrainSamples(IMotionSampleConsumer consumer, long currentUnityTimeUs)
            {
                while (_samples.Count > 0)
                {
                    RawMotionSample s = _samples.Dequeue();
                    consumer.OnMotionSample(in s);
                }
            }

            public bool TryConsumeOverflowGap(out GapRecord gap)
            {
                gap = default;
                return false;
            }
        }

        private sealed class MockSystemStateProvider : ISystemStateProvider
        {
            public bool HasBrightness;
            public double Brightness;
            public bool HasLocked;
            public bool IsLocked;
            public int BrightnessCalls;
            public int LockedCalls;

            public bool TryGetScreenBrightness(out double brightness01)
            {
                BrightnessCalls++;
                brightness01 = Brightness;
                return HasBrightness;
            }

            public bool TryGetScreenLocked(out bool isLocked)
            {
                LockedCalls++;
                isLocked = IsLocked;
                return HasLocked;
            }
        }

        // ====================================================================
        // 1. Packed Sample Round-Trip & Multi-Sensor Bulk Drain (Android + iOS)
        // ====================================================================

        [Test]
        public void PackedMotionRecord_AndCoordinatorBulkDrain_RoundTripExactFloatsUnitsAndProtobuf()
        {
            // Verify lossless float bit packing including negative, subnormal, zero, and large 64-bit timestamp
            long[] buf = new long[PackedMotionRecord.LongsPerSample * 2];
            float subnormal = float.Epsilon;
            PackedMotionRecord.Pack(buf, 0, Sensor.Accelerometer, 9007199254744993L, -9.80665f, subnormal, 123.456f);
            PackedMotionRecord.Pack(buf, 1, Sensor.Magnetometer, 9007199255740993L, 20.0f, -5.0f, -40.0f);

            PackedMotionRecord.Unpack(buf, 0, out Sensor s0, out long ts0, out float x0, out float y0, out float z0);
            Assert.AreEqual(Sensor.Accelerometer, s0);
            Assert.AreEqual(9007199254744993L, ts0);
            Assert.AreEqual(-9.80665f, x0);
            Assert.AreEqual(subnormal, y0);
            Assert.AreEqual(123.456f, z0);

            PackedMotionRecord.Unpack(buf, 1, out Sensor s1, out long ts1, out float x1, out float y1, out float z1);
            Assert.AreEqual(Sensor.Magnetometer, s1);
            Assert.AreEqual(9007199255740993L, ts1);
            Assert.AreEqual(20.0f, x1);
            Assert.AreEqual(-5.0f, y1);
            Assert.AreEqual(-40.0f, z1);

            // Android native drain -> m/s^2 for Accel/Gravity, rad/s for Gyro, uT for Mag
            MockNativeMotionBridge androidBridge = new MockNativeMotionBridge
            {
                SourceKind = CaptureSource.NativeAndroid,
                NativePlatform = Platform.Android
            };
            MockUnityMotionSource unityFallback = new MockUnityMotionSource();
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.HoldRunLength = 0u;

            CompositeMotionCaptureCoordinator coord = new CompositeMotionCaptureCoordinator(
                Platform.Android, androidBridge, unityFallback);
            coord.StartCapture(settings);

            androidBridge.EnqueuePackedSample(Sensor.Accelerometer, 9007199254744993L, 0.12f, -0.08f, 9.80665f);
            androidBridge.EnqueuePackedSample(Sensor.Gyroscope, 9007199254745993L, 0.01f, -0.02f, 0.03f);
            androidBridge.EnqueuePackedSample(Sensor.Gravity, 9007199254746993L, 0.0f, 0.0f, 9.80665f);
            androidBridge.EnqueuePackedSample(Sensor.Magnetometer, 9007199254747993L, 20.0f, -5.0f, -40.0f);

            MotionWindowAggregator agg = new MotionWindowAggregator();
            agg.Configure(settings);
            coord.DrainSamples(agg, 100000L);

            Batch batch = new Batch
            {
                Header = new Header { IdOrg = "o", IdDeployment = "d", IdSource = "s", IdSession = "ses", Sequence = 1 }
            };
            agg.FlushWindowTo(batch.SensorBlocks, batch.Gaps);

            Assert.AreEqual(4, batch.SensorBlocks.Count);
            Assert.AreEqual(ValueUnit.MetresPerSecondSquared, batch.SensorBlocks[0].Unit);
            Assert.AreEqual(CaptureSource.NativeAndroid, batch.SensorBlocks[0].Source);
            Assert.AreEqual(Clock.Sensor, batch.SensorBlocks[0].Clock);
            Assert.AreEqual(9007199254744993L, batch.SensorBlocks[0].FirstTimeUs);
            Assert.AreEqual(ValueUnit.RadiansPerSecond, batch.SensorBlocks[1].Unit);
            Assert.AreEqual(ValueUnit.MetresPerSecondSquared, batch.SensorBlocks[2].Unit);
            Assert.AreEqual(ValueUnit.Microtesla, batch.SensorBlocks[3].Unit);

            // Round-trip through BatchProtobufCodec
            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(batch, writer, out string encErr), encErr);
            byte[] bytes = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes, 0, bytes.Length, out Batch dec, out string decErr), decErr);
            Assert.AreEqual(4, dec.SensorBlocks.Count);
            Assert.AreEqual(9007199254744993L, dec.SensorBlocks[0].FirstTimeUs);

            // Verify iOS bridge unit convention (G for Accel/Gravity)
            IosNativeMotionBridge iosBridge = new IosNativeMotionBridge();
            Assert.AreEqual(ValueUnit.G, iosBridge.GetSensorUnit(Sensor.Accelerometer));
            Assert.AreEqual(ValueUnit.G, iosBridge.GetSensorUnit(Sensor.Gravity));
            Assert.AreEqual(ValueUnit.RadiansPerSecond, iosBridge.GetSensorUnit(Sensor.Gyroscope));
            Assert.AreEqual(ValueUnit.Microtesla, iosBridge.GetSensorUnit(Sensor.Magnetometer));
        }

        // ====================================================================
        // 2. Per-Sensor Partial Fallback (matching batch_schema_2.json)
        // ====================================================================

        [Test]
        public void Coordinator_SupportsPerSensorPartialFallback_GyroRefusedOthersNative()
        {
            MockNativeMotionBridge nativeBridge = new MockNativeMotionBridge();
            nativeBridge.SetStartResult(Sensor.Gyroscope, NativeSensorStartResult.MissingOrRefused);
            MockUnityMotionSource unityFallback = new MockUnityMotionSource();

            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.HoldRunLength = 0u;

            CompositeMotionCaptureCoordinator coord = new CompositeMotionCaptureCoordinator(
                Platform.Android, nativeBridge, unityFallback);
            coord.StartCapture(settings);

            Assert.IsTrue(coord.IsSensorRunningNatively(Sensor.Accelerometer));
            Assert.IsTrue(coord.IsSensorRunningOnUnity(Sensor.Gyroscope));
            Assert.AreEqual(FallbackReason.SensorRefused, coord.GetSensorFallbackReason(Sensor.Gyroscope));
            Assert.IsTrue(coord.IsSensorRunningNatively(Sensor.Gravity));
            Assert.IsTrue(coord.IsSensorRunningNatively(Sensor.Magnetometer));

            // Unity fallback should only request Gyroscope (Principle 5: Battery)
            Assert.AreEqual(0u, unityFallback.LastStartedSettings.AccelerometerHz);
            Assert.AreEqual(100u, unityFallback.LastStartedSettings.GyroscopeHz);

            nativeBridge.EnqueuePackedSample(Sensor.Accelerometer, 10000L, 0.1f, 0.2f, 9.8f);
            nativeBridge.EnqueuePackedSample(Sensor.Gravity, 10000L, 0f, 0f, 9.8f);
            nativeBridge.EnqueuePackedSample(Sensor.Magnetometer, 10000L, 20f, -5f, -40f);
            // Even if Unity produces an Accelerometer event too, selective filter must drop it and only pass Gyro!
            unityFallback.EnqueueSample(Sensor.Accelerometer, 50000L, 99f, 99f, 99f);
            unityFallback.EnqueueSample(Sensor.Gyroscope, 50000L, 0.5f, -0.5f, 0.1f);

            MotionWindowAggregator agg = new MotionWindowAggregator();
            agg.Configure(settings);
            coord.DrainSamples(agg, 50000L);

            List<SensorBlock> blocks = new List<SensorBlock>();
            agg.FlushWindowTo(blocks, null);

            Assert.AreEqual(4, blocks.Count);
            Assert.AreEqual(CaptureSource.NativeAndroid, blocks[0].Source);
            Assert.AreEqual(FallbackReason.Unspecified, blocks[0].FallbackReason);
            Assert.AreEqual(1u, blocks[0].SampleCount, "Unity Accelerometer sample must be filtered out while native Accelerometer is active");

            Assert.AreEqual(CaptureSource.UnityEvents, blocks[1].Source);
            Assert.AreEqual(FallbackReason.SensorRefused, blocks[1].FallbackReason);
            Assert.AreEqual(Clock.Unity, blocks[1].Clock);

            Assert.AreEqual(CaptureSource.NativeAndroid, blocks[2].Source);
            Assert.AreEqual(CaptureSource.NativeAndroid, blocks[3].Source);
        }

        // ====================================================================
        // 3. All 6 FallbackReason Paths + Missing Sensor + Zero-Limit Watchdogs
        // ====================================================================

        [Test]
        public void Coordinator_HandlesAllSixFallbackReasons_AndMissingGyroscopeAndZeroLimits()
        {
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.HoldRunLength = 0u;

            // (A) Platform.Editor -> Unity primary with FallbackReason.Unspecified
            MockUnityMotionSource editorUnity = new MockUnityMotionSource();
            CompositeMotionCaptureCoordinator editorCoord = new CompositeMotionCaptureCoordinator(
                Platform.Editor, new MockNativeMotionBridge(), editorUnity);
            editorCoord.StartCapture(settings);
            Assert.IsTrue(editorCoord.IsSensorRunningOnUnity(Sensor.Accelerometer));
            Assert.AreEqual(FallbackReason.Unspecified, editorCoord.GetSensorFallbackReason(Sensor.Accelerometer));

            // (B) MotionSource.Unity on Android -> FallbackReason.ForcedBySettings
            SessionSettings forcedUnitySettings = settings.Clone();
            forcedUnitySettings.MotionSource = MotionSource.Unity;
            MockUnityMotionSource forcedUnity = new MockUnityMotionSource();
            CompositeMotionCaptureCoordinator forcedCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, new MockNativeMotionBridge(), forcedUnity);
            forcedCoord.StartCapture(forcedUnitySettings);
            Assert.AreEqual(FallbackReason.ForcedBySettings, forcedCoord.GetSensorFallbackReason(Sensor.Accelerometer));

            // (C) !IsPluginAvailable on Android -> FallbackReason.PluginMissing
            MockNativeMotionBridge missingBridge = new MockNativeMotionBridge { IsPluginAvailable = false };
            DiagnosticRecorder missingDiags = new DiagnosticRecorder();
            CompositeMotionCaptureCoordinator missingCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, missingBridge, new MockUnityMotionSource(), missingDiags);
            missingCoord.StartCapture(settings);
            Assert.AreEqual(FallbackReason.PluginMissing, missingCoord.GetSensorFallbackReason(Sensor.Accelerometer));
            Assert.Greater(missingDiags.ActiveCount, 0);

            // (D) Missing Gyroscope on both native and Unity -> left completely Off (no block emitted!)
            MockNativeMotionBridge noGyroBridge = new MockNativeMotionBridge();
            noGyroBridge.SetHasSensor(Sensor.Gyroscope, false);
            MockUnityMotionSource noGyroUnity = new MockUnityMotionSource { HasGyroscope = false };
            CompositeMotionCaptureCoordinator noGyroCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, noGyroBridge, noGyroUnity);
            noGyroCoord.StartCapture(settings);
            Assert.IsFalse(noGyroCoord.HasGyroscope);
            Assert.IsFalse(noGyroCoord.IsSensorRunningNatively(Sensor.Gyroscope));
            Assert.IsFalse(noGyroCoord.IsSensorRunningOnUnity(Sensor.Gyroscope));

            // (E) Start limit watchdog (1000ms) -> NoFirstSample at 1001ms; disabled when 0
            MockNativeMotionBridge noFirstBridge = new MockNativeMotionBridge();
            MockUnityMotionSource noFirstUnity = new MockUnityMotionSource();
            CompositeMotionCaptureCoordinator noFirstCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, noFirstBridge, noFirstUnity);
            noFirstCoord.StartCapture(settings);
            MotionWindowAggregator agg = new MotionWindowAggregator();
            agg.Configure(settings);
            noFirstCoord.DrainSamples(agg, 0L);          // establishes start baseline at 0
            noFirstCoord.DrainSamples(agg, 999000L);     // 999ms -> still native
            Assert.IsTrue(noFirstCoord.IsSensorRunningNatively(Sensor.Accelerometer));
            noFirstCoord.DrainSamples(agg, 1001000L);    // 1001ms > 1000ms -> NoFirstSample
            Assert.IsTrue(noFirstCoord.IsSensorRunningOnUnity(Sensor.Accelerometer));
            Assert.AreEqual(FallbackReason.NoFirstSample, noFirstCoord.GetSensorFallbackReason(Sensor.Accelerometer));

            // Verify native_start_limit_ms = 0 disables start-limit fallback
            SessionSettings zeroStartLimit = settings.Clone();
            zeroStartLimit.NativeStartLimitMs = 0u;
            CompositeMotionCaptureCoordinator zeroStartCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, new MockNativeMotionBridge(), new MockUnityMotionSource());
            zeroStartCoord.StartCapture(zeroStartLimit);
            zeroStartCoord.DrainSamples(agg, 0L);
            zeroStartCoord.DrainSamples(agg, 60000000L); // 60 seconds with no sample -> stays native!
            Assert.IsTrue(zeroStartCoord.IsSensorRunningNatively(Sensor.Accelerometer));

            // (F) Stall limit watchdog (5000ms) -> mid-window split to Unity (SamplesStopped) + ad pause immunity
            MockNativeMotionBridge stallBridge = new MockNativeMotionBridge();
            MockUnityMotionSource stallUnity = new MockUnityMotionSource();
            CompositeMotionCaptureCoordinator stallCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, stallBridge, stallUnity);
            stallCoord.StartCapture(settings);

            MotionWindowAggregator stallAgg = new MotionWindowAggregator();
            stallAgg.Configure(settings);

            stallBridge.EnqueuePackedSample(Sensor.Accelerometer, 10000L, 0.1f, 0.2f, 9.8f);
            stallCoord.DrainSamples(stallAgg, 100000L);
            Assert.IsTrue(stallCoord.IsSensorRunningNatively(Sensor.Accelerometer));

            // 30-second ad pause -> OnApplicationPause(true) then OnApplicationPause(false) must NOT trigger false stall!
            stallCoord.OnApplicationPause(true, 200000L);
            Assert.IsTrue(stallBridge.LastPausedState);
            Assert.AreEqual(120u, stallBridge.LastPausedBufferS);
            stallCoord.OnApplicationPause(false, 30200000L);
            stallBridge.EnqueuePackedSample(Sensor.Accelerometer, 30010000L, 0.2f, 0.3f, 9.8f);
            stallCoord.DrainSamples(stallAgg, 30200000L);
            Assert.IsTrue(stallCoord.IsSensorRunningNatively(Sensor.Accelerometer), "30s ad pause must not trigger false stall fallback");

            // Now stall for 5001ms while unpaused -> switches mid-window to Unity with SamplesStopped!
            stallCoord.DrainSamples(stallAgg, 35201000L);
            Assert.IsTrue(stallCoord.IsSensorRunningOnUnity(Sensor.Accelerometer));
            Assert.AreEqual(FallbackReason.SamplesStopped, stallCoord.GetSensorFallbackReason(Sensor.Accelerometer));

            stallUnity.EnqueueSample(Sensor.Accelerometer, 35210000L, 0.01f, 0.02f, 1.0f);
            stallCoord.DrainSamples(stallAgg, 35210000L);

            List<SensorBlock> stallBlocks = new List<SensorBlock>();
            stallAgg.FlushWindowTo(stallBlocks, null);
            Assert.AreEqual(2, stallBlocks.Count, "Mid-window stall fallback must split into NativeAndroid + UnityEvents blocks");
            Assert.AreEqual(CaptureSource.NativeAndroid, stallBlocks[0].Source);
            Assert.AreEqual(CaptureSource.UnityEvents, stallBlocks[1].Source);
            Assert.AreEqual(FallbackReason.SamplesStopped, stallBlocks[1].FallbackReason);

            // Verify native_stall_limit_ms = 0 disables stall fallback
            SessionSettings zeroStallLimit = settings.Clone();
            zeroStallLimit.NativeStallLimitMs = 0u;
            MockNativeMotionBridge zeroStallBridge = new MockNativeMotionBridge();
            CompositeMotionCaptureCoordinator zeroStallCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, zeroStallBridge, new MockUnityMotionSource());
            zeroStallCoord.StartCapture(zeroStallLimit);
            zeroStallBridge.EnqueuePackedSample(Sensor.Accelerometer, 10000L, 0f, 0f, 9.8f);
            zeroStallCoord.DrainSamples(stallAgg, 100000L);
            zeroStallCoord.DrainSamples(stallAgg, 60100000L); // 60s silence -> stays native!
            Assert.IsTrue(zeroStallCoord.IsSensorRunningNatively(Sensor.Accelerometer));

            // (G) Native runtime error -> FallbackReason.NativeError + Diagnostic warning
            MockNativeMotionBridge errBridge = new MockNativeMotionBridge { PendingErrorCount = 2 };
            DiagnosticRecorder errDiags = new DiagnosticRecorder();
            CompositeMotionCaptureCoordinator errCoord = new CompositeMotionCaptureCoordinator(
                Platform.Android, errBridge, new MockUnityMotionSource(), errDiags);
            errCoord.StartCapture(settings);
            errCoord.DrainSamples(stallAgg, 100000L);
            Assert.IsTrue(errCoord.IsSensorRunningOnUnity(Sensor.Accelerometer));
            Assert.AreEqual(FallbackReason.NativeError, errCoord.GetSensorFallbackReason(Sensor.Accelerometer));
            Assert.Greater(errDiags.ActiveCount, 0);
        }

        // ====================================================================
        // 4. Overflow Gap Forwarding & ISensorClockProvider Window Persistence
        // ====================================================================

        [Test]
        public void Coordinator_ForwardsNativeOverflowGap_AndPreservesSensorClockAcrossMidWindowFallback()
        {
            MockNativeMotionBridge bridge = new MockNativeMotionBridge { SensorClockUs = 9007199254740993L };
            DiagnosticRecorder diags = new DiagnosticRecorder();
            CompositeMotionCaptureCoordinator coord = new CompositeMotionCaptureCoordinator(
                Platform.Android, bridge, new MockUnityMotionSource(), diags);

            SessionSettings settings = SessionSettings.CreateDefaults();
            coord.StartCapture(settings);

            // Sensor clock is available while native is running
            Assert.IsTrue(coord.TryGetSensorTimeUs(out long sensorUs));
            Assert.AreEqual(9007199254740993L, sensorUs);

            // Simulate native ring buffer overflow during a long pause
            bridge.SetOverflow(Sensor.Accelerometer, 1000000L, 125000000L);
            MotionWindowAggregator agg = new MotionWindowAggregator();
            agg.Configure(settings);
            coord.DrainSamples(agg, 100000L);

            Assert.IsTrue(coord.TryConsumeOverflowGap(out GapRecord gap));
            Assert.AreEqual(GapSignal.Accelerometer, gap.Signal);
            Assert.AreEqual(Clock.Sensor, gap.Clock);
            Assert.AreEqual(1000000L, gap.StartTimeUs);
            Assert.AreEqual(125000000L, gap.EndTimeUs);
            Assert.AreEqual(GapCause.BufferOverflowed, gap.Cause);
            Assert.IsFalse(coord.TryConsumeOverflowGap(out _));

            // Trigger mid-window fallback via native drain failure
            bridge.FailNextDrain = true;
            coord.DrainSamples(agg, 200000L);
            Assert.IsFalse(coord.IsSensorRunningNatively(Sensor.Accelerometer));

            // Sensor clock MUST remain available for the rest of the current window
            Assert.IsTrue(coord.TryGetSensorTimeUs(out long stillPresentUs));
            Assert.AreEqual(9007199254740993L, stillPresentUs);

            // After window closes and resets window sensor clock flag, next window omits sensor_us
            coord.ResetWindowSensorClockFlag();
            Assert.IsFalse(coord.TryGetSensorTimeUs(out _));
        }

        // ====================================================================
        // 5. SystemStateMonitor Tests
        // ====================================================================

        [Test]
        public void SystemStateMonitor_PollsAt250msCadence_OmitsUnavailableKeys_AndUpdatesStateTracker()
        {
            MockSystemStateProvider provider = new MockSystemStateProvider
            {
                HasBrightness = false,
                HasLocked = false
            };
            WindowCallOrder order = new WindowCallOrder();
            StateTracker stateTracker = new StateTracker(order);
            SystemStateMonitor monitor = new SystemStateMonitor(provider, stateTracker);

            // Initial poll when platform cannot tell -> keys omitted
            Assert.IsTrue(monitor.Poll(0L));
            Assert.AreEqual(0, stateTracker.CurrentKeyCount);
            Assert.AreEqual(1, provider.BrightnessCalls);
            Assert.AreEqual(1, provider.LockedCalls);

            // Poll at +100ms (< 250ms) -> skipped (no provider call)
            provider.HasBrightness = true;
            provider.Brightness = 0.65;
            provider.HasLocked = true;
            provider.IsLocked = false;
            Assert.IsFalse(monitor.Poll(100000L));
            Assert.AreEqual(1, provider.BrightnessCalls);
            Assert.AreEqual(0, stateTracker.CurrentKeyCount);

            // Poll at +250ms -> forwards brightness and lock into StateTracker
            Assert.IsTrue(monitor.Poll(250000L));
            Assert.AreEqual(2, provider.BrightnessCalls);
            Assert.AreEqual(2, stateTracker.CurrentKeyCount);
            Assert.AreEqual(Value.FromNumber(0.65), stateTracker.CurrentState[SdkStateKeys.ScreenBrightness]);
            Assert.AreEqual(Value.FromFlag(false), stateTracker.CurrentState[SdkStateKeys.ScreenLocked]);

            // Poll at +500ms with lock flipped to true -> updates lock while brightness < 1s throttle stays suppressed
            provider.Brightness = 0.90;
            provider.IsLocked = true;
            Assert.IsTrue(monitor.Poll(500000L));
            Assert.AreEqual(Value.FromNumber(0.65), stateTracker.CurrentState[SdkStateKeys.ScreenBrightness]);
            Assert.AreEqual(Value.FromFlag(true), stateTracker.CurrentState[SdkStateKeys.ScreenLocked]);
        }

        // ====================================================================
        // 6. Regression Test (Reviewer Feedback Attempt 1): Stale Pause State Reset
        // ====================================================================

        [Test]
        public void Coordinator_StartCapture_ClearsStaleNativePausedStateFromPriorSession()
        {
            MockNativeMotionBridge bridge = new MockNativeMotionBridge();
            MockUnityMotionSource unityFallback = new MockUnityMotionSource();
            CompositeMotionCaptureCoordinator coord = new CompositeMotionCaptureCoordinator(
                Platform.Android, bridge, unityFallback);

            SessionSettings zeroBufSettings = SessionSettings.CreateDefaults();
            zeroBufSettings.NativeBufferS = 0u;
            coord.StartCapture(zeroBufSettings);
            coord.OnApplicationPause(true, 100000L);
            Assert.IsTrue(bridge.LastPausedState);
            Assert.AreEqual(0u, bridge.LastPausedBufferS);

            // Simulate session end & restart with new settings (e.g. after long background away)
            coord.StopCapture();
            SessionSettings newSessionSettings = SessionSettings.CreateDefaults();
            newSessionSettings.NativeBufferS = 120u;
            coord.StartCapture(newSessionSettings);

            Assert.IsFalse(bridge.LastPausedState, "StartCapture must reset native paused flag to false");
            Assert.AreEqual(120u, bridge.LastPausedBufferS, "StartCapture must propagate active NativeBufferS");
        }
    }
}
