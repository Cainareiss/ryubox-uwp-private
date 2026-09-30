using ARMeilleure.Instructions;
using ARMeilleure.IntermediateRepresentation;
using ARMeilleure.Memory;
using ARMeilleure.Translation;
using System;
using System.Globalization;
using System.Threading;
using static ARMeilleure.IntermediateRepresentation.Operand.Factory;

namespace ARMeilleure.Diagnostics
{
    // Diagnostic addresses are supplied by the caller, never by title-specific code.
    // Unlike page dirtiness this observes each selected JIT access before execution.
    internal sealed class GuestAccessProbe
    {
        private readonly (ulong Address, bool Write)[] _targets;
        private readonly int[] _counts;

        private GuestAccessProbe((ulong Address, bool Write)[] targets)
        {
            _targets = targets;
            _counts = new int[targets.Length];
        }

        internal static GuestAccessProbe Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256) return null;
            string[] parts = value.Split(',');
            if (parts.Length > 4) return null;
            var targets = new (ulong Address, bool Write)[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                string[] pair = parts[i].Trim().Split(':');
                if (pair.Length != 2 || pair[0] is not ("r" or "w") ||
                    !ulong.TryParse(pair[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong address) ||
                    address < 16 || address > ulong.MaxValue - 16 || (address & 7) != 0) return null;
                targets[i] = (address, pair[0] == "w");
            }
            return new GuestAccessProbe(targets);
        }

        internal void Emit(ArmEmitterContext context, Operand address, bool write, int size)
        {
            foreach (var target in _targets)
            {
                if (target.Write != write) continue;
                Operand skip = Label();
                Operand va = address.Type == OperandType.I64 ? address : context.ZeroExtend32(OperandType.I64, address);
                // The access [va, va+2^size) intersects the watched eight bytes.
                context.BranchIf(skip, context.Subtract(va, Const(target.Address - (1UL << size) + 1)),
                    Const((1UL << size) + 7), Comparison.GreaterOrEqualUI);
                context.Call(typeof(NativeInterface).GetMethod(nameof(NativeInterface.TraceGuestAccess)),
                    Const(context.CurrOp.Address), va, Const(1 << size), Const(write ? 1 : 0));
                context.MarkLabel(skip);
            }
        }

        internal string Observe(ulong pc, ulong address, int size, bool write, IMemoryManager memory)
        {
            for (int i = 0; i < _targets.Length; i++)
            {
                var target = _targets[i];
                if (target.Write != write || size <= 0 || size > 16 || address > ulong.MaxValue - (ulong)size ||
                    address >= target.Address + 8 || address + (ulong)size <= target.Address) continue;
                if (Interlocked.Increment(ref _counts[i]) > 32) return null;
                string before;
                try { before = memory.IsMapped(target.Address) ? $"0x{memory.Read<ulong>(target.Address):X16}" : "unmapped"; }
                catch (Exception e) { before = $"unavailable({e.GetType().Name})"; }
                return $"Guest access probe: pc=0x{pc:X}, {(write ? "write-attempt" : "read")} address=0x{address:X}, bytes={size}, target=0x{target.Address:X}, before={before}";
            }
            return null;
        }
    }
}
