using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Capture;
using Asparagus.Sdk.Internal.Encoding;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone3StreamCaptureTests
    {
        private sealed class FakeScreenProvider : IScreenInfoProvider
        {
            public ScreenRecord Current;

            public ScreenRecord ReadCurrentScreen()
            {
                return Current;
            }
        }

        // ====================================================================
        // 1. Delta Encoding & int32 Extreme Overflow Splitting
        // ====================================================================

        [Test]
        public void SensorBlockBuilder_DeltaEncodesAndSplitsOnInt32Overflow_AndRoundTripsProtobuf()
        {
            SensorBlockBuilder builder = new SensorBlockBuilder(Sensor.Accelerometer);
            builder.Configure(1e-6, 100f, holdTolerance: 0u, holdRunLength: 0u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);

            List<GapRecord> gaps = new List<GapRecord>();

            // Normal deltas + extreme jump exceeding int.MaxValue (-1_500_000_000 -> +1_500_000_000 = +3_000_000_000)
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 10000L, 100, -50, 980, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 20000L, 150, -100, 980, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 30000L, -1500000000, 0, 0, gaps);
            // This next sample has dx = +3_000_000_000 > int.MaxValue -> must split cleanly into a second SensorBlock!
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 40000L, 1500000000, 25, -25, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 50000L, 1500000010, 30, -20, gaps);

            List<SensorBlock> blocks = new List<SensorBlock>();
            builder.FlushTo(blocks);

            Assert.AreEqual(0, gaps.Count);
            Assert.AreEqual(2, blocks.Count, "Extreme int32 delta must split into two blocks");

            // Block 0: 3 samples
            SensorBlock b0 = blocks[0];
            Assert.AreEqual(3u, b0.SampleCount);
            Assert.AreEqual(10000L, b0.FirstTimeUs);
            CollectionAssert.AreEqual(new[] { 10000, 10000 }, b0.TimeStepsUs);
            CollectionAssert.AreEqual(new[] { 100, 50, -1500000150 }, b0.X);
            CollectionAssert.AreEqual(new[] { -50, -50, 100 }, b0.Y);
            CollectionAssert.AreEqual(new[] { 980, 0, -980 }, b0.Z);

            // Block 1: 2 samples starting with absolute +1_500_000_000
            SensorBlock b1 = blocks[1];
            Assert.AreEqual(2u, b1.SampleCount);
            Assert.AreEqual(40000L, b1.FirstTimeUs);
            CollectionAssert.AreEqual(new[] { 10000 }, b1.TimeStepsUs);
            CollectionAssert.AreEqual(new[] { 1500000000, 10 }, b1.X);
            CollectionAssert.AreEqual(new[] { 25, 5 }, b1.Y);
            CollectionAssert.AreEqual(new[] { -25, 5 }, b1.Z);

            // Verify Protobuf round-trip
            Batch batch = new Batch
            {
                Header = new Header { IdOrg = "o", IdDeployment = "d", IdSource = "s", IdSession = "ses", Sequence = 1 }
            };
            batch.SensorBlocks.AddRange(blocks);

            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(batch, writer, out string encErr), encErr);
            byte[] encoded = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(encoded, 0, encoded.Length, out Batch dec, out string decErr), decErr);
            Assert.AreEqual(2, dec.SensorBlocks.Count);
            CollectionAssert.AreEqual(b0.X, dec.SensorBlocks[0].X);
            CollectionAssert.AreEqual(b1.X, dec.SensorBlocks[1].X);
        }

        // ====================================================================
        // 2. Zero-Tolerance & Non-Zero-Tolerance Hold Folding + Delta Across Holds
        // ====================================================================

        [Test]
        public void SensorBlockBuilder_HoldFoldingZeroTolerance_AndDeltaAcrossHold()
        {
            List<GapRecord> gaps = new List<GapRecord>();

            // Part A: 3 identical samples with hold_run_length = 4 stay as full samples
            SensorBlockBuilder bShort = new SensorBlockBuilder(Sensor.Gyroscope);
            bShort.Configure(1e-6, 100f, holdTolerance: 0u, holdRunLength: 4u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);
            bShort.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.RadiansPerSecond, 1000L, 42, 0, 0, gaps);
            bShort.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.RadiansPerSecond, 2000L, 42, 0, 0, gaps);
            bShort.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.RadiansPerSecond, 3000L, 42, 0, 0, gaps);

            List<SensorBlock> shortBlocks = new List<SensorBlock>();
            bShort.FlushTo(shortBlocks);
            Assert.AreEqual(1, shortBlocks.Count);
            Assert.AreEqual(0, shortBlocks[0].Holds.Count);
            Assert.AreEqual(3, shortBlocks[0].X.Count);
            Assert.AreEqual(3u, shortBlocks[0].SampleCount);

            // Part B: 10 identical samples fold completely into 1 Hold (X.Count == 0, FirstTimeUs == 0, Stats empty)
            SensorBlockBuilder bAllHold = new SensorBlockBuilder(Sensor.Gravity);
            bAllHold.Configure(1e-6, 100f, holdTolerance: 0u, holdRunLength: 4u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);
            for (int i = 0; i < 10; i++)
            {
                bAllHold.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 1000L + i * 1000L, 5, -5, 980, gaps);
            }
            List<SensorBlock> allHoldBlocks = new List<SensorBlock>();
            bAllHold.FlushTo(allHoldBlocks);
            Assert.AreEqual(1, allHoldBlocks.Count);
            Assert.AreEqual(0, allHoldBlocks[0].X.Count);
            Assert.AreEqual(0, allHoldBlocks[0].TimeStepsUs.Count);
            Assert.AreEqual(0L, allHoldBlocks[0].FirstTimeUs);
            Assert.AreEqual(10u, allHoldBlocks[0].SampleCount);
            Assert.AreEqual(1, allHoldBlocks[0].Holds.Count);
            Assert.AreEqual(10u, allHoldBlocks[0].Holds[0].SampleCount);
            Assert.AreEqual(1000L, allHoldBlocks[0].Holds[0].StartTimeUs);
            Assert.AreEqual(10000L, allHoldBlocks[0].Holds[0].EndTimeUs);
            Assert.AreEqual(0, allHoldBlocks[0].Holds[0].Stats.Count, "Stats must be empty when hold_tolerance == 0");

            // Part C: Delta across a Hold — A(t=1000, x=100) -> 10-sample hold at x=120 (t=2000..11000) -> B(t=12000, x=250)
            SensorBlockBuilder bAcross = new SensorBlockBuilder(Sensor.Accelerometer);
            bAcross.Configure(1e-6, 100f, holdTolerance: 0u, holdRunLength: 4u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);
            bAcross.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 1000L, 100, 10, 20, gaps);
            for (int i = 0; i < 10; i++)
            {
                bAcross.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 2000L + i * 1000L, 120, 10, 20, gaps);
            }
            bAcross.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 12000L, 250, 30, 50, gaps);

            List<SensorBlock> acrossBlocks = new List<SensorBlock>();
            bAcross.FlushTo(acrossBlocks);
            Assert.AreEqual(1, acrossBlocks.Count);
            SensorBlock blk = acrossBlocks[0];
            Assert.AreEqual(12u, blk.SampleCount);
            Assert.AreEqual(1000L, blk.FirstTimeUs);
            CollectionAssert.AreEqual(new[] { 11000 }, blk.TimeStepsUs, "Time step must span from full sample A to full sample B");
            CollectionAssert.AreEqual(new[] { 100, 150 }, blk.X, "Delta must be B - A (250 - 100 = 150), NOT B - Hold");
            CollectionAssert.AreEqual(new[] { 10, 20 }, blk.Y);
            CollectionAssert.AreEqual(new[] { 20, 30 }, blk.Z);
            Assert.AreEqual(1, blk.Holds.Count);
            Assert.AreEqual(2000L, blk.Holds[0].StartTimeUs);
            Assert.AreEqual(11000L, blk.Holds[0].EndTimeUs);
            Assert.AreEqual(10u, blk.Holds[0].SampleCount);
            Assert.AreEqual(120, blk.Holds[0].X);
        }

        [Test]
        public void SensorBlockBuilder_HoldFoldingNonZeroTolerance_PopulatesExactStatsAndShiftsCandidates()
        {
            SensorBlockBuilder builder = new SensorBlockBuilder(Sensor.Accelerometer);
            builder.Configure(1e-6, 100f, holdTolerance: 5u, holdRunLength: 3u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);
            List<GapRecord> gaps = new List<GapRecord>();

            // Moving sample 50 settles into noisy stillness [0, 3, -4, 2] within +-5 of anchor 0
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 1000L, 50, 10, 100, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 2000L, 0, 10, 100, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 3000L, 3, 12, 98, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 4000L, -4, 8, 105, gaps);
            builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 5000L, 2, 10, 99, gaps);

            List<SensorBlock> blocks = new List<SensorBlock>();
            builder.FlushTo(blocks);

            Assert.AreEqual(1, blocks.Count);
            SensorBlock blk = blocks[0];
            Assert.AreEqual(5u, blk.SampleCount);
            CollectionAssert.AreEqual(new[] { 50 }, blk.X);
            Assert.AreEqual(1, blk.Holds.Count);

            Hold h = blk.Holds[0];
            Assert.AreEqual(4u, h.SampleCount);
            Assert.AreEqual(2000L, h.StartTimeUs);
            Assert.AreEqual(5000L, h.EndTimeUs);
            Assert.AreEqual(0, h.X);
            Assert.AreEqual(3, h.Stats.Count);

            // X stats for [0, 3, -4, 2]: sum = 1, sumSq = 0 + 9 + 16 + 4 = 29, min = -4, max = 3
            Assert.AreEqual(new HoldStats(1L, 29d, -4, 3), h.Stats[0]);
            // Y stats for [10, 12, 8, 10]: sum = 40, sumSq = 100 + 144 + 64 + 100 = 408, min = 8, max = 12
            Assert.AreEqual(new HoldStats(40L, 408d, 8, 12), h.Stats[1]);
            // Z stats for [100, 98, 105, 99]: sum = 402, sumSq = 10000 + 9604 + 11025 + 9801 = 40430, min = 98, max = 105
            Assert.AreEqual(new HoldStats(402L, 40430d, 98, 105), h.Stats[2]);
        }

        // ====================================================================
        // 3. Gap Threshold Math & Hold Splitting Across Gaps
        // ====================================================================

        [Test]
        public void GapThresholdMath_AndActiveHoldSplitAtGap()
        {
            // Longer than both ms and periods
            Assert.AreEqual(250000L, SampleQuantizer.ComputeGapThresholdUs(250u, 5u, 100f));  // max(250ms, 50ms) = 250ms
            Assert.AreEqual(500000L, SampleQuantizer.ComputeGapThresholdUs(250u, 5u, 10f));   // max(250ms, 500ms) = 500ms
            Assert.AreEqual(250000L, SampleQuantizer.ComputeGapThresholdUs(250u, 0u, 100f));  // periods off -> 250ms
            Assert.AreEqual(50000L, SampleQuantizer.ComputeGapThresholdUs(0u, 5u, 100f));     // ms off -> 50ms
            Assert.AreEqual(0L, SampleQuantizer.ComputeGapThresholdUs(0u, 0u, 100f));         // both off -> disabled
            Assert.AreEqual(250000L, SampleQuantizer.ComputeGapThresholdUs(250u, 5u, 0f));    // hz == 0 -> ms decides

            SensorBlockBuilder builder = new SensorBlockBuilder(Sensor.Accelerometer);
            builder.Configure(1e-6, 100f, holdTolerance: 0u, holdRunLength: 3u, gapThresholdMs: 250u, gapThresholdPeriods: 5u);
            List<GapRecord> gaps = new List<GapRecord>();

            // Active hold before gap (t = 10000, 20000, 30000, 40000)
            for (int i = 0; i < 4; i++)
            {
                builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 10000L + i * 10000L, 7, 8, 9, gaps);
            }

            // 500ms silence (> 250ms threshold), followed by same value (7, 8, 9) 4 more times!
            for (int i = 0; i < 4; i++)
            {
                builder.AddQuantizedSample(CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, 540000L + i * 10000L, 7, 8, 9, gaps);
            }

            List<SensorBlock> blocks = new List<SensorBlock>();
            builder.FlushTo(blocks);

            // Gap does NOT start a new block, but splits the Hold into two separate Holds and emits a GapRecord!
            Assert.AreEqual(1, blocks.Count);
            Assert.AreEqual(8u, blocks[0].SampleCount);
            Assert.AreEqual(500000u, blocks[0].LongestGapUs);
            Assert.AreEqual(2, blocks[0].Holds.Count, "Hold must never span across a gap");
            Assert.AreEqual(10000L, blocks[0].Holds[0].StartTimeUs);
            Assert.AreEqual(40000L, blocks[0].Holds[0].EndTimeUs);
            Assert.AreEqual(4u, blocks[0].Holds[0].SampleCount);
            Assert.AreEqual(540000L, blocks[0].Holds[1].StartTimeUs);
            Assert.AreEqual(570000L, blocks[0].Holds[1].EndTimeUs);
            Assert.AreEqual(4u, blocks[0].Holds[1].SampleCount);

            Assert.AreEqual(1, gaps.Count);
            Assert.AreEqual(GapSignal.Accelerometer, gaps[0].Signal);
            Assert.AreEqual(Clock.Sensor, gaps[0].Clock);
            Assert.AreEqual(40000L, gaps[0].StartTimeUs);
            Assert.AreEqual(540000L, gaps[0].EndTimeUs);
            Assert.AreEqual(GapCause.SensorSilent, gaps[0].Cause);
        }

        // ====================================================================
        // 4. W0-10 Magnetometer Exclusion & Source-Switch Block Splitting
        // ====================================================================

        [Test]
        public void MotionWindowAggregator_ExcludesMagnetometerFromBoundedCount_AndSplitsOnSourceSwitch()
        {
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.HoldRunLength = 0u; // send all full for clear counting
            MotionWindowAggregator agg = new MotionWindowAggregator();
            agg.Configure(settings);

            for (int i = 0; i < 100; i++)
            {
                agg.OnMotionSample(new RawMotionSample(Sensor.Accelerometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, i * 10000L, 0.1f, 0.2f, 9.8f));
            }
            for (int i = 0; i < 50; i++)
            {
                agg.OnMotionSample(new RawMotionSample(Sensor.Gyroscope, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.RadiansPerSecond, i * 10000L, 0.01f, 0.02f, 0.03f));
            }
            // Mid-window silent fallback to UnityEvents for Gyroscope
            for (int i = 50; i < 100; i++)
            {
                agg.OnMotionSample(new RawMotionSample(Sensor.Gyroscope, CaptureSource.UnityEvents, FallbackReason.SamplesStopped, Clock.Unity, ValueUnit.RadiansPerSecond, i * 10000L, 0.01f, 0.02f, 0.03f));
            }
            for (int i = 0; i < 10; i++)
            {
                agg.OnMotionSample(new RawMotionSample(Sensor.Gravity, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.MetresPerSecondSquared, i * 100000L, 0f, 0f, 9.81f));
            }
            for (int i = 0; i < 50; i++)
            {
                agg.OnMotionSample(new RawMotionSample(Sensor.Magnetometer, CaptureSource.NativeAndroid, FallbackReason.Unspecified, Clock.Sensor, ValueUnit.Microtesla, i * 100000L, 20f, -5f, -40f));
            }

            Assert.AreEqual(210, agg.BoundedMotionSampleCount, "W0-10: Magnetometer samples must NOT count toward bounded motion samples");
            Assert.AreEqual(260, agg.TotalMotionSampleCount);

            List<SensorBlock> blocks = new List<SensorBlock>();
            List<GapRecord> gaps = new List<GapRecord>();
            agg.FlushWindowTo(blocks, gaps);

            // 1 Acc + 2 Gyro (source switch!) + 1 Gravity + 1 Magnetometer = 5 blocks
            Assert.AreEqual(5, blocks.Count);
            Assert.AreEqual(CaptureSource.NativeAndroid, blocks[1].Source);
            Assert.AreEqual(FallbackReason.Unspecified, blocks[1].FallbackReason);
            Assert.AreEqual(50u, blocks[1].SampleCount);
            Assert.AreEqual(CaptureSource.UnityEvents, blocks[2].Source);
            Assert.AreEqual(FallbackReason.SamplesStopped, blocks[2].FallbackReason);
            Assert.AreEqual(50u, blocks[2].SampleCount);
            Assert.AreEqual(Sensor.Magnetometer, blocks[4].Sensor);
            Assert.AreEqual(50u, blocks[4].SampleCount);

            agg.RecycleBlocks(blocks);
            Assert.AreEqual(0, blocks.Count);
        }

        // ====================================================================
        // 5. Touch Rate Thinning, Pressure Presence & Cross-Window ContactBefore
        // ====================================================================

        [Test]
        public void TouchWindowAggregator_ThinsRate_EnforcesPressureInvariants_AndCarriesContactBefore()
        {
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.TouchHz = 60u;
            settings.TouchHoldTolerance = 5u;
            settings.HoldRunLength = 3u;

            // Part A: 120 Hz touch stream (8333 us spacing) with touch_hz = 60 (16667 us min step), no pressure
            TouchWindowAggregator aggNoPres = new TouchWindowAggregator();
            aggNoPres.Configure(settings, TouchMode.Events);

            aggNoPres.OnTouchSample(new RawTouchSample(1u, TouchPhaseKind.Began, 0L, 100f, 200f, 10f, 10f, 0f, false));
            for (int i = 1; i <= 10; i++)
            {
                aggNoPres.OnTouchSample(new RawTouchSample(1u, TouchPhaseKind.Moved, i * 8333L, 100f + i * 5f, 200f, 10f, 10f, 0f, false));
            }
            // Ended arrives only 2ms after last kept move -> must ALWAYS be kept!
            aggNoPres.OnTouchSample(new RawTouchSample(1u, TouchPhaseKind.Ended, 85000L, 160f, 200f, 10f, 10f, 0f, false));

            Touch touchNoPres = new Touch();
            Assert.IsTrue(aggNoPres.FlushWindowTo(touchNoPres));
            Assert.AreEqual(1, touchNoPres.Contacts.Count);
            TouchContact cNoPres = touchNoPres.Contacts[0];
            Assert.AreEqual(12u, cNoPres.EventsSeen, "EventsSeen counts all raw events before thinning");
            Assert.AreEqual(TouchEnd.Lifted, cNoPres.End);
            Assert.AreEqual(0, cNoPres.Pressure.Count, "Pressure must be empty when HasPressure == false");
            Assert.Less(cNoPres.X.Count, 12, "120 Hz moves must be thinned to ~60 Hz");
            Assert.AreEqual(cNoPres.X.Count - 1, cNoPres.TimeStepsUs.Count);

            // Verify 4 HoldStats (no pressure) vs 5 HoldStats (with pressure) when tolerance > 0
            TouchWindowAggregator aggHold = new TouchWindowAggregator();
            aggHold.Configure(settings, TouchMode.Events);
            for (int i = 0; i < 4; i++)
            {
                TouchPhaseKind p = i == 0 ? TouchPhaseKind.Began : (i == 3 ? TouchPhaseKind.Ended : TouchPhaseKind.Moved);
                aggHold.OnTouchSample(new RawTouchSample(10u, p, i * 20000L, 500f, 600f, 12f, 11f, 0f, false));
                aggHold.OnTouchSample(new RawTouchSample(20u, p, i * 20000L, 500f, 600f, 12f, 11f, i == 3 ? 0f : 0.65f, i != 3));
            }
            Touch touchHold = new Touch();
            aggHold.FlushWindowTo(touchHold);
            Assert.AreEqual(2, touchHold.Contacts.Count);
            Assert.AreEqual(1, touchHold.Contacts[0].Holds.Count);
            Assert.AreEqual(4, touchHold.Contacts[0].Holds[0].Stats.Count, "4 HoldStats when pressure is not reported");
            // Contact 20 had pressure drop to 0 on Ended (outside tol 5), so first 3 samples fold into a Hold with 5 HoldStats and Ended is sent full with pressure!
            Assert.AreEqual(1, touchHold.Contacts[1].Holds.Count);
            Assert.AreEqual(5, touchHold.Contacts[1].Holds[0].Stats.Count, "5 HoldStats when pressure IS reported");
            Assert.AreEqual(touchHold.Contacts[1].X.Count, touchHold.Contacts[1].Pressure.Count);

            // Part B: Cross-window continuation across 3 windows (ContactBefore bounding box accumulation)
            settings.TouchHz = 0u;
            settings.HoldRunLength = 0u;
            TouchWindowAggregator aggCross = new TouchWindowAggregator();
            aggCross.Configure(settings, TouchMode.Events);

            // Window 1: finger 7 comes down at (530, 1210), moves to (540, 1200) -> stays down
            aggCross.OnTouchSample(new RawTouchSample(7u, TouchPhaseKind.Began, 2961000000L, 530f, 1210f, 12.5f, 11f, 0.6f, true));
            aggCross.OnTouchSample(new RawTouchSample(7u, TouchPhaseKind.Moved, 2961050000L, 540f, 1200f, 12.5f, 11f, 0.6f, true));

            Touch w1 = new Touch();
            Assert.IsTrue(aggCross.FlushWindowTo(w1));
            Assert.AreEqual(1, w1.Contacts.Count);
            Assert.IsFalse(w1.Contacts[0].Continued);
            Assert.IsNull(w1.Contacts[0].Before);
            Assert.AreEqual(TouchEnd.StillDown, w1.Contacts[0].End);

            // Window 2: moves to (560, 1180) -> still down
            aggCross.OnTouchSample(new RawTouchSample(7u, TouchPhaseKind.Moved, 2971000000L, 560f, 1180f, 12.5f, 11f, 0.62f, true));

            Touch w2 = new Touch();
            Assert.IsTrue(aggCross.FlushWindowTo(w2));
            Assert.AreEqual(1, w2.Contacts.Count);
            Assert.IsTrue(w2.Contacts[0].Continued);
            Assert.AreEqual(TouchEnd.StillDown, w2.Contacts[0].End);
            Assert.IsNotNull(w2.Contacts[0].Before);
            Assert.AreEqual(2961000000L, w2.Contacts[0].Before.DownTimeUs);
            Assert.AreEqual(5300, w2.Contacts[0].Before.MinX);
            Assert.AreEqual(5400, w2.Contacts[0].Before.MaxX);
            Assert.AreEqual(12000, w2.Contacts[0].Before.MinY);
            Assert.AreEqual(12100, w2.Contacts[0].Before.MaxY);

            // Window 3: lifts at (520, 1190) -> Before must reflect union of Window 1 + Window 2!
            aggCross.OnTouchSample(new RawTouchSample(7u, TouchPhaseKind.Ended, 2981000000L, 520f, 1190f, 10f, 9f, 0f, false));

            Touch w3 = new Touch();
            Assert.IsTrue(aggCross.FlushWindowTo(w3));
            Assert.AreEqual(1, w3.Contacts.Count);
            Assert.IsTrue(w3.Contacts[0].Continued);
            Assert.AreEqual(TouchEnd.Lifted, w3.Contacts[0].End);
            Assert.AreEqual(1, w3.Contacts[0].Pressure.Count, "Continuing pressure-reporting contact preserves Pressure invariant on lift");
            Assert.IsNotNull(w3.Contacts[0].Before);
            Assert.AreEqual(2961000000L, w3.Contacts[0].Before.DownTimeUs);
            Assert.AreEqual(5300, w3.Contacts[0].Before.MinX);
            Assert.AreEqual(5600, w3.Contacts[0].Before.MaxX);
            Assert.AreEqual(11800, w3.Contacts[0].Before.MinY);
            Assert.AreEqual(12100, w3.Contacts[0].Before.MaxY);

            // Round-trip Window 3 Touch through BatchProtobufCodec
            Batch batch = new Batch
            {
                Header = new Header { IdOrg = "o", IdDeployment = "d", IdSource = "s", IdSession = "ses", Sequence = 3 },
                Touch = w3
            };
            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(batch, writer, out string encErr), encErr);
            byte[] bytes = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes, 0, bytes.Length, out Batch dec, out string decErr), decErr);
            Assert.AreEqual(2961000000L, dec.Touch.Contacts[0].Before.DownTimeUs);
            Assert.AreEqual(5600, dec.Touch.Contacts[0].Before.MaxX);
            Assert.AreEqual(11800, dec.Touch.Contacts[0].Before.MinY);
        }

        // ====================================================================
        // 6. ScreenTracker Tests
        // ====================================================================

        [Test]
        public void ScreenTracker_RecordsInWindowChanges_AndPromotesOnFlush()
        {
            FakeScreenProvider provider = new FakeScreenProvider
            {
                Current = new ScreenRecord
                {
                    WidthPx = 1080u,
                    HeightPx = 2400u,
                    DensityDpi = 420f,
                    Orientation = Orientation.Portrait,
                    Origin = CoordinateOrigin.BottomLeft
                }
            };

            ScreenTracker tracker = new ScreenTracker(provider);
            Assert.AreEqual(0L, tracker.WindowOpenScreen.TimeUs);
            Assert.AreEqual(Orientation.Portrait, tracker.WindowOpenScreen.Orientation);

            // Unchanged poll emits nothing
            Assert.IsFalse(tracker.PollForChanges(500000L));
            Assert.AreEqual(0, tracker.ScreenChangeCount);

            // Rotate to LandscapeLeft
            provider.Current = new ScreenRecord
            {
                WidthPx = 2400u,
                HeightPx = 1080u,
                DensityDpi = 420f,
                Orientation = Orientation.LandscapeLeft,
                Origin = CoordinateOrigin.BottomLeft
            };
            Assert.IsTrue(tracker.PollForChanges(2967250000L));
            Assert.AreEqual(1, tracker.ScreenChangeCount);

            // WindowOpenScreen stays Portrait until flush
            Assert.AreEqual(Orientation.Portrait, tracker.WindowOpenScreen.Orientation);

            List<ScreenRecord> flushed = new List<ScreenRecord>();
            tracker.FlushWindowChangesTo(flushed);
            Assert.AreEqual(1, flushed.Count);
            Assert.AreEqual(2967250000L, flushed[0].TimeUs);
            Assert.AreEqual(Orientation.LandscapeLeft, flushed[0].Orientation);
            Assert.AreEqual(2400u, flushed[0].WidthPx);

            // Next window open screen promoted with TimeUs == 0
            Assert.AreEqual(0, tracker.ScreenChangeCount);
            Assert.AreEqual(0L, tracker.WindowOpenScreen.TimeUs);
            Assert.AreEqual(Orientation.LandscapeLeft, tracker.WindowOpenScreen.Orientation);
            Assert.AreEqual(2400u, tracker.WindowOpenScreen.WidthPx);
        }

        // ====================================================================
        // 7. Regression Tests (Reviewer Feedback Attempt 1)
        // ====================================================================

        [Test]
        public void TouchWindowAggregator_NewBeganAfterMissedLiftAcrossWindows_ClearsContinuedAndBefore()
        {
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.TouchHz = 0u;
            settings.HoldRunLength = 0u;

            TouchWindowAggregator agg = new TouchWindowAggregator();
            agg.Configure(settings, TouchMode.Events);

            // Window 1: finger 5 comes down and stays down (lift will be missed while paused/suppressed)
            agg.OnTouchSample(new RawTouchSample(5u, TouchPhaseKind.Began, 1000000L, 200f, 300f, 10f, 10f, 0.5f, true));
            Touch w1 = new Touch();
            Assert.IsTrue(agg.FlushWindowTo(w1));
            Assert.AreEqual(1, w1.Contacts.Count);
            Assert.AreEqual(TouchEnd.StillDown, w1.Contacts[0].End);
            Assert.AreEqual(1, agg.ActiveTouchCount);

            // Window 2: lift was missed; a brand-new Began arrives for finger 5 at a totally new position!
            agg.OnTouchSample(new RawTouchSample(5u, TouchPhaseKind.Began, 20000000L, 900f, 100f, 10f, 10f, 0f, false));
            agg.OnTouchSample(new RawTouchSample(5u, TouchPhaseKind.Ended, 20050000L, 910f, 110f, 10f, 10f, 0f, false));

            Touch w2 = new Touch();
            Assert.IsTrue(agg.FlushWindowTo(w2));
            Assert.AreEqual(1, w2.Contacts.Count);
            Assert.IsFalse(w2.Contacts[0].Continued, "Brand-new Began after missed lift must NOT carry stale Continued=true");
            Assert.IsNull(w2.Contacts[0].Before, "Brand-new Began after missed lift must NOT carry stale ContactBefore");
            Assert.AreEqual(TouchEnd.Lifted, w2.Contacts[0].End);
            Assert.AreEqual(0, w2.Contacts[0].Pressure.Count, "Fresh contact with HasPressure=false must not inherit stale pressure flag");
            Assert.AreEqual(0, agg.ActiveTouchCount);
        }

        [Test]
        public void TouchWindowAggregator_CapsActiveTouchesAtMaxActiveTouches_AndCancelActiveTouchesClears()
        {
            SessionSettings settings = SessionSettings.CreateDefaults();
            settings.TouchHz = 0u;
            settings.HoldRunLength = 0u;

            TouchWindowAggregator agg = new TouchWindowAggregator();
            agg.Configure(settings, TouchMode.Events);

            // Feed 40 distinct unlifted finger IDs (simulating monotonically increasing touchIds with missed lifts)
            for (uint id = 1u; id <= 40u; id++)
            {
                agg.OnTouchSample(new RawTouchSample(id, TouchPhaseKind.Began, id * 10000L, 100f + id, 200f, 10f, 10f, 0f, false));
                Assert.LessOrEqual(agg.ActiveTouchCount, TouchWindowAggregator.MaxActiveTouches);
            }

            Assert.AreEqual(TouchWindowAggregator.MaxActiveTouches, agg.ActiveTouchCount);
            Assert.AreEqual(40, agg.ContactCount, "Evicted in-window contacts are flushed into completed contacts, not lost");

            // CancelActiveTouches flushes active contacts to completed and resets ActiveTouchCount to 0
            agg.CancelActiveTouches();
            Assert.AreEqual(0, agg.ActiveTouchCount);
            Assert.AreEqual(40, agg.ContactCount);

            Touch flushed = new Touch();
            Assert.IsTrue(agg.FlushWindowTo(flushed));
            Assert.AreEqual(40, flushed.Contacts.Count);
            Assert.AreEqual(0, agg.ActiveTouchCount);
        }
    }
}
