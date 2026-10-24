# Ryujinx Xbox Dev Mode port

## Current baseline

- Source: Ryubing/Ryujinx, commit `4c21a617573d19cb72a754e01925778116b85073`.
- Working branch: `xbox-uwp-port`.
- Core baseline: .NET 10 Release build passes with 0 warnings and 0 errors.
- External-drive cache: `E:\ports\ryujinx uwp\.cache`.

The existing executable is an Avalonia Desktop application. Its entry point
uses `UsePlatformDetect()` and `StartWithClassicDesktopLifetime()`, so it is not
an Xbox/UWP host. The port keeps that desktop application intact while adding
an independent host-facing layer.

## First implemented slice

`src/Ryujinx.Uwp` provides `UwpRuntimeBootstrap`. The future UWP app passes
`ApplicationData.Current.LocalFolder.Path` to `Initialize`. This initializes
the existing `AppDataManager` under the sandbox and creates the normal
`games`, `profiles`, `system`, and `Logs` layout without writing to the package
installation directory.

`AppDataManager` now treats `LaunchMode.Custom` as a writable application-data
root for logs. This is also the correct behavior for a UWP host, where the
installed executable directory is read-only.

## Build from the external drive

From `E:\ports\ryujinx uwp\src`:

```powershell
.\eng\Invoke-RyujinxBuild.ps1 -Action Restore
.\eng\Invoke-RyujinxBuild.ps1 -Action Build
```

The script redirects NuGet packages, HTTP cache, plugin cache, and temporary
files to the HD external. It is intentionally separate from the eventual
Xbox packaging script.

## Next milestones

1. Add the UWP/Xbox application project and manifest once the UWP workload is
   installed in Visual Studio.
2. Replace the Avalonia desktop window with a host surface that can present a
   D3D12-backed swap chain.
3. Integrate the existing Vulkan path through the Mesa/Dozen layer and make
   the backend selection explicit for Xbox.
4. Replace desktop SDL3 input/window/audio paths with sandbox-compatible
   adapters.
5. Produce and install an x64 MSIX on Xbox Dev Mode, then profile memory and
   GPU/CPU synchronization on hardware.
