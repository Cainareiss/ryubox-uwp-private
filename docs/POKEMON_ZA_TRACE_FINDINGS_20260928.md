# Pokemon Legends Z-A: latest trace results

Date: 2026-09-28. This is diagnostic evidence; the crash is not fixed.

## Reproduction

- D3D12 direct CoreHost, hidden window, unchanged 5 GiB host cap.
- SafeJit enabled, pinned dispatch, automatic A input every eight seconds.
- Runtime: `artifacts/direct-d3d12-za-probe-v3-20260928`.
- Evidence: `probe-20260927-225454`.
- The game queued 12 frames, then failed after 5m58s with invalid guest read
  `0xD0`. Frame 8 capture is entirely black.

## State immediately before the fault

- `E414640=1` and prerequisite object pointer `E414500=0xC12E2E8`.
- Singleton pointer target `E413780` remains zero; its initialized flag
  `E413880` also remains zero.
- SceneThread entered guest PC `0x8E635B8` with the prerequisite object.
- No probe hit was recorded for initialization PCs `0x8BE3BE0`, `0x8BE3DA8`,
  or `0x8BE3DF0` before the fault at `0x8770B40`.
- Caller `0xAF99AA4` observes `E413880==0`, selects `X0=0`, then calls the
  routine that reads `[X0+0xD0]`.

This narrows the missing path: the prerequisite exists, but the guest path that
constructs/publishes the singleton is not observed before another thread
dereferences it. The exact owner/call path remains unknown. No emulator-side
zero substitution or guest-state patch was applied.

The second call-path trace reproduced the same fault at 5m58s. It recorded
caller block `0xAF99AA4` immediately before callee `0x8770B40`, with `X0=0`,
`E413880=0`, and prerequisite `E414640=1`. Disassembly shows the caller's
`CSEL X0, XZR, X8, EQ` deliberately chooses null while that global flag is
zero. The observed `0x8E635B8` routine is a destructor that clears a different
object's flag; it is not the constructor. The constructor's direct-call and
ADRP/ADD reference scans found no static call site, so it is likely reached by
an indirect/object lifecycle path that still needs tracing.

## Static registration-table lead (2026-09-28)

The decrypted program image contains the 64-bit module-relative offset
`0x006DEBE0` exactly once outside code, at Data module offset `0x3C15648`
(guest VA `0xC11A648`). It sits in a contiguous array of module-relative code
offsets beginning near `0xC11A5B0`. Neighboring entries include unrelated
helpers and the constructor, but not the later init gates `0x6DEDA8` or
`0x6DEDF0`. The array may be a dispatch/registration structure; its owner and
consumer have not been established. Exact ADRP+ADD/load references to the
slot were not found, which is consistent with indexed/indirect access but is
not proof of that interpretation.

This does not yet explain why the singleton stays null. In particular, it is
not evidence that a code-cache or host-memory failure prevented construction.
Do not change guest state or bypass the pool limit based on this lead.

Correction after inspecting the continuation of that routine: `0x8BE3BE0`
does belong to the initialization path. At `0x8BE3C88` it reads `E414640`,
returns early if zero, otherwise calls `0x8E635B8` and then `0x8BE3DA8`.
That gate calls `0x8BE3DF0`, which constructs `E413780` through `0x8BE3E70`
and publishes `E413880=1` using LDXR/STLXR. The previous inference that the
routine was unrelated was based on an incomplete disassembly and is withdrawn.
Likewise, `0x8E635B8` changes resource references in `E414500`; labeling it a
destructor was not established by the available evidence.

## Memory observation

At startup, a 4 MiB `MapPhysicalMemory` request was denied because the modeled
guest application pool had reached its 3285 MiB limit. Later allocations
succeeded after the pool usage dropped. Since this occurred minutes before the
same null dereference, it is not yet established as its cause; the quota must
not be bypassed without proving incorrect alias accounting.

## Verification

- `JitCacheRegression`: 226 checks passed, including eight combinations of
  synchronization callbacks with live GPR, SIMD, and NZCV values.
- These tests do not exercise this title's singleton creation or all guest
  scheduler interleavings.
- No playable frame or title screen was captured. Keep the issue open.

## Next diagnostic

Trace actual stores/publication to `E413780` and `E413880`, plus the caller
block `0xAF99AA4` and fault `0x8770B40`. Follow the now-confirmed initialization
chain rooted at `0x8BE3BE0` and its indirect dispatch slot `0xC11A648`.
Identify whether initialization is skipped, returns early, or publishes via
an indirect path; then make a focused guest-memory/JIT fix and rerun the same
test.

## Reproduction with bounded page-write watch (2026-09-28)

Two hidden CoreHost runs were made with the direct D3D12 runtime, the existing
5 GiB limit, no `RYUJINX_UWP_MEMORY_6GIB`, and no SafeJit override. Both runs
reproduced the same guest null dereference after approximately six minutes:

- Run 1: `artifacts/direct-d3d12-za-writewatch-20260928/probe-20260928-014911`;
  11 presents were counted before teardown, but the saved frame-8 image is
  black. Private commit peaked at 5,098,409,984 bytes (4.75 GiB); JIT
  allocation failures stayed at zero. The memory-pressure state reached Emergency before the fault, so
  pressure remains a concern but is not yet established as the null-pointer
  cause.
- Run 2: `artifacts/direct-d3d12-za-writewatch-20260928/probe-20260928-020048`;
  fault at guest `0x8770B40`, called from `0xAF99AA4` with `X0=0`; the invalid
  guest read is `0xD0`. At that instant `E413780=0`, `E413880=0`, while
  `E414500=0xC12E2E8` and `E414640=1`. Private commit peaked at
  5,128,310,784 bytes (4.78 GiB), still below the 5 GiB limit.
- Both logs show the same call-site branch selecting null while the guard is
  zero. The page containing `E413780`/`E413880` was initially unmapped; the
  only page-dirty notification was at startup runtime PC `0x8501BD0`, not a
  proven store to either field. No later publication was observed by the
  bounded watch.
- The capture for run 2 is also black. This test therefore does not qualify as
  a successful boot or a visual fix.

The opt-in diagnostic `GuestAddressWriteProbe` records precise events and
page-dirty events. A page-dirty event is page-granular, not proof that the
target word itself was written. The 4 KiB watch page was dirtied only once;
the probe does not continuously reprotect the page because doing so during
guest execution would perturb memory tracking. Do not interpret its lack of
later callbacks as proof that no guest instruction ever wrote to either word.

### Disposition before the Canary 1.3.183 comparison

At this point in the investigation the original invalid guest access was
**not fixed**. The evidence ruled out a
JIT allocation failure at the fault and makes a null guest object the direct
cause, but does not identify why the title's initialization path leaves the
singleton unpublished. Returning zero, skipping the dereference, raising the
memory cap, or synthesizing the singleton would hide the failure and could
corrupt guest execution. The next useful step is a targeted guest call-path
trace around the flag's initialization and the indirect registration table,
plus a title/firmware comparison; memory-pressure effects should be correlated
but not assumed causal.

## Exact access probe and reference follow-up (2026-09-28)

The hidden run in
`artifacts/direct-d3d12-za-access-20260928/probe-20260928-113513` reproduced
the failure after approximately 9 min 39 s. The saved frame-8 capture was
visually inspected and is entirely black, not a successful title screen.

The opt-in exact access probe observed startup write attempts to both fields
at `0x8501C80`, and the loader reading `0xC11A648` at `0x85010B8`.
No subsequent publication was observed for the selected targets through this
instrumented path. At the fault the flag remains zero and the guest selects
the null pointer. A write-attempt event does not establish successful atomic
publication; absence of an event is not proof covering every host-side write.

Heavy probing also exposed excessive retire/recompile activity in the first
256 MiB JIT region. A maintenance policy change now prefers growth while an
additional region plus 128 MiB headroom fits the existing budget. Per-commit
admission, maximum region count and the 5 GiB cap remain enforced. This is a
separate performance correction, not a demonstrated fix for the guest null.

- `artifacts/za-access-regression-20260928.log`: 244 checks passed, including
  probe execution and growth-policy boundaries.
- `artifacts/za-growth-build-20260928.log`: NativeAOT publish completed;
  existing restore/trimming/AOT warnings remain.
- `artifacts/direct-d3d12-za-growth-20260928`: published runtime, not yet
  validated in the game with the new maintenance policy.

The user identified Ryubing Canary 1.3.183 as a working reference. Its exact
tag was fetched into an isolated source checkout; see
`RYUBING_REFERENCIA_POKEMON_ZA_20260928.md` for source provenance, differences
and the controlled comparison plan. No upstream code was blindly transplanted.

## Working correction combination from Canary 1.3.183 comparison

The reference comparison identified upstream commit `39f55b2a` (19/11/2025),
which added translated-call depth tracking after Canary 1.3.183. The Ryubox
pinned dispatcher already returns every guest call/jump to the host dispatcher;
it therefore has no nested native guest call stack for this guard to protect.
Keeping the depth instrumentation in that flat mode is redundant and was
selected as the first controlled compatibility variable around the
`resident_ui` initialization.

A NativeAOT run disabled this guard while retaining D3D12, pinned Safe-JIT,
the 5 GiB limit and all guest memory protections:

- Evidence: `artifacts/direct-d3d12-za-canary183-20260928/`
  `probe-20260928-122637/`.
- Runtime: 12 minutes, stopped by the test deadline rather than a crash.
- At 6m52s the prerequisite path entered `0x8E635B8`.
- At 7m18s `0x8BE3BE0`, `0x8BE3C80`, `0x8BE3DA8`, `0x8BE3DF0` and
  `0x8BE3E70` executed in order.
- At 7m40s `E413780=0xC0BBE00` and `E413880=1`; `0x8770B40` received
  `X0=0xE413780`, not null. The old `0xD0` access did not recur.
- 1,221 frames were processed and 709 presented before controlled teardown.
- `errors.log` and `fatal.log` contain zero bytes; JIT allocation failures and
  evictions remained zero. Three lazy regions were used under the unchanged cap.
- Peak private commit was approximately 4.42 GiB. The session entered Emergency
  pressure but cleanup completed without device loss.

The implementation now disables deep-call recursion protection automatically
only for pinned dispatch. Epoch/native nested dispatch retains the upstream
guard. A local diagnostic override remains available as
`RYUJINX_UWP_TEST_DEEP_CALL_PROTECTION` only when the local performance-test
mode is explicitly enabled. This is a dispatcher-mode correction, not a
title-ID workaround.

This was not a one-variable comparison against the older failing binaries:
the successful runtime also includes the new policy that grows the JIT while
another region plus 128 MiB headroom fits. Every earlier failing run showed
massive cold-code churn (`48,128`, `49,152` or `96,256` evictions), whereas
the successful run used three regions with zero evictions. Therefore the
evidence proves the **combination** prevents the observed null access, but does
not yet assign sole causality between flat pinned call flow and stable JIT code
retention. A same-binary run with deep-call protection forced on is still
required to isolate their individual contributions.

`JitCacheRegression` passed 276 checks after extending the exclusive
publication matrix to both guard states, native/managed dispatch, tracked and
untracked PTEs, initial flag values and same/across-call publication. The
CoreHost Release build also completed with zero errors; existing NuGet and
NativeAOT warnings remain.

A second NativeAOT run without the compatibility flag logged the new default correctly:
`deepCallProtection=False; flatPinnedCallFlow=True`. It could not reach guest
execution because the PC then had only about 3.1 GiB of system commit free and
Windows rejected the title's initial 3 GiB commit with error 1455. No pagefile
or memory limit was changed to force that run.

### Remaining visual validation

The frame-8 capture from the successful 12-minute run is uniformly near-black
despite continuing FIFO/present activity. It is not valid title-screen
evidence. The test script had explicitly lowered
`RYUJINX_UWP_CAPTURE_MIN_SECONDARY` to zero, which allowed that monochrome
buffer (`secondaryColorBin=0`) to pass the visibility filter. This proves a
false-positive in the diagnostic setup, not by itself a D3D12 output failure.

The script now defaults to present 600 and restores the normal colour-diversity
filter. Accepting a monochrome frame requires an explicit diagnostic switch.
The invalid guest `0xD0` crash is corrected under pinned dispatch, but a
playable/title-screen image is still **not** validated. A late-frame run needs
enough host commit to reach the title; the current machine had only about
3.46 GiB free versus the previous run's approximately 4.42 GiB peak.
