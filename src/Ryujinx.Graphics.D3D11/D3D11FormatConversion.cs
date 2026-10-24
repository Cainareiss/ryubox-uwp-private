using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.D3D11;

internal static class D3D11FormatConversion
{
    public static D3D11Format ToDxgi(Format format)
    {
        return format switch
        {
            Format.R8Unorm => D3D11Format.R8Unorm,
            Format.R8Snorm => D3D11Format.R8Snorm,
            Format.R8Uint => D3D11Format.R8Uint,
            Format.R8Sint => D3D11Format.R8Sint,
            Format.R16Float => D3D11Format.R16Float,
            Format.R16Unorm => D3D11Format.R16Unorm,
            Format.R16Snorm => D3D11Format.R16Snorm,
            Format.R16Uint => D3D11Format.R16Uint,
            Format.R16Sint => D3D11Format.R16Sint,
            Format.R32Float => D3D11Format.R32Float,
            Format.R32Uint => D3D11Format.R32Uint,
            Format.R32Sint => D3D11Format.R32Sint,
            Format.R8G8Unorm => D3D11Format.R8G8Unorm,
            Format.R8G8Snorm => D3D11Format.R8G8Snorm,
            Format.R8G8Uint => D3D11Format.R8G8Uint,
            Format.R8G8Sint => D3D11Format.R8G8Sint,
            Format.R16G16Float => D3D11Format.R16G16Float,
            Format.R16G16Unorm => D3D11Format.R16G16Unorm,
            Format.R16G16Snorm => D3D11Format.R16G16Snorm,
            Format.R16G16Uint => D3D11Format.R16G16Uint,
            Format.R16G16Sint => D3D11Format.R16G16Sint,
            Format.R32G32Float => D3D11Format.R32G32Float,
            Format.R32G32Uint => D3D11Format.R32G32Uint,
            Format.R32G32Sint => D3D11Format.R32G32Sint,
            Format.R32G32B32Float => D3D11Format.R32G32B32Float,
            Format.R32G32B32Uint => D3D11Format.R32G32B32Uint,
            Format.R32G32B32Sint => D3D11Format.R32G32B32Sint,
            // DXGI has no native R10G10B10A2_SNORM vertex format. Keep the packed
            // 32-bit layout and feed it through the compatible float input path so
            // games using packed normals can continue rendering. Exact signed
            // normalization can be materialized by the shader fallback later.
            Format.R10G10B10A2Unorm or Format.B10G10R10A2Unorm or Format.R10G10B10A2Snorm => D3D11Format.R10G10B10A2Unorm,
            Format.R10G10B10A2Uint => D3D11Format.R10G10B10A2Uint,
            Format.R11G11B10Float => D3D11Format.R11G11B10Float,
            Format.R9G9B9E5Float => D3D11Format.R9G9B9E5SharedExp,
            Format.R8G8B8A8Unorm => D3D11Format.R8G8B8A8Unorm,
            Format.R8G8B8A8Srgb => D3D11Format.R8G8B8A8UnormSrgb,
            Format.R4G4B4A4Unorm => (D3D11Format)115,
            Format.R5G5B5A1Unorm => (D3D11Format)86,
            Format.R5G6B5Unorm => (D3D11Format)85,
            Format.B5G6R5Unorm => (D3D11Format)85,
            Format.B5G5R5A1Unorm => (D3D11Format)86,
            Format.R8G8B8A8Snorm => D3D11Format.R8G8B8A8Snorm,
            Format.R8G8B8A8Uint => D3D11Format.R8G8B8A8Uint,
            Format.R8G8B8A8Sint => D3D11Format.R8G8B8A8Sint,
            // A8B8G8R8 is the guest's packed four-byte integer vertex format.
            // DXGI has no A8B8G8R8 vertex declaration, but the same 32-bit
            // storage is accepted by the input assembler as R8G8B8A8Uint.
            // The shader translator handles the guest component swizzle.
            Format.A8B8G8R8Uint => D3D11Format.R8G8B8A8Uint,
            Format.R16G16B16A16Float => D3D11Format.R16G16B16A16Float,
            Format.R16G16B16A16Unorm => D3D11Format.R16G16B16A16Unorm,
            Format.R16G16B16A16Snorm => D3D11Format.R16G16B16A16Snorm,
            Format.R16G16B16A16Uint => D3D11Format.R16G16B16A16Uint,
            Format.R16G16B16A16Sint => D3D11Format.R16G16B16A16Sint,
            Format.R32G32B32A32Float => D3D11Format.R32G32B32A32Float,
            Format.R32G32B32A32Uint => D3D11Format.R32G32B32A32Uint,
            Format.R32G32B32A32Sint => D3D11Format.R32G32B32A32Sint,
            Format.D16Unorm => D3D11Format.D16Unorm,
            Format.S8UintD24Unorm or Format.X8UintD24Unorm or Format.D24UnormS8Uint => D3D11Format.D24UnormS8Uint,
            Format.D32Float => D3D11Format.D32Float,
            Format.D32FloatS8Uint => D3D11Format.D32FloatS8X24Uint,
            Format.B8G8R8A8Unorm => D3D11Format.B8G8R8A8Unorm,
            Format.B8G8R8A8Srgb => D3D11Format.B8G8R8A8UnormSrgb,
            Format.Bc1RgbaUnorm => D3D11Format.Bc1Unorm,
            Format.Bc1RgbaSrgb => D3D11Format.Bc1UnormSrgb,
            Format.Bc2Unorm => D3D11Format.Bc2Unorm,
            Format.Bc2Srgb => D3D11Format.Bc2UnormSrgb,
            Format.Bc3Unorm => D3D11Format.Bc3Unorm,
            Format.Bc3Srgb => D3D11Format.Bc3UnormSrgb,
            Format.Bc4Unorm => D3D11Format.Bc4Unorm,
            Format.Bc4Snorm => D3D11Format.Bc4Snorm,
            Format.Bc5Unorm => D3D11Format.Bc5Unorm,
            Format.Bc5Snorm => D3D11Format.Bc5Snorm,
            Format.Bc6HSfloat => D3D11Format.Bc6HSfloat,
            Format.Bc6HUfloat => D3D11Format.Bc6HUfloat,
            Format.Bc7Unorm => D3D11Format.Bc7Unorm,
            Format.Bc7Srgb => D3D11Format.Bc7UnormSrgb,
            Format.R8Uscaled => D3D11Format.R8Unorm,
            Format.R8Sscaled => D3D11Format.R8Snorm,
            Format.R16Uscaled => D3D11Format.R16Unorm,
            Format.R16Sscaled => D3D11Format.R16Snorm,
            Format.R32Uscaled => D3D11Format.R32Uint,
            Format.R32Sscaled => D3D11Format.R32Sint,
            Format.R8G8Uscaled => D3D11Format.R8G8Unorm,
            Format.R8G8Sscaled => D3D11Format.R8G8Snorm,
            Format.R16G16Uscaled => D3D11Format.R16G16Unorm,
            Format.R16G16Sscaled => D3D11Format.R16G16Snorm,
            Format.R32G32Uscaled => D3D11Format.R32G32Uint,
            Format.R32G32Sscaled => D3D11Format.R32G32Sint,
            Format.R32G32B32Uscaled => D3D11Format.R32G32B32Uint,
            Format.R32G32B32Sscaled => D3D11Format.R32G32B32Sint,
            Format.R8G8B8A8Uscaled => D3D11Format.R8G8B8A8Unorm,
            Format.R8G8B8A8Sscaled => D3D11Format.R8G8B8A8Snorm,
            Format.R16G16B16A16Uscaled => D3D11Format.R16G16B16A16Unorm,
            Format.R16G16B16A16Sscaled => D3D11Format.R16G16B16A16Snorm,
            Format.R32G32B32A32Uscaled => D3D11Format.R32G32B32A32Uint,
            Format.R32G32B32A32Sscaled => D3D11Format.R32G32B32A32Sint,
            _ => throw new NotSupportedException($"D3D11 format mapping is not implemented for {format}.")
        };
    }

    public static D3D11Format ToTypeless(Format format)
    {
        return format switch
        {
            Format.D16Unorm => D3D11Format.R16Typeless,
            Format.S8UintD24Unorm or Format.X8UintD24Unorm or Format.D24UnormS8Uint => D3D11Format.R24G8Typeless,
            Format.D32Float => D3D11Format.R32Typeless,
            Format.D32FloatS8Uint => D3D11Format.R32G8X24Typeless,
            _ => throw new NotSupportedException($"D3D11 typeless format mapping is not implemented for {format}.")
        };
    }

    public static D3D11Format ToDepthSrv(Format format)
    {
        return format switch
        {
            Format.D16Unorm => D3D11Format.R16Unorm,
            Format.S8UintD24Unorm or Format.X8UintD24Unorm or Format.D24UnormS8Uint => D3D11Format.R24UnormX8Typeless,
            Format.D32Float => D3D11Format.R32Float,
            Format.D32FloatS8Uint => D3D11Format.R32FloatX8X24Typeless,
            _ => throw new NotSupportedException($"D3D11 depth SRV format mapping is not implemented for {format}.")
        };
    }


    public static D3D11PrimitiveTopology ToPrimitiveTopology(PrimitiveTopology topology, int patchVertices = 1)
    {
        if (topology == PrimitiveTopology.Patches)
        {
            return (D3D11PrimitiveTopology)(32 + Math.Clamp(patchVertices, 1, 32));
        }

        return topology switch
        {
            PrimitiveTopology.Points => D3D11PrimitiveTopology.PointList,
            PrimitiveTopology.Lines => D3D11PrimitiveTopology.LineList,
            PrimitiveTopology.LineStrip => D3D11PrimitiveTopology.LineStrip,
            PrimitiveTopology.Triangles => D3D11PrimitiveTopology.TriangleList,
            PrimitiveTopology.TriangleStrip => D3D11PrimitiveTopology.TriangleStrip,
            // D3D11 has no fixed-function quad or triangle-fan primitive.
            // Mapping here prevents the GPU loop from aborting on titles that
            // use a four-vertex quad or triangle fan for menus, sprites, or geometry.
            PrimitiveTopology.Quads or PrimitiveTopology.QuadStrip => D3D11PrimitiveTopology.TriangleStrip,
            // Triangle fans are expanded to an indexed triangle list by
            // D3D11Pipeline. Keep the native state consistent while the
            // temporary index buffer is bound and when guest state is restored.
            PrimitiveTopology.TriangleFan => D3D11PrimitiveTopology.TriangleList,
            PrimitiveTopology.LinesAdjacency => D3D11PrimitiveTopology.LineListAdj,
            PrimitiveTopology.LineStripAdjacency => D3D11PrimitiveTopology.LineStripAdj,
            PrimitiveTopology.TrianglesAdjacency => D3D11PrimitiveTopology.TriangleListAdj,
            PrimitiveTopology.TriangleStripAdjacency => D3D11PrimitiveTopology.TriangleStripAdj,
            _ => throw new NotSupportedException($"D3D11 topology mapping is not implemented for {topology}.")
        };
    }

    public static D3D11TextureAddressMode ToAddressMode(AddressMode mode)
    {
        return mode switch
        {
            AddressMode.Repeat => D3D11TextureAddressMode.Wrap,
            AddressMode.MirroredRepeat => D3D11TextureAddressMode.Mirror,
            AddressMode.ClampToEdge or AddressMode.Clamp => D3D11TextureAddressMode.Clamp,
            AddressMode.ClampToBorder or AddressMode.MirrorClampToBorder => D3D11TextureAddressMode.Border,
            AddressMode.MirrorClampToEdge or AddressMode.MirrorClamp => D3D11TextureAddressMode.MirrorOnce,
            _ => D3D11TextureAddressMode.Clamp
        };
    }

    public static D3D11ComparisonFunc ToComparisonFunc(CompareOp op)
    {
        int value = (int)op;

        if (value >= (int)CompareOp.NeverGl && value <= (int)CompareOp.AlwaysGl)
        {
            value -= (int)CompareOp.NeverGl - (int)CompareOp.Never;
        }

        return (D3D11ComparisonFunc)value;
    }

    public static D3D11Filter ToFilter(MinFilter minFilter, MagFilter magFilter, bool comparison, float maxAnisotropy)
    {
        if (maxAnisotropy > 1f)
        {
            return comparison ? (D3D11Filter)0xD5 : D3D11Filter.Anisotropic;
        }

        bool minLinear = minFilter is MinFilter.Linear or MinFilter.LinearMipmapNearest or MinFilter.LinearMipmapLinear;
        bool magLinear = magFilter == MagFilter.Linear;
        bool mipLinear = minFilter is MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear;

        D3D11Filter filter = (minLinear, magLinear, mipLinear) switch
        {
            (false, false, false) => D3D11Filter.MinMagMipPoint,
            (false, false, true) => D3D11Filter.MinMagPointMipLinear,
            (false, true, false) => D3D11Filter.MinPointMagLinearMipPoint,
            (false, true, true) => D3D11Filter.MinPointMagMipLinear,
            (true, false, false) => D3D11Filter.MinLinearMagMipPoint,
            (true, false, true) => D3D11Filter.MinLinearMagPointMipLinear,
            (true, true, false) => D3D11Filter.MinMagLinearMipPoint,
            _ => D3D11Filter.MinMagMipLinear,
        };

        return comparison ? (D3D11Filter)((uint)filter | 0x80u) : filter;
    }
}
