using ARMeilleure.CodeGen;
using ARMeilleure.CodeGen.Unwinding;
using ARMeilleure.Memory;
using ARMeilleure.Native;
using Humanizer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Memory;
using Ryujinx.Memory;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ARMeilleure.Translation.Cache
{
    internal sealed class JitCacheFullException : OutOfMemoryException
    {
        public JitCacheFullException(string message) : base(message) { }
        public JitCacheFullException(string message, Exception innerException) : base(message, innerException) { }
    }
    public partial class JitCache : IDisposable
    {
        private static readonly int _pageSize = (int)MemoryBlock.GetPageSize();
        private static readonly int _pageMask = _pageSize - 1;

        private const int CodeAlignment = 4; // Bytes.
        private const uint LegacyRegionSize = 256 * 1024 * 1024;
        // Region granularity (MiB, 64..256, multiple of 16) read once per process
        // from "Ryujinx.Uwp.JitCacheRegionMiB". Smaller regions commit/reserve
        // in finer steps; region counts are scaled so total capacity is unchanged.
        private static readonly uint CacheSize = GetConfiguredRegionSize();
        private static int RegionScale => (int)(LegacyRegionSize / CacheSize);
        private const int DefaultMaxRegions = 2;

        private readonly JitCacheInvalidation _jitCacheInvalidator;

        private readonly CacheMemoryAllocator _cacheAllocator;

        private readonly List<CacheEntry> _cacheEntries = [];

        private readonly Lock _lock = new();

        private readonly List<ReservedRegion> _jitRegions = [];
        private readonly int _maxRegions;
        internal int MaxRegions => _maxRegions;
        private static JitCache _activeInstance;
        private bool _disposed;
        private long _allocations;
        private long _unmaps;
        private long _reclaimedBytes;
        private long _allocationFailures;
        private long _evictions;
        private long _allocatedBytes;
        private long _telemetryAllocatedBytes;
        private long _telemetryTimestamp = Environment.TickCount64;

        public void RecordEviction() => Interlocked.Increment(ref _evictions);

        public readonly record struct Telemetry(ulong CommittedBytes, ulong ReservedBytes,
            long FreeBytes, int LargestFreeBlock, int FreeBlocks, int Regions, int Entries,
            long Allocations, long Unmaps, long ReclaimedBytes, long AllocationFailures, long Evictions,
            long AllocationRateBytesPerSecond, double Fragmentation)
        {
            public override string ToString() =>
                $"jitCommitted={CommittedBytes} jitReserved={ReservedBytes} jitUsed={Math.Max(0, (long)ReservedBytes - FreeBytes)} " +
                $"jitFree={FreeBytes} regionCount={Regions} functionCount={Entries} allocationRate={AllocationRateBytesPerSecond}B/s " +
                $"reclaimCount={Unmaps} evictionCount={Evictions} fragmentation={Fragmentation:F4} " +
                $"largestFreeBlock={LargestFreeBlock} freeBlocks={FreeBlocks} allocationFailures={AllocationFailures}";
        }

        public static Telemetry GetDetailedTelemetry()
        {
            JitCache instance = Volatile.Read(ref _activeInstance);
            if (instance == null) return default;
            lock (instance._lock)
            {
                if (instance._disposed) return default;
                ulong committed = 0;
                foreach (ReservedRegion region in instance._jitRegions) committed += region.CurrentSize;
                var free = instance._cacheAllocator.GetTelemetry();
                long now = Environment.TickCount64;
                long elapsed = Math.Max(1, now - instance._telemetryTimestamp);
                long allocated = instance._allocatedBytes;
                long allocationRate = Math.Max(0, allocated - instance._telemetryAllocatedBytes) * 1000 / elapsed;
                instance._telemetryAllocatedBytes = allocated;
                instance._telemetryTimestamp = now;
                double fragmentation = free.FreeBytes <= 0
                    ? 0
                    : 1.0 - Math.Clamp((double)free.LargestBlock / free.FreeBytes, 0.0, 1.0);
                RuntimeMemoryBudget.Observe(MemoryCategory.Jit, checked((long)committed));
                return new(committed, (ulong)instance._jitRegions.Count * CacheSize,
                    free.FreeBytes, free.LargestBlock, free.FreeBlocks,
                    instance._jitRegions.Count, instance._cacheEntries.Count,
                    instance._allocations, instance._unmaps, instance._reclaimedBytes, instance._allocationFailures,
                    Interlocked.Read(ref instance._evictions), allocationRate, fragmentation);
            }
        }

        public static (ulong CommittedBytes, int RegionsCount, int EntriesCount) GetUwpTelemetry()
        {
            Telemetry telemetry = GetDetailedTelemetry();
            return (telemetry.CommittedBytes, telemetry.Regions, telemetry.Entries);
        }

        [SupportedOSPlatform("windows")]
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint FlushInstructionCache(nint hProcess, nint lpAddress, nuint dwSize);

        public JitCache(IJitMemoryAllocator allocator)
        {
            lock (_lock)
            {
                _maxRegions = GetConfiguredMaxRegions();
                _jitRegions.Add(new(allocator, CacheSize));

                _cacheAllocator = new((int)CacheSize);

                if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
                {
                    _jitCacheInvalidator = new JitCacheInvalidation(allocator);
                }

                if (OperatingSystem.IsWindows())
                {
                    JitUnwindWindows.InstallFunctionTableHandler(
                        this, _jitRegions[0].Pointer, CacheSize, _jitRegions[0].Pointer + Allocate(_pageSize)
                    );
                }
                Volatile.Write(ref _activeInstance, this);
            }
        }

        public nint Map(CompiledFunction func)
        {
            byte[] code = func.Code;

            lock (_lock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                int funcOffset = Allocate(code.Length);
                nint funcPtr = GetFunctionPtr(funcOffset);

                if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                {
                    unsafe
                    {
                        fixed (byte* codePtr = code)
                        {
                            JitSupportDarwin.Copy(funcPtr, (nint)codePtr, (ulong)code.Length);
                        }
                    }
                }
                else
                {
                    ReprotectAsWritable(funcOffset, code.Length);
                    Marshal.Copy(code, 0, funcPtr, code.Length);
                    ReprotectAsExecutable(funcOffset, code.Length);

                    if (OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
                    {
                        FlushInstructionCache(Process.GetCurrentProcess().Handle, funcPtr, (nuint)code.Length);
                    }
                    else
                    {
                        _jitCacheInvalidator?.Invalidate(funcPtr, (ulong)code.Length);
                    }
                }

                Add(funcOffset, code.Length, func.UnwindInfo);
                _allocatedBytes += AlignCodeSize(code.Length);

                return funcPtr;
            }
        }

        public void Unmap(nint pointer)
        {
            lock (_lock)
            {
                for (int i = 0; i < _jitRegions.Count; i++)
                {
                    ReservedRegion jitRegion = _jitRegions[i];
                    if (pointer.ToInt64() < jitRegion.Pointer.ToInt64() ||
                        pointer.ToInt64() >= (jitRegion.Pointer + (nint)CacheSize).ToInt64())
                    {
                        continue;
                    }

                    int funcOffset = (int)(pointer.ToInt64() - jitRegion.Pointer.ToInt64() + i * CacheSize);

                    if (TryFind(funcOffset, out CacheEntry entry, out int entryIndex) && entry.Offset == funcOffset)
                    {
                        _cacheAllocator.Free(funcOffset, AlignCodeSize(entry.Size));
                        _cacheEntries.RemoveAt(entryIndex);
                        _unmaps++;
                        _reclaimedBytes += AlignCodeSize(entry.Size);
                    }

                    return;
                }
            }
        }

        private void ReprotectAsWritable(int offset, int size)
        {
            int endOffs = offset + size;
            int regionStart = (offset % (int)CacheSize) & ~_pageMask;
            int regionEnd = endOffs % (int)CacheSize == 0
                ? (((int)CacheSize) + _pageMask) & ~_pageMask
                : ((endOffs % (int)CacheSize) + _pageMask) & ~_pageMask;
            
            GetRegion(offset).Block.MapAsRwx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private void ReprotectAsExecutable(int offset, int size)
        {
            int endOffs = offset + size;
            int regionStart = (offset % (int)CacheSize) & ~_pageMask;
            int regionEnd = endOffs % (int)CacheSize == 0
                ? (((int)CacheSize) + _pageMask) & ~_pageMask
                : ((endOffs % (int)CacheSize) + _pageMask) & ~_pageMask;

            GetRegion(offset).Block.MapAsRx((ulong)regionStart, (ulong)(regionEnd - regionStart));
        }

        private int Allocate(int codeSize)
        {
            codeSize = AlignCodeSize(codeSize);

            int allocOffset = _cacheAllocator.Allocate(codeSize);

            if (allocOffset >= 0)
            {
                CommitAllocation(allocOffset, codeSize);
                return allocOffset;
            }

            if (_jitRegions.Count >= _maxRegions)
            {
                _allocationFailures++;
                var free = _cacheAllocator.GetTelemetry();
                Logger.Error?.Print(LogClass.Cpu, $"JIT admission failed: requestedBytes={codeSize}, freeBytes={free.FreeBytes}, largestFreeBlock={free.LargestBlock}, freeBlocks={free.FreeBlocks}, allocations={_allocations}, unmaps={_unmaps}, reclaimedBytes={_reclaimedBytes}");
                throw new JitCacheFullException(
                    $"JIT cache configured capacity exhausted ({_jitRegions.Count} regions, " +
                    $"{(_jitRegions.Count * CacheSize).Bytes()}, pressure={RuntimeMemoryBudget.Pressure}).");
            }

            // Do not invoke Refresh here: its trim callbacks run external code
            // while this cache lock is held. Use the host's sampled budget.
            // Reservation leaves process headroom; physical commitment is
            // checked separately for each expansion by CommitAllocation.
            long effectiveUsage = RuntimeMemoryBudget.GetEffectiveUsageNoTrim();
            if (_jitRegions.Count >= DefaultMaxRegions * RegionScale &&
                !CanGrowWithinBudget(effectiveUsage, RuntimeMemoryBudget.Limit))
            {
                _allocationFailures++;
                throw new JitCacheFullException(
                    $"JIT cache growth denied by process budget: usage={effectiveUsage}, " +
                    $"limit={RuntimeMemoryBudget.Limit}, regions={_jitRegions.Count}.");
            }

            ReservedRegion newRegion = new(_jitRegions[0].Allocator, CacheSize);
            
            Logger.Warning?.Print(LogClass.Cpu, $"JIT Cache of size {(_jitRegions.Count * CacheSize).Bytes()} exhausted, creating new Cache Region ({((_jitRegions.Count + 1) * CacheSize).Bytes()} Total Allocation).");

            _jitRegions.Add(newRegion);
            _cacheAllocator.AddNewBlocks(1);
            
            allocOffset = _cacheAllocator.Allocate(codeSize);
            if (allocOffset < 0)
            {
                throw new OutOfMemoryException("Failed to allocate in new Cache Region!");
            }

            CommitAllocation(allocOffset, codeSize);
            return allocOffset;
        }

        private void CommitAllocation(int offset, int size)
        {
            try
            {
                // Region reservation is cheap, but each new code page consumes
                // process commit. The old admission check only ran when adding
                // a region, so a nearly-full Xbox process could still call
                // VirtualAlloc and bring down CoreHost with ERROR_COMMITMENT_LIMIT.
                // Leave the same headroom used for a new region and report a
                // recoverable JIT admission failure instead.
                ReservedRegion region = GetRegion(offset);
                ulong desiredSize = (ulong)(offset % (int)CacheSize) + (ulong)size;
                int additionalCommit = checked((int)region.GetAdditionalCommitSize(desiredSize));
                long effectiveUsage = RuntimeMemoryBudget.GetEffectiveUsageNoTrim();
                // Reusing retired code does not consume commit. Charging its
                // size again makes eviction recovery fail under pressure.
                if (additionalCommit > 0 && effectiveUsage > 0 && RuntimeMemoryBudget.Limit > 0 &&
                    !CanCommitWithinBudget(effectiveUsage, RuntimeMemoryBudget.Limit, additionalCommit))
                {
                    throw new JitCacheFullException(
                        $"JIT code-page commit denied by process budget: usage={effectiveUsage}, " +
                        $"limit={RuntimeMemoryBudget.Limit}, requested={size}, additionalCommit={additionalCommit}, regions={_jitRegions.Count}.");
                }

                region.ExpandIfNeeded(desiredSize);
                _allocations++;
            }
            catch (Exception exception) when (exception is OutOfMemoryException || exception is Win32Exception)
            {
                // The allocator slot must be returned and the dispatcher must
                // receive a managed failure, never a reverse P/Invoke crash.
                _cacheAllocator.Free(offset, size);
                _allocationFailures++;
                throw new JitCacheFullException("JIT code-page commit failed.", exception);
            }
            catch
            {
                // MEM_COMMIT failure previously leaked the allocator slot even
                // though no function had been installed there.
                _cacheAllocator.Free(offset, size);
                _allocationFailures++;
                throw;
            }
        }

        private nint GetFunctionPtr(int offset)
        {
            return GetRegion(offset).Pointer + (offset % (int)CacheSize);
        }

        private ReservedRegion GetRegion(int offset)
        {
            int index = offset / (int)CacheSize;

            return _jitRegions[index];
        }

        private static int AlignCodeSize(int codeSize)
        {
            return checked(codeSize + (CodeAlignment - 1)) & ~(CodeAlignment - 1);
        }

        private static bool CanGrowWithinBudget(long usage, long limit)
        {
            // Reserve process headroom; committed pages are admitted separately below.
            const long headroom = 128L * 1024 * 1024;
            return usage > 0 && limit >= headroom && usage <= limit - headroom;
        }

        private static bool CanCommitWithinBudget(long usage, long limit, int requestedBytes)
        {
            // MEM_COMMIT is independently preflighted against the reported
            // process quota; keep emergency room for GPU/OS operations.
            const long headroom = 128L * 1024 * 1024;
            return limit >= headroom + requestedBytes && usage <= limit - headroom - requestedBytes;
        }

        private static int GetConfiguredMaxRegions()
        {
            object configured = AppContext.GetData("Ryujinx.Uwp.JitCacheMaxRegions");
            if (configured is int configuredInteger)
            {
                return Math.Clamp(configuredInteger, DefaultMaxRegions, 8) * RegionScale;
            }

            if (configured is string configuredText && int.TryParse(configuredText, out int configuredValue))
            {
                return Math.Clamp(configuredValue, DefaultMaxRegions, 8) * RegionScale;
            }

            return DefaultMaxRegions * RegionScale;
        }

        private static uint GetConfiguredRegionSize()
        {
            object configured = AppContext.GetData("Ryujinx.Uwp.JitCacheRegionMiB");
            int mib = configured switch
            {
                int value => value,
                string text when int.TryParse(text, out int parsed) => parsed,
                _ => 256,
            };
            // Only exact divisors of 256 MiB keep capacity scaling exact.
            return mib is 64 or 128 ? (uint)mib * 1024 * 1024 : LegacyRegionSize;
        }

        private void Add(int offset, int size, UnwindInfo unwindInfo)
        {
            CacheEntry entry = new(offset, size, unwindInfo);

            int index = _cacheEntries.BinarySearch(entry);

            if (index < 0)
            {
                index = ~index;
            }

            _cacheEntries.Insert(index, entry);
        }

        public bool TryFind(int offset, out CacheEntry entry, out int entryIndex)
        {
            lock (_lock)
            {
                int index = _cacheEntries.BinarySearch(new CacheEntry(offset, 0, default));

                if (index < 0)
                {
                    index = ~index - 1;
                }

                if (index >= 0)
                {
                    entry = _cacheEntries[index];
                    entryIndex = index;
                    return true;
                }
            }

            entry = default;
            entryIndex = 0;
            return false;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                Interlocked.CompareExchange(ref _activeInstance, null, this);
                if (OperatingSystem.IsWindows())
                {
                    JitUnwindWindows.RemoveFunctionTableHandler(_jitRegions[0].Pointer);
                }

                foreach (ReservedRegion jitRegion in _jitRegions)
                {
                    jitRegion.Dispose();
                }

                _jitRegions.Clear();
                _cacheEntries.Clear();
            }
        }
    }
}
