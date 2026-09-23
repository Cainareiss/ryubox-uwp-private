using Ryujinx.Graphics.GAL;
using System;

namespace Ryujinx.Graphics.D3D11;

internal static class D3D11StateConversion
{
    public static D3D11Blend ToBlend(BlendFactor factor)
    {
        return factor switch
        {
            BlendFactor.Zero or BlendFactor.ZeroGl => D3D11Blend.Zero,
            BlendFactor.One or BlendFactor.OneGl => D3D11Blend.One,
            BlendFactor.SrcColor or BlendFactor.SrcColorGl => D3D11Blend.SrcColor,
            BlendFactor.OneMinusSrcColor or BlendFactor.OneMinusSrcColorGl => D3D11Blend.InvSrcColor,
            BlendFactor.SrcAlpha or BlendFactor.SrcAlphaGl => D3D11Blend.SrcAlpha,
            BlendFactor.OneMinusSrcAlpha or BlendFactor.OneMinusSrcAlphaGl => D3D11Blend.InvSrcAlpha,
            BlendFactor.DstAlpha or BlendFactor.DstAlphaGl => D3D11Blend.DestAlpha,
            BlendFactor.OneMinusDstAlpha or BlendFactor.OneMinusDstAlphaGl => D3D11Blend.InvDestAlpha,
            BlendFactor.DstColor or BlendFactor.DstColorGl => D3D11Blend.DestColor,
            BlendFactor.OneMinusDstColor or BlendFactor.OneMinusDstColorGl => D3D11Blend.InvDestColor,
            BlendFactor.SrcAlphaSaturate or BlendFactor.SrcAlphaSaturateGl => D3D11Blend.SrcAlphaSat,
            BlendFactor.Src1Color or BlendFactor.Src1ColorGl => D3D11Blend.Src1Color,
            BlendFactor.OneMinusSrc1Color or BlendFactor.OneMinusSrc1ColorGl => D3D11Blend.InvSrc1Color,
            BlendFactor.Src1Alpha or BlendFactor.Src1AlphaGl => D3D11Blend.Src1Alpha,
            BlendFactor.OneMinusSrc1Alpha or BlendFactor.OneMinusSrc1AlphaGl => D3D11Blend.InvSrc1Alpha,
            BlendFactor.ConstantColor or BlendFactor.ConstantAlpha => D3D11Blend.BlendFactor,
            BlendFactor.OneMinusConstantColor or BlendFactor.OneMinusConstantAlpha => D3D11Blend.InvBlendFactor,
            _ => throw new NotSupportedException($"D3D11 blend factor is not supported: {factor}"),
        };
    }

    public static D3D11BlendOp ToBlendOp(BlendOp op)
    {
        return op switch
        {
            BlendOp.Add or BlendOp.AddGl => D3D11BlendOp.Add,
            BlendOp.Subtract or BlendOp.SubtractGl => D3D11BlendOp.Subtract,
            BlendOp.ReverseSubtract or BlendOp.ReverseSubtractGl => D3D11BlendOp.RevSubtract,
            BlendOp.Minimum or BlendOp.MinimumGl => D3D11BlendOp.Min,
            BlendOp.Maximum or BlendOp.MaximumGl => D3D11BlendOp.Max,
            _ => throw new NotSupportedException($"D3D11 blend operation is not supported: {op}"),
        };
    }

    public static D3D11StencilOp ToStencilOp(StencilOp op)
    {
        return op switch
        {
            StencilOp.Keep or StencilOp.KeepGl => D3D11StencilOp.Keep,
            StencilOp.Zero or StencilOp.ZeroGl => D3D11StencilOp.Zero,
            StencilOp.Replace or StencilOp.ReplaceGl => D3D11StencilOp.Replace,
            StencilOp.IncrementAndClamp or StencilOp.IncrementAndClampGl => D3D11StencilOp.IncrSat,
            StencilOp.DecrementAndClamp or StencilOp.DecrementAndClampGl => D3D11StencilOp.DecrSat,
            StencilOp.Invert or StencilOp.InvertGl => D3D11StencilOp.Invert,
            StencilOp.IncrementAndWrap or StencilOp.IncrementAndWrapGl => D3D11StencilOp.Incr,
            StencilOp.DecrementAndWrap or StencilOp.DecrementAndWrapGl => D3D11StencilOp.Decr,
            _ => throw new NotSupportedException($"D3D11 stencil operation is not supported: {op}"),
        };
    }

    public static D3D11DepthStencilOpDesc ToStencilOpDesc(CompareOp compare, StencilOp fail, StencilOp depthFail, StencilOp pass)
    {
        return new D3D11DepthStencilOpDesc
        {
            StencilFailOp = ToStencilOp(fail),
            StencilDepthFailOp = ToStencilOp(depthFail),
            StencilPassOp = ToStencilOp(pass),
            StencilFunc = D3D11FormatConversion.ToComparisonFunc(compare),
        };
    }

    public static D3D11BlendDesc CreateBlendDesc(ReadOnlySpan<BlendDescriptor> states, ReadOnlySpan<uint> masks)
    {
        D3D11BlendDesc desc = new()
        {
            IndependentBlendEnable = 1,
            RenderTarget0 = DefaultBlend(masks.Length > 0 ? masks[0] : 0xf),
            RenderTarget1 = DefaultBlend(masks.Length > 1 ? masks[1] : 0xf),
            RenderTarget2 = DefaultBlend(masks.Length > 2 ? masks[2] : 0xf),
            RenderTarget3 = DefaultBlend(masks.Length > 3 ? masks[3] : 0xf),
            RenderTarget4 = DefaultBlend(masks.Length > 4 ? masks[4] : 0xf),
            RenderTarget5 = DefaultBlend(masks.Length > 5 ? masks[5] : 0xf),
            RenderTarget6 = DefaultBlend(masks.Length > 6 ? masks[6] : 0xf),
            RenderTarget7 = DefaultBlend(masks.Length > 7 ? masks[7] : 0xf),
        };

        for (int index = 0; index < Math.Min(8, states.Length); index++)
        {
            BlendDescriptor state = states[index];
            D3D11RenderTargetBlendDesc target = new()
            {
                BlendEnable = state.Enable ? 1 : 0,
                SrcBlend = ToBlend(state.ColorSrcFactor),
                DestBlend = ToBlend(state.ColorDstFactor),
                BlendOp = ToBlendOp(state.ColorOp),
                SrcBlendAlpha = ToAlphaBlend(state.AlphaSrcFactor),
                DestAlpha = ToAlphaBlend(state.AlphaDstFactor),
                BlendOpAlpha = ToBlendOp(state.AlphaOp),
                ColorWriteMask = (byte)(index < masks.Length ? masks[index] & 0xf : 0xf),
            };

            switch (index)
            {
                case 0: desc.RenderTarget0 = target; break;
                case 1: desc.RenderTarget1 = target; break;
                case 2: desc.RenderTarget2 = target; break;
                case 3: desc.RenderTarget3 = target; break;
                case 4: desc.RenderTarget4 = target; break;
                case 5: desc.RenderTarget5 = target; break;
                case 6: desc.RenderTarget6 = target; break;
                case 7: desc.RenderTarget7 = target; break;
            }
        }

        return desc;
    }

    private static D3D11Blend ToAlphaBlend(BlendFactor factor)
    {
        // D3D11 rejects color factors in the alpha slots. GAL can express
        // these factors because Vulkan/OpenGL accept the corresponding
        // channel-relative form; map them to the alpha equivalent before the
        // descriptor reaches the Xbox D3D11 validator.
        return factor switch
        {
            BlendFactor.SrcColor or BlendFactor.SrcColorGl => D3D11Blend.SrcAlpha,
            BlendFactor.OneMinusSrcColor or BlendFactor.OneMinusSrcColorGl => D3D11Blend.InvSrcAlpha,
            BlendFactor.DstColor or BlendFactor.DstColorGl => D3D11Blend.DestAlpha,
            BlendFactor.OneMinusDstColor or BlendFactor.OneMinusDstColorGl => D3D11Blend.InvDestAlpha,
            BlendFactor.Src1Color or BlendFactor.Src1ColorGl => D3D11Blend.Src1Alpha,
            BlendFactor.OneMinusSrc1Color or BlendFactor.OneMinusSrc1ColorGl => D3D11Blend.InvSrc1Alpha,
            _ => ToBlend(factor),
        };
    }

    private static D3D11RenderTargetBlendDesc DefaultBlend(uint mask)
    {
        return new D3D11RenderTargetBlendDesc
        {
            BlendEnable = 0,
            SrcBlend = D3D11Blend.One,
            DestBlend = D3D11Blend.Zero,
            BlendOp = D3D11BlendOp.Add,
            SrcBlendAlpha = D3D11Blend.One,
            DestAlpha = D3D11Blend.Zero,
            BlendOpAlpha = D3D11BlendOp.Add,
            ColorWriteMask = (byte)(mask & 0xf),
        };
    }
}
