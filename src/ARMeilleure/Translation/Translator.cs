using ARMeilleure.CodeGen;
using ARMeilleure.Common;
using ARMeilleure.Decoders;
using ARMeilleure.Diagnostics;
using ARMeilleure.Instructions;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation.Cache;
using ARMeilleure.Translation.PTC;
using Ryujinx.Common;
using Ryujinx.Common.Memory;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Translation
{
    public class Translator
    {
        private readonly IJitMemoryAllocator _allocator;
        private readonly EpochReclaimer<TranslatedFunction> _retiredFunctions;
        private readonly Lock _functionPublishLock = new();
        private readonly Lock _translationLock = new();
        private readonly JitReclaimPolicy _reclaimPolicy = new();
        private long _invalidationGeneration;
        private readonly PinnedReclaimer<TranslatedFunction> _pinnedFunctions;
        private readonly Dictionary<ulong, TranslatedFunction> _executingFunctions = new();
        // Guarded by _functionPublishLock, just like the authoritative tree.
        // EnqueueForDeletion removes entries BEFORE executable memory can be freed.
        private readonly Dictionary<ulong, TranslatedFunction> _pinnedLookupCache = new();
        internal bool EnablePinnedLookupCache { get; set; } =
            Environment.GetEnvironmentVariable("RYUJINX_UWP_PINNED_LOOKUP_CACHE") == "1";
        private Exception _dispatchFault;
        private readonly GuestAccessProbe _guestAccessProbe = GuestAccessProbe.Parse(
            Environment.GetEnvironmentVariable("RYUJINX_UWP_GUEST_ACCESS_PROBE"));

        internal void TraceGuestAccess(ulong pc, ulong address, int size, bool write)
        {
            string observation = _guestAccessProbe?.Observe(pc, address, size, write, Memory);
            if (observation != null) Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Cpu, observation);
        }
        private readonly GuestDispatchProbe _guestDispatchProbe = GuestDispatchProbe.Parse(
            Environment.GetEnvironmentVariable("RYUJINX_UWP_GUEST_PROBE"));

        internal void TraceGuestBlock(ulong address, ARMeilleure.State.ExecutionContext context, IMemoryManager memory)
        {
            _guestDispatchProbe?.TraceBlock(address, context, memory);
        }
        internal static bool UsePinnedDispatch => AppContext.TryGetSwitch("Ryujinx.Uwp.PinnedJitDispatch", out bool enabled) && enabled;

        public readonly JitCache JitCache;
        private readonly Ptc _ptc;

        internal TranslatorCache<TranslatedFunction> Functions { get; }
        public IAddressTable<ulong> FunctionTable { get; }
        internal EntryTable<uint> CountTable { get; }
        internal TranslatorStubs Stubs { get; }
        internal TranslatorQueue Queue { get; }
        internal IMemoryManager Memory { get; }

        private Thread[] _backgroundTranslationThreads;
        private volatile int _threadCount;

        private static long _uwpDispatchLookups;
        private static long _uwpTranslationsStarted;
        private static long _uwpTranslationsCompleted;
        private static long _uwpActiveTranslations;
        private static long _uwpLastGuestAddress;
        private static long _uwpLastTranslationStartedAt;
        private static long _uwpLastTranslationCompletedAt;
        private static long _uwpColdWaitTicks;
        private static long _uwpCompileTicks;
        private static long _uwpInvalidatedCompilations;
        private static long _uwpReclaimPasses;
        private static long _uwpReclaimTicks;
        private static long _uwpReclaimedFunctions;
        private static readonly ConcurrentDictionary<ulong, UwpGuestDispatchState> _uwpGuestDispatchStates = new();
        [ThreadStatic]
        private static UwpGuestDispatchState _uwpThreadDispatchState;

        private sealed class UwpGuestDispatchState
        {
            public ulong ThreadUid;
            public long CurrentAddress;
            public long CurrentStartedAt;
            public long CompletedFunctions;
            public long LastNextAddress;
            public long LastCompletedAt;
            public long SupervisorCalls;
            public long LastSupervisorAddress;
            public int LastSupervisorImmediate;
            public long LastSupervisorAt;
        }

        public static string GetUwpSchedulingTelemetry()
        {
            bool safeJit = AppContext.TryGetSwitch(
                "Ryujinx.Uwp.SafeJit",
                out bool safeJitEnabled) && safeJitEnabled;
            bool baselineNativeDispatch = Optimizations.UseUnmanagedDispatchLoop;
            bool backgroundTranslationDisabled = AppContext.TryGetSwitch(
                "Ryujinx.Uwp.DisableBackgroundTranslation",
                out bool backgroundTranslationDisabledEnabled) && backgroundTranslationDisabledEnabled;

            string threadDetails = string.Join(
                "; ",
                _uwpGuestDispatchStates
                    .OrderBy(pair => pair.Key)
                    .Take(12)
                    .Select(pair => string.Format(
                        "uid={0}/current=0x{1:X}/inFunctionFor={2}ms/completed={3}/next=0x{4:X}/lastReturn={5}/svcCount={6}/lastSvc=0x{7:X}:0x{8:X}/lastSvcAt={9}",
                        pair.Key,
                        unchecked((ulong)Interlocked.Read(ref pair.Value.CurrentAddress)),
                        Math.Max(0, Environment.TickCount64 - Interlocked.Read(ref pair.Value.CurrentStartedAt)),
                        Interlocked.Read(ref pair.Value.CompletedFunctions),
                        unchecked((ulong)Interlocked.Read(ref pair.Value.LastNextAddress)),
                        Interlocked.Read(ref pair.Value.LastCompletedAt),
                        Interlocked.Read(ref pair.Value.SupervisorCalls),
                        unchecked((ulong)Interlocked.Read(ref pair.Value.LastSupervisorAddress)),
                        Volatile.Read(ref pair.Value.LastSupervisorImmediate),
                        Interlocked.Read(ref pair.Value.LastSupervisorAt))));

            return string.Format(
                "jitMode={0}, dispatchLookups={1}, translations={2}/{3}, activeTranslations={4}, lastGuestAddress=0x{5:X}, lastTranslationStart={6}, lastTranslationEnd={7}, dispatchThreads={8}: {9}",
                safeJit
                    ? baselineNativeDispatch ? "baseline-native" : "baseline-managed"
                    : backgroundTranslationDisabled ? "baseline-no-rejit" : "dynamic-highcq",
                Interlocked.Read(ref _uwpDispatchLookups),
                Interlocked.Read(ref _uwpTranslationsCompleted),
                Interlocked.Read(ref _uwpTranslationsStarted),
                Interlocked.Read(ref _uwpActiveTranslations),
                unchecked((ulong)Interlocked.Read(ref _uwpLastGuestAddress)),
                Interlocked.Read(ref _uwpLastTranslationStartedAt),
                Interlocked.Read(ref _uwpLastTranslationCompletedAt),
                _uwpGuestDispatchStates.Count,
                threadDetails.Length == 0 ? "none" : threadDetails) +
                $"; jitColdWaitMs={Interlocked.Read(ref _uwpColdWaitTicks) * 1000.0 / Stopwatch.Frequency:F2}" +
                $", jitCompileMs={Interlocked.Read(ref _uwpCompileTicks) * 1000.0 / Stopwatch.Frequency:F2}" +
                $", jitInvalidatedCompilations={Interlocked.Read(ref _uwpInvalidatedCompilations)}" +
                $", jitReclaimPasses={Interlocked.Read(ref _uwpReclaimPasses)}" +
                $", jitReclaimMs={Interlocked.Read(ref _uwpReclaimTicks) * 1000.0 / Stopwatch.Frequency:F2}" +
                $", jitReclaimedFunctions={Interlocked.Read(ref _uwpReclaimedFunctions)}";
        }

        public static void RecordUwpSupervisorCall(ulong threadUid, ulong address, int immediate)
        {
            if (!AppContext.TryGetSwitch("Ryujinx.Uwp.DeepTrace", out bool enabled) || !enabled)
            {
                return;
            }

            UwpGuestDispatchState state = GetUwpGuestDispatchState(threadUid);
            state.SupervisorCalls++;
            Volatile.Write(ref state.LastSupervisorAddress, unchecked((long)address));
            Volatile.Write(ref state.LastSupervisorImmediate, immediate);
            Volatile.Write(ref state.LastSupervisorAt, Environment.TickCount64);
        }

        private static UwpGuestDispatchState GetUwpGuestDispatchState(ulong threadUid)
        {
            UwpGuestDispatchState state = _uwpThreadDispatchState;

            if (state == null || state.ThreadUid != threadUid)
            {
                state = _uwpGuestDispatchStates.GetOrAdd(
                    threadUid,
                    static uid => new UwpGuestDispatchState { ThreadUid = uid });
                _uwpThreadDispatchState = state;
            }

            return state;
        }

        private static bool UseUwpSafeJit()
        {
            return AppContext.TryGetSwitch(
                "Ryujinx.Uwp.SafeJit",
                out bool safeJitEnabled) && safeJitEnabled;
        }

        private static void ResetUwpSchedulingTelemetry()
        {
            Interlocked.Exchange(ref _uwpDispatchLookups, 0);
            Interlocked.Exchange(ref _uwpTranslationsStarted, 0);
            Interlocked.Exchange(ref _uwpTranslationsCompleted, 0);
            Interlocked.Exchange(ref _uwpActiveTranslations, 0);
            Interlocked.Exchange(ref _uwpLastGuestAddress, 0);
            Interlocked.Exchange(ref _uwpLastTranslationStartedAt, 0);
            Interlocked.Exchange(ref _uwpLastTranslationCompletedAt, 0);
            Interlocked.Exchange(ref _uwpColdWaitTicks, 0);
            Interlocked.Exchange(ref _uwpCompileTicks, 0);
            Interlocked.Exchange(ref _uwpInvalidatedCompilations, 0);
            Interlocked.Exchange(ref _uwpReclaimPasses, 0);
            Interlocked.Exchange(ref _uwpReclaimTicks, 0);
            Interlocked.Exchange(ref _uwpReclaimedFunctions, 0);
            _uwpGuestDispatchStates.Clear();
        }

        public Translator(IJitMemoryAllocator allocator, IMemoryManager memory, IAddressTable<ulong> functionTable)
        {
            _allocator = allocator;
            Memory = memory;

            _retiredFunctions = new EpochReclaimer<TranslatedFunction>(func =>
            {
                JitCache.Unmap(func.FuncPointer);
                func.CallCounter?.Dispose();
            });

            JitCache = new JitCache(allocator);
            _pinnedFunctions = new PinnedReclaimer<TranslatedFunction>(func =>
            {
                JitCache.Unmap(func.FuncPointer);
                func.CallCounter?.Dispose();
            });
            
            _ptc = new Ptc(JitCache);

            Queue = new TranslatorQueue();


            CountTable = new EntryTable<uint>();
            Functions = new TranslatorCache<TranslatedFunction>();
            
            FunctionTable = functionTable;
            
            Stubs = new TranslatorStubs(JitCache, FunctionTable);

            FunctionTable.Fill = (ulong)Stubs.SlowDispatchStub;
        }

        public IPtcLoadState LoadDiskCache(PtcCacheInfo cacheInfo, bool enabled)
        {
            _ptc.Initialize(cacheInfo, enabled && _guestAccessProbe == null, Memory.Type);
            return _ptc;
        }

        public void PrepareCodeRange(ulong address, ulong size)
        {
            if (_ptc.Profiler.StaticCodeSize == 0)
            {
                _ptc.Profiler.StaticCodeStart = address;
                _ptc.Profiler.StaticCodeSize = size;
            }
        }

        public void Execute(State.ExecutionContext context, ulong address)
        {
            int threadCountAtEntry = Interlocked.Increment(ref _threadCount);
            bool registered = false;
            try
            {
            if (threadCountAtEntry == 1)
            {
                ResetUwpSchedulingTelemetry();

                if (_ptc.State == PtcState.Enabled)
                {
                    Debug.Assert(Functions.Count == 0);
                    _ptc.LoadTranslations(this);
                    _ptc.MakeAndSaveTranslations(this);
                }

                _ptc.Profiler.Start();

                _ptc.Disable();

                // Simple heuristic, should be user configurable in future. (1 for 4 core/ht or less, 2 for 6 core + ht
                // etc). All threads are normal priority except from the last, which just fills as much of the last core
                // as the os lets it with a low priority. If we only have one rejit thread, it should be normal priority
                // as highCq code is performance critical.
                //
                // TODO: Use physical cores rather than logical. This only really makes sense for processors with
                // hyperthreading. Requires OS specific code.
                bool constrainedScheduling = AppContext.TryGetSwitch(
                    "Ryujinx.Uwp.ConstrainedScheduling",
                    out bool constrainedSchedulingEnabled) && constrainedSchedulingEnabled;
                bool disableBackgroundTranslation = AppContext.TryGetSwitch(
                    "Ryujinx.Uwp.DisableBackgroundTranslation",
                    out bool backgroundTranslationDisabled) && backgroundTranslationDisabled;
                bool safeJit = UseUwpSafeJit();
                int unboundedThreadCount = Math.Max(1, (Environment.ProcessorCount - 6) / 3);
                int threadCount = Math.Min(4, unboundedThreadCount);

                if (safeJit || disableBackgroundTranslation)
                {
                    // NativeAOT/Xbox diagnostics showed the guest becoming stuck
                    // after thousands of baseline functions were replaced by
                    // background High-CQ versions. Keep the known-good baseline
                    // code for the complete session. This also avoids replacing
                    // function-table entries while guest threads are executing.
                    threadCount = 0;
                }
                else if (constrainedScheduling)
                {
                    // Xbox Dev Mode exposes a tight CPU/memory sandbox. A single
                    // low-priority high-CQ worker leaves host time for the GPU,
                    // audio and composition threads during cold JIT startup.
                    threadCount = 1;
                }

                Thread[] backgroundTranslationThreads = new Thread[threadCount];

                for (int i = 0; i < threadCount; i++)
                {
                    bool last = i != 0 && i == unboundedThreadCount - 1;

                    backgroundTranslationThreads[i] = new(BackgroundTranslate)
                    {
                        Name = "CPU.BackgroundTranslatorThread." + i,
                        Priority = constrainedScheduling || last
                            ? ThreadPriority.Lowest
                            : ThreadPriority.Normal,
                    };

                    backgroundTranslationThreads[i].Start();
                }

                Interlocked.Exchange(ref _backgroundTranslationThreads, backgroundTranslationThreads);
                Ryujinx.Common.Logging.Logger.Info?.Print(Ryujinx.Common.Logging.LogClass.Cpu,
                    $"Applied JIT policy: safeJit={safeJit}, highCq={!safeJit && !disableBackgroundTranslation}, backgroundWorkers={threadCount}, pinnedDispatch={UsePinnedDispatch}, ptcEnabled={_ptc.Profiler.Enabled}");
            }

            Statistics.InitializeTimer();

            NativeInterface.RegisterThread(context, Memory, this);
            _retiredFunctions.Enter(context.ThreadUid);
            registered = true;
            if (Optimizations.EnableDebugging)
            {
                context.DebugPc = address;
                do
                {
                    GuestDispatchBoundary(context.ThreadUid);
                    if (Interlocked.CompareExchange(ref context.ShouldStep, 0, 1) == 1)
                    {
                        context.DebugPc = Step(context, context.DebugPc);
                        context.StepHandler();
                    }
                    else
                    {
                        context.DebugPc = ExecuteSingle(context, context.DebugPc);
                    }
                    context.CheckPendingInterrupt();
                }
                while (context.Running && context.DebugPc != 0);
            }
            else if (Optimizations.UseUnmanagedDispatchLoop)
            {
                Stubs.DispatchLoop(context.NativeContextPtr, address);
            }
            else
            {
                do
                {
                    address = ExecuteSingle(context, address);
                    // The managed UWP dispatcher must service the same
                    // scheduler interrupts as the debugger and native
                    // dispatcher paths. Without this check a completed guest
                    // function can immediately enter the next one forever
                    // while a reschedule request remains pending.
                    context.CheckPendingInterrupt();
                }
                while (context.Running && address != 0);
            }
            if (_dispatchFault != null) throw new InvalidOperationException("Guest JIT dispatch failed.", _dispatchFault);
            }
            finally
            {
            ReleasePinnedFunction(context.ThreadUid);
            Allocators.DisposeAll();
            if (registered)
            {
                _retiredFunctions.Exit(context.ThreadUid);
                NativeInterface.UnregisterThread();
            }
            _uwpGuestDispatchStates.TryRemove(context.ThreadUid, out _);
            _uwpThreadDispatchState = null;

            if (Interlocked.Decrement(ref _threadCount) == 0)
            {
                Queue.Dispose();

                Thread[] backgroundTranslationThreads = Interlocked.Exchange(ref _backgroundTranslationThreads, null);

                if (backgroundTranslationThreads != null)
                {
                    foreach (Thread thread in backgroundTranslationThreads)
                    {
                        thread.Join();
                    }
                }

                ClearJitCache();
                JitCache.Dispose();

                Stubs.Dispose();
                FunctionTable.Dispose();
                CountTable.Dispose();

                _ptc.Close();
                _ptc.Profiler.Stop();

                _ptc.Dispose();
                _ptc.Profiler.Dispose();
            }
            }
        }

        internal void GuestDispatchBoundary(ulong threadUid)
        {
            ReleasePinnedFunction(threadUid);
            _retiredFunctions.Boundary(threadUid);
        }

        private void ReleasePinnedFunction(ulong threadUid)
        {
            if (!UsePinnedDispatch) return;
            lock (_functionPublishLock)
            {
                if (_executingFunctions.Remove(threadUid, out var previous)) _pinnedFunctions.Unpin(previous);
            }
        }

        internal TranslatedFunction AcquireFunction(ulong threadUid, ulong address, ExecutionMode mode, bool releasePrevious = false)
        {
            if (_dispatchFault != null) throw new InvalidOperationException("JIT session faulted.", _dispatchFault);
            _guestDispatchProbe?.Trace(address, Memory);
            if (!UsePinnedDispatch) return GetOrTranslate(address, mode);
            Interlocked.Increment(ref _uwpDispatchLookups);
            Interlocked.Exchange(ref _uwpLastGuestAddress, unchecked((long)address));
            lock (_functionPublishLock)
            {
                if (releasePrevious && _executingFunctions.TryGetValue(threadUid, out var previous))
                {
                    // A tight guest loop often re-enters the very same translation.
                    // Keep its existing pin instead of removing/reinserting it and
                    // walking the translation tree. Invalidation marks it retired
                    // under this same lock, so obsolete code can NEVER use this path.
                    if (previous.GuestAddress == address && !_pinnedFunctions.IsRetired(previous))
                    {
                        Volatile.Write(ref previous.LastLookupTick, Environment.TickCount64);
                        return previous;
                    }
                    _executingFunctions.Remove(threadUid);
                    _pinnedFunctions.Unpin(previous);
                }
                if (_executingFunctions.ContainsKey(threadUid)) throw new InvalidOperationException("Nested pinned JIT dispatch.");
                if (EnablePinnedLookupCache && _pinnedLookupCache.TryGetValue(address, out var hot)) return PinFunction(threadUid, hot);
                if (Functions.TryGetValue(address, out var cached)) return PinFunction(threadUid, cached);
            }

            // Serialize cold compilation, not cache hits or invalidation. The
            // compiler uses thread-local arenas, but keeping one cold publisher
            // here preserves the already validated pinned-dispatch ordering.
            long waitStarted = Stopwatch.GetTimestamp();
            lock (_translationLock)
            {
                Interlocked.Add(ref _uwpColdWaitTicks, Stopwatch.GetTimestamp() - waitStarted);
                while (true)
                {
                    long generation;
                    lock (_functionPublishLock)
                    {
                        if (Functions.TryGetValue(address, out var cached)) return PinFunction(threadUid, cached);
                        generation = _invalidationGeneration;
                    }

                    TranslatedFunction function = Translate(address, mode, highCq: false, invalidationGeneration: generation);
                    lock (_functionPublishLock)
                    {
                        // Code may have been modified while the compiler read it.
                        // Never publish an obsolete/mixed translation after IC invalidation.
                        if (generation != _invalidationGeneration)
                        {
                            Interlocked.Increment(ref _uwpInvalidatedCompilations);
                            JitCache.Unmap(function.FuncPointer);
                            function.CallCounter?.Dispose();
                            continue;
                        }

                        TranslatedFunction published = Functions.GetOrAdd(address, function.GuestSize, function);
                        if (published != function)
                        {
                            JitCache.Unmap(function.FuncPointer);
                            function.CallCounter?.Dispose();
                        }
                        else
                        {
                            if (_ptc.Profiler.Enabled) _ptc.Profiler.AddEntry(address, mode, highCq: false);
                            RegisterFunction(address, function);
                        }
                        return PinFunction(threadUid, published);
                    }
                }
            }
        }

        // Lookup and pin must remain atomic with respect to invalidation/reclaim.
        private TranslatedFunction PinFunction(ulong threadUid, TranslatedFunction function)
        {
            Volatile.Write(ref function.LastLookupTick, Environment.TickCount64);
            _pinnedFunctions.Pin(function);
            _executingFunctions.Add(threadUid, function);
            if (EnablePinnedLookupCache)
            {
                if (_pinnedLookupCache.Count >= 512) _pinnedLookupCache.Clear();
                _pinnedLookupCache[function.GuestAddress] = function;
            }
            return function;
        }

        internal void RecordDispatchFault(Exception exception) => Interlocked.CompareExchange(ref _dispatchFault, exception, null);

        private ulong ExecuteSingle(State.ExecutionContext context, ulong address)
        {
            // Pinned managed dispatch releases the previous function and pins
            // the next under ONE publication-lock acquisition. Native dispatch
            // keeps its explicit boundary; epoch mode keeps its reader boundary.
            if (!UsePinnedDispatch) GuestDispatchBoundary(context.ThreadUid);
            UwpGuestDispatchState dispatchState = GetUwpGuestDispatchState(context.ThreadUid);
            Volatile.Write(ref dispatchState.CurrentAddress, unchecked((long)address));
            Volatile.Write(ref dispatchState.CurrentStartedAt, Environment.TickCount64);

            TranslatedFunction func = AcquireFunction(context.ThreadUid, address, context.ExecutionMode, releasePrevious: true);

            Statistics.StartTimer();

            context.ResetCallDepth();
            ulong nextAddr = func.Execute(Stubs.ContextWrapper, context);

            dispatchState.CompletedFunctions++;
            Volatile.Write(ref dispatchState.LastNextAddress, unchecked((long)nextAddr));
            Volatile.Write(ref dispatchState.LastCompletedAt, Environment.TickCount64);

            Statistics.StopTimer(address);

            return nextAddr;
        }

        private ulong Step(State.ExecutionContext context, ulong address)
        {
            TranslatedFunction func = Translate(address, context.ExecutionMode, highCq: false, singleStep: true);

            address = func.Execute(Stubs.ContextWrapper, context);

            EnqueueForDeletion(address, func);

            return address;
        }



        internal TranslatedFunction GetOrTranslate(ulong address, ExecutionMode mode)
        {
            // Epoch dispatch may hit its native function table without entering
            // this path. The probe observes lookups, not every cached call.
            _guestDispatchProbe?.Trace(address, Memory);
            Interlocked.Increment(ref _uwpDispatchLookups);
            Interlocked.Exchange(ref _uwpLastGuestAddress, unchecked((long)address));

            if (!Functions.TryGetValue(address, out TranslatedFunction func))
            {
                func = Translate(address, mode, highCq: false);

                TranslatedFunction oldFunc = Functions.GetOrAdd(address, func.GuestSize, func);

                if (oldFunc != func)
                {
                    JitCache.Unmap(func.FuncPointer);
                    func = oldFunc;
                }

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.AddEntry(address, mode, highCq: false);
                }

                RegisterFunction(address, func);
            }

            Volatile.Write(ref func.LastLookupTick, Environment.TickCount64);
            return func;
        }

        internal void RegisterFunction(ulong guestAddress, TranslatedFunction func)
        {
            lock (_functionPublishLock)
            {
            func.GuestAddress = guestAddress;
            // A concurrent invalidation may have removed this translation while
            // its caller still owns a protected reference. Never republish it.
            if (!Functions.TryGetValue(guestAddress, out var current) || current != func) return;
            if (FunctionTable.IsValid(guestAddress))
            {
                Volatile.Write(ref FunctionTable.GetValue(guestAddress),
                    Optimizations.AllowLcqInFunctionTable || func.HighCq ? (ulong)func.FuncPointer : FunctionTable.Fill);
            }
            }
        }

        internal TranslatedFunction Translate(ulong address, ExecutionMode mode, bool highCq, bool singleStep = false, bool pptcTranslation = false, long? invalidationGeneration = null)
        {
            long started = Stopwatch.GetTimestamp();
            Interlocked.Increment(ref _uwpActiveTranslations);
            try
            {
                return TranslateCore(address, mode, highCq, singleStep, pptcTranslation, invalidationGeneration);
            }
            finally
            {
                Interlocked.Decrement(ref _uwpActiveTranslations);
                Interlocked.Add(ref _uwpCompileTicks, Stopwatch.GetTimestamp() - started);
                // IR arenas are dead after emission/mapping, even if either fails.
                // Keeping them on a live guest or PTC thread retains native memory.
                Allocators.ResetAll();
            }
        }

        private TranslatedFunction TranslateCore(ulong address, ExecutionMode mode, bool highCq, bool singleStep, bool pptcTranslation, long? invalidationGeneration)
        {
            if (!pptcTranslation)
            {
                long started = Interlocked.Read(ref _uwpTranslationsStarted);
                int reclaimMask = RuntimeMemoryBudget.Pressure >= MemoryPressure.High ? 15 : 63;
                if ((started & reclaimMask) == 0)
                {
                    RetireColdTranslations(address);
                }
            }
            if (UseUwpSafeJit())
            {
                highCq = false;
            }

            Interlocked.Increment(ref _uwpTranslationsStarted);
            Interlocked.Exchange(ref _uwpLastGuestAddress, unchecked((long)address));
            Interlocked.Exchange(ref _uwpLastTranslationStartedAt, Environment.TickCount64);

            ArmEmitterContext context = new(
                Memory,
                CountTable,
                FunctionTable,
                Stubs,
                address,
                highCq,
                _ptc.State != PtcState.Disabled,
                mode: Aarch32Mode.User,
                isSingleStep: singleStep);
            context.AccessProbe = _guestAccessProbe;

            Logger.StartPass(PassName.Decoding);

            Block[] blocks = Decoder.Decode(Memory, address, mode, highCq, singleStep ? DecoderMode.SingleInstruction : DecoderMode.MultipleBlocks);

            Logger.EndPass(PassName.Decoding);

            Logger.StartPass(PassName.Translation);

            InstEmitFlowHelper.EmitCallDepthCheckAndIncrement(context, Const(address));
            EmitSynchronization(context);

            if (blocks[0].Address != address)
            {
                context.Branch(context.GetLabel(address));
            }

            ControlFlowGraph cfg = EmitAndGetCFG(context, blocks, out Range funcRange, out Counter<uint> counter, pptcTranslation);

            if (cfg == null)
            {
                Interlocked.Increment(ref _uwpTranslationsCompleted);
                Interlocked.Exchange(ref _uwpLastTranslationCompletedAt, Environment.TickCount64);
                return null;
            }

            ulong funcSize = funcRange.End - funcRange.Start;

            Logger.EndPass(PassName.Translation, cfg);

            Logger.StartPass(PassName.RegisterUsage);

            RegisterUsage.RunPass(cfg, mode);

            Logger.EndPass(PassName.RegisterUsage);

            OperandType retType = OperandType.I64;
            OperandType[] argTypes = [OperandType.I64];

            CompilerOptions options = highCq ? CompilerOptions.HighCq : CompilerOptions.None;

            if (context.HasPtc && !singleStep)
            {
                options |= CompilerOptions.Relocatable;
            }

            CompiledFunction compiledFunc = Compiler.Compile(cfg, argTypes, retType, options, RuntimeInformation.ProcessArchitecture);

            if (context.HasPtc && !singleStep)
            {
                lock (_functionPublishLock)
                {
                    // Do not pair obsolete compiled bytes with a new source hash
                    // when IC invalidation raced the cold compiler.
                    if (invalidationGeneration == null || invalidationGeneration == _invalidationGeneration)
                    {
                        Hash128 hash = Ptc.ComputeHash(Memory, address, funcSize);
                        _ptc.WriteCompiledFunction(address, funcSize, hash, highCq, compiledFunc);
                    }
                }
            }

            GuestFunction func;
            nint funcPointer;
            try
            {
                func = compiledFunc.MapWithPointer<GuestFunction>(JitCache, out funcPointer);
            }
            catch (JitCacheFullException exception) when (!pptcTranslation)
            {
                // Retry the already compiled bytes, not the whole compiler.
                // Active functions remain pinned; no live return address moves.
                var before = JitCache.GetDetailedTelemetry();
                Ryujinx.Common.Logging.Logger.Warning?.Print(
                    Ryujinx.Common.Logging.LogClass.Cpu,
                    $"JIT admission recovery: guestPc=0x{address:X16}, requestedBytes={compiledFunc.Code.Length}, " +
                    $"regions={before.Regions}, committedBytes={before.CommittedBytes}, freeBytes={before.FreeBytes}, " +
                    $"largestFreeBlock={before.LargestFreeBlock}, pressure={RuntimeMemoryBudget.Pressure}; cause={exception.Message}");
                // Map has released its allocator lock. Request process cleanup
                // here, then sample again: admission must see released memory,
                // not the pre-trim high-water sample. This recovery also applies
                // to the shipping native-dispatch profile.
                RuntimeMemoryBudget.RequestTrim(Ryujinx.Common.Memory.MemoryPressure.Critical, admissionFailure: true);
                RuntimeMemoryBudget.Refresh();
                // Only pinned dispatch can synchronously reclaim cold code.
                // Native-dispatch return stacks still require their epoch grace
                // period; never force their retirement to satisfy this retry.
                if (UsePinnedDispatch) RetireColdTranslations(address, compiledFunc.Code.Length);
                try
                {
                    func = compiledFunc.MapWithPointer<GuestFunction>(JitCache, out funcPointer);
                }
                catch (JitCacheFullException retryException)
                {
                    var after = JitCache.GetDetailedTelemetry();
                    Ryujinx.Common.Logging.Logger.Error?.Print(
                        Ryujinx.Common.Logging.LogClass.Cpu,
                        $"JIT admission cancelled: guestPc=0x{address:X16}, requestedBytes={compiledFunc.Code.Length}, " +
                        $"regions={after.Regions}, committedBytes={after.CommittedBytes}, freeBytes={after.FreeBytes}, " +
                        $"largestFreeBlock={after.LargestFreeBlock}, allocationFailures={after.AllocationFailures}, " +
                        $"pressure={RuntimeMemoryBudget.Pressure}; retryCause={retryException.Message}");
                    throw new JitCacheFullException(
                        $"JIT compilation cancelled by memory budget at guest PC 0x{address:X16} after admission recovery (pinnedDispatch={UsePinnedDispatch}).",
                        retryException);
                }
            }

            long completedTranslations = Interlocked.Increment(ref _uwpTranslationsCompleted);
            Interlocked.Exchange(ref _uwpLastTranslationCompletedAt, Environment.TickCount64);

            if (!highCq
                && (completedTranslations & 3) == 0
                && AppContext.TryGetSwitch("Ryujinx.Uwp.ConstrainedScheduling", out bool constrainedScheduling)
                && constrainedScheduling)
            {
                // Long bursts of baseline compilation can otherwise monopolize
                // every CPU quantum granted to an Xbox UWP app. Sleep(0) yields
                // only to ready peers and does not introduce a fixed delay.
                Thread.Sleep(0);
            }

            return new TranslatedFunction(func, funcPointer, counter, funcSize, highCq);
        }

        private void BackgroundTranslate()
        {
            try { BackgroundTranslateCore(); }
            catch (Exception exception) when (UsePinnedDispatch)
            {
                RecordDispatchFault(exception);
            }
            finally { Allocators.DisposeAll(); }
        }

        private void BackgroundTranslateCore()
        {
            while (_threadCount != 0 && Queue.TryDequeue(out RejitRequest request))
            {
                TranslatedFunction func = Translate(request.Address, request.Mode, highCq: true);

                lock (_functionPublishLock)
                {
                    TranslatedFunction retired = null;
                    Functions.AddOrUpdate(request.Address, func.GuestSize, func, (key, oldFunc) =>
                    {
                        retired = oldFunc;
                        return func;
                    });
                    RegisterFunction(request.Address, func);
                    if (retired != null) EnqueueForDeletion(request.Address, retired);
                }

                if (_ptc.Profiler.Enabled)
                {
                    _ptc.Profiler.UpdateEntry(request.Address, request.Mode, highCq: true);
                }

                RegisterFunction(request.Address, func);
            }
        }

        private readonly struct Range
        {
            public ulong Start { get; }
            public ulong End { get; }

            public Range(ulong start, ulong end)
            {
                Start = start;
                End = end;
            }
        }

        private ControlFlowGraph EmitAndGetCFG(
            ArmEmitterContext context,
            Block[] blocks,
            out Range range,
            out Counter<uint> counter,
            bool pptcTranslation)
        {
            counter = null;

            ulong rangeStart = ulong.MaxValue;
            ulong rangeEnd = 0;

            for (int blkIndex = 0; blkIndex < blocks.Length; blkIndex++)
            {
                Block block = blocks[blkIndex];

                if (!block.Exit)
                {
                    if (rangeStart > block.Address)
                    {
                        rangeStart = block.Address;
                    }

                    if (rangeEnd < block.EndAddress)
                    {
                        rangeEnd = block.EndAddress;
                    }
                }

                if (block.Address == context.EntryAddress)
                {
                    if (!context.HighCq)
                    {
                        EmitRejitCheck(context, out counter);
                    }

                    context.ClearQcFlag();
                }

                context.CurrBlock = block;

                context.MarkLabel(context.GetLabel(block.Address));

                if (_guestDispatchProbe?.Contains(block.Address) == true)
                {
                    context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.TraceGuestBlock)), Const(block.Address));
                }

                if (UseUwpSafeJit())
                {
                    // Indirect/self-modifying guest control flow is not always
                    // represented as a statically visible backward edge. A
                    // bounded check at each basic-block entry ensures Xbox
                    // scheduler requests can break those loops as well.
                    EmitSynchronization(context);
                }

                if (block.Exit)
                {
                    // Return to managed rather than tail call.
                    bool useReturns = Optimizations.EnableDebugging;

                    if (Optimizations.EnableDebugging)
                    {
                        EmitDebugPrecisePcUpdate(context, block.Address);
                    }

                    InstEmitFlowHelper.EmitVirtualJump(context, Const(block.Address), isReturn: useReturns);
                }
                else
                {
                    for (int opcIndex = 0; opcIndex < block.OpCodes.Count; opcIndex++)
                    {
                        OpCode opCode = block.OpCodes[opcIndex];

                        context.CurrOp = opCode;

                        bool isLastOp = opcIndex == block.OpCodes.Count - 1;

                        if (isLastOp)
                        {
                            context.SyncQcFlag();

                            if (block.Branch is { Exit: false } && block.Branch.Address <= block.Address)
                            {
                                EmitSynchronization(context);
                            }
                        }

                        if (Optimizations.EnableDebugging)
                        {
                            EmitDebugPrecisePcUpdate(context, opCode.Address);
                        }

                        Operand lblPredicateSkip = default;

                        if (context.IsInIfThenBlock && context.CurrentIfThenBlockCond != Condition.Al)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, context.CurrentIfThenBlockCond.Inverse);
                        }

                        if (opCode is OpCode32 { Cond: < Condition.Al } op)
                        {
                            lblPredicateSkip = Label();

                            InstEmitFlowHelper.EmitCondBranch(context, lblPredicateSkip, op.Cond.Inverse);
                        }

                        if (opCode.Instruction.Emitter != null)
                        {
                            opCode.Instruction.Emitter(context);
                            // if we're pre-compiling PPTC functions, and we hit an Undefined instruction as the first
                            // instruction in the block, mark the function as blacklisted
                            // this way, we don't pre-compile Exlaunch hooks, which allows ExeFS mods to run with PPTC 
                            if (pptcTranslation && opCode.Instruction.Name == InstName.Und && blkIndex == 0)
                            {
                                range = new Range(rangeStart, rangeEnd);
                                return null;
                            }
                        }
                        else
                        {
                            throw new InvalidOperationException($"Invalid instruction \"{opCode.Instruction.Name}\".");
                        }

                        if (lblPredicateSkip != default)
                        {
                            context.MarkLabel(lblPredicateSkip);
                        }

                        if (context.IsInIfThenBlock && opCode.Instruction.Name != InstName.It)
                        {
                            context.AdvanceIfThenBlockState();
                        }
                    }
                }
            }

            range = new Range(rangeStart, rangeEnd);

            return context.GetControlFlowGraph();
        }

        internal static void EmitRejitCheck(ArmEmitterContext context, out Counter<uint> counter)
        {
            bool disableBackgroundTranslation = AppContext.TryGetSwitch(
                "Ryujinx.Uwp.DisableBackgroundTranslation",
                out bool backgroundTranslationDisabled) && backgroundTranslationDisabled;

            if (UseUwpSafeJit() || disableBackgroundTranslation)
            {
                // Safe-JIT deliberately never queues High-CQ replacement code.
                // A null counter also prevents needless per-function allocations.
                counter = null;
                return;
            }

            const int MinsCallForRejit = 100;

            counter = new Counter<uint>(context.CountTable);

            Operand lblEnd = Label();

            Operand address = !context.HasPtc ?
                Const(ref counter.Value) :
                Const(ref counter.Value, Ptc.CountTableSymbol);

            Operand curCount = context.Load(OperandType.I32, address);
            Operand count = context.Add(curCount, Const(1));
            context.Store(address, count);
            context.BranchIf(lblEnd, curCount, Const(MinsCallForRejit), Comparison.NotEqual, BasicBlockFrequency.Cold);

            context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.EnqueueForRejit)), Const(context.EntryAddress));

            context.MarkLabel(lblEnd);
        }

        internal static void EmitSynchronization(EmitterContext context)
        {
            long countOffs = NativeContext.GetCounterOffset();

            Operand lblNonZero = Label();
            Operand lblExit = Label();

            Operand countAddr = context.Add(context.LoadArgument(OperandType.I64, 0), Const(countOffs));
            Operand count = context.Load(OperandType.I32, countAddr);
            context.BranchIfTrue(lblNonZero, count, BasicBlockFrequency.Cold);

            Operand running = context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.CheckSynchronization)));
            context.BranchIfTrue(lblExit, running, BasicBlockFrequency.Cold);

            context.Return(Const(0L));

            context.MarkLabel(lblNonZero);
            count = context.Subtract(count, Const(1));
            context.Store(countAddr, count);

            context.MarkLabel(lblExit);
        }

        internal static void EmitDebugPrecisePcUpdate(EmitterContext context, ulong address)
        {
            long debugPrecisePcOffs = NativeContext.GetDebugPrecisePcOffset();

            Operand debugPrecisePcAddr = context.Add(context.LoadArgument(OperandType.I64, 0), Const(debugPrecisePcOffs));
            context.Store(debugPrecisePcAddr, Const(address));
        }

        public void InvalidateJitCacheRegion(ulong address, ulong size)
        {
            lock (_functionPublishLock)
            {
            _invalidationGeneration++;
            ulong[] overlapAddresses = [];

            int overlapsCount = Functions.GetOverlaps(address, size, ref overlapAddresses);

            if (overlapsCount != 0)
            {
                // If rejit is running, stop it as it may be trying to rejit a function on the invalidated region.
                ClearRejitQueue(allowRequeue: true);
            }

            for (int index = 0; index < overlapsCount; index++)
            {
                ulong overlapAddress = overlapAddresses[index];

                if (Functions.TryGetValue(overlapAddress, out TranslatedFunction overlap))
                {
                    Functions.Remove(overlapAddress);
                    if (FunctionTable.IsValid(overlapAddress))
                        Volatile.Write(ref FunctionTable.GetValue(overlapAddress), FunctionTable.Fill);
                    EnqueueForDeletion(overlapAddress, overlap);
                }
            }

            }
        }

        internal void EnqueueForRejit(ulong guestAddress, ExecutionMode mode)
        {
            Queue.Enqueue(guestAddress, mode);
        }

        private void EnqueueForDeletion(ulong guestAddress, TranslatedFunction func)
        {
            if (UsePinnedDispatch)
            {
                lock (_functionPublishLock)
                {
                    _pinnedLookupCache.Remove(func.GuestAddress);
                    _pinnedFunctions.Retire(func);
                }
            }
            else _retiredFunctions.Retire(func);
        }

        private void RetireColdTranslations(ulong requestedAddress, int requiredBytes = 0)
        {
            lock (_functionPublishLock)
            {
                long now = Environment.TickCount64;
                if (requiredBytes == 0 && !_reclaimPolicy.TrySchedule(now)) return;
                var memory = JitCache.GetDetailedTelemetry();
                if (requiredBytes == 0 && (!JitReclaimPolicy.NeedsReclaim(memory.ReservedBytes, memory.FreeBytes) ||
                    JitReclaimPolicy.PreferGrowth(memory.Regions, JitCache.MaxRegions,
                        RuntimeMemoryBudget.GetEffectiveUsageNoTrim(), RuntimeMemoryBudget.Limit) ||
                    // A reader sleeping in a supervisor call can retain this
                    // batch. Retiring more entries would only allow duplicate
                    // recompilation while none of the old code can be freed.
                    (!UsePinnedDispatch && _retiredFunctions.Pending != 0))) return;

                int maximum = JitReclaimPolicy.MaximumBatch;
                long cutoff = now - JitReclaimPolicy.MinimumAgeMs;
                if (requiredBytes > 0)
                {
                    cutoff = long.MaxValue;
                    maximum = int.MaxValue;
                }
                long started = Stopwatch.GetTimestamp();
                Interlocked.Increment(ref _uwpReclaimPasses);
                var candidates = Functions.AsList()
                    .Where(func => func.GuestAddress != requestedAddress && Volatile.Read(ref func.LastLookupTick) < cutoff &&
                        (!UsePinnedDispatch || !_pinnedFunctions.IsPinned(func)))
                    .OrderBy(func => Volatile.Read(ref func.LastLookupTick)).Take(maximum).ToArray();
                int evicted = 0;
                foreach (var func in candidates)
                {
                    if (!Functions.TryGetValue(func.GuestAddress, out var current) || current != func) continue;
                    Functions.Remove(func.GuestAddress);
                    if (FunctionTable.IsValid(func.GuestAddress))
                        Volatile.Write(ref FunctionTable.GetValue(func.GuestAddress), FunctionTable.Fill);
                    JitCache.RecordEviction();
                    EnqueueForDeletion(func.GuestAddress, func);
                    evicted++;
                    // Maintenance checks in small batches; allocation-failure
                    // recovery checks every release to stop as soon as it fits.
                    if (requiredBytes == 0 && (evicted & 15) != 0) continue;
                    var remaining = JitCache.GetDetailedTelemetry();
                    if (requiredBytes > 0 ? remaining.LargestFreeBlock >= (long)requiredBytes + 4 :
                        remaining.FreeBytes >= JitReclaimPolicy.TargetFreeBytes) break;
                }

                Interlocked.Add(ref _uwpReclaimTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Add(ref _uwpReclaimedFunctions, evicted);

                if (candidates.Length != 0)
                {
                    Ryujinx.Common.Logging.Logger.Info?.Print(
                        Ryujinx.Common.Logging.LogClass.Cpu,
                        $"JIT cold-code eviction scheduled: functions={evicted}, pressure={RuntimeMemoryBudget.Pressure}, " +
                        $"requiredBytes={requiredBytes}, elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}, " +
                        $"pending={(UsePinnedDispatch ? _pinnedFunctions.Pending : _retiredFunctions.Pending)}; {memory}");
                }
            }
        }

        private void ClearJitCache()
        {
            _pinnedLookupCache.Clear();
            // Ensure no attempt will be made to compile new functions due to rejit.
            ClearRejitQueue(allowRequeue: false);

            List<TranslatedFunction> functions = Functions.AsList();

            foreach (TranslatedFunction func in functions)
            {
                JitCache.Unmap(func.FuncPointer);

                func.CallCounter?.Dispose();
            }

            Functions.Clear();

            Debug.Assert(_retiredFunctions.Pending == 0);
        }

        private void ClearRejitQueue(bool allowRequeue)
        {
            if (!allowRequeue)
            {
                Queue.Clear();

                return;
            }

            lock (Queue.Sync)
            {
                while (Queue.Count > 0 && Queue.TryDequeue(out RejitRequest request))
                {
                    if (Functions.TryGetValue(request.Address, out TranslatedFunction func) && func.CallCounter != null)
                    {
                        Volatile.Write(ref func.CallCounter.Value, 0);
                    }
                }
            }
        }

        public void StopBackgroundTranslators()
        {
            Thread[] threads = Volatile.Read(ref _backgroundTranslationThreads);
            if (threads != null)
            {
                Queue.Dispose();
                foreach (Thread thread in threads)
                {
                    if (thread != null && thread.IsAlive && thread != Thread.CurrentThread)
                    {
                        thread.Join();
                    }
                }
                Interlocked.CompareExchange(ref _backgroundTranslationThreads, null, threads);
            }
        }
    }
}
