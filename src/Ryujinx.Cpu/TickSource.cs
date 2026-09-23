using System;
using System.Diagnostics;
using Ryujinx.Common;

namespace Ryujinx.Cpu
{
    public class TickSource : ITickSource
    {
        private readonly object _clockLock = new();
        private readonly double _hostTickFreq;
        private long _accumulatedHostTicks;
        private long _lastHostTicks;
        private long _tickScalar;
        private bool _suspended;
        private long _readCount;
        private long _regressionCount;

        /// <inheritdoc/>
        public ulong Frequency { get; }

        /// <inheritdoc/>
        public ulong Counter => (ulong)(ElapsedSeconds * Frequency);


        public long TickScalar
        {
            get
            {
                lock (_clockLock)
                {
                    return _tickScalar;
                }
            }
            set
            {
                lock (_clockLock)
                {
                    AdvanceLocked();
                    _tickScalar = value;
                }
            }
        }

        private long ElapsedTicks
        {
            get
            {
                lock (_clockLock)
                {
                    AdvanceLocked();
                    return _accumulatedHostTicks;
                }
            }
        }

        /// <inheritdoc/>

        public TimeSpan ElapsedTime => Stopwatch.GetElapsedTime(0, ElapsedTicks);

        /// <inheritdoc/>
        public double ElapsedSeconds => ElapsedTicks * _hostTickFreq;

        public TickSource(ulong frequency)
        {
            Frequency = frequency;
            _hostTickFreq = 1.0 / Stopwatch.Frequency;
            _tickScalar = ITickSource.RealityTickScalar;
            _lastHostTicks = PerformanceCounter.ElapsedTicks;
        }

        /// <inheritdoc/>
        public void Suspend()
        {
            lock (_clockLock)
            {
                AdvanceLocked();
                _suspended = true;
            }
        }

        /// <inheritdoc/>
        public void Resume()
        {
            lock (_clockLock)
            {
                _lastHostTicks = PerformanceCounter.ElapsedTicks;
                _suspended = false;
            }
        }

        private void AdvanceLocked()
        {
            long currentHostTicks = PerformanceCounter.ElapsedTicks;
            long delta = currentHostTicks - _lastHostTicks;

            if (delta < 0)
            {
                _regressionCount++;
                delta = 0;
            }

            if (!_suspended)
            {
                _accumulatedHostTicks += delta * _tickScalar / 100;
            }

            _lastHostTicks = currentHostTicks;
            _readCount++;
        }

        public string GetUwpTelemetry()
        {
            lock (_clockLock)
            {
                AdvanceLocked();
                ulong counter = (ulong)(_accumulatedHostTicks * _hostTickFreq * Frequency);

                return string.Format(
                    "guestCounter={0}, accumulatedHostTicks={1}, lastHostTicks={2}, scalar={3}, suspended={4}, reads={5}, regressions={6}",
                    counter,
                    _accumulatedHostTicks,
                    _lastHostTicks,
                    _tickScalar,
                    _suspended,
                    _readCount,
                    _regressionCount);
            }
        }
    }
}
