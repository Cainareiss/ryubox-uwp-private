using System;
using System.Runtime.InteropServices;

namespace ARMeilleure.Instructions
{
    static class MathHelper
    {
        internal static unsafe nint GetFunctionPointer(string methodName)
        {
            return methodName switch
            {
                nameof(Abs) => (nint)(delegate* unmanaged<double, double>)&Abs,
                nameof(Ceiling) => (nint)(delegate* unmanaged<double, double>)&Ceiling,
                nameof(Floor) => (nint)(delegate* unmanaged<double, double>)&Floor,
                nameof(Round) => (nint)(delegate* unmanaged<double, int, double>)&Round,
                nameof(Truncate) => (nint)(delegate* unmanaged<double, double>)&Truncate,
                _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown double math helper"),
            };
        }

        [UnmanagedCallersOnly]
        public static double Abs(double value)
        {
            return Math.Abs(value);
        }

        [UnmanagedCallersOnly]
        public static double Ceiling(double value)
        {
            return Math.Ceiling(value);
        }

        [UnmanagedCallersOnly]
        public static double Floor(double value)
        {
            return Math.Floor(value);
        }

        [UnmanagedCallersOnly]
        public static double Round(double value, int mode)
        {
            return Math.Round(value, (MidpointRounding)mode);
        }

        [UnmanagedCallersOnly]
        public static double Truncate(double value)
        {
            return Math.Truncate(value);
        }
    }

    static class MathHelperF
    {
        internal static unsafe nint GetFunctionPointer(string methodName)
        {
            return methodName switch
            {
                nameof(Abs) => (nint)(delegate* unmanaged<float, float>)&Abs,
                nameof(Ceiling) => (nint)(delegate* unmanaged<float, float>)&Ceiling,
                nameof(Floor) => (nint)(delegate* unmanaged<float, float>)&Floor,
                nameof(Round) => (nint)(delegate* unmanaged<float, int, float>)&Round,
                nameof(Truncate) => (nint)(delegate* unmanaged<float, float>)&Truncate,
                _ => throw new ArgumentOutOfRangeException(nameof(methodName), methodName, "Unknown float math helper"),
            };
        }

        [UnmanagedCallersOnly]
        public static float Abs(float value)
        {
            return MathF.Abs(value);
        }

        [UnmanagedCallersOnly]
        public static float Ceiling(float value)
        {
            return MathF.Ceiling(value);
        }

        [UnmanagedCallersOnly]
        public static float Floor(float value)
        {
            return MathF.Floor(value);
        }

        [UnmanagedCallersOnly]
        public static float Round(float value, int mode)
        {
            return MathF.Round(value, (MidpointRounding)mode);
        }

        [UnmanagedCallersOnly]
        public static float Truncate(float value)
        {
            return MathF.Truncate(value);
        }
    }
}
