using ARMeilleure.Memory;
using System.Threading;

namespace ARMeilleure.State
{
    public class ExecutionContext
    {
        private const int MinCountForCheck = 4000;

        private readonly NativeContext _nativeContext;
        private readonly Lock _lifecycle = new();
        public enum LifecycleState { Active, Stopping, Disposed }
        private LifecycleState _state;
        public LifecycleState State { get { lock (_lifecycle) return _state; } }

        internal nint NativeContextPtr => _nativeContext.BasePtr;

        private volatile bool _interrupted;

        internal bool Interrupted => _interrupted;

        private readonly ICounter _counter;

        public ulong Pc => _nativeContext.GetPc();

        public static uint CtrEl0 => 0x8444c004;
        public static uint DczidEl0 => 0x00000004;

        public ulong CntfrqEl0 => _counter.Frequency;
        public ulong CntpctEl0 => _counter.Counter;

        // CNTVCT_EL0 = CNTPCT_EL0 - CNTVOFF_EL2
        // Since EL2 isn't implemented, CNTVOFF_EL2 = 0
        public ulong CntvctEl0 => CntpctEl0;

        public long TpidrEl0
        {
            get => _nativeContext.GetTpidrEl0();
            set => _nativeContext.SetTpidrEl0(value);
        }

        public long TpidrroEl0
        {
            get => _nativeContext.GetTpidrroEl0();
            set => _nativeContext.SetTpidrroEl0(value);
        }

        public uint Pstate
        {
            get => _nativeContext.GetPstate();
            set => _nativeContext.SetPstate(value);
        }

        public FPSR Fpsr
        {
            get => (FPSR)_nativeContext.GetFPState((uint)FPSR.Mask);
            set => _nativeContext.SetFPState((uint)value, (uint)FPSR.Mask);
        }

        public FPCR Fpcr
        {
            get => (FPCR)_nativeContext.GetFPState((uint)FPCR.Mask);
            set => _nativeContext.SetFPState((uint)value, (uint)FPCR.Mask);
        }
        public FPCR StandardFpcrValue => (Fpcr & (FPCR.Ahp)) | FPCR.Dn | FPCR.Fz;

        public FPSCR Fpscr
        {
            get => (FPSCR)_nativeContext.GetFPState((uint)FPSCR.Mask);
            set => _nativeContext.SetFPState((uint)value, (uint)FPSCR.Mask);
        }

        public bool IsAarch32 { get; set; }

        public ulong ThreadUid { get; set; }

        internal ExecutionMode ExecutionMode
        {
            get
            {
                if (IsAarch32)
                {
                    return GetPstateFlag(PState.TFlag)
                        ? ExecutionMode.Aarch32Thumb
                        : ExecutionMode.Aarch32Arm;
                }
                else
                {
                    return ExecutionMode.Aarch64;
                }
            }
        }

        public bool Running
        {
            get { lock (_lifecycle) return _state != LifecycleState.Disposed && _nativeContext.GetRunning(); }
            private set => _nativeContext.SetRunning(value);
        }

        private readonly ExceptionCallbackNoArgs _interruptCallback;
        private readonly ExceptionCallback _breakCallback;
        private readonly ExceptionCallbackNoArgs _stepCallback;
        private readonly ExceptionCallback _supervisorCallback;
        private readonly ExceptionCallback _undefinedCallback;

        internal int ShouldStep;
        public ulong DebugPc { get; set; }

        public ExecutionContext(
            IJitMemoryAllocator allocator,
            ICounter counter,
            ExceptionCallbackNoArgs interruptCallback = null,
            ExceptionCallback breakCallback = null,
            ExceptionCallbackNoArgs stepCallback = null,
            ExceptionCallback supervisorCallback = null,
            ExceptionCallback undefinedCallback = null)
        {
            _nativeContext = new NativeContext(allocator);
            _counter = counter;
            _interruptCallback = interruptCallback;
            _breakCallback = breakCallback;
            _stepCallback = stepCallback;
            _supervisorCallback = supervisorCallback;
            _undefinedCallback = undefinedCallback;

            Running = true;

            lock (_lifecycle)
                if (_state != LifecycleState.Disposed) _nativeContext.SetCounter(MinCountForCheck);
        }

        public ulong GetX(int index) => _nativeContext.GetX(index);
        public void SetX(int index, ulong value) => _nativeContext.SetX(index, value);

        public V128 GetV(int index) => _nativeContext.GetV(index);
        public void SetV(int index, V128 value) => _nativeContext.SetV(index, value);

        public bool GetPstateFlag(PState flag) => _nativeContext.GetPstateFlag(flag);
        public void SetPstateFlag(PState flag, bool value) => _nativeContext.SetPstateFlag(flag, value);

        public bool GetFPstateFlag(FPState flag) => _nativeContext.GetFPStateFlag(flag);
        public void SetFPstateFlag(FPState flag, bool value) => _nativeContext.SetFPStateFlag(flag, value);

        internal void ResetCallDepth()
        {
            _nativeContext.ResetCallDepth();
        }

        internal void CheckPendingInterrupt()
        {
            // The managed dispatcher calls this on every function return. Do
            // not take the lifecycle lock or reset the guest polling budget
            // when there is no request. Native synchronization still refills it.
            if (Interrupted) CheckInterrupt();
        }

        internal void CheckInterrupt()
        {
            bool interrupted;
            lock (_lifecycle)
            {
                if (_state == LifecycleState.Disposed) return;
                interrupted = _interrupted;
                _interrupted = false;
            }

            // Scheduler callbacks may sleep; never hold the lifecycle lock here.
            if (interrupted) _interruptCallback?.Invoke(this);

            lock (_lifecycle)
            {
                if (_state != LifecycleState.Disposed)
                {
                    // A second request/Stop during the callback must retain the
                    // zero counter; otherwise a refill delays the new interrupt.
                    _nativeContext.SetCounter(_state == LifecycleState.Active && !_interrupted ? MinCountForCheck : 0);
                }
            }
        }

        public void RequestInterrupt()
        {
            lock (_lifecycle)
            {
            if (_state != LifecycleState.Active) return;
            // This method is normally called by the Horizon scheduler from a
            // different host thread. Publishing only the managed flag lets a
            // translated guest function continue until its 4000-edge budget
            // expires, and the managed UWP dispatch path previously did not
            // check the flag between functions at all. Force the native edge
            // counter to zero so the next synchronization point observes the
            // request immediately.
            _interrupted = true;
            _nativeContext.SetCounter(0);
            }
        }

        public void StepHandler()
        {
            _stepCallback?.Invoke(this);
        }

        public void RequestDebugStep()
        {
            Interlocked.Exchange(ref ShouldStep, 1);
            RequestInterrupt();
        }

        internal void OnBreak(ulong address, int imm)
        {
            if (Optimizations.EnableDebugging)
            {
                DebugPc = Pc;
            }

            _breakCallback?.Invoke(this, address, imm);
        }

        internal void OnSupervisorCall(ulong address, int imm)
        {
            if (Optimizations.EnableDebugging)
            {
                DebugPc = Pc;
            }

            _supervisorCallback?.Invoke(this, address, imm);
        }

        internal void OnUndefined(ulong address, int opCode)
        {
            _undefinedCallback?.Invoke(this, address, opCode);
        }

        public void StopRunning()
        {
            lock (_lifecycle)
            {
            if (_state == LifecycleState.Disposed) return;
            _state = LifecycleState.Stopping;
            Ryujinx.Common.Logging.RuntimeDiagnosticLog.Write("scheduler-race", $"context={GetHashCode()} state=Stopping threadUid={ThreadUid}");
            Running = false;

            _nativeContext.SetCounter(0);
            }
        }

        public void Dispose()
        {
            lock (_lifecycle)
            {
                if (_state == LifecycleState.Disposed) return;
                StopRunning();
                _state = LifecycleState.Disposed;
                Ryujinx.Common.Logging.RuntimeDiagnosticLog.Write("scheduler-race", $"context={GetHashCode()} state=Disposed threadUid={ThreadUid}");
                _nativeContext.Dispose();
            }
        }
    }
}
