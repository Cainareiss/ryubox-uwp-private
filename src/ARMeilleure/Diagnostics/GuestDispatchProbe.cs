using ARMeilleure.Memory;
using ARMeilleure.State;
using Ryujinx.Common.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using ExecutionContext = ARMeilleure.State.ExecutionContext;

namespace ARMeilleure.Diagnostics
{
    // Explicit, bounded diagnostic only. No guest addresses or title-specific
    // behaviour are compiled in. Observations are not atomic across guest threads.
    internal sealed class GuestDispatchProbe
    {
        private readonly Dictionary<ulong, int> _sites;
        private readonly ulong[] _words;
        private readonly int[] _counts;

        private GuestDispatchProbe(ulong[] sites, ulong[] words)
        {
            _sites = new Dictionary<ulong, int>();
            for (int i = 0; i < sites.Length; i++) _sites.TryAdd(sites[i], i);
            _counts = new int[sites.Length];
            _words = words;
        }

        // Format: hex-PC[,hex-PC...]|hex-word-address[,hex-word-address...].
        // Eight sites, eight aligned words and eight hits/site maximum.
        internal static GuestDispatchProbe Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 512) return null;
            string[] parts = value.Split('|');
            if (parts.Length != 2) return null;
            ulong[] sites = ParseAddresses(parts[0], 4);
            ulong[] words = ParseAddresses(parts[1], 8);
            return sites == null || words == null ? null : new GuestDispatchProbe(sites, words);
        }

        private static ulong[] ParseAddresses(string value, ulong alignment)
        {
            string[] parts = value.Split(',');
            if (parts.Length > 8) return null;
            ulong[] result = new ulong[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                if (part.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) part = part[2..];
                if (!ulong.TryParse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong address) ||
                    address == 0 || address > ulong.MaxValue - 8 || (address & (alignment - 1)) != 0)
                    return null;
                result[i] = address;
            }
            return result;
        }

        internal string Observe(ulong pc, ExecutionContext context, IMemoryManager memory)
        {
            if (!_sites.TryGetValue(pc, out int site) || Volatile.Read(ref _counts[site]) >= 8 ||
                Interlocked.Increment(ref _counts[site]) > 8) return null;
            StringBuilder text = new($"Guest dispatch probe: uid={context.ThreadUid}, pc=0x{pc:X}, x0=0x{context.GetX(0):X}, x8=0x{context.GetX(8):X}, lr=0x{context.GetX(30):X}");
            foreach (ulong address in _words)
            {
                text.Append($"; [0x{address:X}]=");
                try
                {
                    // Aligned words cannot cross a page. Do not trigger GPU read
                    // tracking, and never read an address known to be unmapped.
                    text.Append(memory.IsMapped(address) ? $"0x{memory.Read<ulong>(address):X16}" : "unmapped");
                }
                catch (Exception exception)
                {
                    text.Append($"unavailable({exception.GetType().Name})");
                }
            }
            return text.ToString();
        }

        internal void Trace(ulong pc, IMemoryManager memory)
        {
            if (!_sites.ContainsKey(pc)) return;
            string observation = Observe(pc, Instructions.NativeInterface.GetContext(), memory);
            if (observation != null) Ryujinx.Common.Logging.Logger.Info?.Print(LogClass.Cpu, observation);
        }

        internal bool Contains(ulong pc) => _sites.ContainsKey(pc);

        internal void TraceBlock(ulong pc, ExecutionContext context, IMemoryManager memory)
        {
            string observation = Observe(pc, context, memory);
            if (observation != null) Ryujinx.Common.Logging.Logger.Info?.Print(LogClass.Cpu, "Guest block probe: " + observation);
        }
    }
}
