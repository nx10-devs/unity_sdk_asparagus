using System;
using System.Collections.Generic;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    public interface IScreenInfoProvider
    {
        ScreenRecord ReadCurrentScreen();
    }

    /// <summary>
    /// Non-throwing Unity <see cref="IScreenInfoProvider"/> mapping <c>UnityEngine.Screen</c>
    /// width, height, DPI, and orientation into a contract <see cref="ScreenRecord"/>.
    /// </summary>
    public sealed class UnityScreenInfoProvider : IScreenInfoProvider
    {
        public ScreenRecord ReadCurrentScreen()
        {
            ScreenRecord rec = new ScreenRecord
            {
                TimeUs = 0L,
                Origin = CoordinateOrigin.BottomLeft
            };

#if UNITY_5_3_OR_NEWER
            try
            {
                int w = UnityEngine.Screen.width;
                int h = UnityEngine.Screen.height;
                float dpi = UnityEngine.Screen.dpi;

                rec.WidthPx = w > 0 ? (uint)w : 0u;
                rec.HeightPx = h > 0 ? (uint)h : 0u;
                rec.DensityDpi = (dpi > 0f && !float.IsNaN(dpi) && !float.IsInfinity(dpi)) ? dpi : 0f;
                rec.Orientation = MapOrientation(UnityEngine.Screen.orientation);
                rec.Origin = CoordinateOrigin.BottomLeft;
            }
            catch
            {
                // Fault containment: return safe default record.
            }
#endif
            return rec;
        }

#if UNITY_5_3_OR_NEWER
        private static Orientation MapOrientation(UnityEngine.ScreenOrientation orientation)
        {
            switch (orientation)
            {
                case UnityEngine.ScreenOrientation.Portrait:
                    return Orientation.Portrait;
                case UnityEngine.ScreenOrientation.PortraitUpsideDown:
                    return Orientation.PortraitUpsideDown;
                case UnityEngine.ScreenOrientation.LandscapeLeft:
                    return Orientation.LandscapeLeft;
                case UnityEngine.ScreenOrientation.LandscapeRight:
                    return Orientation.LandscapeRight;
                default:
                    return Orientation.Unspecified;
            }
        }
#endif
    }

    /// <summary>
    /// Tracks window-open <see cref="Header.Screen"/> (<c>TimeUs = 0</c>) and bounded in-window
    /// <see cref="Batch.ScreenChanges"/> (<c>&lt;= 750</c>).
    /// </summary>
    public sealed class ScreenTracker
    {
        private readonly IScreenInfoProvider _provider;
        private readonly List<ScreenRecord> _windowChanges = new List<ScreenRecord>(4);

        private ScreenRecord _windowOpenScreen;
        private ScreenRecord _lastSeenScreen;
        private bool _initialized;

        public ScreenTracker(IScreenInfoProvider provider = null)
        {
            _provider = provider ?? new UnityScreenInfoProvider();
            Reset();
        }

        public ScreenRecord WindowOpenScreen
        {
            get { return _windowOpenScreen; }
        }

        public int ScreenChangeCount
        {
            get { return _windowChanges.Count; }
        }

        public void Reset()
        {
            _windowChanges.Clear();
            ScreenRecord current = SafeRead();
            current.TimeUs = 0L;
            _windowOpenScreen = current;
            _lastSeenScreen = current;
            _initialized = true;
        }

        public bool PollForChanges(long unityTimeUs)
        {
            ScreenRecord current = SafeRead();
            current.TimeUs = 0L;

            if (!_initialized)
            {
                _windowOpenScreen = current;
                _lastSeenScreen = current;
                _initialized = true;
                return false;
            }

            if (IsSameDisplayState(current, _lastSeenScreen))
            {
                return false;
            }

            _lastSeenScreen = current;

            if (_windowChanges.Count < BatchConstants.MaxScreenChanges)
            {
                ScreenRecord stamped = current;
                stamped.TimeUs = unityTimeUs;
                _windowChanges.Add(stamped);
            }

            return true;
        }

        public void FlushWindowChangesTo(List<ScreenRecord> target)
        {
            if (target != null)
            {
                for (int i = 0; i < _windowChanges.Count; i++)
                {
                    target.Add(_windowChanges[i]);
                }
            }

            _windowChanges.Clear();

            ScreenRecord nextOpen = _lastSeenScreen;
            nextOpen.TimeUs = 0L;
            _windowOpenScreen = nextOpen;
        }

        private ScreenRecord SafeRead()
        {
            try
            {
                return _provider != null ? _provider.ReadCurrentScreen() : default;
            }
            catch
            {
                return default;
            }
        }

        private static bool IsSameDisplayState(ScreenRecord a, ScreenRecord b)
        {
            return a.WidthPx == b.WidthPx
                && a.HeightPx == b.HeightPx
                && a.DensityDpi.Equals(b.DensityDpi)
                && a.Orientation == b.Orientation
                && a.Origin == b.Origin;
        }
    }
}
