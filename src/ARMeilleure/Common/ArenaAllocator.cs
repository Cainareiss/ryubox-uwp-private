using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Ryujinx.Common.Memory;

namespace ARMeilleure.Common
{
    public unsafe sealed class ArenaAllocator : Allocator
    {
        private class PageInfo
        {
            public byte* Pointer;
            public byte Unused;
            public int UnusedCounter;
        }

        private int _lastReset;
        private ulong _index;
        private int _pageIndex;
        private PageInfo _page;
        private List<PageInfo> _pages;
        private readonly ulong _pageSize;
        private readonly uint _pageCount;
        private readonly List<(nint Pointer, ulong Size)> _extras;
        private static int _activeArenas;
        private static long _totalAllocatedBytes;
        private int _isDisposed;
        private bool _counted;

        public static int ActiveArenas => Volatile.Read(ref _activeArenas);
        public static long TotalAllocatedBytes => Interlocked.Read(ref _totalAllocatedBytes);

        public ArenaAllocator(uint pageSize, uint pageCount)
        {
            if (pageSize == 0) throw new ArgumentOutOfRangeException(nameof(pageSize));
            Interlocked.Increment(ref _activeArenas);
            _counted = true;
            _lastReset = Environment.TickCount;

            // Set _index to pageSize so that the first allocation goes through the slow path.
            _index = pageSize;
            _pageIndex = -1;

            _page = null;
            _pages = [];
            _pageSize = pageSize;
            _pageCount = pageCount;

            _extras = [];
        }

        public Span<T> AllocateSpan<T>(ulong count) where T : unmanaged
        {
            return new Span<T>(Allocate<T>(count), (int)count);
        }

        public override void* Allocate(ulong size)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            if (size > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(size));
            if (_page != null && size <= _pageSize - _index)
            {
                byte* result = _page.Pointer + _index;

                _index += size;

                return result;
            }

            return AllocateSlow(size);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void* AllocateSlow(ulong size)
        {
            if (size > _pageSize)
            {
                void* extra = AllocateTracked(size);

                try { _extras.Add(((nint)extra, size)); }
                catch { FreeTracked(extra, size); throw; }

                return extra;
            }

            if (_pageIndex < 0 || _index + size > _pageSize)
            {
                _index = 0;
                _pageIndex++;
            }

            if (_pageIndex < _pages.Count)
            {
                _page = _pages[_pageIndex];
                _page.Unused = 0;
            }
            else
            {
                _page = new PageInfo
                {
                    Pointer = (byte*)AllocateTracked(_pageSize)
                };

                try { _pages.Add(_page); }
                catch { FreeTracked(_page.Pointer, _pageSize); _page = null; throw; }
            }

            byte* result = _page.Pointer + _index;

            _index += size;

            return result;
        }

        public override void Free(void* block) { }

        public void Reset()
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            _index = _pageSize;
            _pageIndex = -1;
            _page = null;

            // Free excess pages that was allocated.
            // Only discard at this ownership boundary, never while native IR is live.
            uint retainedPages = AppContext.TryGetSwitch("Ryujinx.Uwp.ConstrainedScheduling", out bool constrained)
                && constrained && RuntimeMemoryBudget.Pressure >= MemoryPressure.High ? 0 : _pageCount;
            while (_pages.Count > retainedPages)
            {
                FreeTracked(_pages[^1].Pointer, _pageSize);

                _pages.RemoveAt(_pages.Count - 1);
            }

            // Free extra blocks that are not page-sized
            foreach (var extra in _extras)
            {
                FreeTracked((void*)extra.Pointer, extra.Size);
            }

            _extras.Clear();

            // Free pooled pages that has not been used in a while. Remove pages at the back first, because we try to
            // keep the pages at the front alive, since they're more likely to be hot and in the d-cache.
            bool removing = true;

            // If arena is used frequently, keep pages for longer. Otherwise keep pages for a shorter amount of time.
            int now = Environment.TickCount;
            int count = (now - _lastReset) switch
            {
                >= 5000 => 0,
                >= 2500 => 50,
                >= 1000 => 100,
                >= 10 => 1500,
                _ => 5000,
            };

            for (int i = _pages.Count - 1; i >= 0; i--)
            {
                PageInfo page = _pages[i];

                if (page.Unused == 0)
                {
                    page.UnusedCounter = 0;
                }

                page.UnusedCounter += page.Unused;
                page.Unused = 1;

                // If page not used after `count` resets, remove it.
                if (removing && page.UnusedCounter >= count)
                {
                    FreeTracked(page.Pointer, _pageSize);

                    _pages.RemoveAt(i);
                }
                else
                {
                    removing = false;
                }
            }

            _lastReset = now;
        }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            if (_pages != null)
            {
                foreach (PageInfo info in _pages)
                {
                    FreeTracked(info.Pointer, _pageSize);
                }

                foreach (var extra in _extras)
                {
                    FreeTracked((void*)extra.Pointer, extra.Size);
                }

                _extras.Clear();
                _pages = null;
                _page = null;
            }

            if (_counted) Interlocked.Decrement(ref _activeArenas);
        }

        private static void* AllocateTracked(ulong size)
        {
            void* pointer = NativeAllocator.Instance.Allocate(size);
            long total = Interlocked.Add(ref _totalAllocatedBytes, (long)size);
            RuntimeMemoryBudget.Observe(MemoryCategory.CompilerArena, total);
            return pointer;
        }

        private static void FreeTracked(void* pointer, ulong size)
        {
            NativeAllocator.Instance.Free(pointer);
            long total = Interlocked.Add(ref _totalAllocatedBytes, -(long)size);
            RuntimeMemoryBudget.Observe(MemoryCategory.CompilerArena, Math.Max(0, total));
        }

        ~ArenaAllocator()
        {
            Dispose(false);
        }
    }
}
