using Ryujinx.Cpu;
using Ryujinx.Common.Memory;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.Memory;
using Ryujinx.HLE.HOS.Kernel.Process;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.Memory;
using Ryujinx.Memory.Range;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Ryujinx.HLE.HOS.Kernel
{
    class KernelContext : IDisposable
    {
        public long PrivilegedProcessLowestId { get; set; } = 1;
        public long PrivilegedProcessHighestId { get; set; } = 8;

        public bool EnableVersionChecks { get; set; }

        public bool KernelInitialized { get; }

        public bool Running { get; private set; }

        public volatile bool IsShuttingDown;

        public Switch Device { get; }
        public MemoryBlock Memory { get; }
        public ITickSource TickSource { get; }
        public Syscall Syscall { get; }
        public SyscallHandler SyscallHandler { get; }

        public KResourceLimit ResourceLimit { get; }

        public KMemoryManager MemoryManager { get; }

        public KMemoryBlockSlabManager LargeMemoryBlockSlabManager { get; }
        public KMemoryBlockSlabManager SmallMemoryBlockSlabManager { get; }

        public KSlabHeap UserSlabHeapPages { get; }

        public KCriticalSection CriticalSection { get; }
        public KScheduler[] Schedulers { get; }
        public KPriorityQueue PriorityQueue { get; }
        public KTimeManager TimeManager { get; }
        public KSynchronization Synchronization { get; }
        public KContextIdManager ContextIdManager { get; }

        public ConcurrentDictionary<ulong, KProcess> Processes { get; }
        public ConcurrentDictionary<string, KAutoObject> AutoObjectNames { get; }

        public bool ThreadReselectionRequested { get; set; }

        private ulong _kipId;
        private ulong _processId;
        private ulong _threadUid;
        private readonly object _memoryCommitLock = new();
        private readonly ulong[] _committedMemoryPages;
        private readonly ulong[] _budgetCommittedPages;
        private readonly ulong _memoryCommitPageSize;
        private long _guestCommittedBytes;

        public long GuestCommittedBytes
        {
            get
            {
                lock (_memoryCommitLock)
                {
                    return _guestCommittedBytes;
                }
            }
        }

        public KernelContext(
            ITickSource tickSource,
            Switch device,
            MemoryBlock memory,
            MemorySize memorySize,
            MemoryArrange memoryArrange)
        {
            TickSource = tickSource;
            Device = device;
            Memory = memory;
            _memoryCommitPageSize = MemoryBlock.GetPageSize();
            ulong memoryPageCount = (memory.Size + _memoryCommitPageSize - 1) / _memoryCommitPageSize;
            _committedMemoryPages = new ulong[checked((int)((memoryPageCount + 63) / 64))];
            _budgetCommittedPages = new ulong[_committedMemoryPages.Length];
            KScheduler.CpuCoresCount = device.CpuCoresCount;

            Running = true;

            Syscall = new Syscall(this);

            SyscallHandler = new SyscallHandler(this);

            ResourceLimit = new KResourceLimit(this);

            KernelInit.InitializeResourceLimit(ResourceLimit, memorySize);

            MemoryManager = new KMemoryManager(memorySize, memoryArrange, ReleasePhysicalMemory);

            LargeMemoryBlockSlabManager = new KMemoryBlockSlabManager(KernelConstants.MemoryBlockAllocatorSize * 2);
            SmallMemoryBlockSlabManager = new KMemoryBlockSlabManager(KernelConstants.MemoryBlockAllocatorSize);

            UserSlabHeapPages = new KSlabHeap(
                KernelConstants.UserSlabHeapBase,
                KernelConstants.UserSlabHeapItemSize,
                KernelConstants.UserSlabHeapSize);

            CommitMemory(KernelConstants.UserSlabHeapBase - DramMemoryMap.DramBase, KernelConstants.UserSlabHeapSize);

            CriticalSection = new KCriticalSection(this);
            Schedulers = new KScheduler[KScheduler.CpuCoresCount];
            PriorityQueue = new KPriorityQueue();
            TimeManager = new KTimeManager(this);
            Synchronization = new KSynchronization(this);
            ContextIdManager = new KContextIdManager();

            for (int core = 0; core < KScheduler.CpuCoresCount; core++)
            {
                Schedulers[core] = new KScheduler(this, core);
            }

            StartPreemptionThread();

            KernelInitialized = true;

            Processes = new ConcurrentDictionary<ulong, KProcess>();
            AutoObjectNames = new ConcurrentDictionary<string, KAutoObject>();

            _kipId = KernelConstants.InitialKipId;
            _processId = KernelConstants.InitialProcessId;
        }

        private void StartPreemptionThread()
        {
            void PreemptionThreadStart()
            {
                KScheduler.PreemptionThreadLoop(this);
            }

            new Thread(PreemptionThreadStart) { Name = "HLE.PreemptionThread" }.Start();
        }

        public void CommitMemory(ulong address, ulong size)
        {
            CommitMemory(address, size, null);
        }

        public IReadOnlyList<MemoryRange> CommitMemoryAndGetReusedRanges(ulong address, ulong size)
        {
            List<MemoryRange> reusedRanges = new();

            CommitMemory(address, size, reusedRanges);

            return reusedRanges;
        }

        public void PrepareLazyZeroMemory(ulong address, ulong size)
        {
            if (size == 0)
            {
                return;
            }

            ulong alignment = _memoryCommitPageSize;
            ulong endAddress = (address + size + (alignment - 1)) & ~(alignment - 1);
            address &= ~(alignment - 1);

            ulong firstPage = address / alignment;
            ulong pageCount = (endAddress - address) / alignment;

            lock (_memoryCommitLock)
            {
                for (ulong page = firstPage; page < firstPage + pageCount; page++)
                {
                    int wordIndex = checked((int)(page >> 6));
                    _committedMemoryPages[wordIndex] &= ~(1UL << (int)(page & 63));
                }
            }
        }

        private void CommitMemory(ulong address, ulong size, List<MemoryRange> reusedRanges)
        {
            if (size == 0)
            {
                return;
            }

            ulong alignment = _memoryCommitPageSize;
            ulong endAddress = address + size;

            address &= ~(alignment - 1);
            endAddress = (endAddress + (alignment - 1)) & ~(alignment - 1);

            ulong firstPage = address / alignment;
            ulong pageCount = (endAddress - address) / alignment;

            lock (_memoryCommitLock)
            {
                if (reusedRanges != null)
                {
                    ulong reusedRangeStart = ulong.MaxValue;

                    for (ulong page = firstPage; page < firstPage + pageCount; page++)
                    {
                        int wordIndex = checked((int)(page >> 6));
                        ulong mask = 1UL << (int)(page & 63);
                        bool wasCommitted = (_committedMemoryPages[wordIndex] & mask) != 0;

                        if (wasCommitted && reusedRangeStart == ulong.MaxValue)
                        {
                            reusedRangeStart = page;
                        }
                        else if (!wasCommitted && reusedRangeStart != ulong.MaxValue)
                        {
                            reusedRanges.Add(new MemoryRange(
                                reusedRangeStart * alignment,
                                (page - reusedRangeStart) * alignment));
                            reusedRangeStart = ulong.MaxValue;
                        }
                    }

                    if (reusedRangeStart != ulong.MaxValue)
                    {
                        reusedRanges.Add(new MemoryRange(
                            reusedRangeStart * alignment,
                            (firstPage + pageCount - reusedRangeStart) * alignment));
                    }
                }

                // Keep admission accounting separate from lazy zero-fill reuse:
                // PrepareLazyZeroMemory can clear reuse bits without decommit.
                long additionalBytes = 0;
                for (ulong page = firstPage; page < firstPage + pageCount; page++)
                {
                    if ((_budgetCommittedPages[checked((int)(page >> 6))] & (1UL << (int)(page & 63))) == 0)
                        additionalBytes += checked((long)alignment);
                }

                if (AppContext.TryGetSwitch("Ryujinx.Uwp.LazyGuestCommit", out bool constrained) && constrained)
                {
                    RuntimeMemoryBudget.CheckGuestCommit(additionalBytes, address);
                }

                Memory.Commit(address, endAddress - address);
                _guestCommittedBytes += additionalBytes;
                RuntimeMemoryBudget.Observe(MemoryCategory.Guest, _guestCommittedBytes);

                for (ulong page = firstPage; page < firstPage + pageCount; page++)
                {
                    int wordIndex = checked((int)(page >> 6));
                    _committedMemoryPages[wordIndex] |= 1UL << (int)(page & 63);
                    _budgetCommittedPages[wordIndex] |= 1UL << (int)(page & 63);
                }
            }
        }

        private void ReleasePhysicalMemory(ulong address, ulong pagesCount)
        {
            if (pagesCount == 0)
            {
                return;
            }

            ulong offset = address - DramMemoryMap.DramBase;
            ulong size = pagesCount * KPageTableBase.PageSize;
            ulong alignment = _memoryCommitPageSize;
            ulong firstPage = offset / alignment;
            ulong pageCount = size / alignment;

            lock (_memoryCommitLock)
            {
                long releasedBytes = 0;

                for (ulong page = firstPage; page < firstPage + pageCount; page++)
                {
                    int wordIndex = checked((int)(page >> 6));
                    ulong mask = 1UL << (int)(page & 63);

                    if ((_budgetCommittedPages[wordIndex] & mask) != 0)
                    {
                        releasedBytes += checked((long)alignment);
                    }

                    _committedMemoryPages[wordIndex] &= ~mask;
                    _budgetCommittedPages[wordIndex] &= ~mask;
                }

                Memory.Decommit(offset, size);
                _guestCommittedBytes = Math.Max(0, _guestCommittedBytes - releasedBytes);
                RuntimeMemoryBudget.Observe(MemoryCategory.Guest, _guestCommittedBytes);
            }
        }

        public ulong NewThreadUid()
        {
            return Interlocked.Increment(ref _threadUid) - 1;
        }

        public ulong NewKipId()
        {
            return Interlocked.Increment(ref _kipId) - 1;
        }

        public ulong NewProcessId()
        {
            return Interlocked.Increment(ref _processId) - 1;
        }

        public void Dispose()
        {
            Running = false;

            for (int i = 0; i < KScheduler.CpuCoresCount; i++)
            {
                Schedulers[i].Dispose();
            }

            TimeManager.Dispose();
        }
    }
}
