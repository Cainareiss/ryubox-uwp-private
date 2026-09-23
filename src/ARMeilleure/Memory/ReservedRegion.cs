using System;

namespace ARMeilleure.Memory
{
    public class ReservedRegion : IDisposable
    {
        public const int DefaultGranularity = 65536; // Mapping granularity in Windows.

        public IJitMemoryBlock Block { get; }
        public IJitMemoryAllocator Allocator { get; }

        public nint Pointer => Block.Pointer;

        private readonly ulong _maxSize;
        private readonly ulong _sizeGranularity;
        private ulong _currentSize;

        public ulong CurrentSize => _currentSize;

        public ReservedRegion(IJitMemoryAllocator allocator, ulong maxSize, ulong granularity = 0)
        {
            if (granularity == 0)
            {
                granularity = DefaultGranularity;
            }

            Allocator = allocator;
            Block = allocator.Reserve(maxSize);
            _maxSize = maxSize;
            _sizeGranularity = granularity;
            _currentSize = 0;
        }

        public ulong GetAdditionalCommitSize(ulong desiredSize)
        {
            if (desiredSize > _maxSize)
            {
                throw new OutOfMemoryException();
            }

            if (desiredSize <= _currentSize) return 0;
            ulong remaining = desiredSize - _currentSize;
            return checked((remaining + _sizeGranularity - 1) / _sizeGranularity * _sizeGranularity);
        }

        public void ExpandIfNeeded(ulong desiredSize)
        {
            if (desiredSize > _maxSize) throw new OutOfMemoryException();

            if (desiredSize > _currentSize)
            {
                // Lock, and then check again. We only want to commit once.
                lock (this)
                {
                    if (desiredSize > _currentSize)
                    {
                        ulong moreToCommit = GetAdditionalCommitSize(desiredSize);
                        Block.Commit(_currentSize, moreToCommit);
                        _currentSize += moreToCommit;
                    }
                }
            }
        }

        public void Dispose()
        {
            Block.Dispose();
        }
    }
}
