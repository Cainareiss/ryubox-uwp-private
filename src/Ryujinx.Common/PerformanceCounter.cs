using System;
using System.Diagnostics;

namespace Ryujinx.Common
{
    public static class PerformanceCounter
    {
        private static readonly double _ticksToNs;

        private static bool UseSafeMonotonicClock =>
            AppContext.TryGetSwitch("Ryujinx.Uwp.SafeMonotonicClock", out bool enabled) && enabled;

        /// <summary>
        /// Represents the number of ticks in 1 day.
        /// </summary>
        public static long TicksPerDay { get; }

        /// <summary>
        /// Represents the number of ticks in 1 hour.
        /// </summary>
        public static long TicksPerHour { get; }

        /// <summary>
        /// Represents the number of ticks in 1 minute.
        /// </summary>
        public static long TicksPerMinute { get; }

        /// <summary>
        /// Represents the number of ticks in 1 second.
        /// </summary>
        public static long TicksPerSecond { get; }

        /// <summary>
        /// Represents the number of ticks in 1 millisecond.
        /// </summary>
        public static long TicksPerMillisecond { get; }

        /// <summary>
        /// Gets the number of ticks elapsed since the system started.
        /// </summary>
        public static long ElapsedTicks
        {
            get
            {
                if (UseSafeMonotonicClock)
                {
                    return MillisecondsToTicks(Environment.TickCount64);
                }

                return Stopwatch.GetTimestamp();
            }
        }

        /// <summary>
        /// Gets the number of milliseconds elapsed since the system started.
        /// </summary>
        public static long ElapsedMilliseconds
        {
            get
            {
                if (UseSafeMonotonicClock)
                {
                    return Environment.TickCount64;
                }

                long timestamp = Stopwatch.GetTimestamp();

                return timestamp / TicksPerMillisecond;
            }
        }

        /// <summary>
        /// Gets the number of nanoseconds elapsed since the system started.
        /// </summary>
        public static long ElapsedNanoseconds
        {
            get
            {
                if (UseSafeMonotonicClock)
                {
                    return Environment.TickCount64 * 1_000_000L;
                }

                long timestamp = Stopwatch.GetTimestamp();

                return (long)(timestamp * _ticksToNs);
            }
        }

        static PerformanceCounter()
        {
            TicksPerMillisecond = Stopwatch.Frequency / 1000;
            TicksPerSecond = Stopwatch.Frequency;
            TicksPerMinute = TicksPerSecond * 60;
            TicksPerHour = TicksPerMinute * 60;
            TicksPerDay = TicksPerHour * 24;

            _ticksToNs = 1000000000.0 / Stopwatch.Frequency;
        }

        private static long MillisecondsToTicks(long milliseconds)
        {
            long seconds = milliseconds / 1000;
            long remainingMilliseconds = milliseconds % 1000;

            return checked(seconds * Stopwatch.Frequency + remainingMilliseconds * Stopwatch.Frequency / 1000);
        }

        /// <summary>
        /// Returns both Windows monotonic sources so Xbox diagnostics can prove
        /// whether time is advancing even when one source is unreliable.
        /// </summary>
        public static string GetUwpClockTelemetry()
        {
            long environmentMilliseconds = Environment.TickCount64;
            long stopwatchTicks = Stopwatch.GetTimestamp();

            return string.Format(
                "clockSource={0}, perfTicks={1}, envMilliseconds={2}, rawStopwatchTicks={3}, frequency={4}",
                UseSafeMonotonicClock ? "Environment.TickCount64" : "Stopwatch/QPC",
                UseSafeMonotonicClock ? MillisecondsToTicks(environmentMilliseconds) : stopwatchTicks,
                environmentMilliseconds,
                stopwatchTicks,
                Stopwatch.Frequency);
        }
    }
}
