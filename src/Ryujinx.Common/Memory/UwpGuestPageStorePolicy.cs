using System;

namespace Ryujinx.Common.Memory
{
    /// <summary>
    /// Policy-only foundation for the Xbox guest page store. The pager remains
    /// opt-in and is not activated until the native backing implementation can
    /// preserve guest aliases, zero-fill and teardown semantics.
    /// </summary>
    public readonly record struct UwpGuestPageStorePolicy(
        bool Enabled,
        long BlockBytes,
        long NormalResidentBytes,
        long WarningResidentBytes,
        long HighResidentBytes,
        long CriticalResidentBytes,
        long EmergencyResidentBytes)
    {
        private const long MiB = 1024L * 1024L;
        public const long RequiredBlockBytes = 64L * 1024L;

        public static UwpGuestPageStorePolicy Current => Load();

        public long GetResidentTarget(MemoryPressure pressure) => pressure switch
        {
            MemoryPressure.Emergency => EmergencyResidentBytes,
            MemoryPressure.Critical => CriticalResidentBytes,
            MemoryPressure.High => HighResidentBytes,
            MemoryPressure.Warning => WarningResidentBytes,
            _ => NormalResidentBytes,
        };

        public override string ToString() =>
            $"enabled={Enabled} implementation=policy-only blockBytes={BlockBytes} " +
            $"normalResidentBytes={NormalResidentBytes} warningResidentBytes={WarningResidentBytes} " +
            $"highResidentBytes={HighResidentBytes} criticalResidentBytes={CriticalResidentBytes} " +
            $"emergencyResidentBytes={EmergencyResidentBytes}";

        private static UwpGuestPageStorePolicy Load()
        {
            AppContext.TryGetSwitch("Ryujinx.Uwp.GuestPageStore", out bool enabled);

            long normal = ReadAlignedMiB("Ryujinx.Uwp.GuestPageStore.NormalMiB", 768, 256, 1536);
            long warning = ReadAlignedMiB("Ryujinx.Uwp.GuestPageStore.WarningMiB", 640, 256, normal / MiB);
            long high = ReadAlignedMiB("Ryujinx.Uwp.GuestPageStore.HighMiB", 512, 192, warning / MiB);
            long critical = ReadAlignedMiB("Ryujinx.Uwp.GuestPageStore.CriticalMiB", 384, 128, high / MiB);
            long emergency = ReadAlignedMiB("Ryujinx.Uwp.GuestPageStore.EmergencyMiB", 256, 128, critical / MiB);

            return new UwpGuestPageStorePolicy(
                enabled,
                RequiredBlockBytes,
                normal,
                warning,
                high,
                critical,
                emergency);
        }

        private static long ReadAlignedMiB(string key, long fallbackMiB, long minimumMiB, long maximumMiB)
        {
            long valueMiB = fallbackMiB;
            object configured = AppContext.GetData(key);
            if (configured is long longValue)
            {
                valueMiB = longValue;
            }
            else if (configured is int intValue)
            {
                valueMiB = intValue;
            }
            else if (configured is string text && long.TryParse(text, out long parsed))
            {
                valueMiB = parsed;
            }

            valueMiB = Math.Clamp(valueMiB, minimumMiB, maximumMiB);
            long bytes = checked(valueMiB * MiB);
            return bytes / RequiredBlockBytes * RequiredBlockBytes;
        }
    }
}
