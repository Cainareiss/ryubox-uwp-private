using ARMeilleure.State;
using System;
using System.Runtime.InteropServices;

namespace ARMeilleure.Instructions
{
    static partial class SoftFallback
    {
        // NativeAOT does not emit an [UnmanagedCallersOnly] body when it is
        // reached only through MethodInfo.GetFunctionPointer(). Keep every
        // helper registered by Translation.Delegates rooted by taking its
        // function pointer directly, just like NativeInterface does.
        internal static unsafe nint GetFunctionPointer(string methodName)
        {
            return methodName switch
            {
                nameof(CountLeadingSigns) => (nint)(delegate* unmanaged<ulong, int, ulong>)&CountLeadingSigns,
                nameof(CountLeadingZeros) => (nint)(delegate* unmanaged<ulong, int, ulong>)&CountLeadingZeros,
                nameof(Crc32b) => (nint)(delegate* unmanaged<uint, byte, uint>)&Crc32b,
                nameof(Crc32cb) => (nint)(delegate* unmanaged<uint, byte, uint>)&Crc32cb,
                nameof(Crc32ch) => (nint)(delegate* unmanaged<uint, ushort, uint>)&Crc32ch,
                nameof(Crc32cw) => (nint)(delegate* unmanaged<uint, uint, uint>)&Crc32cw,
                nameof(Crc32cx) => (nint)(delegate* unmanaged<uint, ulong, uint>)&Crc32cx,
                nameof(Crc32h) => (nint)(delegate* unmanaged<uint, ushort, uint>)&Crc32h,
                nameof(Crc32w) => (nint)(delegate* unmanaged<uint, uint, uint>)&Crc32w,
                nameof(Crc32x) => (nint)(delegate* unmanaged<uint, ulong, uint>)&Crc32x,
                nameof(Decrypt) => (nint)(delegate* unmanaged<V128, V128, V128>)&Decrypt,
                nameof(Encrypt) => (nint)(delegate* unmanaged<V128, V128, V128>)&Encrypt,
                nameof(FixedRotate) => (nint)(delegate* unmanaged<uint, uint>)&FixedRotate,
                nameof(HashChoose) => (nint)(delegate* unmanaged<V128, uint, V128, V128>)&HashChoose,
                nameof(HashLower) => (nint)(delegate* unmanaged<V128, V128, V128, V128>)&HashLower,
                nameof(HashMajority) => (nint)(delegate* unmanaged<V128, uint, V128, V128>)&HashMajority,
                nameof(HashParity) => (nint)(delegate* unmanaged<V128, uint, V128, V128>)&HashParity,
                nameof(HashUpper) => (nint)(delegate* unmanaged<V128, V128, V128, V128>)&HashUpper,
                nameof(InverseMixColumns) => (nint)(delegate* unmanaged<V128, V128>)&InverseMixColumns,
                nameof(MixColumns) => (nint)(delegate* unmanaged<V128, V128>)&MixColumns,
                nameof(PolynomialMult64_128) => (nint)(delegate* unmanaged<ulong, ulong, V128>)&PolynomialMult64_128,
                nameof(SatF32ToS32) => (nint)(delegate* unmanaged<float, int>)&SatF32ToS32,
                nameof(SatF32ToS64) => (nint)(delegate* unmanaged<float, long>)&SatF32ToS64,
                nameof(SatF32ToU32) => (nint)(delegate* unmanaged<float, uint>)&SatF32ToU32,
                nameof(SatF32ToU64) => (nint)(delegate* unmanaged<float, ulong>)&SatF32ToU64,
                nameof(SatF64ToS32) => (nint)(delegate* unmanaged<double, int>)&SatF64ToS32,
                nameof(SatF64ToS64) => (nint)(delegate* unmanaged<double, long>)&SatF64ToS64,
                nameof(SatF64ToU32) => (nint)(delegate* unmanaged<double, uint>)&SatF64ToU32,
                nameof(SatF64ToU64) => (nint)(delegate* unmanaged<double, ulong>)&SatF64ToU64,
                nameof(Sha1SchedulePart1) => (nint)(delegate* unmanaged<V128, V128, V128, V128>)&Sha1SchedulePart1,
                nameof(Sha1SchedulePart2) => (nint)(delegate* unmanaged<V128, V128, V128>)&Sha1SchedulePart2,
                nameof(Sha256SchedulePart1) => (nint)(delegate* unmanaged<V128, V128, V128>)&Sha256SchedulePart1,
                nameof(Sha256SchedulePart2) => (nint)(delegate* unmanaged<V128, V128, V128, V128>)&Sha256SchedulePart2,
                nameof(SignedShrImm64) => (nint)(delegate* unmanaged<long, long, int, long>)&SignedShrImm64,
                nameof(Tbl1) => (nint)(delegate* unmanaged<V128, int, V128, V128>)&Tbl1,
                nameof(Tbl2) => (nint)(delegate* unmanaged<V128, int, V128, V128, V128>)&Tbl2,
                nameof(Tbl3) => (nint)(delegate* unmanaged<V128, int, V128, V128, V128, V128>)&Tbl3,
                nameof(Tbl4) => (nint)(delegate* unmanaged<V128, int, V128, V128, V128, V128, V128>)&Tbl4,
                nameof(Tbx1) => (nint)(delegate* unmanaged<V128, V128, int, V128, V128>)&Tbx1,
                nameof(Tbx2) => (nint)(delegate* unmanaged<V128, V128, int, V128, V128, V128>)&Tbx2,
                nameof(Tbx3) => (nint)(delegate* unmanaged<V128, V128, int, V128, V128, V128, V128>)&Tbx3,
                nameof(Tbx4) => (nint)(delegate* unmanaged<V128, V128, int, V128, V128, V128, V128, V128>)&Tbx4,
                nameof(UnsignedShrImm64) => (nint)(delegate* unmanaged<ulong, long, int, ulong>)&UnsignedShrImm64,
                _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown soft-fallback method"),
            };
        }

        [UnmanagedCallersOnly]
        public static V128 PolynomialMult64_128(ulong op1, ulong op2)
        {
            V128 result = V128.Zero;

            V128 op2_128 = new(op2, 0);

            for (int i = 0; i < 64; i++)
            {
                if (((op1 >> i) & 1) == 1)
                {
                    result ^= op2_128 << i;
                }
            }

            return result;
        }
    }
}
