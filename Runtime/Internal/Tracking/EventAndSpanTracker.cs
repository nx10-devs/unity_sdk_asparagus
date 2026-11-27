using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;
using Asparagus.Sdk.Internal.Time;

namespace Asparagus.Sdk.Internal.Tracking
{
    /// <summary>
    /// Tracks game events, session span state (Rules 1-11 &amp; 14), frozen window-open
    /// <see cref="Carried.OpenSpans"/> and <see cref="Carried.LastEventTimeUs"/> snapshots,
    /// and pooled <see cref="GameEvent"/> records.
    /// </summary>
    public sealed class EventAndSpanTracker
    {
        private const int MaxPoolSize = 128;

        private struct TrackedSpan
        {
            public string Name;
            public uint Number;
            public long OpenedTimeUs;
            public bool OpenedByCore;

            public TrackedSpan(string name, uint number, long openedTimeUs, bool openedByCore)
            {
                Name = name;
                Number = number;
                OpenedTimeUs = openedTimeUs;
                OpenedByCore = openedByCore;
            }
        }

        private readonly WindowCallOrder _callOrder;

        // Session-scoped span state (persists across pause/resume/StopTelemetry per Rule 11)
        private readonly List<TrackedSpan> _openSpans = new List<TrackedSpan>(8);
        private uint _nextSpanNumber = 1u;
        private bool _hasLatestEventTimeUs;
        private long _latestEventTimeUs;

        // Frozen window-open snapshot for Carried
        private readonly List<OpenSpan> _windowOpenSpans = new List<OpenSpan>(8);
        private bool _windowOpenHasLastEventTimeUs;
        private long _windowOpenLastEventTimeUs;

        // Active window events + pool
        private readonly List<GameEvent> _windowEvents = new List<GameEvent>(32);
        private readonly Stack<GameEvent> _eventPool = new Stack<GameEvent>(32);

        public EventAndSpanTracker(WindowCallOrder callOrder)
        {
            _callOrder = callOrder ?? new WindowCallOrder();
        }

        public int CurrentWindowEventCount
        {
            get { return _windowEvents.Count; }
        }

        public IReadOnlyList<GameEvent> WindowEvents
        {
            get { return _windowEvents; }
        }

        public int OpenSpanCount
        {
            get { return _openSpans.Count; }
        }

        public void ResetSession()
        {
            _openSpans.Clear();
            _nextSpanNumber = 1u;
            _hasLatestEventTimeUs = false;
            _latestEventTimeUs = 0L;

            _windowOpenSpans.Clear();
            _windowOpenHasLastEventTimeUs = false;
            _windowOpenLastEventTimeUs = 0L;

            RecycleWindowEvents();
        }

        /// <summary>
        /// Records a game event applying all phone-side contract rules (Rules 1-11).
        /// Returns the recorded <see cref="GameEvent"/> or <c>null</c> if <paramref name="eventName"/> is null/empty.
        /// </summary>
        public GameEvent RecordEvent(
            string eventName,
            IReadOnlyDictionary<string, Value> fields,
            bool hasOutcome,
            string outcome,
            string spanOverride,
            long unityTimeUs)
        {
            try
            {
                if (string.IsNullOrEmpty(eventName))
                {
                    return null;
                }

                bool isCore = CoreEventNames.TryGetMetadata(eventName, out CoreEventMetadata meta);
                CoreEventRole role = isCore ? meta.Role : CoreEventRole.None;

                // Rule 5: Resolve span name (caller override wins; otherwise core open/close default)
                string resolvedSpan;
                if (!string.IsNullOrEmpty(spanOverride))
                {
                    resolvedSpan = spanOverride;
                }
                else if (isCore && (role == CoreEventRole.Opens || role == CoreEventRole.Closes))
                {
                    if (meta.DefaultSpan == CoreSpans.Ad)
                    {
                        string adType = null;
                        if (fields != null && fields.TryGetValue("type", out Value typeVal) && typeVal.Kind == ValueKind.Text)
                        {
                            adType = typeVal.TextValue;
                        }
                        resolvedSpan = CoreSpans.AdForType(adType);
                    }
                    else
                    {
                        resolvedSpan = meta.DefaultSpan ?? string.Empty;
                    }
                }
                else
                {
                    resolvedSpan = string.Empty;
                }

                // Rules 6, 7, 8, 9, 10: Compute span marker & number and update open spans
                SpanMarker marker = SpanMarker.Unspecified;
                uint spanNumber = 0u;

                if (!string.IsNullOrEmpty(resolvedSpan))
                {
                    int existingIndex = FindOpenSpanIndex(resolvedSpan);

                    if (role == CoreEventRole.Opens)
                    {
                        // Rule 7 & 9: Replace any open span of the same name ("no end seen") and open a new one
                        if (existingIndex >= 0)
                        {
                            _openSpans.RemoveAt(existingIndex);
                        }

                        spanNumber = _nextSpanNumber++;
                        marker = SpanMarker.Open;
                        _openSpans.Add(new TrackedSpan(resolvedSpan, spanNumber, unityTimeUs, true));
                    }
                    else if (role == CoreEventRole.Closes)
                    {
                        // Rule 8: Close matching open span, or emit Close with spanNumber = 0 if none open
                        marker = SpanMarker.Close;
                        if (existingIndex >= 0)
                        {
                            spanNumber = _openSpans[existingIndex].Number;
                            _openSpans.RemoveAt(existingIndex);
                        }
                        else
                        {
                            spanNumber = 0u;
                        }
                    }
                    else
                    {
                        // Rule 6: Event with no role of its own
                        if (existingIndex < 0)
                        {
                            spanNumber = _nextSpanNumber++;
                            marker = SpanMarker.Open;
                            _openSpans.Add(new TrackedSpan(resolvedSpan, spanNumber, unityTimeUs, false));
                        }
                        else if (_openSpans[existingIndex].OpenedByCore)
                        {
                            // Core-opened span stays open; event carries span name with no marker & number 0
                            marker = SpanMarker.Unspecified;
                            spanNumber = 0u;
                        }
                        else
                        {
                            // Non-core-opened span is closed (toggles OPEN -> CLOSE -> OPEN)
                            spanNumber = _openSpans[existingIndex].Number;
                            marker = SpanMarker.Close;
                            _openSpans.RemoveAt(existingIndex);
                        }
                    }
                }

                GameEvent evt = RentEvent();
                evt.TimeUs = unityTimeUs;
                evt.Name = eventName;

                if (fields != null && fields.Count > 0)
                {
                    if (fields is Dictionary<string, Value> concreteDict)
                    {
                        foreach (KeyValuePair<string, Value> kv in concreteDict)
                        {
                            if (!string.IsNullOrEmpty(kv.Key))
                            {
                                evt.Fields[kv.Key] = kv.Value;
                            }
                        }
                    }
                    else
                    {
                        foreach (KeyValuePair<string, Value> kv in fields)
                        {
                            if (!string.IsNullOrEmpty(kv.Key))
                            {
                                evt.Fields[kv.Key] = kv.Value;
                            }
                        }
                    }
                }

                if (hasOutcome)
                {
                    evt.SetOutcome(outcome);
                }

                evt.SpanName = resolvedSpan;
                evt.SpanMarker = marker;
                evt.SpanNumber = spanNumber;
                evt.Order = _callOrder.NextOrder();

                _hasLatestEventTimeUs = true;
                _latestEventTimeUs = unityTimeUs;

                _windowEvents.Add(evt);
                return evt;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Writes frozen window-open <see cref="Carried.OpenSpans"/> (always non-null per Rule 14)
        /// and optional <see cref="Carried.LastEventTimeUs"/> into <paramref name="carried"/>.
        /// </summary>
        public void WriteCarriedSpansAndLastEventTo(Carried carried)
        {
            if (carried == null)
            {
                return;
            }

            if (carried.OpenSpans == null)
            {
                carried.OpenSpans = new OpenSpans();
            }
            else
            {
                carried.OpenSpans.Clear();
            }

            for (int i = 0; i < _windowOpenSpans.Count; i++)
            {
                carried.OpenSpans.Spans.Add(_windowOpenSpans[i]);
            }

            if (_windowOpenHasLastEventTimeUs)
            {
                carried.SetLastEventTimeUs(_windowOpenLastEventTimeUs);
            }
            else
            {
                carried.ClearLastEventTimeUs();
            }
        }

        /// <summary>
        /// Writes window-open carried spans/last-event-time and transfers all active window
        /// <see cref="GameEvent"/> instances into <paramref name="targetBatch"/>.
        /// </summary>
        public void FlushWindowTo(Batch targetBatch)
        {
            if (targetBatch == null)
            {
                return;
            }

            if (targetBatch.Carried == null)
            {
                targetBatch.Carried = new Carried();
            }

            WriteCarriedSpansAndLastEventTo(targetBatch.Carried);

            for (int i = 0; i < _windowEvents.Count; i++)
            {
                targetBatch.Events.Add(_windowEvents[i]);
            }

            _windowEvents.Clear();
        }

        /// <summary>
        /// Promotes current open spans and latest event timestamp into the next window's open snapshot
        /// and recycles any un-flushed window events.
        /// </summary>
        public void AdvanceWindow()
        {
            _windowOpenSpans.Clear();
            for (int i = 0; i < _openSpans.Count; i++)
            {
                TrackedSpan ts = _openSpans[i];
                _windowOpenSpans.Add(new OpenSpan(ts.Name, ts.Number, ts.OpenedTimeUs));
            }

            _windowOpenHasLastEventTimeUs = _hasLatestEventTimeUs;
            _windowOpenLastEventTimeUs = _latestEventTimeUs;

            RecycleWindowEvents();
        }

        public void ReturnEventsToPool(List<GameEvent> events)
        {
            if (events == null)
            {
                return;
            }

            for (int i = 0; i < events.Count; i++)
            {
                GameEvent evt = events[i];
                if (evt != null && _eventPool.Count < MaxPoolSize)
                {
                    evt.Clear();
                    _eventPool.Push(evt);
                }
            }

            events.Clear();
        }

        private int FindOpenSpanIndex(string spanName)
        {
            for (int i = 0; i < _openSpans.Count; i++)
            {
                if (string.Equals(_openSpans[i].Name, spanName, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private GameEvent RentEvent()
        {
            if (_eventPool.Count > 0)
            {
                return _eventPool.Pop();
            }

            return new GameEvent();
        }

        private void RecycleWindowEvents()
        {
            for (int i = 0; i < _windowEvents.Count; i++)
            {
                GameEvent evt = _windowEvents[i];
                if (evt != null && _eventPool.Count < MaxPoolSize)
                {
                    evt.Clear();
                    _eventPool.Push(evt);
                }
            }

            _windowEvents.Clear();
        }
    }

    /// <summary>
    /// Adapter wiring an <see cref="IEventSink"/> directly to an <see cref="EventAndSpanTracker"/>
    /// stamped by an <see cref="IClockSource"/>.
    /// </summary>
    public sealed class TrackerEventSink : IEventSink
    {
        private readonly EventAndSpanTracker _tracker;
        private readonly IClockSource _clockSource;

        public TrackerEventSink(EventAndSpanTracker tracker, IClockSource clockSource)
        {
            _tracker = tracker;
            _clockSource = clockSource;
        }

        public void SendEvent(string eventName, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            if (_tracker == null || _clockSource == null)
            {
                return;
            }

            _tracker.RecordEvent(eventName, eventData, false, null, span, _clockSource.UnityTimeUs);
        }

        public void SendOutcomeEvent(string eventName, string outcome, IReadOnlyDictionary<string, Value> eventData = null, string span = null)
        {
            if (_tracker == null || _clockSource == null)
            {
                return;
            }

            _tracker.RecordEvent(eventName, eventData, true, outcome ?? string.Empty, span, _clockSource.UnityTimeUs);
        }
    }
}
