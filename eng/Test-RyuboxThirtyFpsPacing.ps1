$ErrorActionPreference = 'Stop'
$source = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../src/Ryujinx.Uwp.CoreHost/CoreHostExports.cs') -Raw
$start = $source.IndexOf('        private static bool ShouldRepeatThirtyFpsPresentation(long sequence)')
$end = $source.IndexOf('        private static bool UsesThirtyFpsPresentationPacing()', $start)
if ($start -lt 0 -or $end -le $start) { throw 'Presentation gate implementation not found.' }
# Compile the production method unchanged against a deterministic clock.
$method = $source.Substring($start, $end - $start)
$prefix = @'
using System;
using System.Threading;
namespace RyuboxPacingRegression {
static class Stopwatch {
    public const long Frequency = 60000000;
    public static long Now;
    public static long GetTimestamp() { return Now; }
}
static class CoreHostLog { public static void Write(string channel, string message) {} }
public static class Gate {
    private static long _pokemonNextPresentationTimestamp;
    private static long _pokemonRepeatedFrameCount;
    private static long _pokemonPacedFrameCount;
    private static ulong _activeProgramId = 0x01009EA00B714000UL;
    public static int Run(int sourceFps, int count, bool jitter) {
        _pokemonNextPresentationTimestamp = _pokemonRepeatedFrameCount = _pokemonPacedFrameCount = 0;
        for (int i = 0; i < count; i++) {
            Stopwatch.Now = 1000000L + i * (Stopwatch.Frequency / sourceFps)
                + (jitter ? (i % 2 == 0 ? 10000L : -10000L) : 0L);
            ShouldRepeatThirtyFpsPresentation(i);
        }
        return (int)_pokemonPacedFrameCount;
    }
    public static bool LateFrameDoesNotBurst() {
        Run(60, 60, false);
        Stopwatch.Now += Stopwatch.Frequency * 3;
        bool late = ShouldRepeatThirtyFpsPresentation(61);
        bool immediate = ShouldRepeatThirtyFpsPresentation(62);
        return !late && immediate;
    }
'@
Add-Type -TypeDefinition ($prefix + $method + "`n}}")
foreach ($rate in @(30, 60, 120)) {
    $actual = [RyuboxPacingRegression.Gate]::Run($rate, $rate * 10, $true)
    if ($actual -ne 300) { throw "Rate $rate with jitter: expected 300 frames / 10 seconds, got $actual" }
    "PASS: ${rate}Hz callbacks with sub-ms jitter -> 300 accepted frames / 10s"
}
$slow = [RyuboxPacingRegression.Gate]::Run(15, 150, $false)
if ($slow -ne 150) { throw "Slow producer lost extra frames: $slow" }
'PASS: 15 FPS producer stays 15 FPS; no extra throttling or invented frames'
if (-not [RyuboxPacingRegression.Gate]::LateFrameDoesNotBurst()) { throw 'Late frame caused catch-up burst.' }
'PASS: long stall resets deadline without a presentation burst'
