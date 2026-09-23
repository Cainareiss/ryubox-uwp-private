using System;

namespace ARMeilleure.Translation.PTC
{
    internal static class PtcStartupPolicy
    {
        public static bool SafeJit => AppContext.TryGetSwitch("Ryujinx.Uwp.SafeJit", out bool value) && value;
        public static bool Constrained => SafeJit ||
            (AppContext.TryGetSwitch("Ryujinx.Uwp.ConstrainedScheduling", out bool value) && value);

        // Cache admission limits reserve headroom for live JIT, guest and GPU.
        public static long MaxPayloadBytes => Constrained ? 128L * 1024 * 1024 : int.MaxValue;
        public static long MaxWarmCodeBytes => Constrained ? 64L * 1024 * 1024 : long.MaxValue;
        public static int WorkerCount(int requested) => Constrained ? 1 : Math.Max(1, requested);
    }
}
