# MARVEL video investigation — 2026-09-06

## Confirmed defects and changes

- `D3D11Resources.ApplyRgba8Swizzle` forced alpha to 255 for
  `Zero/Zero/Zero/Red` views. The recorded MARVEL video fragment shader reads
  all three YUV textures through `.w`. Restore the exact component mapping.
- `D3D11Pipeline.SetViewports` fitted valid GAL viewports to the current
  attachment and host aspect. A 16384x16384 viewport was reduced to 480x384.
  `SupportBufferUpdater` still calculated inverse dimensions from 16384.
  Preserve GAL dimensions; leave presentation scaling to the presenter.
- Scissors were likewise fitted to whichever attachment happened to be bound
  when state was submitted, even though a subsequent target change may reuse
  the scissor. Preserve the GAL rectangle.
- Transform-disabled shader output converts pixel coordinates using a positive
  GAL viewport. D3D11 needs the opposite clip-space Y sign from Vulkan for this
  case. Apply that sign conversion only for Direct3D11, inside the existing
  transform-disabled translation path. Host shader codegen version is 7355.
- One diagnostic run crashed in `TwodClass.UnscaledFullCopy` reading an unmapped
  page (physical address `ulong.MaxValue`). Validate both complete ranges before
  copying; reject invalid commands with a rate-limited diagnostic. This prevents
  that access but does not establish why the guest mapping was missing.

`SetMatrixTransform`, frontend input and audio code were not changed.

## Tests

`dotnet run --project eng/D3D11MaterialRegression -c Release --no-restore`

Passed on a real D3D11 WARP device:

- Two-row video-plane upload/readback retains the original red values in alpha.
- Native viewport query retains both 16384x16384 and 240x192 on a 480x384 target.
  The first test failed before the viewport change (actual 480x384).
- Native scissor query retains 16384x16384.
- Existing constant-buffer remapping/copy/update and shader varying tests pass.

These are synthetic renderer tests, not proof that MARVEL displays correctly.

## Runtime validation

- `artifacts/marvel-video-alpha-geometry-20260906`: alpha correction only;
  hit the unmapped 2D-copy exception.
- `artifacts/marvel-video-viewport-20260906`: viewport/scissor correction,
  before transform-disabled Y correction; black intro remained.
- `artifacts/marvel-video-coordinate-fix-20260906`: full change set; validation
  in progress. Do not claim successful video or Xbox validation until captured.

The detailed shader log contains a normal video quad with positions -1/+1 and
UVs 0/1. The shader source also confirms use of SupportBuffer.ViewportInverse
in the transform-disabled composition shader.

Reference: https://learn.microsoft.com/en-us/windows/win32/api/d3d11/ns-d3d11-d3d11_viewport
documents native viewport limits; dimensions need not fit the render target.
