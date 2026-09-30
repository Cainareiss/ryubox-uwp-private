using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Ryujinx.Common.Memory
{
    public enum MemoryCategory
    {
        Guest,
        Managed,
        Jit,
        Ptc,
        HlslSource,
        Compiler,
        ShaderCache,
        Texture,
        RenderTarget,
        Staging,
        Readback,
        Audio,
        Native,
        NativeAllocator,
        CompilerArena,
        Misc,
        D3D12Resources,
        D3D12Uploads,
    }

    public enum MemoryPressure
    {
        Normal,
        Warning,
        Soft = Warning,
        High,
        Critical,
        Emergency,
    }

    public readonly record struct MemoryBudgetSnapshot(
        long WorkingSet,
        long PrivateBytes,
        long ManagedHeap,
        long ManagedCommitted,
        long EffectiveUsage,
        long AppMemoryUsageLimit,
        long ExpectedAppMemoryUsageLimit,
        MemoryPressure Pressure,
        string LimitSource)
    {
        public override string ToString() =>
            $"workingSet={WorkingSet} privateCommit={PrivateBytes} managedHeap={ManagedHeap} " +
            $"managedCommitted={ManagedCommitted} effectiveUsage={EffectiveUsage} " +
            $"appMemoryLimit={AppMemoryUsageLimit} expectedAppMemoryLimit={ExpectedAppMemoryUsageLimit} " +
            $"pressure={Pressure} budgetSource={LimitSource}";
    }

    /// <summary>
    /// Process-wide admission controller for memory-heavy emulator work.
    /// Category counters are diagnostic and may overlap; pressure is therefore
    /// calculated from host commit/working-set metrics, never by summing them.
    /// </summary>
    public static class RuntimeMemoryBudget
    {
        private const long GiB = 1024L * 1024L * 1024L;
        private static long[] Bytes = NewCounters();
        private static long[] NewCounters() => Enumerable.Repeat(-1L, Enum.GetValues<MemoryCategory>().Length).ToArray();
        private static readonly SemaphoreSlim CompilerGate = new(1, 1);
        private static long _limit = 5L * GiB;
        private static long _expectedLimit = 5L * GiB;
        private static int _pressure;
        private static string _limitSource = "conservative-5GiB";
        private static MemoryBudgetSnapshot _lastSnapshot;
        private static long _lastCompilerTrimAt;
        private static long _lastAdmissionSampleAt;
        private static long _lastAdmissionUsage;
        private static int _trimActive;
        private static MemoryPressure _lastTrimPressure;

        public static event Action<MemoryPressure> TrimRequested;

        public static MemoryPressure Pressure => (MemoryPressure)Volatile.Read(ref _pressure);
        public static long Limit => Interlocked.Read(ref _limit);
        public static long ExpectedLimit => Interlocked.Read(ref _expectedLimit);
        public static MemoryBudgetSnapshot LastSnapshot => _lastSnapshot;

        /// <summary>
        /// Samples process usage without changing pressure state or invoking trim callbacks.
        /// This is safe for admission checks that run while allocator locks are held.
        /// </summary>
        public static long GetEffectiveUsageNoTrim()
        {
            long now = Environment.TickCount64;
            long sampledAt = Interlocked.Read(ref _lastAdmissionSampleAt);
            long cached = Interlocked.Read(ref _lastAdmissionUsage);
            if (cached > 0 && now - sampledAt < 100)
            {
                return cached;
            }

            long workingSet = Environment.WorkingSet;
            long privateBytes = 0;
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                privateBytes = process.PrivateMemorySize64;
            }
            catch
            {
                // Process information may be unavailable in older UWP sandboxes.
            }

            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long managedUsage = Math.Max(GC.GetTotalMemory(false), gcInfo.TotalCommittedBytes);
            long usage = Math.Max(Math.Max(workingSet, privateBytes), managedUsage);
            Interlocked.Exchange(ref _lastAdmissionUsage, usage);
            Interlocked.Exchange(ref _lastAdmissionSampleAt, now);
            return usage;
        }

        public static void Configure(ulong appMemoryLimit, ulong expectedAppMemoryLimit = 0)
        {
            const long fallback = 5L * GiB;
            long current = ToUsableLimit(appMemoryLimit, fallback);
            long expected = ToUsableLimit(expectedAppMemoryLimit, current);
            long useful = Math.Min(current, expected);

            Interlocked.Exchange(ref _limit, useful);
            Interlocked.Exchange(ref _expectedLimit, expected);
            _limitSource = appMemoryLimit > 0 || expectedAppMemoryLimit > 0
                ? "min(host-reported,expected,5GiB)"
                : "conservative-5GiB";
            Refresh();
        }

        public static void ResetSession()
        {
            // A late release from the previous session must update its own
            // counters, never subtract bytes from the new session.
            Interlocked.Exchange(ref Bytes, NewCounters());

            Volatile.Write(ref _pressure, (int)MemoryPressure.Normal);
            _lastSnapshot = default;
            Interlocked.Exchange(ref _lastCompilerTrimAt, 0);
            Interlocked.Exchange(ref _lastAdmissionSampleAt, 0);
            Interlocked.Exchange(ref _lastAdmissionUsage, 0);
        }

        public static void Observe(MemoryCategory category, long bytes) =>
            Interlocked.Exchange(ref Volatile.Read(ref Bytes)[(int)category], bytes);

        public static long GetObservedBytes(MemoryCategory category) =>
            Interlocked.Read(ref Volatile.Read(ref Bytes)[(int)category]);

        public static bool CanAdmitGuestCommit(long used, long limit, long bytes) =>
            used >= 0 && bytes >= 0 && limit > 0 &&
            bytes <= Math.Max(0, limit - 128L * 1024 * 1024) &&
            used <= Math.Max(0, limit - 128L * 1024 * 1024) - bytes;

        public static void CheckGuestCommit(long bytes, ulong address)
        {
            if (bytes == 0) return;
            // This runs under kernel memory locks: do not call Refresh/trim
            // callbacks here, as subscribers can acquire unrelated GPU locks.
            using Process process = Process.GetCurrentProcess();
            long used = Math.Max(process.PrivateMemorySize64, Environment.WorkingSet);
            if (!CanAdmitGuestCommit(used, Limit, bytes))
            {
                throw new OutOfMemoryException(
                    $"Guest commit refused: physicalOffset=0x{address:X}, requested={bytes}, " +
                    $"processUsage={used}, limit={Limit}, headroom=134217728.");
            }
        }

        public static IDisposable Track(MemoryCategory category, long bytes)
        {
            if (bytes < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            long[] counters = Volatile.Read(ref Bytes);
            ref long counter = ref counters[(int)category];
            Interlocked.CompareExchange(ref counter, 0, -1);
            Interlocked.Add(ref counter, bytes);
            return new Lease(() => Interlocked.Add(ref counters[(int)category], -bytes));
        }

        public static MemoryPressure Classify(long used, long limit)
        {
            if (limit <= 0)
            {
                return MemoryPressure.Normal;
            }

            double fraction = (double)used / limit;
            return fraction >= .96 ? MemoryPressure.Emergency :
                fraction >= .90 ? MemoryPressure.Critical :
                fraction >= .82 ? MemoryPressure.High :
                fraction >= .72 ? MemoryPressure.Warning :
                MemoryPressure.Normal;
        }

        public static MemoryBudgetSnapshot Refresh()
        {
            long workingSet = Environment.WorkingSet;
            long privateBytes = 0;
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                privateBytes = process.PrivateMemorySize64;
            }
            catch
            {
                // Process information may be restricted by an older UWP runtime.
            }

            GCMemoryInfo gcInfo = GC.GetGCMemoryInfo();
            long managedHeap = GC.GetTotalMemory(false);
            long managedCommitted = gcInfo.TotalCommittedBytes;
            Observe(MemoryCategory.Managed, managedCommitted > 0 ? managedCommitted : managedHeap);

            long effectiveUsage = Math.Max(Math.Max(workingSet, privateBytes), managedCommitted);
            // Refresh is an explicit new sample, including after trimming.
            // Do not let the admission cache retain an older, higher reading.
            Interlocked.Exchange(ref _lastAdmissionUsage, effectiveUsage);
            Interlocked.Exchange(ref _lastAdmissionSampleAt, Environment.TickCount64);
            MemoryPressure next = ClassifyWithHeadroom(effectiveUsage, Limit);
            MemoryPressure previous = (MemoryPressure)Interlocked.Exchange(ref _pressure, (int)next);
            _lastSnapshot = new MemoryBudgetSnapshot(
                workingSet,
                privateBytes,
                managedHeap,
                managedCommitted,
                effectiveUsage,
                Limit,
                ExpectedLimit,
                next,
                _limitSource);

            if (next >= MemoryPressure.Warning && next != previous)
            {
                RequestTrim(next);
            }

            return _lastSnapshot;
        }

        public static string Sample(long workingSet, long managedBytes)
        {
            MemoryBudgetSnapshot snapshot = Refresh();
            if (workingSet > 0 && snapshot.WorkingSet == 0)
            {
                snapshot = snapshot with { WorkingSet = workingSet };
            }
            Observe(MemoryCategory.Managed, managedBytes);
            string counters = string.Join(" ", Enum.GetValues<MemoryCategory>().Distinct().Select(category =>
            {
                long bytes = GetObservedBytes(category);
                return category + "=" + (bytes < 0 ? "untracked" : bytes.ToString());
            }));
            return $"MEM {snapshot} {counters} categoriesNotAdditive=true diagnosticRecordsDropped={Logging.BufferedDiagnosticWriter.DroppedRecords}";
        }

        public static int RecommendedPtcBatchSize => Pressure switch
        {
            MemoryPressure.Emergency or MemoryPressure.Critical => 0,
            MemoryPressure.High => ReadPositiveInt("Ryujinx.Uwp.PtcBatch.High", 128),
            MemoryPressure.Warning => ReadPositiveInt("Ryujinx.Uwp.PtcBatch.Warning", 256),
            _ => ReadPositiveInt("Ryujinx.Uwp.PtcBatch.Normal", 512),
        };

        public static IDisposable EnterCompiler(long estimatedScratchBytes = 0)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(estimatedScratchBytes);
            CompilerGate.Wait();
            try
            {
                MemoryBudgetSnapshot snapshot = Refresh();
                RequestTrim(snapshot.Pressure);

                long usage = GetEffectiveUsageNoTrim();
                if (!CanAdmitGuestCommit(usage, Limit, estimatedScratchBytes))
                {
                    RequestTrim(MemoryPressure.Critical, admissionFailure: true);
                    usage = Refresh().EffectiveUsage;
                    if (!CanAdmitGuestCommit(usage, Limit, estimatedScratchBytes))
                        throw new OutOfMemoryException(
                            $"Shader compiler admission refused after recovery: processUsage={usage}, " +
                            $"limit={Limit}, estimatedScratch={estimatedScratchBytes}, headroom=134217728.");
                }

                IDisposable scratch = Track(MemoryCategory.Compiler, estimatedScratchBytes);
                long compilerStart = Stopwatch.GetTimestamp();
                Logging.RuntimeGpuTelemetry.CompilerBegin();
                return new Lease(() =>
                {
                    Logging.RuntimeGpuTelemetry.CompilerEnd(compilerStart);
                    scratch.Dispose();
                    CompilerGate.Release();
                });
            }
            catch
            {
                CompilerGate.Release();
                throw;
            }
        }

        // One process-wide owner for recovery. Refresh, compiler admission and
        // subscribers must not cascade several full GCs for the same pressure.
        public static bool RequestTrim(MemoryPressure pressure, bool admissionFailure = false)
        {
            if (pressure < MemoryPressure.Warning || Interlocked.CompareExchange(ref _trimActive, 1, 0) != 0) return false;
            try
            {
                long now = Environment.TickCount64;
                long elapsed = now - Interlocked.Read(ref _lastCompilerTrimAt);
                if (!admissionFailure && _lastCompilerTrimAt != 0 && elapsed < 5000 &&
                    !(pressure > _lastTrimPressure && elapsed >= 500)) return false;
                _lastTrimPressure = pressure;
                Interlocked.Exchange(ref _lastCompilerTrimAt, now);
                long before = GC.GetTotalMemory(false);
                long started = Stopwatch.GetTimestamp();
                TrimRequested?.Invoke(pressure);
                if (pressure >= MemoryPressure.Critical)
                {
                    // Compaction is an emergency tool, not every shader's cost.
                    if (pressure == MemoryPressure.Emergency) GCSettingsCompat.CompactOnce();
                    // An allocation already rejected by admission gets one
                    // actual collection before its retry. Optimized collection
                    // may do nothing, and the routine cooldown must not turn a
                    // recoverable temporary peak into a fatal JIT failure.
                    GC.Collect(2, admissionFailure ? GCCollectionMode.Forced : GCCollectionMode.Optimized,
                        blocking: true, compacting: false);
                }
                Logging.RuntimeDiagnosticLog.Write("memory-trim",
                    $"pressure={pressure} admissionFailure={admissionFailure} elapsedMs={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} managedBefore={before} managedAfter={GC.GetTotalMemory(false)} cooldownMs=5000");
                return true;
            }
            finally { Volatile.Write(ref _trimActive, 0); }
        }

        private static MemoryPressure ClassifyWithHeadroom(long used, long limit)
        {
            long warning = ReadThreshold("Ryujinx.Uwp.Memory.WarningBytes", 330L * GiB / 100, limit, .72);
            long high = ReadThreshold("Ryujinx.Uwp.Memory.HighBytes", 350L * GiB / 100, limit, .82);
            long critical = ReadThreshold("Ryujinx.Uwp.Memory.CriticalBytes", 370L * GiB / 100, limit, .90);
            long emergency = ReadThreshold("Ryujinx.Uwp.Memory.EmergencyBytes", 385L * GiB / 100, limit, .96);

            return used >= emergency ? MemoryPressure.Emergency :
                used >= critical ? MemoryPressure.Critical :
                used >= high ? MemoryPressure.High :
                used >= warning ? MemoryPressure.Warning :
                MemoryPressure.Normal;
        }

        private static long ReadThreshold(string key, long defaultBytes, long limit, double fraction)
        {
            object configured = AppContext.GetData(key);
            if (configured is long longValue && longValue > 0)
            {
                return longValue;
            }
            if (configured is string text && long.TryParse(text, out long parsed) && parsed > 0)
            {
                return parsed;
            }
            return limit > 0 ? Math.Min(defaultBytes, (long)(limit * fraction)) : defaultBytes;
        }

        private static long ToUsableLimit(ulong reported, long fallback)
        {
            if (reported == 0 || reported > long.MaxValue)
            {
                return fallback;
            }
            // Permit up to 6 GiB on hosts that actually report that allowance;
            // Xbox's reported 5 GiB quota still clamps the process to 5 GiB.
            long maxLimit = string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_MEMORY_6GIB"), "1", StringComparison.OrdinalIgnoreCase)
                ? 6L * GiB
                : 5L * GiB;
            return Math.Min((long)reported, maxLimit);
        }

        private static int ReadPositiveInt(string key, int defaultValue)
        {
            object configured = AppContext.GetData(key);
            if (configured is int integer && integer > 0)
            {
                return integer;
            }
            if (configured is string text && int.TryParse(text, out int parsed) && parsed > 0)
            {
                return parsed;
            }
            return defaultValue;
        }

        private sealed class Lease : IDisposable
        {
            private Action _release;

            public Lease(Action release) => _release = release;

            public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
        }

        private static class GCSettingsCompat
        {
            public static void CompactOnce()
            {
                try
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                }
                catch
                {
                    // Optional on constrained runtimes.
                }
            }
        }
    }
}
