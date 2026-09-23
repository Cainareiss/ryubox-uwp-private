using Ryujinx.Cpu;
using Ryujinx.HLE.HOS.Kernel.Threading;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel.SupervisorCall
{
    public static class UwpSvcTelemetry
    {
        private sealed class SvcState
        {
            public int Id;
            public long Address;
            public long Calls;
            public long StartedAt;
            public long CompletedAt;
            public string Stage = "idle";
            public string Detail = "none";
        }

        private static readonly ConcurrentDictionary<ulong, SvcState> States = new();

        internal static void Reset() => States.Clear();

        internal static void Begin(ulong threadUid, ulong address, int id)
        {
            SvcState state = States.GetOrAdd(threadUid, _ => new SvcState());
            Volatile.Write(ref state.Id, id);
            Volatile.Write(ref state.Address, unchecked((long)address));
            Interlocked.Increment(ref state.Calls);
            Volatile.Write(ref state.StartedAt, Environment.TickCount64);
            Volatile.Write(ref state.Stage, "dispatch");
            Volatile.Write(ref state.Detail, "none");
        }

        internal static void SetDetail(ulong threadUid, string detail)
        {
            if (States.TryGetValue(threadUid, out SvcState state))
            {
                Volatile.Write(ref state.Detail, detail ?? "none");
            }
        }

        internal static void SetStage(ulong threadUid, string stage)
        {
            if (States.TryGetValue(threadUid, out SvcState state))
            {
                Volatile.Write(ref state.Stage, stage);
            }
        }

        internal static void Complete(ulong threadUid)
        {
            if (States.TryGetValue(threadUid, out SvcState state))
            {
                Volatile.Write(ref state.CompletedAt, Environment.TickCount64);
                Volatile.Write(ref state.Stage, "complete");
            }
        }

        public static string GetSnapshot()
        {
            long now = Environment.TickCount64;
            string details = string.Join(
                "; ",
                States.OrderBy(pair => pair.Key).Take(64).Select(pair =>
                {
                    SvcState state = pair.Value;
                    long startedAt = Volatile.Read(ref state.StartedAt);
                    return string.Format(
                        "uid={0}/svc=0x{1:X}/address=0x{2:X}/stage={3}/inStageFor={4}ms/calls={5}/completedAt={6}/detail={7}",
                        pair.Key,
                        Volatile.Read(ref state.Id),
                        unchecked((ulong)Volatile.Read(ref state.Address)),
                        Volatile.Read(ref state.Stage),
                        Math.Max(0, now - startedAt),
                        Interlocked.Read(ref state.Calls),
                        Volatile.Read(ref state.CompletedAt),
                        Volatile.Read(ref state.Detail));
                }));

            return States.IsEmpty ? "svcThreads=0" : $"svcThreads={States.Count}: {details}";
        }
    }

    partial class SyscallHandler
    {
        private readonly KernelContext _context;

        public SyscallHandler(KernelContext context)
        {
            _context = context;
            UwpSvcTelemetry.Reset();
        }

        public void SvcCall(IExecutionContext context, ulong address, int id)
        {
            KThread currentThread = KernelStatic.GetCurrentThread();
            ulong threadUid = currentThread.ThreadUid;
            UwpSvcTelemetry.Begin(threadUid, address, id);

            if (currentThread.Owner != null &&
                currentThread.GetUserDisableCount() != 0 &&
                currentThread.Owner.PinnedThreads[currentThread.CurrentCore] == null)
            {
                _context.CriticalSection.Enter();

                currentThread.Owner.PinThread(currentThread);

                currentThread.SetUserInterruptFlag();

                _context.CriticalSection.Leave();
            }

            try
            {
                if (context.IsAarch32)
                    SyscallDispatch.Dispatch32(_context.Syscall, context, id);
                else
                    SyscallDispatch.Dispatch64(_context.Syscall, context, id);
            }
            catch (Exception exception) when (_context.Device.GuestFaultHandler != null &&
                (exception is OutOfMemoryException ||
                 exception is System.ComponentModel.Win32Exception { NativeErrorCode: 1455 or 8 or 14 }))
            {
                // A managed exception must not escape the native JIT callback.
                // The failed syscall may be partially applied; terminate the
                // session rather than returning success to the guest.
                context.StopRunning();
                UwpSvcTelemetry.SetStage(threadUid, "memory-fault");
                _context.Device.GuestFaultHandler(new OutOfMemoryException(
                    $"Guest SVC 0x{id:X} at PC=0x{address:X}, thread={threadUid}: {exception.Message}", exception));
                return;
            }

            UwpSvcTelemetry.SetStage(threadUid, "post-syscall");
            currentThread.HandlePostSyscall();
            UwpSvcTelemetry.Complete(threadUid);
        }
    }
}
