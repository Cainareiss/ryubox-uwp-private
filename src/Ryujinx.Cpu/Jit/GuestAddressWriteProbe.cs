using ARMeilleure.State;
using ARMeilleure.Memory;
using Ryujinx.Common.Logging;
using Ryujinx.Memory.Tracking;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using ExecutionContext = ARMeilleure.State.ExecutionContext;

namespace Ryujinx.Cpu.Jit
{
    /// <summary>
    /// Opt-in, bounded diagnostic watchpoints for guest writes. This does not
    /// alter guest memory; precise notifications return false to preserve the
    /// normal tracking path.
    /// </summary>
    internal sealed class GuestAddressWriteProbe : IDisposable
    {
        private const int MaxAddresses = 4;
        private const int MaxWritesPerAddress = 16;
        private readonly List<RegionHandle> _handles = new();
        private readonly int[] _counts;

        [ThreadStatic]
        private static ExecutionContext _currentContext;

        private GuestAddressWriteProbe(IVirtualMemoryManagerTracked memory, ulong[] addresses)
        {
            _counts = new int[addresses.Length];
            for (int i = 0; i < addresses.Length; i++)
            {
                int index = i;
                ulong watchedAddress = addresses[i];
                RegionHandle handle = memory.BeginTracking(watchedAddress, sizeof(ulong),
                    unchecked((int)(0x52590000u + (uint)i)));
                Logger.Info?.Print(LogClass.Cpu,
                    $"Guest write watch registered: address=0x{watchedAddress:X16}, unmapped={handle.Unmapped}, page=0x{handle.Address:X16}-0x{handle.EndAddress:X16}");
                handle.RegisterPreciseAction((address, size, write) =>
                {
                    if (write && address < watchedAddress + sizeof(ulong) && watchedAddress < address + size &&
                        Interlocked.Increment(ref _counts[index]) <= MaxWritesPerAddress)
                    {
                        ExecutionContext context = _currentContext;
                        Logger.Info?.Print(LogClass.Cpu,
                            $"Guest write watch: address=0x{address:X16}, size=0x{size:X}, pc={(context == null ? "unknown" : $"0x{context.Pc:X16}")}, threadUid={(context == null ? "unknown" : $"0x{context.ThreadUid:X16}")}, managedThread={Environment.CurrentManagedThreadId}, hit={Volatile.Read(ref _counts[index])}/{MaxWritesPerAddress}");
                    }
                    return false;
                });
                handle.RegisterDirtyEvent(() =>
                {
                    int hit = Interlocked.Increment(ref _counts[index]);
                    if (hit <= MaxWritesPerAddress)
                    {
                        ExecutionContext context = _currentContext;
                        Logger.Info?.Print(LogClass.Cpu,
                            $"Guest write watch page dirtied: target=0x{watchedAddress:X16}, page=0x{handle.Address:X16}-0x{handle.EndAddress:X16}, pc={(context == null ? "unknown" : $"0x{context.Pc:X16}")}, threadUid={(context == null ? "unknown" : $"0x{context.ThreadUid:X16}")}, managedThread={Environment.CurrentManagedThreadId}, hit={hit}/{MaxWritesPerAddress}");
                    }
                });
                _handles.Add(handle);
            }
        }

        internal static GuestAddressWriteProbe Create(IMemoryManager memory)
        {
            string value = Environment.GetEnvironmentVariable("RYUJINX_UWP_GUEST_WRITE_PROBE");
            if (string.IsNullOrWhiteSpace(value) || memory is not IVirtualMemoryManagerTracked tracked)
            {
                return null;
            }

            string[] parts = value.Split(',');
            if (parts.Length is 0 or > MaxAddresses)
            {
                return null;
            }

            ulong[] addresses = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) part = part[2..];
                if (!ulong.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out addresses[i]) ||
                    addresses[i] == 0 || addresses[i] > ulong.MaxValue - sizeof(ulong))
                {
                    Logger.Warning?.Print(LogClass.Cpu, "Ignoring invalid guest write-watch configuration.");
                    return null;
                }
            }

            try
            {
                var probe = new GuestAddressWriteProbe(tracked, addresses);
                Logger.Info?.Print(LogClass.Cpu, $"Guest write watch enabled for {addresses.Length} bounded address(es).");
                return probe;
            }
            catch (Exception exception)
            {
                Logger.Warning?.Print(LogClass.Cpu, $"Could not enable guest write watch: {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        internal static IDisposable Enter(ExecutionContext context)
        {
            ExecutionContext previous = _currentContext;
            _currentContext = context;
            return new ContextScope(previous);
        }

        public void Dispose()
        {
            foreach (RegionHandle handle in _handles)
            {
                handle.Dispose();
            }
            _handles.Clear();
        }

        private sealed class ContextScope : IDisposable
        {
            private readonly ExecutionContext _previous;
            private int _disposed;

            internal ContextScope(ExecutionContext previous) => _previous = previous;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    _currentContext = _previous;
                }
            }
        }
    }
}
