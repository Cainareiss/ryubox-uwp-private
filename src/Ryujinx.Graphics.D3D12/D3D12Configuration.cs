using System;

namespace Ryujinx.Graphics.D3D12;

/// <summary>
/// Compatibility and feature switches for the Ryubox D3D12 backend.
/// Allows granular gating and validation of features per phase.
/// </summary>
public static class D3D12Configuration
{
    /// <summary>
    /// Phase 1: Typed upload footprints and subresource layout helpers using ID3D12Device::GetCopyableFootprints.
    /// </summary>
    public static bool TypedUploadFootprints { get; set; } = GetSwitch("Ryubox.D3D12.TypedUploadFootprints", true);

    /// <summary>
    /// Phase 2: Native Texture2DArray, Cubemap, and CubemapArray texture support.
    /// </summary>
    public static bool TextureArrays { get; set; } = GetSwitch("Ryubox.D3D12.TextureArrays", true);

    /// <summary>
    /// Phase 4: Typeless texture storage and format family view reinterpretation.
    /// </summary>
    public static bool TypelessViews { get; set; } = GetSwitch("Ryubox.D3D12.TypelessViews", true);

    /// <summary>
    /// Phase 5: Extended DXGI formats support (e.g. R32Uint, depth and integer formats).
    /// </summary>
    public static bool ExtendedFormats { get; set; } = GetSwitch("Ryubox.D3D12.ExtendedFormats", true);

    /// <summary>
    /// Phase 7: GPU hang watchdog and stall telemetry.
    /// </summary>
    public static bool GpuWatchdog { get; set; } = GetSwitch("Ryubox.D3D12.GpuWatchdog", true);

    private static bool GetSwitch(string name, bool defaultValue)
    {
        if (AppContext.TryGetSwitch(name, out bool isEnabled))
        {
            return isEnabled;
        }

        string envVar = name.Replace('.', '_').ToUpperInvariant();
        string envVal = Environment.GetEnvironmentVariable(envVar) ?? Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrEmpty(envVal))
        {
            if (bool.TryParse(envVal, out bool parsedBool))
            {
                return parsedBool;
            }
            if (envVal == "1") return true;
            if (envVal == "0") return false;
        }

        return defaultValue;
    }
}
