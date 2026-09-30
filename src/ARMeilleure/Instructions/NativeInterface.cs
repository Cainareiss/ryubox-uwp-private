using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation;
using System;
using System.Runtime.InteropServices;
using ExecutionContext = ARMeilleure.State.ExecutionContext;

namespace ARMeilleure.Instructions
{
    static class NativeInterface
    {
        // NativeAOT does not guarantee that MethodInfo.MethodHandle.GetFunctionPointer()
        // can resolve an [UnmanagedCallersOnly] method through reflection. Keep this
        // address acquisition as a direct function-pointer reference so the compiler
        // can root and emit the entry point deterministically.
        internal static unsafe nint GetFunctionAddressPointer()
        {
            return GetFunctionPointer(nameof(GetFunctionAddress));
        }

        internal static unsafe nint GetFunctionPointer(string methodName)
        {
            // NativeAOT cannot obtain a callable body for
            // [UnmanagedCallersOnly] methods through MethodHandle reflection.
            // Direct references both root the native entry points and avoid
            // creating a managed reflection invoker for them.
            return methodName switch
            {
                nameof(Break) => (nint)(delegate* unmanaged<ulong, int, void>)&Break,
                nameof(SupervisorCall) => (nint)(delegate* unmanaged<ulong, int, void>)&SupervisorCall,
                nameof(Undefined) => (nint)(delegate* unmanaged<ulong, int, void>)&Undefined,
                nameof(GetCtrEl0) => (nint)(delegate* unmanaged<ulong>)&GetCtrEl0,
                nameof(GetDczidEl0) => (nint)(delegate* unmanaged<ulong>)&GetDczidEl0,
                nameof(GetCntfrqEl0) => (nint)(delegate* unmanaged<ulong>)&GetCntfrqEl0,
                nameof(GetCntpctEl0) => (nint)(delegate* unmanaged<ulong>)&GetCntpctEl0,
                nameof(GetCntvctEl0) => (nint)(delegate* unmanaged<ulong>)&GetCntvctEl0,
                nameof(ReadByte) => (nint)(delegate* unmanaged<ulong, byte>)&ReadByte,
                nameof(ReadUInt16) => (nint)(delegate* unmanaged<ulong, ushort>)&ReadUInt16,
                nameof(ReadUInt32) => (nint)(delegate* unmanaged<ulong, uint>)&ReadUInt32,
                nameof(ReadUInt64) => (nint)(delegate* unmanaged<ulong, ulong>)&ReadUInt64,
                nameof(ReadVector128) => (nint)(delegate* unmanaged<ulong, V128>)&ReadVector128,
                nameof(WriteByte) => (nint)(delegate* unmanaged<ulong, byte, void>)&WriteByte,
                nameof(WriteUInt16) => (nint)(delegate* unmanaged<ulong, ushort, void>)&WriteUInt16,
                nameof(WriteUInt32) => (nint)(delegate* unmanaged<ulong, uint, void>)&WriteUInt32,
                nameof(WriteUInt64) => (nint)(delegate* unmanaged<ulong, ulong, void>)&WriteUInt64,
                nameof(WriteVector128) => (nint)(delegate* unmanaged<ulong, V128, void>)&WriteVector128,
                nameof(EnqueueForRejit) => (nint)(delegate* unmanaged<ulong, void>)&EnqueueForRejit,
                nameof(SignalMemoryTracking) => (nint)(delegate* unmanaged<ulong, ulong, byte, void>)&SignalMemoryTracking,
                nameof(ThrowInvalidMemoryAccess) => (nint)(delegate* unmanaged<ulong, void>)&ThrowInvalidMemoryAccess,
                nameof(GetFunctionAddress) => (nint)(delegate* unmanaged<ulong, ulong>)&GetFunctionAddress,
                nameof(InvalidateCacheLine) => (nint)(delegate* unmanaged<ulong, void>)&InvalidateCacheLine,
                nameof(CheckSynchronization) => (nint)(delegate* unmanaged<byte>)&CheckSynchronization,
                nameof(GuestDispatchBoundary) => (nint)(delegate* unmanaged<void>)&GuestDispatchBoundary,
                nameof(TraceGuestBlock) => (nint)(delegate* unmanaged<ulong, void>)&TraceGuestBlock,
                nameof(TraceGuestAccess) => (nint)(delegate* unmanaged<ulong, ulong, int, int, void>)&TraceGuestAccess,
                nameof(AcquirePinnedFunction) => (nint)(delegate* unmanaged<ulong, ulong>)&AcquirePinnedFunction,
                _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown native interface method"),
            };
        }

        private class ThreadContext
        {
            public ExecutionContext Context { get; }
            public IMemoryManager Memory { get; }
            public Translator Translator { get; }

            public ThreadContext(ExecutionContext context, IMemoryManager memory, Translator translator)
            {
                Context = context;
                Memory = memory;
                Translator = translator;
            }
        }

        [ThreadStatic]
        private static ThreadContext Context;

        public static void RegisterThread(ExecutionContext context, IMemoryManager memory, Translator translator)
        {
            Context = new ThreadContext(context, memory, translator);
        }

        public static void UnregisterThread()
        {
            Context = null;
        }

        [UnmanagedCallersOnly]
        public static void GuestDispatchBoundary()
        {
            Context.Translator.GuestDispatchBoundary(Context.Context.ThreadUid);
        }

        [UnmanagedCallersOnly]
        public static void TraceGuestBlock(ulong address)
        {
            Context.Translator.TraceGuestBlock(address, Context.Context, Context.Memory);
        }

        [UnmanagedCallersOnly]
        public static void TraceGuestAccess(ulong pc, ulong address, int size, int write)
        {
            Context.Translator.TraceGuestAccess(pc, address, size, write != 0);
        }

        [UnmanagedCallersOnly]
        public static void Break(ulong address, int imm)
        {
            Statistics.PauseTimer();

            GetContext().OnBreak(address, imm);

            Statistics.ResumeTimer();
        }

        [UnmanagedCallersOnly]
        public static void SupervisorCall(ulong address, int imm)
        {
            Statistics.PauseTimer();

            GetContext().OnSupervisorCall(address, imm);

            Statistics.ResumeTimer();
        }

        [UnmanagedCallersOnly]
        public static void Undefined(ulong address, int opCode)
        {
            Statistics.PauseTimer();

            GetContext().OnUndefined(address, opCode);

            Statistics.ResumeTimer();
        }

        #region "System registers"
        [UnmanagedCallersOnly]
        public static ulong GetCtrEl0()
        {
            return ExecutionContext.CtrEl0;
        }

        [UnmanagedCallersOnly]
        public static ulong GetDczidEl0()
        {
            return ExecutionContext.DczidEl0;
        }

        [UnmanagedCallersOnly]
        public static ulong GetCntfrqEl0()
        {
            return GetContext().CntfrqEl0;
        }

        [UnmanagedCallersOnly]
        public static ulong GetCntpctEl0()
        {
            return GetContext().CntpctEl0;
        }

        [UnmanagedCallersOnly]
        public static ulong GetCntvctEl0()
        {
            return GetContext().CntvctEl0;
        }
        #endregion

        #region "Read"
        [UnmanagedCallersOnly]
        public static byte ReadByte(ulong address)
        {
            return ReadGuestChecked<byte>(address);
        }

        [UnmanagedCallersOnly]
        public static ushort ReadUInt16(ulong address)
        {
            return ReadGuestChecked<ushort>(address);
        }

        [UnmanagedCallersOnly]
        public static uint ReadUInt32(ulong address)
        {
            return ReadGuestChecked<uint>(address);
        }

        [UnmanagedCallersOnly]
        public static ulong ReadUInt64(ulong address)
        {
            return ReadGuestChecked<ulong>(address);
        }

        [UnmanagedCallersOnly]
        public static V128 ReadVector128(ulong address)
        {
            return ReadGuestChecked<V128>(address);
        }
        #endregion

        #region "Write"
        [UnmanagedCallersOnly]
        public static void WriteByte(ulong address, byte value)
        {
            WriteGuestChecked(address, value);
        }

        [UnmanagedCallersOnly]
        public static void WriteUInt16(ulong address, ushort value)
        {
            WriteGuestChecked(address, value);
        }

        [UnmanagedCallersOnly]
        public static void WriteUInt32(ulong address, uint value)
        {
            WriteGuestChecked(address, value);
        }

        [UnmanagedCallersOnly]
        public static void WriteUInt64(ulong address, ulong value)
        {
            WriteGuestChecked(address, value);
        }

        [UnmanagedCallersOnly]
        public static void WriteVector128(ulong address, V128 value)
        {
            WriteGuestChecked(address, value);
        }
        #endregion

        // An exception cannot unwind through UnmanagedCallersOnly/JIT frames.
        // The emitter checks Running immediately after each memory callback and
        // returns to the managed dispatcher BEFORE consuming the return value.
        private static T ReadGuestChecked<T>(ulong address) where T : unmanaged
        {
            try { return GetMemoryManager().ReadGuest<T>(address); }
            catch (Exception exception)
            {
                RecordMemoryFault(exception);
                return default; // ABI placeholder; never committed to a guest register.
            }
        }

        private static void WriteGuestChecked<T>(ulong address, T value) where T : unmanaged
        {
            try { GetMemoryManager().WriteGuest(address, value); }
            catch (Exception exception) { RecordMemoryFault(exception); }
        }

        private static void RecordMemoryFault(Exception exception)
        {
            Context.Translator.RecordDispatchFault(exception);
            GetContext().StopRunning();
        }

        [UnmanagedCallersOnly]
        public static void EnqueueForRejit(ulong address)
        {
            Context.Translator.EnqueueForRejit(address, GetContext().ExecutionMode);
        }

        [UnmanagedCallersOnly]
        public static void SignalMemoryTracking(ulong address, ulong size, byte write)
        {
            try { GetMemoryManager().SignalMemoryTracking(address, size, write == 1); }
            catch (Exception exception) { RecordMemoryFault(exception); }
        }

        [UnmanagedCallersOnly]
        public static void ThrowInvalidMemoryAccess(ulong address)
        {
            RecordMemoryFault(new InvalidAccessException(address));
        }

        [UnmanagedCallersOnly]
        public static ulong GetFunctionAddress(ulong address)
        {
            try
            {
                // This callback is entered from generated guest code. In
                // particular, cache admission and compilation can fail while
                // resolving a previously unseen guest address. Do not let an
                // exception cross the unmanaged boundary: NativeAOT treats
                // that as an unhandled process exception rather than a guest
                // fault, which used to terminate the entire CoreHost.
                TranslatedFunction function = Context.Translator.GetOrTranslate(address, GetContext().ExecutionMode);

                return (ulong)function.FuncPointer.ToInt64();
            }
            catch (Exception exception)
            {
                Context.Translator.RecordDispatchFault(exception);
                GetContext().StopRunning();
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        public static ulong AcquirePinnedFunction(ulong address)
        {
            try
            {
                ExecutionContext execution = GetContext();
                // The previous guest call has returned. Service pending scheduler
                // work here and transfer its pin in ONE managed callback/lock,
                // rather than crossing native/managed twice for every function.
                execution.CheckPendingInterrupt();
                if (!execution.Running) return 0;
                return (ulong)Context.Translator.AcquireFunction(
                    execution.ThreadUid, address, execution.ExecutionMode, releasePrevious: true).FuncPointer;
            }
            catch (Exception exception)
            {
                // Never let an exception cross reverse P/Invoke/JIT frames.
                // Return through the dispatcher and report on its managed side.
                Context.Translator.RecordDispatchFault(exception);
                GetContext().StopRunning();
                return 0;
            }
        }

        [UnmanagedCallersOnly]
        public static void InvalidateCacheLine(ulong address)
        {
            Context.Translator.InvalidateJitCacheRegion(address, InstEmit.DczSizeInBytes);
        }

        [UnmanagedCallersOnly]
        public static byte CheckSynchronization()
        {
            Statistics.PauseTimer();

            ExecutionContext context = GetContext();

            context.CheckInterrupt();

            Statistics.ResumeTimer();

            return (byte)(context.Running ? 1 : 0);
        }

        public static ExecutionContext GetContext()
        {
            return Context.Context;
        }

        public static IMemoryManager GetMemoryManager()
        {
            return Context.Memory;
        }
    }
}
