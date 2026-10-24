using ARMeilleure.Instructions;
using System;
using System.Reflection;

namespace ARMeilleure.Translation
{
    // NativeAOT does not materialize an unmanaged entry point when an
    // UnmanagedCallersOnly method is reached exclusively through reflection.
    // Generated guest code calls these addresses directly, so keep an
    // explicit, ABI-accurate reference to every SoftFloat helper registered
    // by Delegates.
    static class SoftFloatFunctionPointers
    {
        public static unsafe nint Get(MethodInfo method)
        {
            string type = method.DeclaringType?.Name;
            string name = method.Name;

            return type switch
            {
                nameof(SoftFloat16_32) => (nint)(delegate* unmanaged<ushort, float>)&SoftFloat16_32.FPConvert,
                nameof(SoftFloat16_64) => (nint)(delegate* unmanaged<ushort, double>)&SoftFloat16_64.FPConvert,
                nameof(SoftFloat32_16) => (nint)(delegate* unmanaged<float, ushort>)&SoftFloat32_16.FPConvert,
                nameof(SoftFloat64_16) => (nint)(delegate* unmanaged<double, ushort>)&SoftFloat64_16.FPConvert,
                nameof(SoftFloat32) => Get32(name),
                nameof(SoftFloat64) => Get64(name),
                _ => throw new ArgumentOutOfRangeException(nameof(method), type, "Unknown SoftFloat family"),
            };
        }

        private static unsafe nint Get32(string name)
        {
            return name switch
            {
                nameof(SoftFloat32.FPAdd) => Binary32(&SoftFloat32.FPAdd),
                nameof(SoftFloat32.FPAddFpscr) => BinaryFpscr32(&SoftFloat32.FPAddFpscr),
                nameof(SoftFloat32.FPCompare) => (nint)(delegate* unmanaged<float, float, byte, int>)&SoftFloat32.FPCompare,
                nameof(SoftFloat32.FPCompareEQ) => Binary32(&SoftFloat32.FPCompareEQ),
                nameof(SoftFloat32.FPCompareEQFpscr) => BinaryFpscr32(&SoftFloat32.FPCompareEQFpscr),
                nameof(SoftFloat32.FPCompareGE) => Binary32(&SoftFloat32.FPCompareGE),
                nameof(SoftFloat32.FPCompareGEFpscr) => BinaryFpscr32(&SoftFloat32.FPCompareGEFpscr),
                nameof(SoftFloat32.FPCompareGT) => Binary32(&SoftFloat32.FPCompareGT),
                nameof(SoftFloat32.FPCompareGTFpscr) => BinaryFpscr32(&SoftFloat32.FPCompareGTFpscr),
                nameof(SoftFloat32.FPCompareLE) => Binary32(&SoftFloat32.FPCompareLE),
                nameof(SoftFloat32.FPCompareLEFpscr) => BinaryFpscr32(&SoftFloat32.FPCompareLEFpscr),
                nameof(SoftFloat32.FPCompareLT) => Binary32(&SoftFloat32.FPCompareLT),
                nameof(SoftFloat32.FPCompareLTFpscr) => BinaryFpscr32(&SoftFloat32.FPCompareLTFpscr),
                nameof(SoftFloat32.FPDiv) => Binary32(&SoftFloat32.FPDiv),
                nameof(SoftFloat32.FPMax) => Binary32(&SoftFloat32.FPMax),
                nameof(SoftFloat32.FPMaxFpscr) => BinaryFpscr32(&SoftFloat32.FPMaxFpscr),
                nameof(SoftFloat32.FPMaxNum) => Binary32(&SoftFloat32.FPMaxNum),
                nameof(SoftFloat32.FPMaxNumFpscr) => BinaryFpscr32(&SoftFloat32.FPMaxNumFpscr),
                nameof(SoftFloat32.FPMin) => Binary32(&SoftFloat32.FPMin),
                nameof(SoftFloat32.FPMinFpscr) => BinaryFpscr32(&SoftFloat32.FPMinFpscr),
                nameof(SoftFloat32.FPMinNum) => Binary32(&SoftFloat32.FPMinNum),
                nameof(SoftFloat32.FPMinNumFpscr) => BinaryFpscr32(&SoftFloat32.FPMinNumFpscr),
                nameof(SoftFloat32.FPMul) => Binary32(&SoftFloat32.FPMul),
                nameof(SoftFloat32.FPMulFpscr) => BinaryFpscr32(&SoftFloat32.FPMulFpscr),
                nameof(SoftFloat32.FPMulAdd) => Ternary32(&SoftFloat32.FPMulAdd),
                nameof(SoftFloat32.FPMulAddFpscr) => TernaryFpscr32(&SoftFloat32.FPMulAddFpscr),
                nameof(SoftFloat32.FPMulSub) => Ternary32(&SoftFloat32.FPMulSub),
                nameof(SoftFloat32.FPMulSubFpscr) => TernaryFpscr32(&SoftFloat32.FPMulSubFpscr),
                nameof(SoftFloat32.FPMulX) => Binary32(&SoftFloat32.FPMulX),
                nameof(SoftFloat32.FPNegMulAdd) => Ternary32(&SoftFloat32.FPNegMulAdd),
                nameof(SoftFloat32.FPNegMulSub) => Ternary32(&SoftFloat32.FPNegMulSub),
                nameof(SoftFloat32.FPRecipEstimate) => Unary32(&SoftFloat32.FPRecipEstimate),
                nameof(SoftFloat32.FPRecipEstimateFpscr) => UnaryFpscr32(&SoftFloat32.FPRecipEstimateFpscr),
                nameof(SoftFloat32.FPRecipStep) => Binary32(&SoftFloat32.FPRecipStep),
                nameof(SoftFloat32.FPRecipStepFused) => Binary32(&SoftFloat32.FPRecipStepFused),
                nameof(SoftFloat32.FPRecpX) => Unary32(&SoftFloat32.FPRecpX),
                nameof(SoftFloat32.FPRSqrtEstimate) => Unary32(&SoftFloat32.FPRSqrtEstimate),
                nameof(SoftFloat32.FPRSqrtEstimateFpscr) => UnaryFpscr32(&SoftFloat32.FPRSqrtEstimateFpscr),
                nameof(SoftFloat32.FPRSqrtStep) => Binary32(&SoftFloat32.FPRSqrtStep),
                nameof(SoftFloat32.FPRSqrtStepFused) => Binary32(&SoftFloat32.FPRSqrtStepFused),
                nameof(SoftFloat32.FPSqrt) => Unary32(&SoftFloat32.FPSqrt),
                nameof(SoftFloat32.FPSub) => Binary32(&SoftFloat32.FPSub),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown SoftFloat32 method"),
            };
        }

        private static unsafe nint Get64(string name)
        {
            return name switch
            {
                nameof(SoftFloat64.FPAdd) => Binary64(&SoftFloat64.FPAdd),
                nameof(SoftFloat64.FPAddFpscr) => BinaryFpscr64(&SoftFloat64.FPAddFpscr),
                nameof(SoftFloat64.FPCompare) => (nint)(delegate* unmanaged<double, double, byte, int>)&SoftFloat64.FPCompare,
                nameof(SoftFloat64.FPCompareEQ) => Binary64(&SoftFloat64.FPCompareEQ),
                nameof(SoftFloat64.FPCompareEQFpscr) => BinaryFpscr64(&SoftFloat64.FPCompareEQFpscr),
                nameof(SoftFloat64.FPCompareGE) => Binary64(&SoftFloat64.FPCompareGE),
                nameof(SoftFloat64.FPCompareGEFpscr) => BinaryFpscr64(&SoftFloat64.FPCompareGEFpscr),
                nameof(SoftFloat64.FPCompareGT) => Binary64(&SoftFloat64.FPCompareGT),
                nameof(SoftFloat64.FPCompareGTFpscr) => BinaryFpscr64(&SoftFloat64.FPCompareGTFpscr),
                nameof(SoftFloat64.FPCompareLE) => Binary64(&SoftFloat64.FPCompareLE),
                nameof(SoftFloat64.FPCompareLEFpscr) => BinaryFpscr64(&SoftFloat64.FPCompareLEFpscr),
                nameof(SoftFloat64.FPCompareLT) => Binary64(&SoftFloat64.FPCompareLT),
                nameof(SoftFloat64.FPCompareLTFpscr) => BinaryFpscr64(&SoftFloat64.FPCompareLTFpscr),
                nameof(SoftFloat64.FPDiv) => Binary64(&SoftFloat64.FPDiv),
                nameof(SoftFloat64.FPMax) => Binary64(&SoftFloat64.FPMax),
                nameof(SoftFloat64.FPMaxFpscr) => BinaryFpscr64(&SoftFloat64.FPMaxFpscr),
                nameof(SoftFloat64.FPMaxNum) => Binary64(&SoftFloat64.FPMaxNum),
                nameof(SoftFloat64.FPMaxNumFpscr) => BinaryFpscr64(&SoftFloat64.FPMaxNumFpscr),
                nameof(SoftFloat64.FPMin) => Binary64(&SoftFloat64.FPMin),
                nameof(SoftFloat64.FPMinFpscr) => BinaryFpscr64(&SoftFloat64.FPMinFpscr),
                nameof(SoftFloat64.FPMinNum) => Binary64(&SoftFloat64.FPMinNum),
                nameof(SoftFloat64.FPMinNumFpscr) => BinaryFpscr64(&SoftFloat64.FPMinNumFpscr),
                nameof(SoftFloat64.FPMul) => Binary64(&SoftFloat64.FPMul),
                nameof(SoftFloat64.FPMulFpscr) => BinaryFpscr64(&SoftFloat64.FPMulFpscr),
                nameof(SoftFloat64.FPMulAdd) => Ternary64(&SoftFloat64.FPMulAdd),
                nameof(SoftFloat64.FPMulAddFpscr) => TernaryFpscr64(&SoftFloat64.FPMulAddFpscr),
                nameof(SoftFloat64.FPMulSub) => Ternary64(&SoftFloat64.FPMulSub),
                nameof(SoftFloat64.FPMulSubFpscr) => TernaryFpscr64(&SoftFloat64.FPMulSubFpscr),
                nameof(SoftFloat64.FPMulX) => Binary64(&SoftFloat64.FPMulX),
                nameof(SoftFloat64.FPNegMulAdd) => Ternary64(&SoftFloat64.FPNegMulAdd),
                nameof(SoftFloat64.FPNegMulSub) => Ternary64(&SoftFloat64.FPNegMulSub),
                nameof(SoftFloat64.FPRecipEstimate) => Unary64(&SoftFloat64.FPRecipEstimate),
                nameof(SoftFloat64.FPRecipEstimateFpscr) => UnaryFpscr64(&SoftFloat64.FPRecipEstimateFpscr),
                nameof(SoftFloat64.FPRecipStep) => Binary64(&SoftFloat64.FPRecipStep),
                nameof(SoftFloat64.FPRecipStepFused) => Binary64(&SoftFloat64.FPRecipStepFused),
                nameof(SoftFloat64.FPRecpX) => Unary64(&SoftFloat64.FPRecpX),
                nameof(SoftFloat64.FPRSqrtEstimate) => Unary64(&SoftFloat64.FPRSqrtEstimate),
                nameof(SoftFloat64.FPRSqrtEstimateFpscr) => UnaryFpscr64(&SoftFloat64.FPRSqrtEstimateFpscr),
                nameof(SoftFloat64.FPRSqrtStep) => Binary64(&SoftFloat64.FPRSqrtStep),
                nameof(SoftFloat64.FPRSqrtStepFused) => Binary64(&SoftFloat64.FPRSqrtStepFused),
                nameof(SoftFloat64.FPSqrt) => Unary64(&SoftFloat64.FPSqrt),
                nameof(SoftFloat64.FPSub) => Binary64(&SoftFloat64.FPSub),
                _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown SoftFloat64 method"),
            };
        }

        private static unsafe nint Unary32(delegate* unmanaged<float, float> pointer) => (nint)pointer;
        private static unsafe nint Binary32(delegate* unmanaged<float, float, float> pointer) => (nint)pointer;
        private static unsafe nint BinaryFpscr32(delegate* unmanaged<float, float, byte, float> pointer) => (nint)pointer;
        private static unsafe nint Ternary32(delegate* unmanaged<float, float, float, float> pointer) => (nint)pointer;
        private static unsafe nint TernaryFpscr32(delegate* unmanaged<float, float, float, byte, float> pointer) => (nint)pointer;
        private static unsafe nint UnaryFpscr32(delegate* unmanaged<float, byte, float> pointer) => (nint)pointer;
        private static unsafe nint Unary64(delegate* unmanaged<double, double> pointer) => (nint)pointer;
        private static unsafe nint Binary64(delegate* unmanaged<double, double, double> pointer) => (nint)pointer;
        private static unsafe nint BinaryFpscr64(delegate* unmanaged<double, double, byte, double> pointer) => (nint)pointer;
        private static unsafe nint Ternary64(delegate* unmanaged<double, double, double, double> pointer) => (nint)pointer;
        private static unsafe nint TernaryFpscr64(delegate* unmanaged<double, double, double, byte, double> pointer) => (nint)pointer;
        private static unsafe nint UnaryFpscr64(delegate* unmanaged<double, byte, double> pointer) => (nint)pointer;
    }
}
