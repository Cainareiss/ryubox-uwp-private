# MARVEL Cosmic Invasion and in-game FPS overlay

Game: user's `E:/jogos para testar ryuboxz/MARVEL Cosmic Invasion.nsp`,
v1.0.0 / 010059D020C26000. Testing on i3-7020U / Intel HD 620 / 8 GiB.
This desktop validation cannot certify Series S performance.

## Reproduced launch failure

First run (2026-09-06 02:39 UTC): core Start succeeded, firmware reused, but no
guest frame was presented. DMA threw InvalidMemoryRegionException reading an
unmapped GPU source. Cleanup reduced working set from 1065 MiB to 222 MiB.

Second run (04:20 UTC) added exact DMA register logging. It failed again:

```text
arg=0x00000586 src=0x0 dst=0x100240000 size=1 lines=0
remap=0x00004444 constA=0x00000000 constB=0x00000000
srcPa=0xFFFFFFFFFFFFFFFF dstPa=0x159E2F0000
```

This is a constant-byte fill, not a source read. The previous implementation
only recognized a restrictive four-byte fill and fell into CopyGobBlockLinearToLinear.
Added bounded constant patterns for one-, two-, three- and four-byte components,
including ConstB and multiple destination components. NoWrite/source-dependent
remaps still follow the original path; real memory errors are not swallowed.
The temporary buffer is at most 4096 bytes; fill length cannot allocate a huge
temporary array. Separate correction honors semaphore-only launches with no data
transfer, but that was NOT the cause of this particular MARVEL command.

Register definitions checked against NVIDIA's primary header:
https://github.com/NVIDIA/open-gpu-doc/blob/master/classes/dma-copy/clb0b5.h

14 DMA policy tests pass, including the exact MARVEL remap value. The third
end-to-end run passed the former DMA crash point: the same `0x00000586`
byte-fill commands executed without an exception. That run was stopped by the
validation monitor, not by a process crash.

The following run with automatic A/L+R pulses also started successfully and
loaded the title layers, but remained in a presentation stall:
`surfaceLoops` advanced while `buffersAcquired=0`, `framesEnqueued=0`,
`nativePresent=0/0`, and `presented=0`. No fatal exception was logged. This is
now a separate MARVEL/title presentation issue, not the original DMA invalid-
source crash.

## Correct FPS meaning

The previous perf-1s FPS counted processed FIFO batches, producing nonzero FPS
even while presented=0. It now separates guest presentation FPS from FIFO rate.
frameMs is reciprocal average rate, NOT a measured percentile or jitter statistic.

The previous visible ImGui FPS widget only counted menu frames. It is replaced
with an in-game-only native D3D11 overlay. It counts successfully presented new
uploads/copies, excluding re-presentation without a fresh guest frame. Default on,
settings toggle persisted, missing setting in older snapshots defaults to on.

Rendering uses a separate deferred D3D11 context and ExecuteCommandList with full
state restoration. It does not call frontend BuildFrame, input processing, sound
or navigation. Vertex/index buffers are reused when capacity is sufficient.
The overlay is integrated in CPU upload and native GPU presentation paths.

Tests:

- `eng/Test-RyuboxGameFpsOverlay.ps1`: real native rendering and GPU readback of a
  synthetic background, on/off/on, verifies white text and panel pixels, zero
  changes outside the overlay rectangle, and no pending frontend action. Passed.
  This fixture is NOT a screenshot of a running game.
- `eng/PtcSettingsRegression`: eight checks including persisted FPS on/off,
  migration of old settings, and native exports independent of the PTC mailbox.

When a title has not submitted a frame, the in-game counter correctly remains
at `0 FPS`; it does not fabricate a 60 FPS value. No 60 FPS, successful MARVEL
gameplay or Xbox validation is claimed by these tests.

The SurfaceFlinger diagnostic run identified and fixed one title compatibility
issue: MARVEL calls `OpenLayer(1)` twice. The second call now returns success
idempotently. A follow-up run confirmed both calls return `Success`, but the
title still produced no `QueueBuffer`; it reaches SDL/Vulkan initialization and
then stalls before submitting the first buffer. The remaining black screen is
therefore below the layer-open path and still needs a title GPU-submit
investigation.
