using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Encoding;
using Asparagus.Sdk.Internal.Time;
using Asparagus.Sdk.Internal.Tracking;
using NUnit.Framework;

namespace Asparagus.Sdk.Tests
{
    [TestFixture]
    public sealed class Milestone2ClockStateAndEventsTests
    {
        private sealed class FakeClockSource : IClockSource
        {
            public long SteadyTimeUs { get; set; }
            public long UnityTimeUs { get; set; }
            public long WallUtcMs { get; set; }
            public int UtcOffsetMinutes { get; set; }
            public bool HasSensorClock { get; set; }
            public long SensorTimeUs { get; set; }

            public bool TryGetSensorTimeUs(out long sensorUs)
            {
                if (HasSensorClock)
                {
                    sensorUs = SensorTimeUs;
                    return true;
                }

                sensorUs = 0L;
                return false;
            }
        }

        // ====================================================================
        // 1. SessionClock Tests
        // ====================================================================

        [Test]
        public void SessionClock_AnchorsOnServerTime_FloorsWindowStart_AndIgnoresWallClockDrift()
        {
            FakeClockSource source = new FakeClockSource
            {
                SteadyTimeUs = 5000000L,
                UnityTimeUs = 23500000L,
                WallUtcMs = 1794734100662L, // 3 minutes fast compared to server
                UtcOffsetMinutes = 60,
                HasSensorClock = false
            };

            SessionClock clock = new SessionClock(source);
            clock.StartSession(1794733920250L, 84u);

            // Fractional server_utc_ms floors CurrentWindowStartUs to exact whole second
            Assert.AreEqual(1794733920000000L, clock.CurrentWindowStartUs);
            Assert.AreEqual(1794733920250000L, clock.CurrentSessionUs);

            ServerAnchor anchor = new ServerAnchor();
            clock.CopyServerAnchorTo(anchor);
            Assert.AreEqual(1794733920250L, anchor.ServerUtcMs);
            Assert.AreEqual(84u, anchor.RoundTripMs);
            Assert.IsTrue(anchor.HasAtReceipt);
            Assert.AreEqual(1794733920250000L, anchor.AtReceipt.SessionUs);
            Assert.AreEqual(23500000L, anchor.AtReceipt.UnityUs);
            Assert.AreEqual(1794734100662L, anchor.AtReceipt.WallUtcMs);
            Assert.IsFalse(anchor.AtReceipt.HasSensorUs, "SensorUs must be absent when no native source is running");

            // Advance steady clock by 4.5s while tampering with wall clock (+2 hours)
            source.SteadyTimeUs += 4500000L;
            source.UnityTimeUs += 4500000L;
            source.WallUtcMs += 7200000L;
            source.HasSensorClock = true;
            source.SensorTimeUs = 9007199254740993L;

            ClockReading reading = clock.ReadClocks();
            Assert.AreEqual(1794733924750000L, reading.SessionUs);
            Assert.AreEqual(28000000L, reading.UnityUs);
            Assert.IsTrue(reading.HasSensorUs);
            Assert.AreEqual(9007199254740993L, reading.SensorUs);
            Assert.AreEqual(1794734100662L + 7200000L, reading.WallUtcMs);

            // Token expiry uses SessionClock (~1794733924750 ms), NOT tampered WallUtcMs (~1794741300662 ms)
            Assert.IsFalse(clock.IsTokenExpired(1794733925000L));
            source.SteadyTimeUs += 300000L; // +0.3s -> sessionMs = 1794733925050
            Assert.IsTrue(clock.IsTokenExpired(1794733925000L));
        }

        [Test]
        public void SessionClock_WindowSlicing_FullAndEarlyClose_AlwaysWholeSeconds()
        {
            FakeClockSource source = new FakeClockSource
            {
                SteadyTimeUs = 1000000L,
                UnityTimeUs = 1000000L,
                WallUtcMs = 1794733920000L
            };
            SessionClock clock = new SessionClock(source);
            clock.StartSession(1794733920500L, 20u);

            Assert.AreEqual(1794733920000000L, clock.CurrentWindowStartUs);
            Assert.AreEqual(1794733930000000L, clock.GetWindowEndUs(10u));
            Assert.IsFalse(clock.IsCurrentWindowFull(10u));

            // Advance 9.5s -> reaches 1794733930000000 exact
            source.SteadyTimeUs += 9500000L;
            Assert.IsTrue(clock.IsCurrentWindowFull(10u));

            uint fullLen = clock.AdvanceWindow(clock.GetWindowEndUs(10u), true, 10u);
            Assert.AreEqual(10u, fullLen);
            Assert.AreEqual(1794733930000000L, clock.CurrentWindowStartUs);

            // Early close after 2.3s
            source.SteadyTimeUs += 2300000L;
            long earlyClosedUs = clock.CurrentSessionUs;
            uint earlyLen = clock.AdvanceWindow(earlyClosedUs, false, 10u);
            Assert.AreEqual(3u, earlyLen);
            Assert.AreEqual(1794733932000000L, clock.CurrentWindowStartUs);
            Assert.AreEqual(0L, clock.CurrentWindowStartUs % 1000000L);
        }

        [Test]
        public void SessionClock_BackgroundPauseResume_StepsWholeSecondsOrStartsNewSession()
        {
            FakeClockSource source = new FakeClockSource
            {
                SteadyTimeUs = 10000000L,
                UnityTimeUs = 50000000L,
                WallUtcMs = 1794736860000L
            };
            SessionClock clock = new SessionClock(source);
            clock.StartSession(1794736860000L, 30u);

            // Case 1: Short pause where steady clock lagged wall clock by 2.7s -> step +2 whole seconds
            clock.OnPause();
            source.SteadyTimeUs += 1000000L; // 1.0s
            source.UnityTimeUs += 1000000L;
            source.WallUtcMs += 3700L; // 3.7s -> lag is 2.7s -> step 2s

            SessionResumeResult res1 = clock.OnResume(1800u, out LifecycleRecord? step1);
            Assert.AreEqual(SessionResumeResult.ContinueSession, res1);
            Assert.IsTrue(step1.HasValue);
            Assert.AreEqual(LifecycleKind.ClockStepped, step1.Value.Kind);
            Assert.AreEqual(2L, step1.Value.ClockStepS);
            Assert.AreEqual(51000000L, step1.Value.TimeUs);
            // SessionUs = 1794736860000000 + 1000000 (steady) + 2000000 (step) = 1794736863000000
            Assert.AreEqual(1794736863000000L, clock.CurrentSessionUs);
            Assert.AreEqual(1794736863000000L, clock.CurrentWindowStartUs);

            // Case 2: Short pause where steady lag < 1s -> no CLOCK_STEPPED record
            clock.OnPause();
            source.SteadyTimeUs += 5000000L;
            source.UnityTimeUs += 5000000L;
            source.WallUtcMs += 5800L; // 0.8s lag

            SessionResumeResult res2 = clock.OnResume(1800u, out LifecycleRecord? step2);
            Assert.AreEqual(SessionResumeResult.ContinueSession, res2);
            Assert.IsFalse(step2.HasValue);

            // Case 3: Pause of exactly 1800s (not strictly greater than 1800s) -> ContinueSession
            clock.OnPause();
            source.SteadyTimeUs += 1800L * 1000000L;
            source.UnityTimeUs += 1800L * 1000000L;
            source.WallUtcMs += 1800L * 1000L;
            SessionResumeResult res3 = clock.OnResume(1800u, out LifecycleRecord? step3);
            Assert.AreEqual(SessionResumeResult.ContinueSession, res3);
            Assert.IsFalse(step3.HasValue);

            // Case 4: Pause of 1801s (> new_session_after_s = 1800s) -> StartNewSession, clock not stepped
            long sessionBeforeLongPause = clock.CurrentSessionUs;
            clock.OnPause();
            source.SteadyTimeUs += 1000000L; // steady froze during sleep
            source.UnityTimeUs += 1000000L;
            source.WallUtcMs += 1801L * 1000L;

            SessionResumeResult res4 = clock.OnResume(1800u, out LifecycleRecord? step4);
            Assert.AreEqual(SessionResumeResult.StartNewSession, res4);
            Assert.IsFalse(step4.HasValue);
            Assert.AreEqual(sessionBeforeLongPause + 1000000L, clock.CurrentSessionUs);
        }

        [Test]
        public void SystemClockSource_TicksToMicroseconds_IsExactAtLargeTickCounts()
        {
            // 100 days at 1 GHz = 8.64e15 ticks (exceeds 2^53 double exact integer range)
            long freq = 1000000000L;
            long ticks = 8640000L * freq + 123456000L; // 8,640,000.123456 s
            long expectedUs = 8640000L * 1000000L + 123456L;
            Assert.AreEqual(expectedUs, SystemClockSource.TicksToMicroseconds(ticks, freq));
        }

        // ====================================================================
        // 2. StateTracker, Carried.State & nx10_* Rules Tests
        // ====================================================================

        [Test]
        public void StateTracker_FreezesWindowOpenCarriedState_AndAdvancesAcrossWindows()
        {
            WindowCallOrder order = new WindowCallOrder();
            StateTracker tracker = new StateTracker(order);

            // Window 1: start empty, apply 3 mutations
            Assert.IsTrue(tracker.SetAttribute(StandardStateKeys.GameLevel, "1", 1000L));
            Assert.IsTrue(tracker.SetAttribute(StandardStateKeys.View, "gameplay", 2000L));
            Assert.IsTrue(tracker.RemoveAttribute(StandardStateKeys.View, 3000L));

            Batch window1 = new Batch();
            tracker.FlushWindowTo(window1);

            // Window 1 Carried.State must still be present-and-empty (Version = 0)
            Assert.IsNotNull(window1.Carried);
            Assert.IsNotNull(window1.Carried.State);
            Assert.AreEqual(0u, window1.Carried.State.Version);
            Assert.AreEqual(0, window1.Carried.State.Values.Count);

            // Window 1 StateRecords must reflect Versions 1, 2, 3 and Orders 1, 2, 3
            Assert.AreEqual(3, window1.StateRecords.Count);
            Assert.AreEqual(StateKind.Patch, window1.StateRecords[0].Kind);
            Assert.AreEqual(1u, window1.StateRecords[0].Version);
            Assert.AreEqual(1u, window1.StateRecords[0].Order);
            Assert.AreEqual(Value.FromText("1"), window1.StateRecords[0].Values[StandardStateKeys.GameLevel]);

            Assert.AreEqual(2u, window1.StateRecords[1].Version);
            Assert.AreEqual(2u, window1.StateRecords[1].Order);

            Assert.AreEqual(StateKind.Patch, window1.StateRecords[2].Kind);
            Assert.AreEqual(3u, window1.StateRecords[2].Version);
            Assert.AreEqual(3u, window1.StateRecords[2].Order);
            CollectionAssert.AreEqual(new[] { StandardStateKeys.View }, window1.StateRecords[2].RemovedKeys);

            // Advance to Window 2 -> Carried.State now carries Version 3 and game_level = "1"
            tracker.AdvanceWindow();
            order.ResetWindow();
            tracker.ReturnRecordsToPool(window1.StateRecords);

            Assert.IsTrue(tracker.SetAttribute(StandardStateKeys.Mode, "ranked", 11000L));

            Batch window2 = new Batch();
            tracker.FlushWindowTo(window2);
            Assert.IsNotNull(window2.Carried.State);
            Assert.AreEqual(3u, window2.Carried.State.Version);
            Assert.AreEqual(1, window2.Carried.State.Values.Count);
            Assert.AreEqual(Value.FromText("1"), window2.Carried.State.Values[StandardStateKeys.GameLevel]);
            Assert.AreEqual(1, window2.StateRecords.Count);
            Assert.AreEqual(4u, window2.StateRecords[0].Version);
            Assert.AreEqual(1u, window2.StateRecords[0].Order);
        }

        [Test]
        public void StateTracker_EnforcesNx10ReservedKeyProtection_AndPreservesSdkKeysOnPutAndClear()
        {
            List<string> warnings = new List<string>();
            WindowCallOrder order = new WindowCallOrder();
            StateTracker tracker = new StateTracker(order, msg => warnings.Add(msg));

            // SDK sets brightness & screen lock
            Assert.IsTrue(tracker.UpdateSdkScreenBrightness(0.8, 1000000L));
            Assert.IsTrue(tracker.UpdateSdkScreenLocked(false, 1000100L));
            Assert.AreEqual(2u, tracker.CurrentVersion);
            Assert.AreEqual(2u, order.Current);

            // Game attempts to overwrite/remove reserved nx10_* keys -> declined with 1 warning line each
            Assert.IsFalse(tracker.SetAttribute(SdkStateKeys.ScreenBrightness, 0.5, 2000000L));
            Assert.AreEqual(1, warnings.Count);
            Assert.AreEqual(2u, tracker.CurrentVersion);
            Assert.AreEqual(2u, order.Current);

            Assert.IsFalse(tracker.RemoveAttribute(SdkStateKeys.ScreenLocked, 2100000L));
            Assert.AreEqual(2, warnings.Count);
            Assert.AreEqual(2u, tracker.CurrentVersion);
            Assert.AreEqual(2u, order.Current);

            // Game calls SetAttributes with both game keys and multiple reserved nx10_* keys
            Dictionary<string, Value> mixed = new Dictionary<string, Value>
            {
                { StandardStateKeys.GameLevel, "7" },
                { "nx10_custom", "hack" },
                { SdkStateKeys.ScreenLocked, true }
            };
            Assert.IsTrue(tracker.SetAttributes(mixed, 2200000L));
            Assert.AreEqual(3, warnings.Count, "SetAttributes with reserved keys must emit exactly one warning line per call");
            Assert.AreEqual(3u, tracker.CurrentVersion);
            Assert.AreEqual(3u, order.Current);

            // Resulting state & PUT record must contain game_level="7" AND preserved SDK nx10_* keys (NOT nx10_custom)
            StateRecord putRec = tracker.WindowRecords[tracker.WindowRecords.Count - 1];
            Assert.AreEqual(StateKind.Put, putRec.Kind);
            Assert.AreEqual(3, putRec.Values.Count);
            Assert.AreEqual(Value.FromText("7"), putRec.Values[StandardStateKeys.GameLevel]);
            Assert.AreEqual(Value.FromNumber(0.8), putRec.Values[SdkStateKeys.ScreenBrightness]);
            Assert.AreEqual(Value.FromFlag(false), putRec.Values[SdkStateKeys.ScreenLocked]);
            Assert.IsFalse(putRec.Values.ContainsKey("nx10_custom"));

            // ClearAttributes clears game keys while preserving active SDK nx10_* keys
            Assert.IsTrue(tracker.ClearAttributes(2300000L));
            StateRecord clearRec = tracker.WindowRecords[tracker.WindowRecords.Count - 1];
            Assert.AreEqual(StateKind.Put, clearRec.Kind);
            Assert.AreEqual(2, clearRec.Values.Count);
            Assert.IsFalse(clearRec.Values.ContainsKey(StandardStateKeys.GameLevel));
            Assert.AreEqual(Value.FromNumber(0.8), clearRec.Values[SdkStateKeys.ScreenBrightness]);
            Assert.AreEqual(Value.FromFlag(false), clearRec.Values[SdkStateKeys.ScreenLocked]);
        }

        [Test]
        public void StateTracker_SdkBrightnessAndLock_EnforceThresholdsThrottlingAndUnknownOmission()
        {
            WindowCallOrder order = new WindowCallOrder();
            StateTracker tracker = new StateTracker(order);

            // Unknown values omitted
            Assert.IsFalse(tracker.UpdateSdkScreenBrightness(-1.0, 0L));
            Assert.IsFalse(tracker.UpdateSdkScreenBrightness(double.NaN, 0L));
            Assert.IsFalse(tracker.UpdateSdkScreenBrightness(double.PositiveInfinity, 0L));
            Assert.IsFalse(tracker.UpdateSdkScreenLocked(null, 0L));
            Assert.AreEqual(0, tracker.CurrentKeyCount);
            Assert.AreEqual(0, tracker.WindowRecords.Count);

            // Initial brightness 0.50 sent
            Assert.IsTrue(tracker.UpdateSdkScreenBrightness(0.50, 1000000L));
            Assert.AreEqual(Value.FromNumber(0.50), tracker.CurrentState[SdkStateKeys.ScreenBrightness]);

            // Delta < 0.05 (0.53) suppressed even after 2s
            Assert.IsFalse(tracker.UpdateSdkScreenBrightness(0.53, 3000000L));

            // Delta >= 0.05 (0.60) but only +500ms since last sent (1000000 + 500000) -> suppressed
            Assert.IsFalse(tracker.UpdateSdkScreenBrightness(0.60, 1500000L));

            // Delta == 0.05 (0.55) at exactly +1000ms (2000000) -> sent!
            Assert.IsTrue(tracker.UpdateSdkScreenBrightness(0.55, 2000000L));
            Assert.AreEqual(Value.FromNumber(0.55), tracker.CurrentState[SdkStateKeys.ScreenBrightness]);

            // Values > 1.0 clamp to 1.0
            Assert.IsTrue(tracker.UpdateSdkScreenBrightness(1.4, 3000000L));
            Assert.AreEqual(Value.FromNumber(1.0), tracker.CurrentState[SdkStateKeys.ScreenBrightness]);

            // Screen lock: first value emitted, duplicate suppressed, transitions emitted
            Assert.IsTrue(tracker.UpdateSdkScreenLocked(false, 4000000L));
            Assert.IsFalse(tracker.UpdateSdkScreenLocked(false, 4100000L));
            Assert.IsTrue(tracker.UpdateSdkScreenLocked(true, 4200000L));
            Assert.IsFalse(tracker.UpdateSdkScreenLocked(true, 4300000L));
            Assert.IsTrue(tracker.UpdateSdkScreenLocked(false, 4400000L));
            Assert.AreEqual(Value.FromFlag(false), tracker.CurrentState[SdkStateKeys.ScreenLocked]);
            Assert.AreEqual(6, tracker.WindowRecords.Count); // 3 brightness + 3 lock transitions
        }

        [Test]
        public void DiagnosticRecorder_DeduplicatesWithinWindowAndCapsDistinctCodes()
        {
            DiagnosticRecorder rec = new DiagnosticRecorder();
            rec.RecordWarning("sensor_silent", "gyro silent", 100L);
            rec.RecordWarning("sensor_silent", "gyro silent again", 200L);
            rec.RecordError("sensor_silent", "different severity", 300L);

            Assert.AreEqual(2, rec.ActiveCount);
            Assert.AreEqual(2u, rec.ActiveDiagnostics[0].Count);
            Assert.AreEqual(1u, rec.ActiveDiagnostics[1].Count);

            for (int i = 0; i < 80; i++)
            {
                rec.RecordWarning("code_" + i, "detail", 400L + i);
            }
            Assert.AreEqual(DiagnosticRecorder.MaxDistinctDiagnosticsPerWindow, rec.ActiveCount);

            List<Diagnostic> drained = new List<Diagnostic>();
            rec.DrainTo(drained);
            Assert.AreEqual(DiagnosticRecorder.MaxDistinctDiagnosticsPerWindow, drained.Count);
            Assert.AreEqual(0, rec.ActiveCount);
            rec.ReturnToPool(drained);
            Assert.AreEqual(0, drained.Count);
        }

        // ====================================================================
        // 3. EventAndSpanTracker, 18 Rules, Shared Order & CoreEventSender
        // ====================================================================

        [Test]
        public void EventAndSpanTracker_EnforcesAllSpanRules_AndOverlappingAdSpans()
        {
            WindowCallOrder order = new WindowCallOrder();
            EventAndSpanTracker tracker = new EventAndSpanTracker(order);

            // Rule 8: Closing when no span is open -> Close with SpanNumber = 0
            GameEvent orphanClose = tracker.RecordEvent(CoreEventNames.GameLevelFailed, null, false, null, null, 100L);
            Assert.AreEqual(CoreSpans.Level, orphanClose.SpanName);
            Assert.AreEqual(SpanMarker.Close, orphanClose.SpanMarker);
            Assert.AreEqual(0u, orphanClose.SpanNumber);

            // Rule 4 & 5: Raw SendEvent with core name gets core role & default span
            GameEvent lvl1 = tracker.RecordEvent(CoreEventNames.GameLevelStarted, null, false, null, null, 200L);
            Assert.AreEqual(CoreSpans.Level, lvl1.SpanName);
            Assert.AreEqual(SpanMarker.Open, lvl1.SpanMarker);
            Assert.AreEqual(1u, lvl1.SpanNumber);

            // Rule 6 (core-opened span): Role == None event with span="level" gets no marker & number 0, leaves "level" open
            GameEvent midClick = tracker.RecordEvent("button_clicked", null, false, null, CoreSpans.Level, 300L);
            Assert.AreEqual(CoreSpans.Level, midClick.SpanName);
            Assert.AreEqual(SpanMarker.Unspecified, midClick.SpanMarker);
            Assert.AreEqual(0u, midClick.SpanNumber);

            GameEvent midRev = tracker.RecordEvent(CoreEventNames.AdRevenue, null, true, Outcomes.Converted, CoreSpans.Level, 350L);
            Assert.AreEqual(CoreSpans.Level, midRev.SpanName);
            Assert.AreEqual(SpanMarker.Unspecified, midRev.SpanMarker);
            Assert.AreEqual(0u, midRev.SpanNumber);

            // Rule 7: Opening game_level_started while "level" (#1) is open replaces #1 and opens #2
            GameEvent lvl2 = tracker.RecordEvent(CoreEventNames.GameLevelStarted, null, false, null, null, 400L);
            Assert.AreEqual(CoreSpans.Level, lvl2.SpanName);
            Assert.AreEqual(SpanMarker.Open, lvl2.SpanMarker);
            Assert.AreEqual(2u, lvl2.SpanNumber);
            Assert.AreEqual(1, tracker.OpenSpanCount);

            // Closing "level" closes #2
            GameEvent lvl2Win = tracker.RecordEvent(CoreEventNames.GameLevelSucceeded, null, false, null, null, 500L);
            Assert.AreEqual(SpanMarker.Close, lvl2Win.SpanMarker);
            Assert.AreEqual(2u, lvl2Win.SpanNumber);
            Assert.AreEqual(0, tracker.OpenSpanCount);

            // Rule 5 & 9: Overlapping ad spans (ad_banner stays open while ad_rewarded opens & closes; no type -> "ad")
            Dictionary<string, Value> bannerFields = new Dictionary<string, Value> { { "type", "banner" } };
            Dictionary<string, Value> rewardedFields = new Dictionary<string, Value> { { "type", "rewarded" } };

            GameEvent bannerOpen = tracker.RecordEvent(CoreEventNames.AdStarted, bannerFields, false, null, null, 600L);
            Assert.AreEqual("ad_banner", bannerOpen.SpanName);
            Assert.AreEqual(SpanMarker.Open, bannerOpen.SpanMarker);
            Assert.AreEqual(3u, bannerOpen.SpanNumber);

            GameEvent rewOpen = tracker.RecordEvent(CoreEventNames.AdStarted, rewardedFields, false, null, null, 700L);
            Assert.AreEqual("ad_rewarded", rewOpen.SpanName);
            Assert.AreEqual(SpanMarker.Open, rewOpen.SpanMarker);
            Assert.AreEqual(4u, rewOpen.SpanNumber);

            GameEvent untypedAdOpen = tracker.RecordEvent(CoreEventNames.AdStarted, null, false, null, null, 750L);
            Assert.AreEqual("ad", untypedAdOpen.SpanName);
            Assert.AreEqual(SpanMarker.Open, untypedAdOpen.SpanMarker);
            Assert.AreEqual(5u, untypedAdOpen.SpanNumber);

            GameEvent rewClose = tracker.RecordEvent(CoreEventNames.AdFinished, rewardedFields, false, null, null, 800L);
            Assert.AreEqual("ad_rewarded", rewClose.SpanName);
            Assert.AreEqual(SpanMarker.Close, rewClose.SpanMarker);
            Assert.AreEqual(4u, rewClose.SpanNumber);

            // ad_banner (#3) and ad (#5) remain open
            Assert.AreEqual(2, tracker.OpenSpanCount);

            // Rule 5: Caller span override wins even on core event
            GameEvent customSpanLvl = tracker.RecordEvent(CoreEventNames.GameLevelStarted, null, false, null, "boss_rush", 900L);
            Assert.AreEqual("boss_rush", customSpanLvl.SpanName);
            Assert.AreEqual(SpanMarker.Open, customSpanLvl.SpanMarker);
            Assert.AreEqual(6u, customSpanLvl.SpanNumber);

            // Rule 6: Name-only core spans & custom spans toggle OPEN -> CLOSE -> OPEN
            GameEvent offerOpen = tracker.RecordEvent("offer_shown", null, false, null, CoreSpans.RewardedOffer, 1000L);
            Assert.AreEqual(CoreSpans.RewardedOffer, offerOpen.SpanName);
            Assert.AreEqual(SpanMarker.Open, offerOpen.SpanMarker);
            Assert.AreEqual(7u, offerOpen.SpanNumber);

            GameEvent offerClose = tracker.RecordEvent("offer_hidden", null, false, null, CoreSpans.RewardedOffer, 1100L);
            Assert.AreEqual(SpanMarker.Close, offerClose.SpanMarker);
            Assert.AreEqual(7u, offerClose.SpanNumber);

            GameEvent offerReopen = tracker.RecordEvent("offer_shown", null, false, null, CoreSpans.RewardedOffer, 1200L);
            Assert.AreEqual(SpanMarker.Open, offerReopen.SpanMarker);
            Assert.AreEqual(8u, offerReopen.SpanNumber);

            // Verify all other name-only core spans work identically
            string[] nameOnlySpans = { CoreSpans.Popup, CoreSpans.Settings, CoreSpans.Backgrounded, CoreSpans.TutorialOrOnboarding };
            for (int i = 0; i < nameOnlySpans.Length; i++)
            {
                GameEvent o = tracker.RecordEvent("ui_open", null, false, null, nameOnlySpans[i], 1300L + i * 20);
                GameEvent c = tracker.RecordEvent("ui_close", null, false, null, nameOnlySpans[i], 1310L + i * 20);
                Assert.AreEqual(SpanMarker.Open, o.SpanMarker);
                Assert.AreEqual(SpanMarker.Close, c.SpanMarker);
                Assert.AreEqual(o.SpanNumber, c.SpanNumber);
            }
        }

        [Test]
        public void CarriedSpansLastEventTime_AndSharedWindowOrder_WorkAcrossWindowBoundaries()
        {
            WindowCallOrder order = new WindowCallOrder();
            StateTracker stateTracker = new StateTracker(order);
            EventAndSpanTracker eventTracker = new EventAndSpanTracker(order);

            // Interleaved calls in Window 1: Event (1), State (2), SDK State (3), Event (4)
            GameEvent e1 = eventTracker.RecordEvent(CoreEventNames.GameLevelStarted, null, false, null, null, 1000L);
            stateTracker.SetAttribute(StandardStateKeys.GameLevel, "1", 1500L);
            stateTracker.UpdateSdkScreenBrightness(0.75, 2000L);
            GameEvent e2 = eventTracker.RecordEvent("custom_ping", null, false, null, null, 2500L);

            Assert.AreEqual(1u, e1.Order);
            Assert.AreEqual(2u, stateTracker.WindowRecords[0].Order);
            Assert.AreEqual(3u, stateTracker.WindowRecords[1].Order);
            Assert.AreEqual(4u, e2.Order);

            Batch w1 = new Batch();
            stateTracker.FlushWindowTo(w1);
            eventTracker.FlushWindowTo(w1);

            // Window 1 Carried: empty state, empty OpenSpans (present!), LastEventTimeUs ABSENT
            Assert.IsNotNull(w1.Carried);
            Assert.IsNotNull(w1.Carried.State);
            Assert.AreEqual(0, w1.Carried.State.Values.Count);
            Assert.IsNotNull(w1.Carried.OpenSpans);
            Assert.AreEqual(0, w1.Carried.OpenSpans.Spans.Count);
            Assert.IsFalse(w1.Carried.HasLastEventTimeUs);

            // Advance to Window 2 (open span "level" #1 persists per Rule 11; order resets to 1)
            stateTracker.AdvanceWindow();
            eventTracker.AdvanceWindow();
            order.ResetWindow();

            GameEvent e3 = eventTracker.RecordEvent(CoreEventNames.GameLevelSucceeded, null, false, null, null, 12000L);
            Assert.AreEqual(1u, e3.Order);
            Assert.AreEqual(SpanMarker.Close, e3.SpanMarker);
            Assert.AreEqual(1u, e3.SpanNumber);

            Batch w2 = new Batch();
            stateTracker.FlushWindowTo(w2);
            eventTracker.FlushWindowTo(w2);

            Assert.IsNotNull(w2.Carried.OpenSpans);
            Assert.AreEqual(1, w2.Carried.OpenSpans.Spans.Count);
            Assert.AreEqual(new OpenSpan(CoreSpans.Level, 1u, 1000L), w2.Carried.OpenSpans.Spans[0]);
            Assert.IsTrue(w2.Carried.HasLastEventTimeUs);
            Assert.AreEqual(2500L, w2.Carried.LastEventTimeUs);
        }

        [Test]
        public void CoreEventSender_AllElevenMethods_ProduceExactContractFieldsAndRoundTripProtobuf()
        {
            FakeClockSource clock = new FakeClockSource { UnityTimeUs = 100000L };
            WindowCallOrder order = new WindowCallOrder();
            EventAndSpanTracker tracker = new EventAndSpanTracker(order);
            CoreEventSender sender = new CoreEventSender(new TrackerEventSink(tracker, clock));

            sender.LevelStarted("3", attemptNumber: 2, difficulty: 4.5);
            clock.UnityTimeUs += 1000L;
            sender.LevelSucceeded("3", score: 1250.5, duration: 42.0, attemptNumber: 2);
            clock.UnityTimeUs += 1000L;
            sender.LevelStarted("4");
            clock.UnityTimeUs += 1000L;
            sender.LevelFailed("4", score: 100, duration: 12.5, attemptNumber: 1);
            clock.UnityTimeUs += 1000L;
            sender.AdStarted("rewarded", placement: "revive", adsInSet: 2, adNumber: 1);
            clock.UnityTimeUs += 1000L;
            sender.AdFinished("rewarded", placement: "revive", completed: true, adsInSet: 2, adNumber: 1);
            clock.UnityTimeUs += 1000L;
            sender.AdRevenue("rewarded", 0.025, "USD", outcome: Outcomes.Converted, network: "admob", reason: "ad_displayed");
            clock.UnityTimeUs += 1000L;
            sender.Purchase("coins_500", 4.99, "USD", outcome: Outcomes.UnConverted, reason: "user_cancelled", quantity: 1, transactionId: "tx-99");
            clock.UnityTimeUs += 1000L;
            sender.PurchaseViewOpened("shop", product: "coins_500", amount: 4.99, currency: "USD");
            clock.UnityTimeUs += 1000L;
            sender.PurchaseViewClosed("shop", product: "coins_500", amount: 4.99, currency: "USD", reason: "dismissed");
            clock.UnityTimeUs += 1000L;
            sender.GamblingRoundStarted("slots", machine: "lucky_7", betAmount: 10.0, betCurrency: "chips", autoSpin: true);
            clock.UnityTimeUs += 1000L;
            sender.GamblingRoundFinished("slots", "jackpot", betAmount: 10.0, betCurrency: "chips", returnAmount: 500.0, returnCurrency: "chips", machine: "lucky_7");

            Assert.AreEqual(12, tracker.CurrentWindowEventCount);

            Batch batch = new Batch
            {
                Header = new Header
                {
                    IdOrg = "o",
                    IdDeployment = "d",
                    IdSource = "s",
                    IdSession = "ses",
                    Sequence = 1,
                    SchemaVersion = BatchConstants.SchemaVersion
                }
            };
            tracker.FlushWindowTo(batch);

            // Round-trip through BatchProtobufCodec
            ProtobufWireWriter writer = new ProtobufWireWriter();
            Assert.IsTrue(BatchProtobufCodec.TryEncodeBatch(batch, writer, out string encErr), encErr);
            byte[] bytes = writer.ToArray();
            Assert.IsTrue(BatchProtobufCodec.TryDecodeBatch(bytes, 0, bytes.Length, out Batch dec, out string decErr), decErr);

            Assert.AreEqual(12, dec.Events.Count);

            // 1. game_level_started
            Assert.AreEqual(CoreEventNames.GameLevelStarted, dec.Events[0].Name);
            Assert.AreEqual(Value.FromText("3"), dec.Events[0].Fields["level"]);
            Assert.AreEqual(Value.FromNumber(2), dec.Events[0].Fields["attempt_number"]);
            Assert.AreEqual(Value.FromNumber(4.5), dec.Events[0].Fields["difficulty"]);
            Assert.AreEqual(CoreSpans.Level, dec.Events[0].SpanName);
            Assert.AreEqual(SpanMarker.Open, dec.Events[0].SpanMarker);

            // 2. game_level_succeeded
            Assert.AreEqual(CoreEventNames.GameLevelSucceeded, dec.Events[1].Name);
            Assert.AreEqual(Value.FromNumber(1250.5), dec.Events[1].Fields["score"]);
            Assert.AreEqual(Value.FromNumber(42.0), dec.Events[1].Fields["duration"]);
            Assert.AreEqual(SpanMarker.Close, dec.Events[1].SpanMarker);
            Assert.AreEqual(dec.Events[0].SpanNumber, dec.Events[1].SpanNumber);

            // 4. game_level_failed
            Assert.AreEqual(CoreEventNames.GameLevelFailed, dec.Events[3].Name);
            Assert.AreEqual(SpanMarker.Close, dec.Events[3].SpanMarker);

            // 5 & 6. ad_started / ad_finished
            Assert.AreEqual(CoreEventNames.AdStarted, dec.Events[4].Name);
            Assert.AreEqual("ad_rewarded", dec.Events[4].SpanName);
            Assert.AreEqual(Value.FromNumber(2), dec.Events[4].Fields["ads_in_set"]);
            Assert.AreEqual(CoreEventNames.AdFinished, dec.Events[5].Name);
            Assert.AreEqual(Value.FromFlag(true), dec.Events[5].Fields["completed"]);
            Assert.AreEqual(dec.Events[4].SpanNumber, dec.Events[5].SpanNumber);

            // 7. ad_revenue
            Assert.AreEqual(CoreEventNames.AdRevenue, dec.Events[6].Name);
            Assert.IsTrue(dec.Events[6].HasOutcome);
            Assert.AreEqual(Outcomes.Converted, dec.Events[6].Outcome);
            Assert.AreEqual(Value.FromNumber(0.025), dec.Events[6].Fields["amount"]);
            Assert.AreEqual(Value.FromText("admob"), dec.Events[6].Fields["network"]);

            // 8. in_app_purchase
            Assert.AreEqual(CoreEventNames.InAppPurchase, dec.Events[7].Name);
            Assert.IsTrue(dec.Events[7].HasOutcome);
            Assert.AreEqual(Outcomes.UnConverted, dec.Events[7].Outcome);
            Assert.AreEqual(Value.FromText("coins_500"), dec.Events[7].Fields["product"]);
            Assert.AreEqual(Value.FromNumber(4.99), dec.Events[7].Fields["price"]);
            Assert.AreEqual(Value.FromText("tx-99"), dec.Events[7].Fields["transaction_id"]);

            // 9 & 10. purchase_view opened / closed
            Assert.AreEqual(CoreEventNames.InAppPurchaseViewOpened, dec.Events[8].Name);
            Assert.AreEqual(CoreSpans.PurchaseView, dec.Events[8].SpanName);
            Assert.AreEqual(CoreEventNames.InAppPurchaseViewClosed, dec.Events[9].Name);
            Assert.AreEqual(Value.FromText("dismissed"), dec.Events[9].Fields["reason"]);
            Assert.AreEqual(dec.Events[8].SpanNumber, dec.Events[9].SpanNumber);

            // 11 & 12. gambling_round_started / finished
            Assert.AreEqual(CoreEventNames.GamblingRoundStarted, dec.Events[10].Name);
            Assert.AreEqual(CoreSpans.Round, dec.Events[10].SpanName);
            Assert.AreEqual(Value.FromFlag(true), dec.Events[10].Fields["auto_spin"]);
            Assert.AreEqual(CoreEventNames.GamblingRoundFinished, dec.Events[11].Name);
            Assert.AreEqual(Value.FromText("jackpot"), dec.Events[11].Fields["result"]);
            Assert.AreEqual(Value.FromNumber(500.0), dec.Events[11].Fields["return_amount"]);
            Assert.AreEqual(dec.Events[10].SpanNumber, dec.Events[11].SpanNumber);
        }
    }
}
