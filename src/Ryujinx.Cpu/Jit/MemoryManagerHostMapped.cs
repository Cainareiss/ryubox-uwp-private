using ARMeilleure.Memory;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using Ryujinx.Memory.Tracking;
using System;
using System.Buffers;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Ryujinx.Cpu.Jit
{
    /// <summary>
    /// Represents a CPU memory manager which maps guest virtual memory directly onto a host virtual region.
    /// </summary>
    public sealed class MemoryManagerHostMapped : VirtualMemoryManagerRefCountedBase, IMemoryManager, IVirtualMemoryManagerTracked
    {
        private readonly InvalidAccessHandler _invalidAccessHandler;
        private readonly bool _unsafeMode;

        private readonly AddressSpace _addressSpace;

        private readonly PageTable<ulong> _pageTable;

        private readonly MemoryEhMeilleure _memoryEh;

        private readonly bool _lazyGuestCommit;
        private readonly ulong _lazyCommitGranularity;
        private readonly long[] _lazyCommittedBlocks;

        private static long _uwpLazyCommitAttempts;
        private static long _uwpLazyCommittedBytes;
        private static long _uwpLazyCommitFailures;
        private static long _uwpLazyCommitLastError;
        private static long _uwpLazyCommitLastAddress;
        private static long _uwpLazyCommitLastSize;
        private static long _uwpLazyCommitLastWorkingSet;
        private static string _uwpLazyCommitLastMessage = "none";

        private readonly ManagedPageFlags _pages;

        /// <inheritdoc/>
        public bool UsesPrivateAllocations => false;

        public int AddressSpaceBits { get; }

        public nint PageTablePointer => _addressSpace.Base.Pointer;

        public MemoryManagerType Type => _unsafeMode ? MemoryManagerType.HostMappedUnsafe : MemoryManagerType.HostMapped;

        public MemoryTracking Tracking { get; }

        public event Action<ulong, ulong> UnmapEvent;

        protected override ulong AddressSpaceSize { get; }

        /// <summary>
        /// Creates a new instance of the host mapped memory manager.
        /// </summary>
        /// <param name="addressSpace">Address space instance to use</param>
        /// <param name="unsafeMode">True if unmanaged access should not be masked (unsafe), false otherwise.</param>
        /// <param name="invalidAccessHandler">Optional function to handle invalid memory accesses</param>
        public MemoryManagerHostMapped(AddressSpace addressSpace, bool unsafeMode, InvalidAccessHandler invalidAccessHandler)
        {
            _addressSpace = addressSpace;
            _pageTable = new PageTable<ulong>();
            _invalidAccessHandler = invalidAccessHandler;
            _unsafeMode = unsafeMode;
            AddressSpaceSize = addressSpace.AddressSpaceSize;

            ulong asSize = PageSize;
            int asBits = PageBits;

            while (asSize < AddressSpaceSize)
            {
                asSize <<= 1;
                asBits++;
            }

            AddressSpaceBits = asBits;

            _pages = new ManagedPageFlags(AddressSpaceBits);

            Tracking = new MemoryTracking(this, (int)MemoryBlock.GetPageSize(), invalidAccessHandler);
            AppContext.TryGetSwitch("Ryujinx.Uwp.LazyGuestCommit", out _lazyGuestCommit);
            // Four-KiB commits generated roughly 60,000 VirtualAlloc calls before
            // Xbox stopped accepting commits, despite a working set below 800 MiB.
            // Group four host pages: this remains sparse enough for the 5 GiB cap,
            // while reducing VM/VAD pressure by 75 percent.
            _lazyCommitGranularity = Math.Max((ulong)MemoryBlock.GetPageSize(), 16UL * 1024UL);
            _lazyCommittedBlocks = _lazyGuestCommit
                ? new long[checked((int)(((addressSpace.BackingMemory.Size + _lazyCommitGranularity - 1) / _lazyCommitGranularity + 63) / 64))]
                : Array.Empty<long>();
            _memoryEh = new MemoryEhMeilleure(
                _addressSpace.Base,
                _addressSpace.Mirror,
                Tracking,
                _lazyGuestCommit ? HandleVirtualMemoryEvent : null);
        }

        private ulong HandleVirtualMemoryEvent(ulong address, ulong size, bool write)
        {
            if (!IsMapped(address))
            {
                return 0;
            }

            if (!EnsurePhysicalCommitment(GetPhysicalAddressInternal(address), size))
            {
                // Never allow a managed exception to cross the native vectored
                // exception-handler callback. Returning zero leaves the original
                // fault unhandled and produces a useful native crash record if
                // the sandbox has genuinely exhausted all commit capacity.
                return 0;
            }

            ulong pageSize = (ulong)MemoryBlock.GetPageSize();
            ulong alignedAddress = address & ~(pageSize - 1);
            ulong alignedEnd = (address + size + pageSize - 1) & ~(pageSize - 1);
            Tracking.VirtualMemoryEvent(alignedAddress, alignedEnd - alignedAddress, write);

            return (ulong)_addressSpace.Base.Pointer + address;
        }

        private bool EnsurePhysicalCommitment(ulong physicalAddress, ulong size)
        {
            if (!_lazyGuestCommit || size == 0)
            {
                return true;
            }

            ulong start = physicalAddress & ~(_lazyCommitGranularity - 1);
            ulong end = Math.Min(
                (physicalAddress + size + _lazyCommitGranularity - 1) & ~(_lazyCommitGranularity - 1),
                _addressSpace.BackingMemory.Size);

            for (ulong blockAddress = start; blockAddress < end; blockAddress += _lazyCommitGranularity)
            {
                ulong block = blockAddress / _lazyCommitGranularity;
                int wordIndex = checked((int)(block >> 6));
                long mask = 1L << (int)(block & 63);

                if ((Volatile.Read(ref _lazyCommittedBlocks[wordIndex]) & mask) != 0)
                {
                    continue;
                }

                ulong commitSize = Math.Min(_lazyCommitGranularity, _addressSpace.BackingMemory.Size - blockAddress);
                Interlocked.Increment(ref _uwpLazyCommitAttempts);
                try
                {
                    _addressSpace.BackingMemory.Commit(blockAddress, commitSize);
                }
                catch (SystemException exception)
                {
                    Interlocked.Increment(ref _uwpLazyCommitFailures);
                    int nativeError = exception is Win32Exception win32Exception
                        ? win32Exception.NativeErrorCode
                        : exception.HResult;
                    long workingSet = 0;
                    try
                    {
                        workingSet = Process.GetCurrentProcess().WorkingSet64;
                    }
                    catch
                    {
                    }

                    Interlocked.Exchange(ref _uwpLazyCommitLastError, nativeError);
                    Interlocked.Exchange(ref _uwpLazyCommitLastAddress, (long)blockAddress);
                    Interlocked.Exchange(ref _uwpLazyCommitLastSize, (long)commitSize);
                    Interlocked.Exchange(ref _uwpLazyCommitLastWorkingSet, workingSet);
                    Volatile.Write(ref _uwpLazyCommitLastMessage,
                        $"{exception.GetType().Name}: {exception.Message} (thread={Environment.CurrentManagedThreadId})");
                    return false;
                }

                Interlocked.Or(ref _lazyCommittedBlocks[wordIndex], mask);
                Interlocked.Add(ref _uwpLazyCommittedBytes, (long)commitSize);
            }

            return true;
        }

        private void EnsurePhysicalCommitmentOrThrow(ulong physicalAddress, ulong size)
        {
            if (!EnsurePhysicalCommitment(physicalAddress, size))
            {
                throw new OutOfMemoryException(
                    $"Unable to commit sparse guest memory at PA 0x{physicalAddress:X} (size 0x{size:X}).");
            }
        }

        public static string GetUwpLazyCommitTelemetry()
        {
            return $"lazyCommitGranularity={16 * 1024}, " +
                $"lazyCommitAttempts={Interlocked.Read(ref _uwpLazyCommitAttempts)}, " +
                $"lazyCommittedBytes={Interlocked.Read(ref _uwpLazyCommittedBytes)}, " +
                $"lazyCommitFailures={Interlocked.Read(ref _uwpLazyCommitFailures)}, " +
                $"lazyCommitLastError=0x{Interlocked.Read(ref _uwpLazyCommitLastError):X8}, " +
                $"lazyCommitLastAddress=0x{Interlocked.Read(ref _uwpLazyCommitLastAddress):X}, " +
                $"lazyCommitLastSize=0x{Interlocked.Read(ref _uwpLazyCommitLastSize):X}, " +
                $"lazyCommitLastWorkingSet={Interlocked.Read(ref _uwpLazyCommitLastWorkingSet)}, " +
                $"lazyCommitLastMessage={Volatile.Read(ref _uwpLazyCommitLastMessage)}";
        }

        /// <summary>
        /// Ensures the combination of virtual address and size is part of the addressable space and fully mapped.
        /// </summary>
        /// <param name="va">Virtual address of the range</param>
        /// <param name="size">Size of the range in bytes</param>
        private void AssertMapped(ulong va, ulong size)
        {
            if (!ValidateAddressAndSize(va, size) || !_pages.IsRangeMapped(va, size))
            {
                throw new InvalidMemoryRegionException($"Not mapped: va=0x{va:X16}, size=0x{size:X16}");
            }
        }

        /// <inheritdoc/>
        public void Map(ulong va, ulong pa, ulong size, MemoryMapFlags flags)
        {
            AssertValidAddressAndSize(va, size);

            _addressSpace.Map(va, pa, size, flags);
            _pages.AddMapping(va, size);
            PtMap(va, pa, size);

            Tracking.Map(va, size);
        }

        /// <inheritdoc/>
        public void Unmap(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            UnmapEvent?.Invoke(va, size);
            Tracking.Unmap(va, size);

            _pages.RemoveMapping(va, size);
            PtUnmap(va, size);
            _addressSpace.Unmap(va, size);
        }

        private void PtMap(ulong va, ulong pa, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Map(va, pa);

                va += PageSize;
                pa += PageSize;
                size -= PageSize;
            }
        }

        private void PtUnmap(ulong va, ulong size)
        {
            while (size != 0)
            {
                _pageTable.Unmap(va);

                va += PageSize;
                size -= PageSize;
            }
        }

        public override T Read<T>(ulong va)
        {
            try
            {
                AssertMapped(va, (ulong)Unsafe.SizeOf<T>());

                return _addressSpace.Mirror != null
                    ? _addressSpace.Mirror.Read<T>(va)
                    : base.Read<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return default;
            }
        }
        
        public override bool TryReadUnsafe(ulong va, int length, out Span<byte> data)
        {
            throw new NotImplementedException();
        }

        public override T ReadTracked<T>(ulong va)
        {
            try
            {
                return base.ReadTracked<T>(va);
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return default;
            }
        }

        public override void Read(ulong va, Span<byte> data)
        {
            try
            {
                AssertMapped(va, (ulong)data.Length);

                if (_addressSpace.Mirror != null)
                {
                    _addressSpace.Mirror.Read(va, data);
                }
                else
                {
                    base.Read(va, data);
                }
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override void Write<T>(ulong va, T value)
        {
            try
            {
                SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), write: true);

                if (_addressSpace.Mirror != null)
                {
                    _addressSpace.Mirror.Write(va, value);
                }
                else
                {
                    base.Write(va, value);
                }
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override void Write(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                SignalMemoryTracking(va, (ulong)data.Length, write: true);

                if (_addressSpace.Mirror != null)
                {
                    _addressSpace.Mirror.Write(va, data);
                }
                else
                {
                    base.Write(va, data);
                }
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override void WriteUntracked(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                AssertMapped(va, (ulong)data.Length);

                if (_addressSpace.Mirror != null)
                {
                    _addressSpace.Mirror.Write(va, data);
                }
                else
                {
                    base.WriteUntracked(va, data);
                }
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }
            }
        }

        public override bool WriteWithRedundancyCheck(ulong va, ReadOnlySpan<byte> data)
        {
            try
            {
                SignalMemoryTracking(va, (ulong)data.Length, false);

                if (_addressSpace.Mirror == null)
                {
                    return base.WriteWithRedundancyCheck(va, data);
                }

                Span<byte> target = _addressSpace.Mirror.GetSpan(va, data.Length);
                bool changed = !data.SequenceEqual(target);

                if (changed)
                {
                    data.CopyTo(target);
                }

                return changed;
            }
            catch (InvalidMemoryRegionException)
            {
                if (_invalidAccessHandler == null || !_invalidAccessHandler(va))
                {
                    throw;
                }

                return true;
            }
        }

        public override ReadOnlySequence<byte> GetReadOnlySequence(ulong va, int size, bool tracked = false)
        {
            if (tracked)
            {
                SignalMemoryTracking(va, (ulong)size, write: false);
            }
            else
            {
                AssertMapped(va, (ulong)size);
            }

            return _addressSpace.Mirror != null
                ? new ReadOnlySequence<byte>(_addressSpace.Mirror.GetMemory(va, size))
                : base.GetReadOnlySequence(va, size, tracked: false);
        }

        public override ReadOnlySpan<byte> GetSpan(ulong va, int size, bool tracked = false)
        {
            if (tracked)
            {
                SignalMemoryTracking(va, (ulong)size, write: false);
            }
            else
            {
                AssertMapped(va, (ulong)size);
            }

            return _addressSpace.Mirror != null
                ? _addressSpace.Mirror.GetSpan(va, size)
                : base.GetSpan(va, size, tracked: false);
        }

        public override WritableRegion GetWritableRegion(ulong va, int size, bool tracked = false)
        {
            if (tracked)
            {
                SignalMemoryTracking(va, (ulong)size, true);
            }
            else
            {
                AssertMapped(va, (ulong)size);
            }

            return _addressSpace.Mirror != null
                ? _addressSpace.Mirror.GetWritableRegion(va, size)
                : base.GetWritableRegion(va, size, tracked: false);
        }

        public ref T GetRef<T>(ulong va) where T : unmanaged
        {
            SignalMemoryTracking(va, (ulong)Unsafe.SizeOf<T>(), true);

            if (_addressSpace.Mirror != null)
            {
                return ref _addressSpace.Mirror.GetRef<T>(va);
            }

            if (!IsContiguous(va, Unsafe.SizeOf<T>()))
            {
                ThrowMemoryNotContiguous();
            }

            ulong physicalAddress = GetPhysicalAddressChecked(va);
            EnsurePhysicalCommitmentOrThrow(physicalAddress, (ulong)Unsafe.SizeOf<T>());
            return ref _addressSpace.BackingMemory.GetRef<T>(physicalAddress);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool IsMapped(ulong va)
        {
            return ValidateAddress(va) && _pages.IsMapped(va);
        }

        /// <inheritdoc/>
        public bool IsRangeMapped(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            return _pages.IsRangeMapped(va, size);
        }

        /// <inheritdoc/>
        public IEnumerable<HostMemoryRange> GetHostRegions(ulong va, ulong size)
        {
            AssertValidAddressAndSize(va, size);

            if (_addressSpace.Mirror != null)
            {
                return Enumerable.Repeat(new HostMemoryRange((nuint)(ulong)_addressSpace.Mirror.GetPointer(va, size), size), 1);
            }

            List<HostMemoryRange> hostRegions = [];

            foreach (MemoryRange physicalRegion in GetPhysicalRegions(va, size))
            {
                EnsurePhysicalCommitmentOrThrow(physicalRegion.Address, physicalRegion.Size);
                nint pointer = _addressSpace.BackingMemory.GetPointer(physicalRegion.Address, physicalRegion.Size);
                hostRegions.Add(new HostMemoryRange((nuint)(ulong)pointer, physicalRegion.Size));
            }

            return hostRegions;
        }

        /// <inheritdoc/>
        public IEnumerable<MemoryRange> GetPhysicalRegions(ulong va, ulong size)
        {
            int pages = GetPagesCount(va, (uint)size, out va);

            List<MemoryRange> regions = [];

            ulong regionStart = GetPhysicalAddressChecked(va);
            ulong regionSize = PageSize;

            for (int page = 0; page < pages - 1; page++)
            {
                if (!ValidateAddress(va + PageSize))
                {
                    return null;
                }

                ulong newPa = GetPhysicalAddressChecked(va + PageSize);

                if (GetPhysicalAddressChecked(va) + PageSize != newPa)
                {
                    regions.Add(new MemoryRange(regionStart, regionSize));
                    regionStart = newPa;
                    regionSize = 0;
                }

                va += PageSize;
                regionSize += PageSize;
            }

            regions.Add(new MemoryRange(regionStart, regionSize));

            return regions;
        }

        private ulong GetPhysicalAddressChecked(ulong va)
        {
            if (!IsMapped(va))
            {
                ThrowInvalidMemoryRegionException($"Not mapped: va=0x{va:X16}");
            }

            return GetPhysicalAddressInternal(va);
        }

        private ulong GetPhysicalAddressInternal(ulong va)
        {
            return _pageTable.Read(va) + (va & PageMask);
        }

        /// <remarks>
        /// This function also validates that the given range is both valid and mapped, and will throw if it is not.
        /// </remarks>
        public override void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null)
        {
            AssertValidAddressAndSize(va, size);

            if (precise)
            {
                Tracking.VirtualMemoryEvent(va, size, write, precise: true, exemptId);
                return;
            }

            _pages.SignalMemoryTracking(Tracking, va, size, write, exemptId);
        }

        /// <inheritdoc/>
        public void Reprotect(ulong va, ulong size, MemoryPermission protection)
        {
            // TODO
        }

        /// <inheritdoc/>
        public void TrackingReprotect(ulong va, ulong size, MemoryPermission protection, bool guest)
        {
            if (guest)
            {
                _addressSpace.Base.Reprotect(va, size, protection, false);
            }
            else
            {
                _pages.TrackingReprotect(va, size, protection);
            }
        }

        /// <inheritdoc/>
        public RegionHandle BeginTracking(ulong address, ulong size, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginTracking(address, size, id, flags);
        }

        /// <inheritdoc/>
        public MultiRegionHandle BeginGranularTracking(ulong address, ulong size, IEnumerable<IRegionHandle> handles, ulong granularity, int id, RegionFlags flags = RegionFlags.None)
        {
            return Tracking.BeginGranularTracking(address, size, handles, granularity, id, flags);
        }

        /// <inheritdoc/>
        public SmartMultiRegionHandle BeginSmartGranularTracking(ulong address, ulong size, ulong granularity, int id)
        {
            return Tracking.BeginSmartGranularTracking(address, size, granularity, id);
        }

        /// <summary>
        /// Disposes of resources used by the memory manager.
        /// </summary>
        protected override void Destroy()
        {
            _addressSpace.Dispose();
            _memoryEh.Dispose();
        }

        protected override Memory<byte> GetPhysicalAddressMemory(nuint pa, int size)
        {
            EnsurePhysicalCommitmentOrThrow(pa, (ulong)size);
            return _addressSpace.BackingMemory.GetMemory(pa, size);
        }

        protected override Span<byte> GetPhysicalAddressSpan(nuint pa, int size)
        {
            EnsurePhysicalCommitmentOrThrow(pa, (ulong)size);
            return _addressSpace.BackingMemory.GetSpan(pa, size);
        }

        protected override nuint TranslateVirtualAddressChecked(ulong va)
            => (nuint)GetPhysicalAddressChecked(va);

        protected override nuint TranslateVirtualAddressUnchecked(ulong va)
            => (nuint)GetPhysicalAddressInternal(va);
    }
}
