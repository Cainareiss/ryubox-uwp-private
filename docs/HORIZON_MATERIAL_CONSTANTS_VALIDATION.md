# Horizon Chase: material constants and requested 30 FPS presentation

## Scope

Shared D3D11 correctness fixes, retaining the bounded Horizon native JIT profile
from 0.2.0.98. No changes to audio, guest resolution, crop, XAML matrix or firmware.
These changes are not yet proof of compatibility with Alien Hominid or Xbox.

## Reproduced defects

1. `SetStorageBuffers` wrote `_uniformBufferAssignments`, while
   `SetUniformBuffers` did not. `RebindUniformBuffers` therefore restored the
   wrong descriptor namespace after program changes and temporary host passes.
   The cache now records only uniform assignments, including explicit unbinds.
2. `CopyBuffer` invalidated the CPU mirror, but `GetConstantBuffer` returned an
   old cached GPU constant buffer unchanged. Creating another slice also used
   the stale mirror. An isolated software D3D11 test reproduced the mismatch.
   Known CPU mirrors now follow GPU buffer copies; only unknown GPU writes
   require synchronization before constant slices are used. All cached slices
   are refreshed together, preserving untouched bytes after partial updates.

## Automated checks

Run `dotnet run --project eng/D3D11MaterialRegression -c Release` on Windows.
Uses a real WARP D3D11 device, with no game, firmware, keys or UI required.

- Initial constant upload and zero padding.
- Storage/uniform namespace separation.
- Actual pixel constant slots b0 and b1 after switching resource layouts,
  without resubmitting the guest uniform assignment.
- Existing and newly created slices after GPU copy.
- Partial CPU update preserves copied bytes.
- Unknown GPU write refreshes every cached slice.
- Explicit uniform unbind replaces the old assignment.

Evidence: `artifacts/material-regression-before.log` fails the GPU-copy check;
`artifacts/material-regression-after.log` passes all ten checks.

## Frame presentation target

Horizon (`01009EA00B714000`) opts into the existing 30 FPS presentation gate.
This does not change guest vblank, game clock or JIT configuration, and does not
sleep on the GPU/FIFO thread. A 0.5 ms tolerance prevents tiny scheduling jitter
from missing an entire display refresh; absolute deadlines avoid cumulative drift.

`eng/Test-RyuboxThirtyFpsPacing.ps1` compiles the production gate method against
a deterministic clock. Checks: 30/60/120 Hz jittered input -> 300 presentations
over 10 seconds; a 15 FPS producer remains 15 FPS; no catch-up burst after a stall.
Evidence: `artifacts/horizon-pacing-regression.log`.

This is a presentation target, not a guarantee that the game produces 30 FPS.
Visual race verification and Xbox performance measurements remain separate.

## Interactive validation

CoreHost Release/NativeAOT publish succeeded on 2026-09-04. SHA-256:
`BB6A51F2035323E0322A82A462CA8AF8A4240412A4E3B7D4D38C1A1AAE279969`.
Build log: `artifacts/horizon-material-corehost-publish.log`.

Visible host test started at 23:59:42 UTC using that published binary and the
existing local SSD diagnostic copy of the user's NSP. It advanced through
Aquiris, title, World Tour, San Francisco and Grass Hills.

Real desktop capture at 00:04:48 UTC (2026-09-05):
`artifacts/horizon-material-race-check.png`.
Race is active (lap 1/3, 102 MPH). The previously black player car is red;
trees, buildings, hills, bridge and flags are colored. This verifies the reported
black-material defect in this Horizon scene. It is not a claim that all visual
defects in all titles are fixed. Alien Hominid has not been retested.

The first race performance samples were about 10-12 FPS on this PC, below the
configured 30 FPS presentation target. The time-gate regression tests must not
be interpreted as evidence of 30 FPS gameplay on the PC or Xbox. The visible
host was left open for user validation and subsequently stopped at 00:04:54 UTC
(normal `Stopping CoreHost` entry, no new error-log entry). The user confirmed
the image was correct and requested an MSIX. No shutdown or installation performed.

## Package

Version 0.2.0.99 was built and signed successfully. Package inspection confirmed
the race-tested CoreHost hash and unchanged native frontend from 0.2.0.98.
Further frame-rate optimization is deferred as requested.

Package:
`artifacts/xbox/Release/AppPackages/Ryujinx.Uwp.App_0.2.0.99_x64_Test/Ryujinx.Uwp.App_0.2.0.99_x64.msix`

- Size: 111,648,802 bytes.
- SHA-256: `FE41D847940272DC1C31A23D45F9FD383A28F6134C49C7C1E827454E4C87E7CD`.
- Authenticode status: Valid.
- Identity/version/architecture: Ryujinx.Xbox.UWP / 0.2.0.99 / x64.
- No NSP/NSZ/XCI/NCA, keys or private signing certificate in the package.
- Build log: `artifacts/horizon-material-msix-0.2.0.99.log`.
- Not installed automatically.
