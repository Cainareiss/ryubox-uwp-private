using System.Threading;
using System;

namespace Ryujinx.HLE.HOS.Kernel.Threading
{
    public static class UwpKernelCriticalSectionTelemetry
    {
        private static KCriticalSection _instance;

        internal static void Register(KCriticalSection instance) => Volatile.Write(ref _instance, instance);

        public static string GetSnapshot() => Volatile.Read(ref _instance)?.GetTelemetry() ?? "kernelCritical=unavailable";
    }

    class KCriticalSection
    {
        private readonly KernelContext _context;
        private int _recursionCount;
        private int _waiters;
        private int _ownerHostThreadId;
        private long _ownerGuestThreadUid;
        private long _ownerAcquiredAt;
        private long _lastReleasedAt;
        private string _ownerOrigin = "none";

        [ThreadStatic]
        private static int _telemetryDepth;

        // type is not Lock due to Monitor class usage
        public object Lock { get; } = new();

        public KCriticalSection(KernelContext context)
        {
            _context = context;
            UwpKernelCriticalSectionTelemetry.Register(this);
        }

        public void Enter()
        {
            EnterMonitor("kernel");

            _recursionCount++;
        }

        internal void EnterRaw(string origin)
        {
            EnterMonitor(origin);
        }

        private void EnterMonitor(string origin)
        {
            Interlocked.Increment(ref _waiters);
            Monitor.Enter(Lock);
            Interlocked.Decrement(ref _waiters);

            if (_telemetryDepth++ == 0)
            {
                KThread currentThread = KernelStatic.GetCurrentThread();
                Volatile.Write(ref _ownerHostThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _ownerGuestThreadUid, unchecked((long)(currentThread?.ThreadUid ?? 0)));
                Volatile.Write(ref _ownerAcquiredAt, Environment.TickCount64);
                Volatile.Write(ref _ownerOrigin, origin);
            }
        }

        public void Leave()
        {
            if (_recursionCount == 0)
            {
                return;
            }

            if (--_recursionCount == 0)
            {
                ulong scheduledCoresMask = KScheduler.SelectThreads(_context);

                ReleaseTelemetryOwnership();
                Monitor.Exit(Lock);

                KThread currentThread = KernelStatic.GetCurrentThread();
                bool isCurrentThreadSchedulable = currentThread != null && currentThread.IsSchedulable;
                if (isCurrentThreadSchedulable)
                {
                    KScheduler.EnableScheduling(_context, scheduledCoresMask);
                }
                else
                {
                    KScheduler.EnableSchedulingFromForeignThread(_context, scheduledCoresMask);

                    // If the thread exists but is not schedulable, we still want to suspend
                    // it if it's not runnable. That allows the kernel to still block HLE threads
                    // even if they are not scheduled on guest cores.
                    if (currentThread != null && !currentThread.IsSchedulable && currentThread.Context.Running)
                    {
                        currentThread.SchedulerWaitEvent.Wait();
                    }
                }
            }
            else
            {
                _telemetryDepth--;
                Monitor.Exit(Lock);
            }
        }

        internal void LeaveRaw()
        {
            ReleaseTelemetryOwnership();
            Monitor.Exit(Lock);
        }

        private void ReleaseTelemetryOwnership()
        {
            if (--_telemetryDepth == 0)
            {
                Volatile.Write(ref _lastReleasedAt, Environment.TickCount64);
                Volatile.Write(ref _ownerOrigin, "none");
                Volatile.Write(ref _ownerGuestThreadUid, 0);
                Volatile.Write(ref _ownerHostThreadId, 0);
                Volatile.Write(ref _ownerAcquiredAt, 0);
            }
        }

        internal string GetTelemetry()
        {
            long acquiredAt = Volatile.Read(ref _ownerAcquiredAt);
            return string.Format(
                "kernelCriticalOwnerHost={0}, ownerGuestUid={1}, ownerOrigin={2}, heldFor={3}ms, waiters={4}, lastReleasedAt={5}",
                Volatile.Read(ref _ownerHostThreadId),
                unchecked((ulong)Volatile.Read(ref _ownerGuestThreadUid)),
                Volatile.Read(ref _ownerOrigin),
                acquiredAt == 0 ? 0 : Math.Max(0, Environment.TickCount64 - acquiredAt),
                Volatile.Read(ref _waiters),
                Volatile.Read(ref _lastReleasedAt));
        }
    }
}
