namespace ARMeilleure.Translation.Cache
{
    // Unmapping code recycles slots, but ReservedRegion does not decommit pages.
    // Process-wide pressure must not turn a mostly empty JIT into a purge/rejit
    // loop: maintain allocation headroom, not an artificial private-commit target.
    internal sealed class JitReclaimPolicy
    {
        internal const long LowFreeBytes = 64L * 1024 * 1024;
        internal const long TargetFreeBytes = 96L * 1024 * 1024;
        internal const long MinimumAgeMs = 5000;
        internal const int MaximumBatch = 512;
        private long _nextScan;

        // Serialized by the translator publication lock. Allocation failure is
        // handled separately and never suppressed by this maintenance cooldown.
        internal bool TrySchedule(long now)
        {
            if (now < _nextScan) return false;
            _nextScan = now + 1000;
            return true;
        }

        internal static bool NeedsReclaim(ulong reservedBytes, long freeBytes) =>
            reservedBytes >= 256UL * 1024 * 1024 && freeBytes < LowFreeBytes;
    }
}
