using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Aggregates active and completed touch contacts within a batch window, manages cross-window
    /// finger continuation (<see cref="ContactBefore"/>), pools touch contract objects, and tracks
    /// W8-26 bounds (<see cref="ContactCount"/> &lt;= 750, <see cref="TotalTouchSampleCount"/> &lt;= 7500).
    /// </summary>
    public sealed class TouchWindowAggregator : ITouchSampleConsumer
    {
        public const int MaxActiveTouches = 32;

        private const int MaxContactPoolSize = 64;
        private const int MaxHoldPoolSize = 128;
        private const int MaxBeforePoolSize = 32;
        private const int MaxBuilderPoolSize = 32;

        private readonly List<TouchContactBuilder> _activeBuilders = new List<TouchContactBuilder>(10);
        private readonly List<TouchContact> _completedContacts = new List<TouchContact>(16);
        private readonly List<GapRecord> _windowGaps = new List<GapRecord>(8);

        private readonly Stack<TouchContactBuilder> _builderPool = new Stack<TouchContactBuilder>(10);
        private readonly Stack<TouchContact> _contactPool = new Stack<TouchContact>(16);
        private readonly Stack<TouchHold> _holdPool = new Stack<TouchHold>(32);
        private readonly Stack<ContactBefore> _beforePool = new Stack<ContactBefore>(16);

        private TouchMode _touchMode = TouchMode.Unspecified;
        private float _requestedHz;
        private long _minStepUs;
        private uint _holdTolerance;
        private uint _holdRunLength;
        private long _gapThresholdUs;

        private double _positionScale = SampleQuantizer.TouchPositionScale;
        private double _sizeScale = SampleQuantizer.TouchSizeScale;
        private double _pressureScale = SampleQuantizer.TouchPressureScale;

        private int _totalTouchSampleCount;

        public TouchWindowAggregator()
        {
            Configure(SessionSettings.CreateDefaults(), TouchMode.Unspecified);
        }

        public TouchMode TouchMode
        {
            get { return _touchMode; }
            set { _touchMode = value; }
        }

        public int ContactCount
        {
            get
            {
                int count = _completedContacts.Count;
                for (int i = 0; i < _activeBuilders.Count; i++)
                {
                    if (_activeBuilders[i].HasWindowData)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public int TotalTouchSampleCount
        {
            get { return _totalTouchSampleCount; }
        }

        public int ActiveTouchCount
        {
            get { return _activeBuilders.Count; }
        }

        public int GapCount
        {
            get { return _windowGaps.Count; }
        }

        public void Configure(
            SessionSettings settings,
            TouchMode mode,
            double positionScale = SampleQuantizer.TouchPositionScale,
            double sizeScale = SampleQuantizer.TouchSizeScale,
            double pressureScale = SampleQuantizer.TouchPressureScale)
        {
            SessionSettings s = settings ?? SessionSettings.CreateDefaults();
            _touchMode = mode;
            _requestedHz = s.TouchHz;
            _minStepUs = s.TouchHz > 0u ? (long)Math.Round(1000000.0 / s.TouchHz, MidpointRounding.AwayFromZero) : 0L;
            _holdTolerance = s.TouchHoldTolerance;
            _holdRunLength = s.HoldRunLength;
            _gapThresholdUs = SampleQuantizer.ComputeGapThresholdUs(s.GapThresholdMs, s.GapThresholdPeriods, _requestedHz);

            _positionScale = positionScale > 0d ? positionScale : SampleQuantizer.TouchPositionScale;
            _sizeScale = sizeScale > 0d ? sizeScale : SampleQuantizer.TouchSizeScale;
            _pressureScale = pressureScale > 0d ? pressureScale : SampleQuantizer.TouchPressureScale;
        }

        public void OnTouchSample(in RawTouchSample sample)
        {
            int idx = FindActiveBuilderIndex(sample.FingerId);
            TouchContactBuilder builder;

            if (idx >= 0)
            {
                builder = _activeBuilders[idx];
                // If a new Began arrives for a fingerId that was already active (missed lift in this
                // or a previous window), always finalize/discard the old contact and start fresh.
                if (sample.Phase == TouchPhaseKind.Began)
                {
                    if (builder.HasWindowData)
                    {
                        TouchContact oldContact = builder.FlushForWindow(_holdTolerance, this);
                        if (oldContact != null)
                        {
                            _completedContacts.Add(oldContact);
                        }
                    }
                    else
                    {
                        builder.DiscardActiveContact(this);
                    }

                    builder.BeginNewContact(sample.FingerId, sample.TimeUs, _holdRunLength);
                }
            }
            else
            {
                if (_activeBuilders.Count >= MaxActiveTouches)
                {
                    TouchContactBuilder evicted = _activeBuilders[0];
                    _activeBuilders.RemoveAt(0);
                    if (evicted.HasWindowData)
                    {
                        TouchContact evictedContact = evicted.FlushForWindow(_holdTolerance, this);
                        if (evictedContact != null)
                        {
                            _completedContacts.Add(evictedContact);
                        }
                    }
                    else
                    {
                        evicted.DiscardActiveContact(this);
                    }
                    ReturnBuilder(evicted);
                }

                builder = RentBuilder();
                builder.BeginNewContact(sample.FingerId, sample.TimeUs, _holdRunLength);
                _activeBuilders.Add(builder);
                idx = _activeBuilders.Count - 1;
            }

            int added = builder.AddSample(
                in sample,
                _positionScale,
                _sizeScale,
                _pressureScale,
                _minStepUs,
                _holdTolerance,
                _holdRunLength,
                _gapThresholdUs,
                this,
                _windowGaps);

            _totalTouchSampleCount += added;

            if (builder.IsFinished)
            {
                TouchContact finished = builder.FlushForWindow(_holdTolerance, this);
                if (finished != null)
                {
                    _completedContacts.Add(finished);
                }

                _activeBuilders.RemoveAt(idx);
                ReturnBuilder(builder);
            }
        }

        /// <summary>
        /// Flushes completed and still-down contacts into <paramref name="targetTouch"/>.
        /// Returns <c>true</c> if at least one contact was emitted; still-down fingers are retained
        /// with <see cref="ContactBefore"/> snapshots for the next window.
        /// </summary>
        public bool FlushWindowTo(Touch targetTouch, List<GapRecord> gapsTarget = null)
        {
            if (gapsTarget != null)
            {
                for (int i = 0; i < _windowGaps.Count; i++)
                {
                    gapsTarget.Add(_windowGaps[i]);
                }
            }
            _windowGaps.Clear();

            int emittedCount = 0;

            if (targetTouch != null)
            {
                targetTouch.Mode = _touchMode;
                targetTouch.RequestedHz = _requestedHz;
                targetTouch.PositionScale = _positionScale;
                targetTouch.SizeScale = _sizeScale;
                targetTouch.PressureScale = _pressureScale;
            }

            for (int i = 0; i < _completedContacts.Count; i++)
            {
                TouchContact c = _completedContacts[i];
                if (targetTouch != null)
                {
                    targetTouch.Contacts.Add(c);
                    emittedCount++;
                }
                else
                {
                    RecycleContact(c);
                }
            }
            _completedContacts.Clear();

            for (int i = 0; i < _activeBuilders.Count; i++)
            {
                TouchContactBuilder builder = _activeBuilders[i];
                TouchContact stillDown = builder.FlushForWindow(_holdTolerance, this);
                if (stillDown != null)
                {
                    if (targetTouch != null)
                    {
                        targetTouch.Contacts.Add(stillDown);
                        emittedCount++;
                    }
                    else
                    {
                        RecycleContact(stillDown);
                    }
                }
            }

            _totalTouchSampleCount = 0;
            return emittedCount > 0;
        }

        /// <summary>
        /// Finalizes any in-window active touches into completed contacts and clears all active
        /// finger trackers (e.g., when touch capture is paused or suppressed).
        /// </summary>
        public void CancelActiveTouches()
        {
            for (int i = 0; i < _activeBuilders.Count; i++)
            {
                TouchContactBuilder builder = _activeBuilders[i];
                if (builder.HasWindowData)
                {
                    TouchContact flushed = builder.FlushForWindow(_holdTolerance, this);
                    if (flushed != null)
                    {
                        _completedContacts.Add(flushed);
                    }
                }
                else
                {
                    builder.DiscardActiveContact(this);
                }
                ReturnBuilder(builder);
            }

            _activeBuilders.Clear();
        }

        public void ResetSession()
        {
            for (int i = 0; i < _activeBuilders.Count; i++)
            {
                _activeBuilders[i].DiscardActiveContact(this);
                ReturnBuilder(_activeBuilders[i]);
            }
            _activeBuilders.Clear();

            for (int i = 0; i < _completedContacts.Count; i++)
            {
                RecycleContact(_completedContacts[i]);
            }
            _completedContacts.Clear();
            _windowGaps.Clear();
            _totalTouchSampleCount = 0;
        }

        public void RecycleTouch(Touch touch)
        {
            if (touch == null)
            {
                return;
            }

            for (int i = 0; i < touch.Contacts.Count; i++)
            {
                RecycleContact(touch.Contacts[i]);
            }
            touch.Clear();
        }

        internal TouchContact RentContact()
        {
            return _contactPool.Count > 0 ? _contactPool.Pop() : new TouchContact();
        }

        internal TouchHold RentTouchHold()
        {
            return _holdPool.Count > 0 ? _holdPool.Pop() : new TouchHold();
        }

        internal ContactBefore RentContactBefore()
        {
            return _beforePool.Count > 0 ? _beforePool.Pop() : new ContactBefore();
        }

        internal void RecycleContact(TouchContact contact)
        {
            if (contact == null)
            {
                return;
            }

            for (int i = 0; i < contact.Holds.Count; i++)
            {
                TouchHold hold = contact.Holds[i];
                if (hold != null && _holdPool.Count < MaxHoldPoolSize)
                {
                    hold.Clear();
                    _holdPool.Push(hold);
                }
            }

            if (contact.Before != null && _beforePool.Count < MaxBeforePoolSize)
            {
                contact.Before.Clear();
                _beforePool.Push(contact.Before);
            }

            contact.Clear();
            if (_contactPool.Count < MaxContactPoolSize)
            {
                _contactPool.Push(contact);
            }
        }

        private int FindActiveBuilderIndex(uint fingerId)
        {
            for (int i = 0; i < _activeBuilders.Count; i++)
            {
                if (_activeBuilders[i].FingerId == fingerId)
                {
                    return i;
                }
            }
            return -1;
        }

        private TouchContactBuilder RentBuilder()
        {
            return _builderPool.Count > 0 ? _builderPool.Pop() : new TouchContactBuilder();
        }

        private void ReturnBuilder(TouchContactBuilder builder)
        {
            builder.ResetAll();
            if (_builderPool.Count < MaxBuilderPoolSize)
            {
                _builderPool.Push(builder);
            }
        }
    }
}
