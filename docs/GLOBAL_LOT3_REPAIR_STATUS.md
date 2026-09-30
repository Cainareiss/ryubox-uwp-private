# Global repair checkpoint - 2026-09-05

Scope: global regressions, per the user's latest request. Preserve the validated
Alien Hominid/Horizon material fix and presentation scaling. Bomberman remains
closed at the user's request; it is not counted as a successful menu/ingame test.

## New archive

`E:/jogos para testar ryuboxz/Logs (3).zip` contains 92 HLE log names:
54 also occur in `Logs (2)-1.zip`, and 38 are additional names. Aggregated error
logs repeat events; neither occurrence counts nor log counts are game-pass counts.
The baseline script now exposes LogFile/ArchiveCohort and detects missing AOT
code, content-integrity error codes and cover-decoder errors separately.

Sonic Mania and Alien Hominid are not present in this new batch. Sonic Superstars
must not be substituted for Sonic Mania. The user's observed 15 FPS regression
is not assigned a root cause without a corresponding trace.

## NativeAOT crash: concrete root cause and correction

`CoreHost/fatal.log` reports:

```text
System.NotSupportedException: This object cannot be invoked because no code was generated for it: 'ARMeilleure.Instructions.SoftFloat64.FPRSqrtEstimate(System.Double)'.
```

`EmitterContext.Call` still used MethodHandle.GetFunctionPointer for SoftFloat,
although Delegates already had explicitly rooted unmanaged entry points. It now
uses the same ABI-correct SoftFloatFunctionPointers path. No instruction behavior,
FP precision or game-specific profile was changed.

`eng/SoftFloatAotRegression` was published and executed as actual NativeAOT:
88 guest-callable helper emission targets passed with dynamicCodeSupported=False.
This tests pointer emission and AOT reachability, not numeric FP semantics or a
complete Transformers EarthSpark playthrough. The test excludes managed-only
implementation helpers because NativeAOT omits the UCO pseudo reflection attribute.

## Compiler arena ownership and retention

Confirmed in source:

- ResetAll omitted LiveRanges and LiveIntervals; repeated optimized compilations
  could retain their native payload until thread collection/finalization.
- Translation reset ran only on its success path.
- Guest, background translator and PTC workers had no explicit arena disposal.
- ResetAll instantiated unused pools merely to reset them.

Changes:

- Reset all six existing pools without creating new ones.
- Reset after successful or failed translation through finally; active translation
  telemetry is decremented on failure as well.
- Dispose thread-local pools on guest execution exit, background worker exit and
  PTC worker exit, on the owning thread after IR is no longer in use.
- Under constrained scheduling/high memory pressure, retain zero pooled pages
  at a reset boundary. Never free live IR to meet a budget.
- Track actual allocated arena payload bytes (including oversized blocks), not
  just arena counts. CoreHost's jit-cache log now includes arenaAllocatedBytes.
- Make disposal idempotent and reject use after disposal.

Regression fixture: `eng/ArenaLifecycleRegression`. Includes exact byte accounting,
pool rewinding, pressure shedding, repeated disposal, 100 worker failure/cleanup
cycles, and 100 basic plus 100 optimized actual compilations. Repeated compilation
keeps stable native arena payload and identical generated machine code. The test
does not execute that code or establish whole-game memory stability.

## Memory findings that remain open

Shovel Knight variants show increasing working set while managed/JIT counters
remain much smaller. King of Cards reaches roughly 3.8 GiB with roughly 124 MiB
managed and 132 MiB JIT. Hazard bytes are CUMULATIVE copied bytes, not live RAM.
The records do not prove that every copied byte leaked.

Hades II approaches 3.9 GiB working set with only one recorded hazard copy and
then fails allocation of a 4096x4096 RGBA texture with HRESULT 0x8007000E.
Therefore the batch's memory failures cannot all be explained by hazard copies.
New arena byte telemetry will distinguish compiler retention from GPU resources.
Texture/staging accounting, full session-start failure teardown, safe JIT pressure
reclaim and dynamic-loop gradient lowering still need work/validation.

## Packaging and validation boundary

MSIX 0.2.0.100 was generated and signed before the SoftFloat/arena changes above.
It was NOT installed by this work and does NOT contain these newest corrections.
Do not offer that existing artifact as a completed updated release.

The corrected audio pool was exercised in a second Bomberman run: thousands of
resubmissions succeeded with a small live packet count and no completed-packet
accumulation. Warm PTC profiling/translation/save also occurred. Presented black
frames did not prove arrival at the menu; the user closed the game.

Rerun evidence lives under artifacts/: softfloat-aot-regression.log,
arena-lifecycle-regression.log, structural-jit-regression.log,
structural-ptc-regression.log and structural-material-regression.log.
Eighteen JIT/lifecycle/budget tests, fifteen PTC tests and twelve actual FXC/WARP
material tests passed again after this round. No new Xbox/FPS claim is made.
