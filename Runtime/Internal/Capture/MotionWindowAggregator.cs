using System.Collections.Generic;
using Asparagus.Sdk.Contracts;

namespace Asparagus.Sdk.Internal.Capture
{
    /// <summary>
    /// Coordinates per-sensor <see cref="SensorBlockBuilder"/> instances for Accelerometer,
    /// Gyroscope, Gravity, and Magnetometer plus window <see cref="GapRecord"/> tracking.
    /// Exposes <see cref="BoundedMotionSampleCount"/> excluding Magnetometer per W0-10.
    /// </summary>
    public sealed class MotionWindowAggregator : IMotionSampleConsumer
    {
        private readonly SensorBlockBuilder _accelerometer = new SensorBlockBuilder(Sensor.Accelerometer);
        private readonly SensorBlockBuilder _gyroscope = new SensorBlockBuilder(Sensor.Gyroscope);
        private readonly SensorBlockBuilder _gravity = new SensorBlockBuilder(Sensor.Gravity);
        private readonly SensorBlockBuilder _magnetometer = new SensorBlockBuilder(Sensor.Magnetometer);

        private readonly List<GapRecord> _windowGaps = new List<GapRecord>(16);

        private bool _accelerometerEnabled;
        private bool _gyroscopeEnabled;
        private bool _gravityEnabled;
        private bool _magnetometerEnabled;

        public MotionWindowAggregator()
        {
            Configure(SessionSettings.CreateDefaults());
        }

        /// <summary>
        /// W8-26 bounded motion sample count (Accelerometer + Gyroscope + Gravity ONLY;
        /// explicitly excludes Magnetometer per W0-10 / BUILDING_AN_SDK.md).
        /// </summary>
        public int BoundedMotionSampleCount
        {
            get
            {
                return _accelerometer.WindowSampleCount
                    + _gyroscope.WindowSampleCount
                    + _gravity.WindowSampleCount;
            }
        }

        public int TotalMotionSampleCount
        {
            get { return BoundedMotionSampleCount + _magnetometer.WindowSampleCount; }
        }

        public int GapCount
        {
            get { return _windowGaps.Count; }
        }

        public void Configure(
            SessionSettings settings,
            double accelerometerScale = SampleQuantizer.AccelerometerScale,
            double gyroscopeScale = SampleQuantizer.GyroscopeScale,
            double gravityScale = SampleQuantizer.GravityScale,
            double magnetometerScale = SampleQuantizer.MagnetometerScale)
        {
            SessionSettings s = settings ?? SessionSettings.CreateDefaults();

            _accelerometerEnabled = s.AccelerometerHz > 0u;
            _gyroscopeEnabled = s.GyroscopeHz > 0u;
            _gravityEnabled = s.GravityHz > 0u;
            _magnetometerEnabled = s.MagnetometerHz > 0u;

            _accelerometer.Configure(
                accelerometerScale,
                s.AccelerometerHz,
                s.AccelerometerHoldTolerance,
                s.HoldRunLength,
                s.GapThresholdMs,
                s.GapThresholdPeriods);

            _gyroscope.Configure(
                gyroscopeScale,
                s.GyroscopeHz,
                s.GyroscopeHoldTolerance,
                s.HoldRunLength,
                s.GapThresholdMs,
                s.GapThresholdPeriods);

            _gravity.Configure(
                gravityScale,
                s.GravityHz,
                s.GravityHoldTolerance,
                s.HoldRunLength,
                s.GapThresholdMs,
                s.GapThresholdPeriods);

            _magnetometer.Configure(
                magnetometerScale,
                s.MagnetometerHz,
                s.MagnetometerHoldTolerance,
                s.HoldRunLength,
                s.GapThresholdMs,
                s.GapThresholdPeriods);
        }

        public void OnMotionSample(in RawMotionSample sample)
        {
            switch (sample.Sensor)
            {
                case Sensor.Accelerometer:
                    if (_accelerometerEnabled)
                    {
                        _accelerometer.AddSample(in sample, _windowGaps);
                    }
                    break;

                case Sensor.Gyroscope:
                    if (_gyroscopeEnabled)
                    {
                        _gyroscope.AddSample(in sample, _windowGaps);
                    }
                    break;

                case Sensor.Gravity:
                    if (_gravityEnabled)
                    {
                        _gravity.AddSample(in sample, _windowGaps);
                    }
                    break;

                case Sensor.Magnetometer:
                    if (_magnetometerEnabled)
                    {
                        _magnetometer.AddSample(in sample, _windowGaps);
                    }
                    break;
            }
        }

        public void RecordExternalGap(GapRecord gap)
        {
            _windowGaps.Add(gap);
        }

        public void FlushWindowTo(List<SensorBlock> blocksTarget, List<GapRecord> gapsTarget)
        {
            _accelerometer.FlushTo(blocksTarget);
            _gyroscope.FlushTo(blocksTarget);
            _gravity.FlushTo(blocksTarget);
            _magnetometer.FlushTo(blocksTarget);

            if (gapsTarget != null)
            {
                for (int i = 0; i < _windowGaps.Count; i++)
                {
                    gapsTarget.Add(_windowGaps[i]);
                }
            }

            _windowGaps.Clear();
        }

        public void ResetWindow()
        {
            _accelerometer.ResetWindow();
            _gyroscope.ResetWindow();
            _gravity.ResetWindow();
            _magnetometer.ResetWindow();
            _windowGaps.Clear();
        }

        public void RecycleBlocks(List<SensorBlock> blocks)
        {
            if (blocks == null)
            {
                return;
            }

            for (int i = 0; i < blocks.Count; i++)
            {
                SensorBlock block = blocks[i];
                if (block == null)
                {
                    continue;
                }

                switch (block.Sensor)
                {
                    case Sensor.Accelerometer:
                        _accelerometer.RecycleBlock(block);
                        break;
                    case Sensor.Gyroscope:
                        _gyroscope.RecycleBlock(block);
                        break;
                    case Sensor.Gravity:
                        _gravity.RecycleBlock(block);
                        break;
                    case Sensor.Magnetometer:
                        _magnetometer.RecycleBlock(block);
                        break;
                    default:
                        block.Clear();
                        break;
                }
            }

            blocks.Clear();
        }
    }
}
