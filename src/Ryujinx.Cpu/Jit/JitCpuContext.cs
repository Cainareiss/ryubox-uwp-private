using ARMeilleure.Common;
using ARMeilleure.Memory;
using ARMeilleure.Translation;
using ARMeilleure.Translation.PTC;
using Ryujinx.Cpu.Signal;
using System.Threading;

namespace Ryujinx.Cpu.Jit
{
    class JitCpuContext : ICpuContext
    {
        private readonly ITickSource _tickSource;
        private readonly Translator _translator;
        private readonly IMemoryManager _memory;
        private readonly GuestAddressWriteProbe _guestAddressWriteProbe;
        private int _disposed;

        public JitCpuContext(ITickSource tickSource, IMemoryManager memory, bool for64Bit)
        {
            _tickSource = tickSource;
            _memory = memory;
            _guestAddressWriteProbe = GuestAddressWriteProbe.Create(memory);
            
            bool sparse = memory.Type is not MemoryManagerType.SoftwareMmu and not MemoryManagerType.SoftwarePageTable;
            IAddressTable<ulong> functionTable = sparse ? SparseAddressTable<ulong>.CreateForArm(for64Bit) : AddressTable<ulong>.CreateForArm(for64Bit);
            
            _translator = new Translator(new JitMemoryAllocator(forJit: true), memory, functionTable);

            // The UWP SoftwarePageTable sparse-commit handler lives entirely in
            // Ryujinx.Uwp.Native. NativeAOT rejects the managed reverse-P/Invoke
            // callback that the regular host-mapped signal path relies on.
            if (memory.Type.IsHostMappedOrTracked)
            {
                NativeSignalHandler.InitializeSignalHandler();
            }

            memory.UnmapEvent += UnmapHandler;
        }

        private void UnmapHandler(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }

        /// <inheritdoc/>
        public IExecutionContext CreateExecutionContext(ExceptionCallbacks exceptionCallbacks)
        {
            return new JitExecutionContext(new JitMemoryAllocator(), _tickSource, exceptionCallbacks);
        }

        /// <inheritdoc/>
        public void Execute(IExecutionContext context, ulong address)
        {
            using (GuestAddressWriteProbe.Enter(((JitExecutionContext)context).Impl))
            {
                _translator.Execute(((JitExecutionContext)context).Impl, address);
            }
        }

        /// <inheritdoc/>
        public void InvalidateCacheRegion(ulong address, ulong size)
        {
            _translator.InvalidateJitCacheRegion(address, size);
        }

        /// <inheritdoc/>
        public IDiskCacheLoadState LoadDiskCache(PtcCacheInfo cacheInfo, bool enabled)
        {
            return new JitDiskCacheLoadState(_translator.LoadDiskCache(cacheInfo, enabled));
        }

        /// <inheritdoc/>
        public void PrepareCodeRange(ulong address, ulong size)
        {
            _translator.FunctionTable.SignalCodeRange(address, size);
            _translator.PrepareCodeRange(address, size);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _memory.UnmapEvent -= UnmapHandler;
            _guestAddressWriteProbe?.Dispose();
            // Wake and join optional High-CQ workers even when a guest fault
            // bypasses Translator.Execute's normal last-thread cleanup.
            _translator.StopBackgroundTranslators();
        }
    }
}
