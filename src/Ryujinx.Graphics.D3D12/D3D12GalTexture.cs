using System;
using System.Runtime.InteropServices;
using Ryujinx.Common.Memory;
using Ryujinx.Graphics.GAL;

namespace Ryujinx.Graphics.D3D12;

public sealed class D3D12GalTexture : ITexture
{
    private sealed class Storage
    {
        public D3D12Texture Texture;
        public int References = 1;
    }
    private readonly D3D12Renderer _renderer;
    private readonly TextureCreateInfo _info;
    // D3D12_SHADER_COMPONENT_MAPPING_ALWAYS_SET_BIT_AVOIDING_ZEROMEM_MISTAKES.
    // Keep the ABI value here so GAL views can select a real SRV swizzle.
    private const uint ComponentMappingAlwaysSet = 1u << 12;
    private readonly uint _componentMapping;
    private int RowBytes => checked(Width * _info.BytesPerPixel);
    private Storage _storage;
    private D3D12Texture _native;
    private readonly bool _ownsNativeView;
    internal D3D12Texture Native { get { ObjectDisposedException.ThrowIf(_storage == null, this); return _native; } }
    internal uint ComponentMapping { get { _ = Native; return _componentMapping; } }
    internal TextureCreateInfo Info => _info;
    public int Width => _info.Width;
    public int Height => _info.Height;
    internal D3D12GalTexture(D3D12Renderer renderer, TextureCreateInfo info)
    {
        Validate(info); _renderer = renderer; _info = info; _componentMapping = EncodeComponentMapping(info);
        D3D12Texture texture = info.Target == Target.Texture3D
            ? renderer.Device.CreateTexture3D((uint)Width, (uint)Height, (uint)info.Depth, (uint)info.Levels,
                D3D12TextureFormat.Get(info.Format))
            : renderer.Device.CreateTexture((uint)Width, (uint)Height, (uint)info.GetLayers(), (uint)info.Levels,
                D3D12TextureFormat.Get(info.Format), (uint)info.Samples);
        _storage = new() { Texture = texture };
        _native = texture;
    }
    internal D3D12GalTexture(D3D12GalTexture source, TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        Validate(info);
        if (!CompatibleStorage(source._info, info))
            throw new NotSupportedException($"D3D12 texture views cannot reinterpret: src(target={source._info.Target}, fmt={source._info.Format}, samples={source._info.Samples}, bpp={source._info.BytesPerPixel}, bw={source._info.BlockWidth}, bh={source._info.BlockHeight}, dsm={source._info.DepthStencilMode}) vs dst(target={info.Target}, fmt={info.Format}, samples={info.Samples}, bpp={info.BytesPerPixel}, bw={info.BlockWidth}, bh={info.BlockHeight}, dsm={info.DepthStencilMode})");
        if (firstLevel < 0 || info.Levels <= 0 || firstLevel > source._info.Levels - info.Levels)
            throw new ArgumentOutOfRangeException(nameof(firstLevel));
        int sourceLayers = source._info.Target == Target.Texture3D ? Math.Max(1, source._info.Depth >> firstLevel) : source._info.GetLayers();
        int viewLayers = info.Target == Target.Texture3D ? info.Depth : info.GetLayers();
        if (firstLayer < 0 || viewLayers <= 0 || firstLayer > sourceLayers - viewLayers)
            throw new ArgumentOutOfRangeException(nameof(firstLayer));
        _renderer = source._renderer; _info = info; _componentMapping = EncodeComponentMapping(info); _storage = source._storage;
        ObjectDisposedException.ThrowIf(_storage == null, source);
        System.Threading.Interlocked.Increment(ref _storage.References);
        try
        {
            var viewFormat = D3D12TextureFormat.Get(info.Format);
            _native = _renderer.Device.CreateTextureView(source.Native, (uint)firstLevel, (uint)info.Levels,
                (uint)firstLayer, (uint)viewLayers, (uint)info.Width, (uint)info.Height, (uint)info.Depth, viewFormat);
            _ownsNativeView = true;
        }
        catch
        {
            System.Threading.Interlocked.Decrement(ref _storage.References);
            _storage = null;
            throw;
        }
    }
    private static bool IsCompatibleTarget(Target left, Target right)
    {
        if (left == right) return true;
        bool left2D = left is Target.Texture2D or Target.Texture2DArray or Target.Cubemap or Target.CubemapArray;
        bool right2D = right is Target.Texture2D or Target.Texture2DArray or Target.Cubemap or Target.CubemapArray;
        if (left2D && right2D) return true;
        bool leftMs = left is Target.Texture2DMultisample or Target.Texture2DMultisampleArray;
        bool rightMs = right is Target.Texture2DMultisample or Target.Texture2DMultisampleArray;
        if (leftMs && rightMs) return true;
        return false;
    }
    private static bool CompatibleStorage(TextureCreateInfo left, TextureCreateInfo right) =>
        left.Samples == right.Samples && left.BlockWidth == right.BlockWidth &&
        left.BlockHeight == right.BlockHeight && left.BytesPerPixel == right.BytesPerPixel &&
        (left.Format == right.Format || (D3D12Configuration.TypelessViews && D3D12TextureFormat.AreInSameFamily(left.Format, right.Format))) &&
        IsCompatibleTarget(left.Target, right.Target);
    private static uint Component(SwizzleComponent component) => component switch
    {
        SwizzleComponent.Red => 0,
        SwizzleComponent.Green => 1,
        SwizzleComponent.Blue => 2,
        SwizzleComponent.Alpha => 3,
        SwizzleComponent.Zero => 4,
        SwizzleComponent.One => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(component)),
    };
    private static uint EncodeComponentMapping(TextureCreateInfo info) => ComponentMappingAlwaysSet |
        Component(info.SwizzleR) | (Component(info.SwizzleG) << 3) |
        (Component(info.SwizzleB) << 6) | (Component(info.SwizzleA) << 9);
    private static void Validate(TextureCreateInfo info)
    {
        var format = D3D12TextureFormat.Get(info.Format);
        bool combinedDepthStencil = info.Format is Format.D24UnormS8Uint or Format.S8UintD24Unorm or Format.X8UintD24Unorm;
        if (format.IsDepth && info.DepthStencilMode != DepthStencilMode.Depth && !combinedDepthStencil)
            throw new NotSupportedException("D3D12 depth/stencil textures currently expose their depth view; stencil-only views are not implemented.");
        if (info.Width <= 0 || info.Height <= 0 || info.BytesPerPixel != format.Bytes)
            throw new ArgumentException("Texture dimensions or bytes per pixel do not match its format.");

        bool validTarget;
        if (D3D12Configuration.TextureArrays)
        {
            validTarget =
                (info.Target == Target.Texture2D && info.Samples == 1 && info.Depth == 1) ||
                (info.Target == Target.Texture2DMultisample && info.Samples > 1 && info.Depth == 1 && info.Levels == 1) ||
                (info.Target == Target.Texture2DArray && info.Samples == 1 && info.Depth >= 1) ||
                (info.Target == Target.Texture2DMultisampleArray && info.Samples > 1 && info.Depth >= 1 && info.Levels == 1) ||
                (info.Target == Target.Cubemap && info.Samples == 1) ||
                (info.Target == Target.CubemapArray && info.Samples == 1 && info.Depth >= 1) ||
                (info.Target == Target.Texture3D && info.Samples == 1 && info.Depth > 0 && !format.IsDepth);
        }
        else
        {
            validTarget =
                (info.Target == Target.Texture2D && info.Samples == 1 && info.Depth == 1 && info.Levels == 1) ||
                (info.Target == Target.Texture2DMultisample && info.Samples > 1 && info.Depth == 1 && info.Levels == 1) ||
                (info.Target == Target.Texture3D && info.Samples == 1 && info.Depth > 0 && !format.IsDepth);
        }

        if (!validTarget || info.Levels <= 0 || info.IsCompressed)
            throw new NotSupportedException(
                $"D3D12 GAL texture requires a validated uncompressed 2D/MSAA/3D path " +
                $"(target={info.Target}, size={info.Width}x{info.Height}x{info.Depth}, levels={info.Levels}, " +
                $"samples={info.Samples}, format={info.Format}, compressed={info.IsCompressed}, " +
                $"swizzle={info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}).");
    }
    internal void CheckOwner(D3D12Renderer renderer)
    {
        if (!ReferenceEquals(renderer, _renderer)) throw new ArgumentException("Texture belongs to another renderer.");
        _ = Native;
    }
    internal D3D12GalTexture CreateRenderTargetSliceView(int firstLayer, int layers)
    {
        _ = Native;
        if (_info.Target != Target.Texture3D &&
            _info.Target != Target.Texture2DArray &&
            _info.Target != Target.Texture2DMultisampleArray &&
            _info.Target != Target.Cubemap &&
            _info.Target != Target.CubemapArray)
        {
            if (firstLayer == 0 && layers == 1) return this;
            throw new NotSupportedException("Layered color clears currently require a 3D or array render target.");
        }
        int maxLayers = _info.Target == Target.Texture3D ? _info.Depth : _info.GetLayers();
        if (firstLayer < 0 || layers <= 0 || firstLayer > maxLayers - layers)
            throw new ArgumentOutOfRangeException(nameof(firstLayer));

        TextureCreateInfo viewInfo = new(
            _info.Width, _info.Height, layers, 1, _info.Samples,
            _info.BlockWidth, _info.BlockHeight, _info.BytesPerPixel,
            _info.Format, _info.DepthStencilMode, _info.Target,
            _info.SwizzleR, _info.SwizzleG, _info.SwizzleB, _info.SwizzleA);
        return new D3D12GalTexture(this, viewInfo, firstLayer, 0);
    }
    private void Subresource(int layer, int level)
    {
        if ((uint)level >= (uint)_info.Levels) throw new ArgumentOutOfRangeException(nameof(level));
        int slices = _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : _info.GetLayers();
        if ((uint)layer >= (uint)slices) throw new ArgumentOutOfRangeException(nameof(layer));
    }
    public void CopyTo(ITexture destination, int firstLayer, int firstLevel) => CopyTo(destination, 0, firstLayer, 0, firstLevel);
    public void CopyTo(ITexture destination, int srcLayer, int dstLayer, int srcLevel, int dstLevel)
    {
        Subresource(srcLayer, srcLevel);
        var target = _renderer.Texture(destination);
        if (D3D12Pipeline.ProbeTargets)
        {
            try
            {
                string line = $"[CopyTo Subresource] src={Width}x{Height} fmt={_info.Format} id={Native?.Id} (L{srcLayer},M{srcLevel}) -> dst={target.Width}x{target.Height} fmt={target._info.Format} id={target.Native?.Id} (L{dstLayer},M{dstLevel})\n";
                System.IO.File.AppendAllText(@"C:\Users\Cainã\AppData\Local\Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\Logs\draw_targets.txt", line);
            }
            catch {}
        }
        target.Subresource(dstLayer, dstLevel);
        int copyWidth = Math.Min(Math.Max(1, Width >> srcLevel), Math.Max(1, target.Width >> dstLevel));
        int copyHeight = Math.Min(Math.Max(1, Height >> srcLevel), Math.Max(1, target.Height >> dstLevel));
        int srcW = Math.Max(1, Width >> srcLevel);
        int srcH = Math.Max(1, Height >> srcLevel);
        int dstW = Math.Max(1, target.Width >> dstLevel);
        int dstH = Math.Max(1, target.Height >> dstLevel);
        if (_info.Format != target._info.Format || (_native.Format.IsDepth && (copyWidth != srcW || copyHeight != srcH || copyWidth != dstW || copyHeight != dstH)))
        {
            // The guest can request a copy dependency between differently
            // typed views or partial depth regions. D3D12 CopyTextureRegion correctly rejects that
            // operation; use the existing shader conversion path over the
            // common extent instead of terminating the emulation thread.
            // This overload is a subresource copy, not a scaled blit.
            if (_info.Target == Target.Texture3D || target._info.Target == Target.Texture3D)
                throw new NotSupportedException("D3D12 copies between differently formatted 3D textures require a typed conversion shader.");
            CopyTo(target, new(0, 0, copyWidth, copyHeight), new(0, 0, copyWidth, copyHeight), false);
            return;
        }
        _renderer.EnsureCommands();
        _renderer.Device.CopyTexture(Native, target.Native, (uint)srcLevel, (uint)dstLevel,
            0, 0, (uint)srcLayer,
            0, 0, (uint)dstLayer,
            (uint)copyWidth, (uint)copyHeight, 1);
    }
    public void CopyTo(ITexture destination, Extents2D source, Extents2D target, bool linearFilter)
    {
        var texture = _renderer.Texture(destination);
        if (D3D12Pipeline.ProbeTargets)
        {
            try
            {
                string line = $"[CopyTo Extents] src={Width}x{Height} fmt={_info.Format} id={Native?.Id} ({source.X1},{source.Y1}-{source.X2},{source.Y2}) -> dst={texture.Width}x{texture.Height} fmt={texture._info.Format} id={texture.Native?.Id} ({target.X1},{target.Y1}-{target.X2},{target.Y2})\n";
                System.IO.File.AppendAllText(@"C:\Users\Cainã\AppData\Local\Packages\Ryujinx.Xbox.UWP_z7hww2mmeny5j\LocalState\Logs\draw_targets.txt", line);
            }
            catch {}
        }
        if (_info.Target == Target.Texture3D || texture._info.Target == Target.Texture3D)
            throw new NotSupportedException("Scaled 3D texture blits require a Texture3D sampling shader and explicit Z extent.");
        _renderer.Blit(this, texture, new(source.X1, source.Y1, source.X2, source.Y2),
            new(target.X1, target.Y1, target.X2, target.Y2), linearFilter);
    }
    public void CopyTo(BufferRange range, int layer, int level, int stride)
    {
        Subresource(layer, level);
        // Validate all destination rows before the first upload. Otherwise a
        // later row can fail after earlier rows already modified the buffer.
        // The GAL contract permits a destination range smaller than the full
        // texture. D3D11 truncates the copy to the available range, and the
        // texture cache uses that behavior for partial host synchronization.
        _renderer.Buffer(range.Handle, range.Offset, range.Size);
        // Explicit packing/readback path. Presentation never calls this method.
        using var data = GetData(layer, level);
        ReadOnlySpan<byte> source = data.Get();

        // A zero stride means tightly packed. A positive stride smaller than
        // one row is also a linear/truncated copy in the GAL contract; it is
        // not a reason to abort the emulation thread.
        int rowBytes = checked(Math.Max(1, Width >> level) * _info.BytesPerPixel);
        int rows = Math.Max(1, Height >> level);
        int slices = _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1;
        if (stride <= 0 || stride < rowBytes)
        {
            int copySize = Math.Min(range.Size, source.Length);
            if (stride > 0) copySize = Math.Min(copySize, checked(stride * rows * slices));
            _renderer.SetBufferData(range.Handle, range.Offset, source[..copySize]);
            return;
        }

        int sourceStride = _info.GetMipStride(level);
        for (int z = 0; z < slices; ++z)
        for (int y = 0; y < rows; ++y)
        {
            int destinationOffset = checked((z * rows + y) * stride);
            if (destinationOffset >= range.Size) break;
            int copySize = Math.Min(rowBytes, range.Size - destinationOffset);
            _renderer.SetBufferData(range.Handle, range.Offset + destinationOffset,
                source.Slice((z * rows + y) * sourceStride, copySize));
        }
    }
    public ITexture CreateView(TextureCreateInfo info, int firstLayer, int firstLevel)
    {
        Validate(info);
        return _renderer.CreateTextureView(this, info, firstLayer, firstLevel);
    }
    private bool _hasBeenUsedBefore;
    private bool _hasBeenRead;
    private static bool _loggedMsaaInitialSkip;

    private static bool IsAllZero(ReadOnlySpan<byte> data)
    {
        ReadOnlySpan<ulong> ulongs = MemoryMarshal.Cast<byte, ulong>(data);
        for (int i = 0; i < ulongs.Length; i++)
        {
            if (ulongs[i] != 0) return false;
        }
        int remainder = data.Length % sizeof(ulong);
        for (int i = data.Length - remainder; i < data.Length; i++)
        {
            if (data[i] != 0) return false;
        }
        return true;
    }

    private void HandleMsaaSetData(ReadOnlySpan<byte> span, int layer, int level, Rectangle<int>? region)
    {
        bool isDepth = D3D12TextureFormat.Get(_info.Format).IsDepth;
        bool isZero = IsAllZero(span);
        ulong resId = _storage?.Texture != null ? _renderer.Device.ResourceId(_storage.Texture) : 0;
        int layers = _info.GetLayers();

        string classification;
        if (!_hasBeenUsedBefore && !_hasBeenRead)
        {
            if (isDepth)
            {
                classification = "B (transient depth/stencil target)";
            }
            else if (isZero)
            {
                classification = "A (transient render target with zero initial data)";
            }
            else
            {
                classification = "C (color texture containing guest data)";
            }
        }
        else if (isDepth)
        {
            classification = "B (transient depth/stencil target)";
        }
        else
        {
            classification = "C (color texture containing guest data)";
        }

        Ryujinx.Common.Logging.Logger.Notice.Print(Ryujinx.Common.Logging.LogClass.Gpu,
            $"[MSAA SetData] ResId={resId} Format={_info.Format} Target={_info.Target} " +
            $"Samples={_info.Samples} {Width}x{Height} Layers={layers} Levels={_info.Levels} " +
            $"IsDepth={isDepth} Bytes={span.Length} Layer={layer} Level={level} " +
            $"UsedBefore={_hasBeenUsedBefore} ReadBefore={_hasBeenRead} Class={classification}");

        // Phase 1: Safe MSAA initialization policy for A and B
        if (!_hasBeenUsedBefore && !_hasBeenRead && (isDepth || isZero))
        {
            if (!_loggedMsaaInitialSkip)
            {
                _loggedMsaaInitialSkip = true;
                Ryujinx.Common.Logging.Logger.Notice.Print(Ryujinx.Common.Logging.LogClass.Gpu,
                    "Skipped redundant MSAA initial data because resource is fully overwritten before first read.");
            }
            return;
        }

        // Phase 2: Genuine guest data -> MSAA expansion
        if (!isDepth)
        {
            TextureCreateInfo tempInfo = new(
                _info.Width, _info.Height, layers, _info.Levels, 1,
                _info.BlockWidth, _info.BlockHeight, _info.BytesPerPixel,
                _info.Format, _info.DepthStencilMode,
                _info.Target == Target.Texture2DMultisampleArray ? Target.Texture2DArray : Target.Texture2D,
                _info.SwizzleR, _info.SwizzleG, _info.SwizzleB, _info.SwizzleA);

            var temp1x = (D3D12GalTexture)_renderer.CreateTexture(tempInfo);
            try
            {
                temp1x.UploadFlatData(span, layer, level, region);

                _renderer.Blit(temp1x, this,
                    new Extents2DF(0, 0, Width, Height),
                    new Extents2DF(0, 0, Width, Height),
                    false);
                _hasBeenUsedBefore = true;
                return;
            }
            finally
            {
                temp1x.Release();
            }
        }

        throw new NotSupportedException($"MSAA depth/stencil upload with non-zero guest data is not supported for {_info.Format}.");
    }

    private void UploadFlatData(ReadOnlySpan<byte> span, int layer, int level, Rectangle<int>? region)
    {
        if (region.HasValue)
        {
            UploadFlatRegion(span, layer, level, region.Value);
        }
        else if (layer == 0 && level == 0 && span.Length >= _info.GetMipSize(0))
        {
            UploadFlatAll(span);
        }
        else
        {
            UploadFlatSubresource(span, layer, level);
        }
    }

    private void UploadFlatAll(ReadOnlySpan<byte> span)
    {
        _renderer.EnsureCommands();
        int offset = 0;
        int layers = _info.GetLayers();
        for (int level = 0; level < _info.Levels; ++level)
        {
            int mipSize = _info.GetMipSize(level);
            if (offset > span.Length - mipSize) break;
            int width = Math.Max(1, Width >> level), height = Math.Max(1, Height >> level);
            int depth = _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1;
            int sliceSize = _info.GetMipSize2D(level);

            if (_info.Target == Target.Texture3D)
            {
                _renderer.Device.Upload(Native, (uint)level, 0, 0, 0, (uint)width, (uint)height, (uint)depth,
                    span.Slice(offset, mipSize), (uint)_info.GetMipStride(level), (uint)sliceSize);
            }
            else
            {
                for (int layer = 0; layer < layers; ++layer)
                {
                    int layerOffset = offset + layer * sliceSize;
                    if (layerOffset + sliceSize > span.Length) break;
                    _renderer.Device.Upload(Native, (uint)level, 0, 0, (uint)layer, (uint)width, (uint)height, 1,
                        span.Slice(layerOffset, sliceSize), (uint)_info.GetMipStride(level), (uint)sliceSize);
                }
            }
            offset += mipSize;
        }
    }

    private void UploadFlatSubresource(ReadOnlySpan<byte> span, int layer, int level)
    {
        int size = _info.Target == Target.Texture3D ? _info.GetMipSize(level) : _info.GetMipSize2D(level);
        if (span.Length < size) throw new ArgumentException("The mip upload does not contain enough data.", nameof(span));
        int width = Math.Max(1, Width >> level), height = Math.Max(1, Height >> level);
        int depth = _info.Target == Target.Texture3D ? Math.Max(1, _info.Depth >> level) : 1;
        _renderer.EnsureCommands();
        _renderer.Device.Upload(Native, (uint)level, 0, 0, (uint)layer, (uint)width, (uint)height, (uint)depth,
            span[..size], (uint)_info.GetMipStride(level), (uint)_info.GetMipSize2D(level));
    }

    private void UploadFlatRegion(ReadOnlySpan<byte> span, int layer, int level, Rectangle<int> region)
    {
        int levelWidth = Math.Max(1, Width >> level);
        int levelHeight = Math.Max(1, Height >> level);
        if (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0 ||
            region.Width > levelWidth - region.X || region.Height > levelHeight - region.Y)
            throw new ArgumentOutOfRangeException(nameof(region));
        int pitch = checked(region.Width * _info.BytesPerPixel);
        int uploadPitch = checked((pitch + 3) & ~3);
        int paddedRequired = checked(uploadPitch * region.Height);
        int packedRequired = checked(pitch * region.Height);
        if (span.Length < packedRequired) throw new ArgumentException("Partial texture upload data is too short.", nameof(span));
        int sourcePitch = span.Length >= paddedRequired ? uploadPitch : pitch;
        if (Native.Format.IsDepth)
        {
            // D3D12 forbids partial copies involving a depth resource.
            // Explicit CPU read/modify/upload preserves texels outside the region.
            using var previous = GetData(layer, level);
            byte[] merged = previous.Get().ToArray();
            int mipRowBytes = levelWidth * _info.BytesPerPixel;
            for (int y = 0; y < region.Height; ++y)
                span.Slice(y * sourcePitch, pitch).CopyTo(merged.AsSpan((region.Y + y) * mipRowBytes + region.X * _info.BytesPerPixel));
            _renderer.EnsureCommands();
            _renderer.Device.Upload(Native, (uint)level, 0, 0, (uint)layer, (uint)levelWidth, (uint)levelHeight, 1,
                merged, (uint)mipRowBytes, (uint)merged.Length);
            return;
        }
        _renderer.EnsureCommands();
        ReadOnlySpan<byte> upload = span;
        if (sourcePitch != uploadPitch)
        {
            byte[] padded = new byte[paddedRequired];
            for (int y = 0; y < region.Height; y++)
                span.Slice(y * sourcePitch, pitch).CopyTo(padded.AsSpan(y * uploadPitch, pitch));
            upload = padded;
        }
        _renderer.Device.Upload(Native, (uint)level, (uint)region.X, (uint)region.Y,
            (uint)layer, (uint)region.Width, (uint)region.Height, 1,
            upload[..paddedRequired], (uint)uploadPitch, (uint)paddedRequired);
    }

    public PinnedSpan<byte> GetData()
    {
        _renderer.Flush();
        _hasBeenRead = true;
        int size = 0;
        for (int level = 0; level < _info.Levels; ++level) size = checked(size + _info.GetMipSize(level));
        byte[] result = new byte[size];
        int offset = 0;
        for (int level = 0; level < _info.Levels; ++level)
        {
            int mipSize = _info.GetMipSize(level);
            byte[] mip = _renderer.Device.ReadTexture(Native, (uint)level, mipSize,
                (uint)_info.GetMipStride(level), (uint)_info.GetMipSize2D(level));
            mip.CopyTo(result, offset); offset += mipSize;
        }
        return D3D12Renderer.Pin(result);
    }
    public PinnedSpan<byte> GetData(int layer, int level)
    {
        Subresource(layer, level);
        _renderer.Flush();
        _hasBeenRead = true;
        int size = _info.GetMipSize(level);
        return D3D12Renderer.Pin(_renderer.Device.ReadTexture(Native, (uint)level, size,
            (uint)_info.GetMipStride(level), (uint)_info.GetMipSize2D(level)));
    }
    public void SetData(MemoryOwner<byte> data)
    {
        using (data)
        {
            if (_info.Samples != 1)
            {
                HandleMsaaSetData(data.Span, 0, 0, null);
                return;
            }
            UploadFlatAll(data.Span);
            _hasBeenUsedBefore = true;
        }
    }
    public void SetData(MemoryOwner<byte> data, int layer, int level)
    {
        using (data)
        {
            Subresource(layer, level);
            if (_info.Samples != 1)
            {
                HandleMsaaSetData(data.Span, layer, level, null);
                return;
            }
            UploadFlatSubresource(data.Span, layer, level);
            _hasBeenUsedBefore = true;
        }
    }
    public void SetData(MemoryOwner<byte> data, int layer, int level, Rectangle<int> region)
    {
        using (data)
        {
            Subresource(layer, level);
            if (_info.Samples != 1)
            {
                HandleMsaaSetData(data.Span, layer, level, region);
                return;
            }
            UploadFlatRegion(data.Span, layer, level, region);
            _hasBeenUsedBefore = true;
        }
    }
    public void SetStorage(BufferRange buffer) => throw new NotSupportedException("A non-buffer D3D12 texture cannot be rebound as a buffer texture.");
    public void Release()
    {
        var storage = System.Threading.Interlocked.Exchange(ref _storage, null);
        if (storage != null)
        {
            _renderer.ForgetTexture(this);
            if (_ownsNativeView) _native.Dispose();
            _native = null;
            if (System.Threading.Interlocked.Decrement(ref storage.References) == 0) storage.Texture.Dispose();
        }
    }
}
