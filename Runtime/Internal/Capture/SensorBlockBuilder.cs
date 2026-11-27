using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Zero-GC per-sensor stream encoder that converts raw motion samples into delta-encoded
    /// <see cref="SensorBlock"/>s with run-length <see cref="Hold"/> folding, gap detection,
    /// source-change splitting, and <c>int32</c>-overflow block splitting.
    /// </summary>
    public sealed class SensorBlockBuilder
    {
        private const int DefaultCandidateCapacity = 16;
        private const int MaxBlockPoolSize = 32;
        private const int MaxHoldPoolSize = 128;

        private struct QuantizedSample
        {
            public long TimeUs;
            public int X;
            public int Y;
            public int Z;

            public QuantizedSample(long timeUs, int x, int y, int z)
            {
                TimeUs = timeUs;
                X = x;
                Y = y;
                Z = z;
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

        private struct ActiveHoldState
        {
            public long StartTimeUs;
            public long EndTimeUs;
            public uint SampleCount;
            public int AnchorX;
            public int AnchorY;
            public int AnchorZ;
            public AxisAccumulator StatsX;
            public AxisAccumulator StatsY;
            public AxisAccumulator StatsZ;

            public void StartFrom(in QuantizedSample first)
            {
                StartTimeUs = first.TimeUs;
                EndTimeUs = first.TimeUs;
                SampleCount = 1u;
                AnchorX = first.X;
                AnchorY = first.Y;
                AnchorZ = first.Z;
                StatsX.Init(first.X);
                StatsY.Init(first.Y);
                StatsZ.Init(first.Z);
            }

            public void Append(long timeUs, int x, int y, int z)
            {
                EndTimeUs = timeUs;
                SampleCount++;
                StatsX.Add(x);
                StatsY.Add(y);
                StatsZ.Add(z);
            }
        }

        private readonly Sensor _sensor;
        private readonly GapSignal _gapSignal;

        private double _scale;
        private float _requestedHz;
        private uint _holdTolerance;
        private uint _holdRunLength;
        private long _gapThresholdUs;

        private readonly List<SensorBlock> _completedBlocks = new List<SensorBlock>(4);
        private readonly Stack<SensorBlock> _blockPool = new Stack<SensorBlock>(8);
        private readonly Stack<Hold> _holdPool = new Stack<Hold>(16);

        private SensorBlock _currentBlock;
        private bool _hasActiveBlock;
        private CaptureSource _blockSource;
        private FallbackReason _blockFallback;
        private Clock _blockClock;
        private ValueUnit _blockUnit;

        private bool _hasPrevAnySample;
        private long _prevAnySampleTimeUs;

        private bool _hasLastFull;
        private long _lastFullTimeUs;
        private int _lastFullX;
        private int _lastFullY;
        private int _lastFullZ;

        private QuantizedSample[] _candidates = new QuantizedSample[DefaultCandidateCapacity];
        private int _candHead;
        private int _candCount;

        private bool _hasActiveHold;
        private ActiveHoldState _activeHold;

        private int _windowSampleCount;

        public SensorBlockBuilder(Sensor sensor)
        {
            _sensor = sensor;
            _gapSignal = SampleQuantizer.ToGapSignal(sensor);
            _scale = SampleQuantizer.GetDefaultMotionScale(sensor);
        }

        public Sensor Sensor
        {
            get { return _sensor; }
        }

        public float RequestedHz
        {
            get { return _requestedHz; }
        }

        public int WindowSampleCount
        {
            get { return _windowSampleCount; }
        }

        public void Configure(
            double scale,
            float requestedHz,
            uint holdTolerance,
            uint holdRunLength,
            uint gapThresholdMs,
            uint gapThresholdPeriods)
        {
            _scale = scale > 0d ? scale : SampleQuantizer.GetDefaultMotionScale(_sensor);
            _requestedHz = requestedHz > 0f ? requestedHz : 0f;
            _holdTolerance = holdTolerance;
            _holdRunLength = holdRunLength;
            _gapThresholdUs = SampleQuantizer.ComputeGapThresholdUs(gapThresholdMs, gapThresholdPeriods, _requestedHz);

            int neededCap = (int)Math.Min(Math.Max((uint)DefaultCandidateCapacity, holdRunLength), 4096u);
            if (_candidates.Length < neededCap)
            {
                _candidates = new QuantizedSample[neededCap];
            }
        }

        public void ResetWindow()
        {
            if (_currentBlock != null)
            {
                ReturnBlockToPool(_currentBlock);
                _currentBlock = null;
            }

            for (int i = 0; i < _completedBlocks.Count; i++)
            {
                ReturnBlockToPool(_completedBlocks[i]);
            }
            _completedBlocks.Clear();

            _hasActiveBlock = false;
            _hasPrevAnySample = false;
            _prevAnySampleTimeUs = 0L;
            _hasLastFull = false;
            _lastFullTimeUs = 0L;
            _lastFullX = 0;
            _lastFullY = 0;
            _lastFullZ = 0;
            _candHead = 0;
            _candCount = 0;
            _hasActiveHold = false;
            _windowSampleCount = 0;
        }

        public void AddSample(in RawMotionSample sample, List<GapRecord> gapsTarget)
        {
            int qx = SampleQuantizer.Quantize(sample.X, _scale);
            int qy = SampleQuantizer.Quantize(sample.Y, _scale);
            int qz = SampleQuantizer.Quantize(sample.Z, _scale);
            AddQuantizedSample(sample.Source, sample.FallbackReason, sample.Clock, sample.Unit, sample.TimeUs, qx, qy, qz, gapsTarget);
        }

        public void AddQuantizedSample(
            CaptureSource source,
            FallbackReason fallbackReason,
            Clock clock,
            ValueUnit unit,
            long timeUs,
            int qx,
            int qy,
            int qz,
            List<GapRecord> gapsTarget)
        {
            if (_hasActiveBlock &&
                (source != _blockSource || fallbackReason != _blockFallback || clock != _blockClock || unit != _blockUnit))
            {
                FinalizeCurrentBlock();
            }

            if (!_hasActiveBlock)
            {
                StartNewBlock(source, fallbackReason, clock, unit);
            }

            long tUs = timeUs;
            if (_hasPrevAnySample)
            {
                if (tUs < _prevAnySampleTimeUs)
                {
                    tUs = _prevAnySampleTimeUs;
                }

                long dtUs = tUs - _prevAnySampleTimeUs;
                uint clampedDt = dtUs >= uint.MaxValue ? uint.MaxValue : (uint)dtUs;
                if (clampedDt > _currentBlock.LongestGapUs)
                {
                    _currentBlock.LongestGapUs = clampedDt;
                }

                if (_gapThresholdUs > 0L && dtUs > _gapThresholdUs)
                {
                    // Close active hold or flush partial candidates BEFORE the gap so no hold spans a gap.
                    FlushPendingHoldAndCandidates();

                    if (gapsTarget != null)
                    {
                        gapsTarget.Add(new GapRecord
                        {
                            Signal = _gapSignal,
                            Clock = _blockClock,
                            StartTimeUs = _prevAnySampleTimeUs,
                            EndTimeUs = tUs,
                            Cause = GapCause.SensorSilent
                        });
                    }
                }
            }

            _hasPrevAnySample = true;
            _prevAnySampleTimeUs = tUs;
            _windowSampleCount++;

            if (_holdRunLength == 0u)
            {
                EmitFullSample(tUs, qx, qy, qz);
                return;
            }

            if (_hasActiveHold)
            {
                if (WithinTolerance(qx, qy, qz, _activeHold.AnchorX, _activeHold.AnchorY, _activeHold.AnchorZ, _holdTolerance))
                {
                    _activeHold.Append(tUs, qx, qy, qz);
                    return;
                }

                CommitActiveHold();
            }

            if (_candCount == 0)
            {
                EnqueueCandidate(new QuantizedSample(tUs, qx, qy, qz));
                TryPromoteCandidatesToHold();
                return;
            }

            QuantizedSample anchor = PeekCandidate(0);
            if (WithinTolerance(qx, qy, qz, anchor.X, anchor.Y, anchor.Z, _holdTolerance))
            {
                EnqueueCandidate(new QuantizedSample(tUs, qx, qy, qz));
                TryPromoteCandidatesToHold();
                return;
            }

            // Sample broke partial candidate run: emit anchor, shift forward until remaining + incoming fit new anchor.
            QuantizedSample brokenAnchor = DequeueCandidate();
            EmitFullSample(brokenAnchor.TimeUs, brokenAnchor.X, brokenAnchor.Y, brokenAnchor.Z);

            while (_candCount > 0)
            {
                if (AllCandidatesAndIncomingFitAnchor(qx, qy, qz))
                {
                    break;
                }

                QuantizedSample emitted = DequeueCandidate();
                EmitFullSample(emitted.TimeUs, emitted.X, emitted.Y, emitted.Z);
            }

            EnqueueCandidate(new QuantizedSample(tUs, qx, qy, qz));
            TryPromoteCandidatesToHold();
        }

        /// <summary>
        /// Flushes any active hold, partial candidate buffer, and completed blocks into
        /// <paramref name="targetBlocks"/> and resets window state.
        /// </summary>
        public void FlushTo(List<SensorBlock> targetBlocks)
        {
            if (_hasActiveBlock)
            {
                FinalizeCurrentBlock();
            }

            if (targetBlocks != null)
            {
                for (int i = 0; i < _completedBlocks.Count; i++)
                {
                    SensorBlock block = _completedBlocks[i];
                    if (block.SampleCount > 0u)
                    {
                        targetBlocks.Add(block);
                    }
                    else
                    {
                        ReturnBlockToPool(block);
                    }
                }
            }
            else
            {
                for (int i = 0; i < _completedBlocks.Count; i++)
                {
                    ReturnBlockToPool(_completedBlocks[i]);
                }
            }

            _completedBlocks.Clear();
            _windowSampleCount = 0;
        }

        public void RecycleBlock(SensorBlock block)
        {
            if (block == null)
            {
                return;
            }

            ReturnBlockToPool(block);
        }

        private void StartNewBlock(CaptureSource source, FallbackReason fallbackReason, Clock clock, ValueUnit unit)
        {
            SensorBlock block = RentBlock();
            block.Sensor = _sensor;
            block.Source = source;
            block.FallbackReason = fallbackReason;
            block.Clock = clock;
            block.Unit = unit;
            block.Scale = _scale;
            block.RequestedHz = _requestedHz;

            _currentBlock = block;
            _hasActiveBlock = true;
            _blockSource = source;
            _blockFallback = fallbackReason;
            _blockClock = clock;
            _blockUnit = unit;

            _hasPrevAnySample = false;
            _prevAnySampleTimeUs = 0L;
            _hasLastFull = false;
            _lastFullTimeUs = 0L;
            _lastFullX = 0;
            _lastFullY = 0;
            _lastFullZ = 0;
            _candHead = 0;
            _candCount = 0;
            _hasActiveHold = false;
        }

        private void FinalizeCurrentBlock()
        {
            FlushPendingHoldAndCandidates();

            if (_currentBlock != null)
            {
                if (_currentBlock.SampleCount > 0u)
                {
                    _completedBlocks.Add(_currentBlock);
                }
                else
                {
                    ReturnBlockToPool(_currentBlock);
                }

                _currentBlock = null;
            }

            _hasActiveBlock = false;
            _hasPrevAnySample = false;
            _hasLastFull = false;
        }

        private void FlushPendingHoldAndCandidates()
        {
            if (_hasActiveHold)
            {
                CommitActiveHold();
            }

            while (_candCount > 0)
            {
                QuantizedSample s = DequeueCandidate();
                EmitFullSample(s.TimeUs, s.X, s.Y, s.Z);
            }
        }

        private void EmitFullSample(long timeUs, int qx, int qy, int qz)
        {
            if (!_hasLastFull)
            {
                _currentBlock.FirstTimeUs = timeUs;
                _currentBlock.X.Add(qx);
                _currentBlock.Y.Add(qy);
                _currentBlock.Z.Add(qz);
                _currentBlock.SampleCount++;

                _hasLastFull = true;
                _lastFullTimeUs = timeUs;
                _lastFullX = qx;
                _lastFullY = qy;
                _lastFullZ = qz;
                return;
            }

            long dt = timeUs - _lastFullTimeUs;
            if (dt < 0L)
            {
                dt = 0L;
            }

            long dx = (long)qx - _lastFullX;
            long dy = (long)qy - _lastFullY;
            long dz = (long)qz - _lastFullZ;

            if (dt > int.MaxValue ||
                dx < int.MinValue || dx > int.MaxValue ||
                dy < int.MinValue || dy > int.MaxValue ||
                dz < int.MinValue || dz > int.MaxValue)
            {
                // Split cleanly into a new SensorBlock with the same identity to prevent int32 overflow.
                CaptureSource src = _blockSource;
                FallbackReason fb = _blockFallback;
                Clock clk = _blockClock;
                ValueUnit unit = _blockUnit;

                if (_currentBlock.SampleCount > 0u)
                {
                    _completedBlocks.Add(_currentBlock);
                }
                else
                {
                    ReturnBlockToPool(_currentBlock);
                }

                _currentBlock = RentBlock();
                _currentBlock.Sensor = _sensor;
                _currentBlock.Source = src;
                _currentBlock.FallbackReason = fb;
                _currentBlock.Clock = clk;
                _currentBlock.Unit = unit;
                _currentBlock.Scale = _scale;
                _currentBlock.RequestedHz = _requestedHz;

                _currentBlock.FirstTimeUs = timeUs;
                _currentBlock.X.Add(qx);
                _currentBlock.Y.Add(qy);
                _currentBlock.Z.Add(qz);
                _currentBlock.SampleCount = 1u;

                _hasLastFull = true;
                _lastFullTimeUs = timeUs;
                _lastFullX = qx;
                _lastFullY = qy;
                _lastFullZ = qz;
                return;
            }

            _currentBlock.TimeStepsUs.Add((int)dt);
            _currentBlock.X.Add((int)dx);
            _currentBlock.Y.Add((int)dy);
            _currentBlock.Z.Add((int)dz);
            _currentBlock.SampleCount++;

            _lastFullTimeUs = timeUs;
            _lastFullX = qx;
            _lastFullY = qy;
            _lastFullZ = qz;
        }

        private void TryPromoteCandidatesToHold()
        {
            if (_holdRunLength == 0u || _candCount < (int)_holdRunLength)
            {
                return;
            }

            QuantizedSample first = DequeueCandidate();
            _activeHold.StartFrom(in first);
            while (_candCount > 0)
            {
                QuantizedSample next = DequeueCandidate();
                _activeHold.Append(next.TimeUs, next.X, next.Y, next.Z);
            }

            _hasActiveHold = true;
        }

        private void CommitActiveHold()
        {
            Hold hold = RentHold();
            hold.StartTimeUs = _activeHold.StartTimeUs;
            hold.EndTimeUs = _activeHold.EndTimeUs;
            hold.SampleCount = _activeHold.SampleCount;
            hold.X = _activeHold.AnchorX;
            hold.Y = _activeHold.AnchorY;
            hold.Z = _activeHold.AnchorZ;

            if (_holdTolerance > 0u)
            {
                hold.Stats.Add(_activeHold.StatsX.ToHoldStats());
                hold.Stats.Add(_activeHold.StatsY.ToHoldStats());
                hold.Stats.Add(_activeHold.StatsZ.ToHoldStats());
            }

            _currentBlock.Holds.Add(hold);
            _currentBlock.SampleCount += hold.SampleCount;
            _hasActiveHold = false;
        }

        private bool AllCandidatesAndIncomingFitAnchor(int incomingX, int incomingY, int incomingZ)
        {
            QuantizedSample anchor = PeekCandidate(0);
            if (!WithinTolerance(incomingX, incomingY, incomingZ, anchor.X, anchor.Y, anchor.Z, _holdTolerance))
            {
                return false;
            }

            for (int i = 1; i < _candCount; i++)
            {
                QuantizedSample item = PeekCandidate(i);
                if (!WithinTolerance(item.X, item.Y, item.Z, anchor.X, anchor.Y, anchor.Z, _holdTolerance))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool WithinTolerance(int x, int y, int z, int anchorX, int anchorY, int anchorZ, uint tolerance)
        {
            long tol = tolerance;
            long dx = (long)x - anchorX;
            if (dx < 0L) dx = -dx;
            if (dx > tol) return false;

            long dy = (long)y - anchorY;
            if (dy < 0L) dy = -dy;
            if (dy > tol) return false;

            long dz = (long)z - anchorZ;
            if (dz < 0L) dz = -dz;
            return dz <= tol;
        }

        private void EnqueueCandidate(in QuantizedSample sample)
        {
            if (_candCount == _candidates.Length)
            {
                QuantizedSample[] grown = new QuantizedSample[_candidates.Length * 2];
                for (int i = 0; i < _candCount; i++)
                {
                    grown[i] = _candidates[(_candHead + i) % _candidates.Length];
                }
                _candidates = grown;
                _candHead = 0;
            }

            int tail = (_candHead + _candCount) % _candidates.Length;
            _candidates[tail] = sample;
            _candCount++;
        }

        private QuantizedSample PeekCandidate(int offset)
        {
            return _candidates[(_candHead + offset) % _candidates.Length];
        }

        private QuantizedSample DequeueCandidate()
        {
            QuantizedSample sample = _candidates[_candHead];
            _candHead = (_candHead + 1) % _candidates.Length;
            _candCount--;
            if (_candCount == 0)
            {
                _candHead = 0;
            }
            return sample;
        }

        private SensorBlock RentBlock()
        {
            return _blockPool.Count > 0 ? _blockPool.Pop() : new SensorBlock();
        }

        private Hold RentHold()
        {
            return _holdPool.Count > 0 ? _holdPool.Pop() : new Hold();
        }

        private void ReturnBlockToPool(SensorBlock block)
        {
            if (block == null)
            {
                return;
            }

            for (int i = 0; i < block.Holds.Count; i++)
            {
                Hold hold = block.Holds[i];
                if (hold != null && _holdPool.Count < MaxHoldPoolSize)
                {
                    hold.Clear();
                    _holdPool.Push(hold);
                }
            }

            block.Clear();
            if (_blockPool.Count < MaxBlockPoolSize)
            {
                _blockPool.Push(block);
            }
        }
    }
}
