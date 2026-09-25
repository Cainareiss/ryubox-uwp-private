using Ryujinx.Common.Memory;
using System;
using System.Linq;

AppContext.SetSwitch("Ryujinx.Uwp.GuestPageStore", true);
UwpGuestPageStorePolicy policy = UwpGuestPageStorePolicy.Current;

Require(policy.Enabled, "explicit opt-in was not honored");
Require(policy.BlockBytes == 64 * 1024, "page-store block size must remain 64 KiB");
Require(policy.GetResidentTarget(MemoryPressure.Normal) >= policy.GetResidentTarget(MemoryPressure.Warning),
    "warning target exceeds normal target");
Require(policy.GetResidentTarget(MemoryPressure.Warning) >= policy.GetResidentTarget(MemoryPressure.High),
    "high target exceeds warning target");
Require(policy.GetResidentTarget(MemoryPressure.High) >= policy.GetResidentTarget(MemoryPressure.Critical),
    "critical target exceeds high target");
Require(policy.GetResidentTarget(MemoryPressure.Critical) >= policy.GetResidentTarget(MemoryPressure.Emergency),
    "emergency target exceeds critical target");
Require(policy.GetResidentTarget(MemoryPressure.Emergency) >= 128L * 1024 * 1024,
    "emergency target is below the safe policy floor");

foreach (MemoryPressure pressure in Enum.GetValues<MemoryPressure>().Distinct())
{
    Require(policy.GetResidentTarget(pressure) % policy.BlockBytes == 0,
        $"{pressure} target is not block aligned");
}

const long FiveGiB = 5L * 1024 * 1024 * 1024;
Require(RuntimeMemoryBudget.GuestCommitHeadroom(4096) == 32L * 1024 * 1024,
    "critical 4 KiB kernel/IPC commits must use the protected 32 MiB reserve");
Require(RuntimeMemoryBudget.GuestCommitHeadroom(2L * 1024 * 1024) == 128L * 1024 * 1024,
    "bulk guest commits must retain the 128 MiB reserve");
Require(RuntimeMemoryBudget.CanAdmitGuestCommit(5_235_699_712, FiveGiB, 4096),
    "Wonder's reported 4 KiB commit should fit inside the critical reserve");
Require(!RuntimeMemoryBudget.CanAdmitGuestCommit(FiveGiB - 16L * 1024 * 1024, FiveGiB, 4096),
    "critical commits must not consume the final 32 MiB reserve");
Require(!RuntimeMemoryBudget.CanAdmitGuestCommit(2L * 1024 * 1024 * 1024, FiveGiB, 3L * 1024 * 1024 * 1024),
    "bulk 3 GiB commits must not bypass the standard reserve");

Console.WriteLine("Guest page-store policy regression passed: " + policy);

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
