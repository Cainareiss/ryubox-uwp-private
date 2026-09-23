using System;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

// Only color formats with a float/normalized sampling contract are enabled here.
// Depth resources use a separate typeless-resource/typed-view path. Integer,
// compressed and stencil-only views still need their own operation paths.
internal readonly record struct D3D12TextureFormat(uint Dxgi, int Bytes)
{
    // D16_UNORM (55), D32_FLOAT (40), D24_UNORM_S8_UINT (45) and D32_FLOAT_S8X24_UINT (20).
    internal bool IsDepth => Dxgi is 55 or 40 or 45 or 20;
    internal static D3D12TextureFormat Get(Format format) => format switch
    {
        Format.R8Unorm => new(61, 1),
        Format.R8Snorm => new(63, 1),
        Format.R8Uint => new(62, 1),
        Format.R8Sint => new(64, 1),

        Format.R8G8Unorm => new(49, 2),
        Format.R8G8Snorm => new(51, 2),
        Format.R8G8Uint => new(50, 2),
        Format.R8G8Sint => new(52, 2),

        Format.R8G8B8A8Unorm => new(28, 4),
        Format.R8G8B8A8Snorm => new(31, 4),
        Format.R8G8B8A8Uint => new(30, 4),
        Format.R8G8B8A8Sint => new(32, 4),
        // sRGB uses the same four-byte storage as UNORM, with the hardware
        // performing the transfer function at SRV/RTV sampling and writes.
        Format.R8G8B8A8Srgb => new(29, 4),

        Format.B8G8R8A8Unorm => new(87, 4),
        Format.B8G8R8A8Srgb => new(91, 4),

        Format.R16Unorm => new(56, 2),
        Format.R16Snorm => new(58, 2),
        Format.R16Uint => new(57, 2),
        Format.R16Sint => new(59, 2),
        Format.R16Float => new(54, 2),

        Format.R16G16Unorm => new(35, 4),
        Format.R16G16Snorm => new(37, 4),
        Format.R16G16Uint => new(36, 4),
        Format.R16G16Sint => new(38, 4),
        Format.R16G16Float => new(34, 4),

        Format.R16G16B16A16Unorm => new(11, 8),
        Format.R16G16B16A16Snorm => new(13, 8),
        Format.R16G16B16A16Uint => new(12, 8),
        Format.R16G16B16A16Sint => new(14, 8),
        Format.R16G16B16A16Float => new(10, 8),

        Format.R32Float => new(41, 4),
        Format.R32Uint => new(42, 4),
        Format.R32Sint => new(43, 4),

        Format.R32G32Float => new(16, 8),
        Format.R32G32Uint => new(17, 8),
        Format.R32G32Sint => new(18, 8),

        Format.R32G32B32A32Float => new(2, 16),
        Format.R32G32B32A32Uint => new(3, 16),
        Format.R32G32B32A32Sint => new(4, 16),

        // Packed 32-bit color formats used by several 3D titles. Both have
        // native D3D12 SRV/RTV views and one packed texel per four bytes.
        Format.R10G10B10A2Unorm => new(24, 4),
        Format.R10G10B10A2Uint => new(25, 4),
        Format.R11G11B10Float => new(26, 4),
        Format.R9G9B9E5Float => new(67, 4),

        Format.D16Unorm => new(55, 2),
        Format.D32Float => new(40, 4),
        // Keep the guest's combined depth/stencil format. Native allocation
        // turns it into R24G8_TYPELESS and creates D24_UNORM_S8_UINT DSVs,
        // with R24_UNORM_X8_TYPELESS for depth sampling.
        Format.D24UnormS8Uint => new(45, 4),
        // Both guest layouts use the same native D24/S8 storage. The GAL
        // stencil-only/depth-only view distinction is handled by the depth
        // mode validation; the D3D12 resource remains typeless R24G8.
        Format.S8UintD24Unorm => new(45, 4),
        Format.X8UintD24Unorm => new(45, 4),
        Format.D32FloatS8Uint => new(20, 8),
        _ => throw new NotSupportedException($"D3D12 texture format {format} has no validated format/view path yet."),
    };

    internal enum FormatFamily
    {
        Unknown,
        R8,
        R8G8,
        R8G8B8A8,
        B8G8R8A8,
        R16,
        R16G16,
        R16G16B16A16,
        R32,
        R32G32,
        R32G32B32A32,
        R10G10B10A2,
        R11G11B10,
        R9G9B9E5,
        D16,
        D32,
        D24S8,
        D32S8,
    }

    internal static FormatFamily GetFamily(Format format) => format switch
    {
        Format.R8Unorm or Format.R8Snorm or Format.R8Uint or Format.R8Sint => FormatFamily.R8,
        Format.R8G8Unorm or Format.R8G8Snorm or Format.R8G8Uint or Format.R8G8Sint => FormatFamily.R8G8,
        Format.R8G8B8A8Unorm or Format.R8G8B8A8Snorm or Format.R8G8B8A8Uint or Format.R8G8B8A8Sint or Format.R8G8B8A8Srgb => FormatFamily.R8G8B8A8,
        Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb => FormatFamily.B8G8R8A8,
        Format.R16Unorm or Format.R16Snorm or Format.R16Uint or Format.R16Sint or Format.R16Float or Format.D16Unorm => FormatFamily.R16,
        Format.R16G16Unorm or Format.R16G16Snorm or Format.R16G16Uint or Format.R16G16Sint or Format.R16G16Float => FormatFamily.R16G16,
        Format.R16G16B16A16Unorm or Format.R16G16B16A16Snorm or Format.R16G16B16A16Uint or Format.R16G16B16A16Sint or Format.R16G16B16A16Float => FormatFamily.R16G16B16A16,
        Format.R32Float or Format.R32Uint or Format.R32Sint or Format.D32Float => FormatFamily.R32,
        Format.R32G32Float or Format.R32G32Uint or Format.R32G32Sint => FormatFamily.R32G32,
        Format.R32G32B32A32Float or Format.R32G32B32A32Uint or Format.R32G32B32A32Sint => FormatFamily.R32G32B32A32,
        Format.R10G10B10A2Unorm or Format.R10G10B10A2Uint => FormatFamily.R10G10B10A2,
        Format.R11G11B10Float => FormatFamily.R11G11B10,
        Format.R9G9B9E5Float => FormatFamily.R9G9B9E5,
        Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm => FormatFamily.D24S8,
        Format.D32FloatS8Uint => FormatFamily.D32S8,
        _ => FormatFamily.Unknown,
    };

    internal static bool AreInSameFamily(Format a, Format b)
    {
        if (a == b) return true;
        var fa = GetFamily(a);
        var fb = GetFamily(b);
        return fa != FormatFamily.Unknown && fa == fb;
    }
}
