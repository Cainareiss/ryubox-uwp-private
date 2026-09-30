# Structural repair plan — 2026-09-05

Baseline: signed 0.2.0.99, Horizon material fix verified. Preserve presentation
matrix, orientation/crop profiles and validated constant-buffer fixes.
Request: attached 12-step plan; primary target Let's Go Pikachu.
Evidence: `E:/jogos para testar ryuboxz/Logs (2)-1.zip` (53 HLE logs).

## Ordered work / completion gates

1. **In progress — PTC end-to-end.** Confirmed missing native-to-managed settings
   bridge: ImGui default true but changed callback only handled FPS overlay;
   persisted snapshot defaults false. Added independent PTC change mailbox,
   restore/persistence and removed Captain Toad-only forced-enable override.
   Implemented transactional cache/profile writes, fallback to validated backup,
   final short-session profile flush, bounded metadata parsing and constrained
   PTC startup admission (128 MiB payload, 64 MiB warm code, one worker).
   Safe-JIT no longer preloads High-CQ code or recreates it from profile hints.
   Native DLL and managed frontend compile; serializer/native-export smoke test
   passes. Still need installed UI toggle and real cold/warm game validation.
   Explicit user disable must remain respected. Default is not forcibly enabled.
2. **In progress — JIT telemetry and safe reclaim.** Fixed allocator range leak
   on failed commit; reservation is now published only after successful reserve;
   telemetry/dispose synchronize and disposal is idempotent. Seven isolated tests
   pass with real virtual memory, including injected commit failure and reuse.
   `jit-cache.log` reports committed/reserved/free/largest free block/fragmentation
   inputs/allocations/unmaps/reclaimed address bytes/failures every second.
   Reclaimed address space does NOT imply physical RAM was decommitted.
   Epoch-delayed retirement and bounded cold-candidate eviction are implemented.
   The hard-limit exception still exists if no safe range becomes available.
   Never unmap executing code or merely remove the guard. Coordinate dispatch
   links, cached delegates, native return stacks and quiescence before eviction.
3. Partial — memory categories, pressure bands, host-reported budget bounded by
   5 GiB, PTC/HLSL/JIT/managed accounting, serialized FXC cache misses and optional
   bytecode-cache shedding. Uninstrumented categories explicitly say untracked.
   Texture/staging/native totals and additional pressure actions remain pending.
4. Implemented/repro tested — normalization dropped TEXCOORD16+ declarations,
   leaving dangling structure member accesses. Preserve those declarations;
   minimal reproduction compiles using actual FXC. Per-hash HLSL and metadata
   failure files added. Zero failures across the historical batch is unverified.
5. Partial — fallback uses previously proven storage descriptors with full
   generated address/range checks; rich unresolved-binding log. Does not solve
   all missing-provenance cases; Winter Burrow regression remains pending.
6. Implemented/unit tested — lifecycle lock serializes interrupt versus native
   context disposal; 300 concurrent stress cycles pass. Entire scheduler teardown
   and exceptions during translator startup remain unverified.
7. Implemented — constrained host queries app-data scope with GetDiskFreeSpaceExW
   instead of DriveInfo on Q:\ root. Xbox sandbox/quotas still need validation.
8. Diagnostic implementation — map/unmap wrappers preserve all original kernel
   checks and record range/alignment/permission/handle/process/result. No kernel
   error is masked without evidence; exact failing game sequence is pending.
9. Pending — gradient/dynamic-loop semantic lowering.
10. Implemented, native validation in progress — 64 in-flight packets, 8 MiB payload
    bound, 32-block/8 MiB reusable pool, 256 retained-packet bound, counters and
    explicit HRESULT logging. Six silent real-XAudio2 checks passed. The first
    Bomberman run revealed completed-packet accumulation when the game queues a
    registered buffer repeatedly. Submit now removes completed packet records
    (missing record already means consumed); a 300-resubmission test was added.
    This regression was caught before packaging. All seven real XAudio2 checks,
    including 300 resubmissions without unregister, now pass. Rechecking Bomberman.
11. Partial — CNMT Patch/DLC rejected with base-game explanation; loose ExeFS
    fallback now requires main.npdm AND main. Frontend catalogue classification,
    associated-update display and System/Unknown metadata remain pending.
12. Pending — regression CSV, twenty listed games, long Pokemon sessions.

No replacement MSIX certified until required validations genuinely pass.
A successful compilation is not a twenty-game regression test.

## Evidence and reproducible checks

- `eng/Read-RyuboxBatchBaseline.ps1` reads the original ZIP without extracting or
  executing it. Output: `artifacts/structural-baseline/{game-regression,error-evidence,error-summary}.csv`.
  53 historical HLE logs; 40 contain PTC disabled. Seven HLE logs contain the JIT
  hard-limit pattern; aggregate fatal/session files repeat some of those errors.
  35 invalid-subscript occurrences in `CoreHost/d3d11-shader.log`.
  Unknown menu/ingame/FPS remain unknown, not inferred from application loading.
- `eng/PtcRegression`: real PTC serializer/disk fixtures, valid code metadata,
  short session flush, backup fallback, invalid relocation bounds, failure paths,
  timer/stop/dispose stress, constrained policy. Does not execute cached guest code.
- `eng/PtcSettingsRegression`: production managed settings serializer with an
  explicitly isolated storage double, plus actual native DLL exports. Does not
  claim UWP sandbox persistence or mouse/controller navigation was exercised.
- `eng/JitCacheRegression`: real Windows reservations/protection and injected
  commit failure; map/unmap/reuse/coalescing/telemetry/dispose. Does not run a game.
- Compile logs: `artifacts/structural-{ptc-native,ptc-frontend,corehost}-build.log`.
  Initial frontend `/t:Compile` skipped generated shell source; full `/t:Build`
  with packaging disabled succeeded. No frontend source rollback was needed.
- Existing 0.2.0.99 MSIX remains untouched; development changes are not installed.

### Previous checkpoint (before epoch implementation)

15 PTC checks + 4 settings/native-export checks + 7 JIT allocation checks passed.
Re-ran the 10 real WARP material checks and 5 deterministic presentation-pacing
checks: all passed. These 41 checks are NOT 41 game runs. CoreHost managed build,
frontend build and native host compilation succeeded (see logs above).
No new NativeAOT publish, MSIX, Xbox run or long Pokemon regression was performed.
Existing 0.2.0.99 MSIX SHA256 remains
`FE41D847940272DC1C31A23D45F9FD383A28F6134C49C7C1E827454E4C87E7CD`.

Next implementation gate is safe code retirement: current `_oldFuncs` owns stale
translations until session teardown. Removing them immediately is unsafe because
guest native return stacks may still reference them. A candidate epoch mechanism
must include both managed and native dispatch boundaries, sleeping SVC threads,
concurrent function-table publication, background workers and exception teardown.
Counting only managed `ExecuteSingle` entries misses the native dispatcher path.
No such mechanism was claimed or enabled in that previous checkpoint.

### Current development checkpoint

Epoch retirement includes managed and generated native dispatch boundaries,
dispatch-table invalidation before retirement, publication serialization and
reader exit. A sleeping SVC with a live native return stack prevents reclaim.
Cold selection is approximate (lookup age, not every native table hit); pressure
can still hit the guard while readers are not quiescent. Do not claim the JIT
hard-limit criterion or long-session memory stability has passed.

18 allocation/epoch/lifecycle/memory/filesystem checks and 12 real FXC/WARP
shader/material checks passed. NativeAOT publication, native audio and frontend
builds succeeded. Seven silent XAudio2 tests, 15 PTC tests, four settings tests
and five pacing tests pass: 61 checks, not 61 game runs.
No replacement package or Bomberman launch has been certified at this checkpoint.

Bomberman first native run: Application Loaded, PTC v7022, 6,823 profiled functions
saved at shutdown. Audio backpressure due to retained completed packets blocked
progress; this run does not count as menu/ingame success. MapSharedMemory first
returned InvalidMemoryRegion then Success at a different aligned address, two
milliseconds apart: this observed retry is not itself evidence of a fatal bug.

The saved Arashi Gaiden failure is shader hash
`6EAF2590C6297B1757A05C9FA49073A348A7E188400E2B24A83570E5FF6F1E3C`:
implicit Sample inside nested loops with packed-half counters. Safe derivative
hoisting requires proving the coordinate offset is uniform; no SampleLevel(0)
substitution or forced unroll has been introduced.

The user corrected the archive path to `E:/jogos para testar ryuboxz/Logs (3).zip`.
It has now been analyzed: 92 HLE logs, including 54 previously listed file names
and 38 additional names. Names identify archive cohorts, not independent crashes.
See `GLOBAL_LOT3_REPAIR_STATUS.md` for the current checkpoint; older checkpoints
above remain a history of what was and was not validated at each stage.

UWP API basis: https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getdiskfreespaceexw
