using System;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

public sealed unsafe class D3D12Sampler : ISampler
{
    private readonly D3D12SamplerDescriptor _descriptor;
    private bool _disposed;
    internal D3D12SamplerDescriptor Descriptor { get { ObjectDisposedException.ThrowIf(_disposed, this); return _descriptor; } }
    public D3D12Sampler(SamplerCreateInfo info)
    {
        if (info.CompareMode is not (CompareMode.None or CompareMode.CompareRToTexture))
            throw new ArgumentOutOfRangeException(nameof(info), "Invalid sampler comparison mode.");
        if (!float.IsFinite(info.MipLodBias) || info.MipLodBias < -16 || info.MipLodBias > 15.99f ||
            !float.IsFinite(info.MaxAnisotropy) || info.MaxAnisotropy < 1 ||
            !float.IsFinite(info.MinLod) || !float.IsFinite(info.MaxLod) || info.MinLod > info.MaxLod)
            throw new ArgumentException("Invalid sampler bias, anisotropy or LOD range.");
        uint min = info.MinFilter switch { MinFilter.Nearest or MinFilter.NearestMipmapNearest or MinFilter.NearestMipmapLinear => 0,
            MinFilter.Linear or MinFilter.LinearMipmapNearest or MinFilter.LinearMipmapLinear => 1, _ => throw new ArgumentOutOfRangeException(nameof(info)) };
        uint mag = info.MagFilter switch { MagFilter.Nearest => 0, MagFilter.Linear => 1, _ => throw new ArgumentOutOfRangeException(nameof(info)) };
        uint mip = info.MinFilter is MinFilter.NearestMipmapLinear or MinFilter.LinearMipmapLinear ? 1u : 0;
        bool compare = info.CompareMode == CompareMode.CompareRToTexture;
        var d = new D3D12SamplerDescriptor { Filter = (min << 4) | (mag << 2) | mip | (compare ? 0x80u : 0),
            AddressU = Address(info.AddressU), AddressV = Address(info.AddressV), AddressW = Address(info.AddressP),
            Comparison = Comparison(info.CompareOp), MaxAnisotropy = (uint)Math.Clamp(info.MaxAnisotropy, 1, 16),
            MinLod = info.MinLod, MaxLod = info.MaxLod, MipLodBias = info.MipLodBias };
        if (info.MaxAnisotropy > 1) d.Filter = 0x55u | (compare ? 0x80u : 0);
        if (info.MinFilter is MinFilter.Nearest or MinFilter.Linear) d.MinLod = d.MaxLod = 0;
        d.BorderColor[0] = info.BorderColor.Red; d.BorderColor[1] = info.BorderColor.Green;
        d.BorderColor[2] = info.BorderColor.Blue; d.BorderColor[3] = info.BorderColor.Alpha;
        if (!float.IsFinite(d.MinLod) || !float.IsFinite(d.MaxLod) || d.MinLod > d.MaxLod) throw new ArgumentException("Invalid sampler LOD range.");
        _descriptor = d;
    }
    private static uint Address(AddressMode mode) => mode switch
    {
        AddressMode.Repeat => 1,
        AddressMode.MirroredRepeat => 2,
        AddressMode.ClampToEdge or AddressMode.Clamp => 3,
        AddressMode.ClampToBorder or AddressMode.MirrorClampToBorder => 4,
        AddressMode.MirrorClampToEdge or AddressMode.MirrorClamp => 5,
        _ => 3,
    };
    internal static uint Comparison(CompareOp op) => (int)op switch
    {
        >= 1 and <= 8 => (uint)op,
        >= 0x200 and <= 0x207 => (uint)((int)op - 0x200 + 1),
        _ => throw new ArgumentOutOfRangeException(nameof(op)),
    };
    public void Dispose() => _disposed = true;
}

public sealed class D3D12TextureArray : ITextureArray
{
    internal ITexture[] Textures { get; private set; }
    internal ISampler[] Samplers { get; private set; }
    public D3D12TextureArray(int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        Textures = new ITexture[size]; Samplers = new ISampler[size];
    }
    public void SetTextures(int index, ITexture[] textures)
    {
        ObjectDisposedException.ThrowIf(Textures == null, this); ArgumentNullException.ThrowIfNull(textures);
        if ((uint)index > (uint)Textures.Length || (uint)textures.Length > (uint)(Textures.Length - index))
            throw new ArgumentOutOfRangeException(nameof(index), "Texture span exceeds array bounds.");
        D3D12TextureBindings.Replace(Textures, index, textures);
    }
    public void SetSamplers(int index, ISampler[] samplers)
    {
        ObjectDisposedException.ThrowIf(Samplers == null, this); ArgumentNullException.ThrowIfNull(samplers);
        if ((uint)index > (uint)Samplers.Length || (uint)samplers.Length > (uint)(Samplers.Length - index))
            throw new ArgumentOutOfRangeException(nameof(index), "Sampler span exceeds array bounds.");
        foreach (var sampler in samplers) if (sampler != null && sampler is not D3D12Sampler) throw new ArgumentException("Foreign sampler.");
        samplers.AsSpan().CopyTo(Samplers.AsSpan(index, samplers.Length));
    }
    public void Dispose()
    {
        if (Textures != null) foreach (D3D12GalTexture texture in Textures) texture?.ReleaseBinding();
        Textures = null; Samplers = null;
    }
}

public sealed class D3D12ImageArray : IImageArray
{
    internal ITexture[] Images { get; private set; }
    public D3D12ImageArray(int size) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size); Images = new ITexture[size]; }
    public void SetImages(int index, ITexture[] images)
    {
        ObjectDisposedException.ThrowIf(Images == null, this); ArgumentNullException.ThrowIfNull(images);
        if ((uint)index > (uint)Images.Length || (uint)images.Length > (uint)(Images.Length - index))
            throw new ArgumentOutOfRangeException(nameof(index), $"Image span exceeds array bounds: index={index}, images.Length={images.Length}, Images.Length={Images.Length}");
        D3D12TextureBindings.Replace(Images, index, images);
    }
    public void Dispose()
    {
        if (Images != null) foreach (D3D12GalTexture image in Images) image?.ReleaseBinding();
        Images = null;
    }
}

internal static class D3D12TextureBindings
{
    internal static void Replace(ITexture[] destination, int index, ITexture[] input)
    {
        // Take all references before changing any slot, with rollback if one
        // element was released. This also handles overlapping caller arrays.
        ITexture[] next = (ITexture[])input.Clone();
        int retained = 0;
        try
        {
            foreach (var texture in next)
            {
                if (texture != null && texture is not D3D12GalTexture) throw new ArgumentException("Foreign texture.");
                ((D3D12GalTexture)texture)?.RetainBinding();
                ++retained;
            }
        }
        catch
        {
            for (int i = 0; i < retained; ++i) ((D3D12GalTexture)next[i])?.ReleaseBinding();
            throw;
        }
        for (int i = 0; i < next.Length; ++i)
        {
            var previous = (D3D12GalTexture)destination[index + i];
            destination[index + i] = next[i];
            previous?.ReleaseBinding();
        }
    }
}
