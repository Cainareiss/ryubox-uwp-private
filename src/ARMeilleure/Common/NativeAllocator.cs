using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using Ryujinx.Common.Memory;

namespace ARMeilleure.Common
{
    public unsafe sealed class NativeAllocator : Allocator
    {
        public static NativeAllocator Instance { get; } = new();
        private readonly ConcurrentDictionary<nint, ulong> _allocations = new();
        private long _allocatedBytes;

        public long AllocatedBytes => Interlocked.Read(ref _allocatedBytes);

        public override void* Allocate(ulong size)
        {
            void* result = (void*)Marshal.AllocHGlobal((nint)size);

            if (result == null)
            {
                throw new OutOfMemoryException();
            }

            nint pointer = (nint)result;
            if (_allocations.TryAdd(pointer, size))
            {
                long total = Interlocked.Add(ref _allocatedBytes, checked((long)size));
                RuntimeMemoryBudget.Observe(MemoryCategory.NativeAllocator, total);
            }

            return result;
        }

        public override void Free(void* block)
        {
            nint pointer = (nint)block;
            if (_allocations.TryRemove(pointer, out ulong size))
            {
                long total = Interlocked.Add(ref _allocatedBytes, -checked((long)size));
                RuntimeMemoryBudget.Observe(MemoryCategory.NativeAllocator, Math.Max(0, total));
            }
            Marshal.FreeHGlobal(pointer);
        }
    }
}
