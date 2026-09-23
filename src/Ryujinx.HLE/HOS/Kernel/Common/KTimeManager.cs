using Ryujinx.Common;
using Ryujinx.Common.PreciseSleep;
using System;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.Common
{
    public static class UwpTimeManagerTelemetry
    {
        private static long _scheduled;
        private static long _expired;
        private static long _unscheduled;
        private static long _lastTimeoutNs;
        private static long _lastScheduledAt;
        private static long _lastExpiredAt;
        private static int _queueDepth;
        private static int _initialized;

        internal static void Reset()
        {
            Interlocked.Exchange(ref _scheduled, 0);
            Interlocked.Exchange(ref _expired, 0);
            Interlocked.Exchange(ref _unscheduled, 0);
            Interlocked.Exchange(ref _lastTimeoutNs, 0);
            Interlocked.Exchange(ref _lastScheduledAt, 0);
            Interlocked.Exchange(ref _lastExpiredAt, 0);
            Volatile.Write(ref _queueDepth, 0);
            Volatile.Write(ref _initialized, 0);
        }

        internal static void Initialized() => Volatile.Write(ref _initialized, 1);

        internal static void Scheduled(long timeoutNs, int queueDepth)
        {
            Interlocked.Increment(ref _scheduled);
            Interlocked.Exchange(ref _lastTimeoutNs, timeoutNs);
            Interlocked.Exchange(ref _lastScheduledAt, Environment.TickCount64);
            Volatile.Write(ref _queueDepth, queueDepth);
        }

        internal static void Expired(int queueDepth)
        {
            Interlocked.Increment(ref _expired);
            Interlocked.Exchange(ref _lastExpiredAt, Environment.TickCount64);
            Volatile.Write(ref _queueDepth, queueDepth);
        }

        internal static void Unscheduled(int removed, int queueDepth)
        {
            if (removed > 0)
            {
                Interlocked.Add(ref _unscheduled, removed);
            }
            Volatile.Write(ref _queueDepth, queueDepth);
        }

        public static string GetSnapshot() => string.Format(
            "timeManagerInitialized={0}, scheduled={1}, expired={2}, unscheduled={3}, queueDepth={4}, lastTimeoutNs={5}, lastScheduledAt={6}, lastExpiredAt={7}",
            Volatile.Read(ref _initialized),
            Interlocked.Read(ref _scheduled),
            Interlocked.Read(ref _expired),
            Interlocked.Read(ref _unscheduled),
            Volatile.Read(ref _queueDepth),
            Interlocked.Read(ref _lastTimeoutNs),
            Interlocked.Read(ref _lastScheduledAt),
            Interlocked.Read(ref _lastExpiredAt));
    }

    class KTimeManager : IDisposable
    {
        public static readonly long DefaultTimeIncrementNanoseconds = ConvertGuestTicksToNanoseconds(2);

        private class WaitingObject
        {
            public IKFutureSchedulerObject Object { get; }
            public long TimePoint { get; }

            public WaitingObject(IKFutureSchedulerObject schedulerObj, long timePoint)
            {
                Object = schedulerObj;
                TimePoint = timePoint;
            }
        }

        private readonly KernelContext _context;
        private readonly List<WaitingObject> _waitingObjects;
        private IPreciseSleepEvent _waitEvent;

        private bool _keepRunning;
        private long _enforceWakeupFromSpinWait;

        private const long NanosecondsPerSecond = 1000000000L;
        private const long NanosecondsPerMillisecond = 1000000L;

        public KTimeManager(KernelContext context)
        {
            _context = context;
            _waitingObjects = [];
            _keepRunning = true;
            UwpTimeManagerTelemetry.Reset();

            Thread work = new(WaitAndCheckScheduledObjects)
            {
                Name = "HLE.TimeManager",
                IsBackground = true,
            };

            work.Start();
        }

        public void ScheduleFutureInvocation(IKFutureSchedulerObject schedulerObj, long timeout)
        {
            long startTime = PerformanceCounter.ElapsedTicks;
            long timePoint = startTime + ConvertNanosecondsToHostTicks(timeout);

            if (timePoint < startTime)
            {
                timePoint = long.MaxValue;
            }

            timePoint = _waitEvent.AdjustTimePoint(timePoint, timeout);

            _context.CriticalSection.EnterRaw("time-schedule");
            try
            {
                _waitingObjects.Add(new WaitingObject(schedulerObj, timePoint));
                UwpTimeManagerTelemetry.Scheduled(timeout, _waitingObjects.Count);

                if (timeout < NanosecondsPerMillisecond)
                {
                    Interlocked.Exchange(ref _enforceWakeupFromSpinWait, 1);
                }
            }
            finally
            {
                _context.CriticalSection.LeaveRaw();
            }

            _waitEvent.Signal();
        }

        public void UnscheduleFutureInvocation(IKFutureSchedulerObject schedulerObj)
        {
            int removed = 0;
            _context.CriticalSection.EnterRaw("time-unschedule");
            try
            {
                for (int index = _waitingObjects.Count - 1; index >= 0; index--)
                {
                    if (_waitingObjects[index].Object == schedulerObj)
                    {
                        _waitingObjects.RemoveAt(index);
                        removed++;
                    }
                }
                UwpTimeManagerTelemetry.Unscheduled(removed, _waitingObjects.Count);
            }
            finally
            {
                _context.CriticalSection.LeaveRaw();
            }
        }

        private void WaitAndCheckScheduledObjects()
        {
            WaitingObject next;

            using (_waitEvent = PreciseSleepHelper.CreateEvent())
            {
                UwpTimeManagerTelemetry.Initialized();
                while (_keepRunning)
                {
                    _context.CriticalSection.EnterRaw("time-select");
                    try
                    {
                        Interlocked.Exchange(ref _enforceWakeupFromSpinWait, 0);

                        next = GetNextWaitingObject();
                    }
                    finally
                    {
                        _context.CriticalSection.LeaveRaw();
                    }

                    if (next != null)
                    {
                        long timePoint = PerformanceCounter.ElapsedTicks;

                        if (next.TimePoint > timePoint)
                        {
                            if (!_waitEvent.SleepUntil(next.TimePoint))
                            {
                                PreciseSleepHelper.SpinWaitUntilTimePoint(next.TimePoint, ref _enforceWakeupFromSpinWait);
                            }
                        }

                        bool timeUp = PerformanceCounter.ElapsedTicks >= next.TimePoint;

                        if (timeUp)
                        {
                            _context.CriticalSection.EnterRaw("time-expire");
                            try
                            {
                                if (_waitingObjects.Remove(next))
                                {
                                    next.Object.TimeUp();
                                    UwpTimeManagerTelemetry.Expired(_waitingObjects.Count);
                                }
                            }
                            finally
                            {
                                _context.CriticalSection.LeaveRaw();
                            }
                        }
                    }
                    else
                    {
                        _waitEvent.Sleep();
                    }
                }
            }
        }

        private WaitingObject GetNextWaitingObject()
        {
            WaitingObject selected = null;

            long lowestTimePoint = long.MaxValue;

            for (int index = _waitingObjects.Count - 1; index >= 0; index--)
            {
                WaitingObject current = _waitingObjects[index];

                if (current.TimePoint <= lowestTimePoint)
                {
                    selected = current;
                    lowestTimePoint = current.TimePoint;
                }
            }

            return selected;
        }

        public static long ConvertNanosecondsToMilliseconds(long time)
        {
            time /= NanosecondsPerMillisecond;

            if ((ulong)time > int.MaxValue)
            {
                return int.MaxValue;
            }

            return time;
        }

        public static long ConvertMillisecondsToNanoseconds(long time)
        {
            return time * NanosecondsPerMillisecond;
        }

        public static long ConvertNanosecondsToHostTicks(long ns)
        {
            long nsDiv = ns / NanosecondsPerSecond;
            long nsMod = ns % NanosecondsPerSecond;
            long tickDiv = PerformanceCounter.TicksPerSecond / NanosecondsPerSecond;
            long tickMod = PerformanceCounter.TicksPerSecond % NanosecondsPerSecond;

            long baseTicks = (nsMod * tickMod + PerformanceCounter.TicksPerSecond - 1) / NanosecondsPerSecond;
            return (nsDiv * tickDiv) * NanosecondsPerSecond + nsDiv * tickMod + nsMod * tickDiv + baseTicks;
        }

        public static long ConvertGuestTicksToNanoseconds(long ticks)
        {
            return (long)Math.Ceiling(ticks * (1000000000.0 / 19200000.0));
        }

        public static long ConvertHostTicksToTicks(long time)
        {
            return (long)((time / (double)PerformanceCounter.TicksPerSecond) * 19200000.0);
        }

        public void Dispose()
        {
            _keepRunning = false;
            _waitEvent?.Signal();
        }
    }
}
