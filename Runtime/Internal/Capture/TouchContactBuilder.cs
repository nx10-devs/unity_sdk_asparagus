using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Zero-GC single-finger touch stream encoder supporting <c>touch_hz</c> rate thinning,
    /// delta encoding across <see cref="TouchHold"/>s, optional pressure presence invariants,
    /// and cross-window <see cref="ContactBefore"/> bounding-box tracking (Decision 135).
    /// </summary>
    public sealed class TouchContactBuilder
    {
        private const int DefaultCandidateCapacity = 16;

        private struct QuantizedTouchPoint
        {
            public long TimeUs;
            public int X;
            public int Y;
            public int SizeMajor;
            public int SizeMinor;
            public int Pressure;

            public QuantizedTouchPoint(long timeUs, int x, int y, int sizeMajor, int sizeMinor, int pressure)
            {
                TimeUs = timeUs;
                X = x;
                Y = y;
                SizeMajor = sizeMajor;
                SizeMinor = sizeMinor;
                Pressure = pressure;
            }
        }

        private struct AxisAccumulator
        {
            public long Sum;
            public double SumOfSquares;
            public int Min;
            public int Max;

            public void Init(int v)
            {
                Sum = v;
                double dv = v;
                SumOfSquares = dv * dv;
                Min = v;
                Max = v;
            }

            public void Add(int v)
            {
                Sum += v;
                double dv = v;
                SumOfSquares += dv * dv;
                if (v < Min) Min = v;
                if (v > Max) Max = v;
            }

            public HoldStats ToHoldStats()
            {
                return new HoldStats(Sum, SumOfSquares, Min, Max);
            }
        }

        private struct ActiveTouchHoldState
        {
            public long StartTimeUs;
            public long EndTimeUs;
            public uint SampleCount;
            public int AnchorX;
            public int AnchorY;
            public int AnchorSizeMajor;
            public int AnchorSizeMinor;
            public int AnchorPressure;
            public AxisAccumulator StatsX;
            public AxisAccumulator StatsY;
            public AxisAccumulator StatsSizeMajor;
            public AxisAccumulator StatsSizeMinor;
            public AxisAccumulator StatsPressure;

            public void StartFrom(in QuantizedTouchPoint first)
            {
                StartTimeUs = first.TimeUs;
                EndTimeUs = first.TimeUs;
                SampleCount = 1u;
                AnchorX = first.X;
                AnchorY = first.Y;
                AnchorSizeMajor = first.SizeMajor;
                AnchorSizeMinor = first.SizeMinor;
                AnchorPressure = first.Pressure;
                StatsX.Init(first.X);
                StatsY.Init(first.Y);
                StatsSizeMajor.Init(first.SizeMajor);
                StatsSizeMinor.Init(first.SizeMinor);
                StatsPressure.Init(first.Pressure);
            }

            public void Append(long timeUs, int x, int y, int sizeMajor, int sizeMinor, int pressure)
            {
                EndTimeUs = timeUs;
                SampleCount++;
                StatsX.Add(x);
                StatsY.Add(y);
                StatsSizeMajor.Add(sizeMajor);
                StatsSizeMinor.Add(sizeMinor);
                StatsPressure.Add(pressure);
            }
        }

        private uint _fingerId;
        private bool _continued;
        private bool _hasBefore;
        private long _downTimeUs;
        private int _beforeMinX;
        private int _beforeMaxX;
        private int _beforeMinY;
        private int _beforeMaxY;

        private bool _hasRunningBounds;
        private int _runningMinX;
        private int _runningMaxX;
        private int _runningMinY;
        private int _runningMaxY;

        private TouchEnd _end;
        private uint _eventsSeen;
        private bool _hasWindowPressureDecision;
        private bool _reportsPressure;

        private bool _hasLastKept;
        private long _lastKeptTimeUs;

        private bool _hasLastFull;
        private long _lastFullTimeUs;
        private int _lastFullX;
        private int _lastFullY;
        private int _lastFullSizeMajor;
        private int _lastFullSizeMinor;
        private int _lastFullPressure;

        private QuantizedTouchPoint[] _candidates = new QuantizedTouchPoint[DefaultCandidateCapacity];
        private int _candHead;
        private int _candCount;

        private bool _hasActiveHold;
        private ActiveTouchHoldState _activeHold;

        private TouchContact _activeContact;
        private int _windowSampleCount;

        public uint FingerId
        {
            get { return _fingerId; }
        }

        public TouchEnd End
        {
            get { return _end; }
        }

        public bool IsFinished
        {
            get { return _end == TouchEnd.Lifted || _end == TouchEnd.Cancelled; }
        }

        public bool HasWindowData
        {
            get { return _windowSampleCount > 0; }
        }

        public int WindowSampleCount
        {
            get { return _windowSampleCount; }
        }

        public void BeginNewContact(uint fingerId, long downTimeUs, uint holdRunLength)
        {
            ResetAll();
            _fingerId = fingerId;
            _downTimeUs = downTimeUs;
            _end = TouchEnd.StillDown;
            EnsureCandidateCapacity(holdRunLength);
        }

        public void ResetAll()
        {
            _fingerId = 0u;
            _continued = false;
            _hasBefore = false;
            _downTimeUs = 0L;
            _beforeMinX = 0;
            _beforeMaxX = 0;
            _beforeMinY = 0;
            _beforeMaxY = 0;
            _hasRunningBounds = false;
            _runningMinX = 0;
            _runningMaxX = 0;
            _runningMinY = 0;
            _runningMaxY = 0;
            _end = TouchEnd.StillDown;
            _eventsSeen = 0u;
            _hasWindowPressureDecision = false;
            _reportsPressure = false;
            _hasLastKept = false;
            _lastKeptTimeUs = 0L;
            _hasLastFull = false;
            _lastFullTimeUs = 0L;
            _lastFullX = 0;
            _lastFullY = 0;
            _lastFullSizeMajor = 0;
            _lastFullSizeMinor = 0;
            _lastFullPressure = 0;
            _candHead = 0;
            _candCount = 0;
            _hasActiveHold = false;
            _activeContact = null;
            _windowSampleCount = 0;
        }

        /// <summary>
        /// Ingests one raw touch sample for this finger. Returns the number of newly kept samples (0 or 1).
        /// </summary>
        public int AddSample(
            in RawTouchSample sample,
            double positionScale,
            double sizeScale,
            double pressureScale,
            long minStepUs,
            uint holdTolerance,
            uint holdRunLength,
            long gapThresholdUs,
            TouchWindowAggregator poolOwner,
            List<GapRecord> gapsTarget)
        {
            _eventsSeen++;

            bool isTerminal = sample.Phase == TouchPhaseKind.Ended || sample.Phase == TouchPhaseKind.Cancelled;
            if (sample.Phase == TouchPhaseKind.Ended)
            {
                _end = TouchEnd.Lifted;
            }
            else if (sample.Phase == TouchPhaseKind.Cancelled)
            {
                _end = TouchEnd.Cancelled;
            }

            bool mustKeep = sample.Phase == TouchPhaseKind.Began || isTerminal;
            long tUs = sample.TimeUs;
            if (_hasLastKept && tUs < _lastKeptTimeUs)
            {
                tUs = _lastKeptTimeUs;
            }

            if (!mustKeep && minStepUs > 0L && _hasLastKept && (tUs - _lastKeptTimeUs) < minStepUs)
            {
                return 0;
            }

            EnsureCandidateCapacity(holdRunLength);

            if (!_hasWindowPressureDecision)
            {
                if (!_continued)
                {
                    _reportsPressure = sample.HasPressure;
                }
                else if (sample.HasPressure)
                {
                    _reportsPressure = true;
                }
                _hasWindowPressureDecision = true;
            }

            if (_activeContact == null)
            {
                _activeContact = poolOwner.RentContact();
                _activeContact.FingerId = _fingerId;
                _activeContact.Continued = _continued;
                if (_continued && _hasBefore)
                {
                    ContactBefore before = poolOwner.RentContactBefore();
                    before.DownTimeUs = _downTimeUs;
                    before.MinX = _beforeMinX;
                    before.MaxX = _beforeMaxX;
                    before.MinY = _beforeMinY;
                    before.MaxY = _beforeMaxY;
                    _activeContact.Before = before;
                }
            }

            int qx = SampleQuantizer.Quantize(sample.X, positionScale);
            int qy = SampleQuantizer.Quantize(sample.Y, positionScale);
            int qMaj = SampleQuantizer.Quantize(sample.SizeMajor, sizeScale);
            int qMin = SampleQuantizer.Quantize(sample.SizeMinor, sizeScale);
            int qPres = (_reportsPressure && sample.HasPressure)
                ? SampleQuantizer.Quantize(sample.Pressure, pressureScale)
                : 0;

            if (!_hasRunningBounds)
            {
                _hasRunningBounds = true;
                _runningMinX = qx;
                _runningMaxX = qx;
                _runningMinY = qy;
                _runningMaxY = qy;
            }
            else
            {
                if (qx < _runningMinX) _runningMinX = qx;
                if (qx > _runningMaxX) _runningMaxX = qx;
                if (qy < _runningMinY) _runningMinY = qy;
                if (qy > _runningMaxY) _runningMaxY = qy;
            }

            if (_hasLastKept && gapThresholdUs > 0L)
            {
                long dtUs = tUs - _lastKeptTimeUs;
                if (dtUs > gapThresholdUs)
                {
                    // Never allow a TouchHold to run across a gap.
                    FlushPending(holdTolerance, poolOwner);
                    if (gapsTarget != null)
                    {
                        gapsTarget.Add(new GapRecord
                        {
                            Signal = GapSignal.Touch,
                            Clock = Clock.Unity,
                            StartTimeUs = _lastKeptTimeUs,
                            EndTimeUs = tUs,
                            Cause = GapCause.SensorSilent
                        });
                    }
                }
            }

            _hasLastKept = true;
            _lastKeptTimeUs = tUs;
            _windowSampleCount++;

            if (holdRunLength == 0u)
            {
                EmitFullSample(tUs, qx, qy, qMaj, qMin, qPres);
                return 1;
            }

            if (_hasActiveHold)
            {
                if (WithinTolerance(qx, qy, qMaj, qMin, qPres,
                    _activeHold.AnchorX, _activeHold.AnchorY, _activeHold.AnchorSizeMajor, _activeHold.AnchorSizeMinor, _activeHold.AnchorPressure,
                    holdTolerance, _reportsPressure))
                {
                    _activeHold.Append(tUs, qx, qy, qMaj, qMin, qPres);
                    return 1;
                }

                CommitActiveHold(holdTolerance, poolOwner);
            }

            if (_candCount == 0)
            {
                EnqueueCandidate(new QuantizedTouchPoint(tUs, qx, qy, qMaj, qMin, qPres));
                TryPromoteCandidatesToHold(holdRunLength);
                return 1;
            }

            QuantizedTouchPoint anchor = PeekCandidate(0);
            if (WithinTolerance(qx, qy, qMaj, qMin, qPres,
                anchor.X, anchor.Y, anchor.SizeMajor, anchor.SizeMinor, anchor.Pressure,
                holdTolerance, _reportsPressure))
            {
                EnqueueCandidate(new QuantizedTouchPoint(tUs, qx, qy, qMaj, qMin, qPres));
                TryPromoteCandidatesToHold(holdRunLength);
                return 1;
            }

            QuantizedTouchPoint broken = DequeueCandidate();
            EmitFullSample(broken.TimeUs, broken.X, broken.Y, broken.SizeMajor, broken.SizeMinor, broken.Pressure);

            while (_candCount > 0)
            {
                if (AllCandidatesAndIncomingFitAnchor(qx, qy, qMaj, qMin, qPres, holdTolerance))
                {
                    break;
                }

                QuantizedTouchPoint emitted = DequeueCandidate();
                EmitFullSample(emitted.TimeUs, emitted.X, emitted.Y, emitted.SizeMajor, emitted.SizeMinor, emitted.Pressure);
            }

            EnqueueCandidate(new QuantizedTouchPoint(tUs, qx, qy, qMaj, qMin, qPres));
            TryPromoteCandidatesToHold(holdRunLength);
            return 1;
        }

        /// <summary>
        /// Finalizes this contact for the current window and returns its <see cref="TouchContact"/>
        /// (or <c>null</c> if zero samples arrived in this window).
        /// If the finger is still down, snapshots <see cref="ContactBefore"/> for the next window.
        /// </summary>
        public TouchContact FlushForWindow(uint holdTolerance, TouchWindowAggregator poolOwner)
        {
            TouchContact result = null;

            if (_activeContact != null && _windowSampleCount > 0)
            {
                FlushPending(holdTolerance, poolOwner);
                _activeContact.End = _end;
                _activeContact.EventsSeen = _eventsSeen;
                result = _activeContact;
                _activeContact = null;
            }
            else if (_activeContact != null)
            {
                poolOwner.RecycleContact(_activeContact);
                _activeContact = null;
            }

            if (_end == TouchEnd.StillDown)
            {
                _continued = true;
                if (_hasRunningBounds)
                {
                    _hasBefore = true;
                    _beforeMinX = _runningMinX;
                    _beforeMaxX = _runningMaxX;
                    _beforeMinY = _runningMinY;
                    _beforeMaxY = _runningMaxY;
                }

                _eventsSeen = 0u;
                _hasWindowPressureDecision = false;
                _hasLastKept = false;
                _lastKeptTimeUs = 0L;
                _hasLastFull = false;
                _lastFullTimeUs = 0L;
                _lastFullX = 0;
                _lastFullY = 0;
                _lastFullSizeMajor = 0;
                _lastFullSizeMinor = 0;
                _lastFullPressure = 0;
                _candHead = 0;
                _candCount = 0;
                _hasActiveHold = false;
                _windowSampleCount = 0;
            }

            return result;
        }

        public void DiscardActiveContact(TouchWindowAggregator poolOwner)
        {
            if (_activeContact != null)
            {
                poolOwner.RecycleContact(_activeContact);
                _activeContact = null;
            }
            ResetAll();
        }

        private void FlushPending(uint holdTolerance, TouchWindowAggregator poolOwner)
        {
            if (_hasActiveHold)
            {
                CommitActiveHold(holdTolerance, poolOwner);
            }

            while (_candCount > 0)
            {
                QuantizedTouchPoint pt = DequeueCandidate();
                EmitFullSample(pt.TimeUs, pt.X, pt.Y, pt.SizeMajor, pt.SizeMinor, pt.Pressure);
            }
        }

        private void EmitFullSample(long timeUs, int qx, int qy, int qMaj, int qMin, int qPres)
        {
            if (!_hasLastFull)
            {
                _activeContact.FirstTimeUs = timeUs;
                _activeContact.X.Add(qx);
                _activeContact.Y.Add(qy);
                _activeContact.SizeMajor.Add(qMaj);
                _activeContact.SizeMinor.Add(qMin);
                if (_reportsPressure)
                {
                    _activeContact.Pressure.Add(qPres);
                }

                _hasLastFull = true;
                _lastFullTimeUs = timeUs;
                _lastFullX = qx;
                _lastFullY = qy;
                _lastFullSizeMajor = qMaj;
                _lastFullSizeMinor = qMin;
                _lastFullPressure = qPres;
                return;
            }

            long dt = timeUs - _lastFullTimeUs;
            if (dt < 0L) dt = 0L;
            if (dt > int.MaxValue) dt = int.MaxValue;

            _activeContact.TimeStepsUs.Add((int)dt);
            _activeContact.X.Add(ClampDelta(qx, _lastFullX));
            _activeContact.Y.Add(ClampDelta(qy, _lastFullY));
            _activeContact.SizeMajor.Add(ClampDelta(qMaj, _lastFullSizeMajor));
            _activeContact.SizeMinor.Add(ClampDelta(qMin, _lastFullSizeMinor));
            if (_reportsPressure)
            {
                _activeContact.Pressure.Add(ClampDelta(qPres, _lastFullPressure));
            }

            _lastFullTimeUs = timeUs;
            _lastFullX = qx;
            _lastFullY = qy;
            _lastFullSizeMajor = qMaj;
            _lastFullSizeMinor = qMin;
            _lastFullPressure = qPres;
        }

        private static int ClampDelta(int cur, int prev)
        {
            long d = (long)cur - prev;
            if (d > int.MaxValue) return int.MaxValue;
            if (d < int.MinValue) return int.MinValue;
            return (int)d;
        }

        private void TryPromoteCandidatesToHold(uint holdRunLength)
        {
            if (holdRunLength == 0u || _candCount < (int)holdRunLength)
            {
                return;
            }

            QuantizedTouchPoint first = DequeueCandidate();
            _activeHold.StartFrom(in first);
            while (_candCount > 0)
            {
                QuantizedTouchPoint next = DequeueCandidate();
                _activeHold.Append(next.TimeUs, next.X, next.Y, next.SizeMajor, next.SizeMinor, next.Pressure);
            }

            _hasActiveHold = true;
        }

        private void CommitActiveHold(uint holdTolerance, TouchWindowAggregator poolOwner)
        {
            TouchHold hold = poolOwner.RentTouchHold();
            hold.StartTimeUs = _activeHold.StartTimeUs;
            hold.EndTimeUs = _activeHold.EndTimeUs;
            hold.SampleCount = _activeHold.SampleCount;
            hold.X = _activeHold.AnchorX;
            hold.Y = _activeHold.AnchorY;
            hold.SizeMajor = _activeHold.AnchorSizeMajor;
            hold.SizeMinor = _activeHold.AnchorSizeMinor;
            hold.Pressure = _reportsPressure ? _activeHold.AnchorPressure : 0;

            if (holdTolerance > 0u)
            {
                hold.Stats.Add(_activeHold.StatsX.ToHoldStats());
                hold.Stats.Add(_activeHold.StatsY.ToHoldStats());
                hold.Stats.Add(_activeHold.StatsSizeMajor.ToHoldStats());
                hold.Stats.Add(_activeHold.StatsSizeMinor.ToHoldStats());
                if (_reportsPressure)
                {
                    hold.Stats.Add(_activeHold.StatsPressure.ToHoldStats());
                }
            }

            _activeContact.Holds.Add(hold);
            _hasActiveHold = false;
        }

        private bool AllCandidatesAndIncomingFitAnchor(
            int qx, int qy, int qMaj, int qMin, int qPres, uint holdTolerance)
        {
            QuantizedTouchPoint anchor = PeekCandidate(0);
            if (!WithinTolerance(qx, qy, qMaj, qMin, qPres,
                anchor.X, anchor.Y, anchor.SizeMajor, anchor.SizeMinor, anchor.Pressure,
                holdTolerance, _reportsPressure))
            {
                return false;
            }

            for (int i = 1; i < _candCount; i++)
            {
                QuantizedTouchPoint item = PeekCandidate(i);
                if (!WithinTolerance(item.X, item.Y, item.SizeMajor, item.SizeMinor, item.Pressure,
                    anchor.X, anchor.Y, anchor.SizeMajor, anchor.SizeMinor, anchor.Pressure,
                    holdTolerance, _reportsPressure))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool WithinTolerance(
            int x, int y, int maj, int min, int pres,
            int ax, int ay, int amaj, int amin, int apres,
            uint tolerance,
            bool checkPressure)
        {
            long tol = tolerance;
            if (AbsDiff(x, ax) > tol) return false;
            if (AbsDiff(y, ay) > tol) return false;
            if (AbsDiff(maj, amaj) > tol) return false;
            if (AbsDiff(min, amin) > tol) return false;
            if (checkPressure && AbsDiff(pres, apres) > tol) return false;
            return true;
        }

        private static long AbsDiff(int a, int b)
        {
            long d = (long)a - b;
            return d < 0L ? -d : d;
        }

        private void EnsureCandidateCapacity(uint holdRunLength)
        {
            int needed = (int)Math.Min(Math.Max((uint)DefaultCandidateCapacity, holdRunLength), 4096u);
            if (_candidates.Length < needed)
            {
                _candidates = new QuantizedTouchPoint[needed];
            }
        }

        private void EnqueueCandidate(in QuantizedTouchPoint point)
        {
            if (_candCount == _candidates.Length)
            {
                QuantizedTouchPoint[] grown = new QuantizedTouchPoint[_candidates.Length * 2];
                for (int i = 0; i < _candCount; i++)
                {
                    grown[i] = _candidates[(_candHead + i) % _candidates.Length];
                }
                _candidates = grown;
                _candHead = 0;
            }

            int tail = (_candHead + _candCount) % _candidates.Length;
            _candidates[tail] = point;
            _candCount++;
        }

        private QuantizedTouchPoint PeekCandidate(int offset)
        {
            return _candidates[(_candHead + offset) % _candidates.Length];
        }

        private QuantizedTouchPoint DequeueCandidate()
        {
            QuantizedTouchPoint pt = _candidates[_candHead];
            _candHead = (_candHead + 1) % _candidates.Length;
            _candCount--;
            if (_candCount == 0)
            {
                _candHead = 0;
            }
            return pt;
        }
    }
}
