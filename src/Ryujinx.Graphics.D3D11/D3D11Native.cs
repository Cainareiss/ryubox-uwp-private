using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryujinx.Graphics.D3D11;

internal enum D3D11Usage : uint
{
    Default = 0,
    Immutable = 1,
    Dynamic = 2,
    Staging = 3,
}

[Flags]
internal enum D3D11BindFlags : uint
{
    None = 0,
    VertexBuffer = 0x1,
    IndexBuffer = 0x2,
    ConstantBuffer = 0x4,
    ShaderResource = 0x8,
    StreamOutput = 0x10,
    RenderTarget = 0x20,
    DepthStencil = 0x40,
    UnorderedAccess = 0x80,
}

[Flags]
internal enum D3D11FormatSupport : uint
{
    None = 0,
    Texture2D = 0x20,
    TextureCube = 0x80,
    ShaderLoad = 0x100,
    ShaderSample = 0x200,
    ShaderSampleComparison = 0x400,
    Mip = 0x1000,
    MipAutogen = 0x2000,
    RenderTarget = 0x4000,
    Blendable = 0x8000,
    DepthStencil = 0x10000,
    MultisampleResolve = 0x40000,
    MultisampleRenderTarget = 0x200000,
    MultisampleLoad = 0x400000,
    ShaderGather = 0x800000,
    TypedUnorderedAccessView = 0x2000000,
}

[Flags]
internal enum D3D11CpuAccessFlags : uint
{
    None = 0,
    Write = 0x10000,
    Read = 0x20000,
}

[Flags]
internal enum D3D11ResourceMiscFlags : uint
{
    None = 0,
    GenerateMips = 0x1,
    TextureCube = 0x4,
    BufferAllowRawViews = 0x20,
    BufferStructured = 0x40,
}

internal enum D3D11QueryType : uint
{
    Event = 0,
    Occlusion = 1,
    SoStatistics = 6,
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11QueryDesc
{
    public D3D11QueryType Query;
    public uint MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11SoStatistics
{
    public ulong NumPrimitivesWritten;
    public ulong PrimitivesStorageNeeded;
}

internal enum D3D11MapType : uint
{
    Read = 1,
    Write = 2,
    ReadWrite = 3,
    WriteDiscard = 4,
    WriteNoOverwrite = 5,
}

[Flags]
internal enum D3D11MapFlags : uint
{
    None = 0,
    DoNotWait = 0x100000,
}

internal enum D3D11PrimitiveTopology : uint
{
    Undefined = 0,
    PointList = 1,
    LineList = 2,
    LineStrip = 3,
    TriangleList = 4,
    TriangleStrip = 5,
    LineListAdj = 10,
    LineStripAdj = 11,
    TriangleListAdj = 12,
    TriangleStripAdj = 13,
    ControlPointPatchList1 = 33,
}

internal enum D3D11Filter : uint
{
    MinMagMipPoint = 0,
    MinMagPointMipLinear = 1,
    MinPointMagLinearMipPoint = 4,
    MinPointMagMipLinear = 5,
    MinLinearMagMipPoint = 16,
    MinLinearMagPointMipLinear = 17,
    MinMagLinearMipPoint = 20,
    MinMagMipLinear = 21,
    Anisotropic = 85,
    ComparisonMinMagMipPoint = 0x80,
    ComparisonMinMagMipLinear = 0x95,
}

internal enum D3D11TextureAddressMode : uint
{
    Wrap = 1,
    Mirror = 2,
    Clamp = 3,
    Border = 4,
    MirrorOnce = 5,
}

internal enum D3D11ComparisonFunc : uint
{
    Never = 1,
    Less = 2,
    Equal = 3,
    LessEqual = 4,
    Greater = 5,
    NotEqual = 6,
    GreaterEqual = 7,
    Always = 8,
}

internal enum D3D11FillMode : uint
{
    Wireframe = 2,
    Solid = 3,
}

internal enum D3D11CullMode : uint
{
    None = 1,
    Front = 2,
    Back = 3,
}

internal enum D3D11Blend : uint
{
    Zero = 1,
    One = 2,
    SrcColor = 3,
    InvSrcColor = 4,
    SrcAlpha = 5,
    InvSrcAlpha = 6,
    DestAlpha = 7,
    InvDestAlpha = 8,
    DestColor = 9,
    InvDestColor = 10,
    SrcAlphaSat = 11,
    BlendFactor = 14,
    InvBlendFactor = 15,
    Src1Color = 16,
    InvSrc1Color = 17,
    Src1Alpha = 18,
    InvSrc1Alpha = 19,
}

internal enum D3D11BlendOp : uint
{
    Add = 1,
    Subtract = 2,
    RevSubtract = 3,
    Min = 4,
    Max = 5,
}

internal enum D3D11ColorWriteEnable : byte
{
    Red = 1,
    Green = 2,
    Blue = 4,
    Alpha = 8,
    All = 15,
}

internal enum D3D11StencilOp : uint
{
    Keep = 1,
    Zero = 2,
    Replace = 3,
    IncrSat = 4,
    DecrSat = 5,
    Invert = 6,
    Incr = 7,
    Decr = 8,
}

internal enum D3D11InputClassification : uint
{
    PerVertexData = 0,
    PerInstanceData = 1,
}

internal enum D3D11DepthWriteMask : uint
{
    Zero = 0,
    All = 1,
}

internal enum D3D11SrvDimension : uint
{
    Buffer = 1,
    Texture1D = 2,
    Texture1DArray = 3,
    Texture2D = 4,
    Texture2DArray = 5,
    Texture2DMultisample = 6,
    Texture2DMultisampleArray = 7,
    Texture3D = 8,
    TextureCube = 9,
    TextureCubeArray = 10,
    BufferExtended = 11,
}

internal enum D3D11RtvDimension : uint
{
    Texture1D = 2,
    Texture1DArray = 3,
    Texture2D = 4,
    Texture2DArray = 5,
    Texture2DMultisample = 6,
    Texture2DMultisampleArray = 7,
    Texture3D = 8,
}

internal enum D3D11DsvDimension : uint
{
    Texture1D = 1,
    Texture1DArray = 2,
    Texture2D = 3,
    Texture2DArray = 4,
    Texture2DMultisample = 5,
    Texture2DMultisampleArray = 6,
}

internal enum D3D11UavDimension : uint
{
    Buffer = 1,
    Texture1D = 2,
    Texture1DArray = 3,
    Texture2D = 4,
    Texture2DArray = 5,
    Texture3D = 8,
}

internal enum D3D11Format : uint
{
    Unknown = 0,
    R32G32B32A32Typeless = 1,
    R32G32B32A32Float = 2,
    R32G32B32A32Uint = 3,
    R32G32B32A32Sint = 4,
    R32G8X24Typeless = 19,
    D32FloatS8X24Uint = 20,
    R32FloatX8X24Typeless = 21,
    R32G32B32Typeless = 5,
    R32G32B32Float = 6,
    R32G32B32Uint = 7,
    R32G32B32Sint = 8,
    R16G16B16A16Typeless = 9,
    R16G16B16A16Float = 10,
    R16G16B16A16Unorm = 11,
    R16G16B16A16Uint = 12,
    R16G16B16A16Snorm = 13,
    R16G16B16A16Sint = 14,
    R32G32Typeless = 15,
    R32G32Float = 16,
    R32G32Uint = 17,
    R32G32Sint = 18,
    R10G10B10A2Unorm = 24,
    R10G10B10A2Uint = 25,
    R11G11B10Float = 26,
    R9G9B9E5SharedExp = 67,
    R8G8B8A8Unorm = 28,
    R8G8B8A8UnormSrgb = 29,
    R8G8B8A8Uint = 30,
    R8G8B8A8Snorm = 31,
    R8G8B8A8Sint = 32,
    R16G16Typeless = 33,
    R16G16Float = 34,
    R16G16Unorm = 35,
    R16G16Uint = 36,
    R16G16Snorm = 37,
    R16G16Sint = 38,
    R32Typeless = 39,
    D32Float = 40,
    R32Float = 41,
    R32Uint = 42,
    R32Sint = 43,
    R24G8Typeless = 44,
    D24UnormS8Uint = 45,
    R24UnormX8Typeless = 46,
    X24TypelessG8Uint = 47,
    R8G8Typeless = 48,
    R8G8Unorm = 49,
    R8G8Uint = 50,
    R8G8Snorm = 51,
    R8G8Sint = 52,
    R16Typeless = 53,
    R16Float = 54,
    D16Unorm = 55,
    R16Unorm = 56,
    R16Uint = 57,
    R16Snorm = 58,
    R16Sint = 59,
    R8Typeless = 60,
    R8Unorm = 61,
    R8Uint = 62,
    R8Snorm = 63,
    R8Sint = 64,
    Bc1Unorm = 71,
    Bc1UnormSrgb = 72,
    Bc2Unorm = 74,
    Bc2UnormSrgb = 75,
    Bc3Unorm = 77,
    Bc3UnormSrgb = 78,
    Bc4Unorm = 80,
    Bc4Snorm = 81,
    Bc5Unorm = 83,
    Bc5Snorm = 84,
    B8G8R8A8Unorm = 87,
    B8G8R8A8UnormSrgb = 91,
    Bc6HUfloat = 95,
    Bc6HSfloat = 96,
    Bc7Unorm = 98,
    Bc7UnormSrgb = 99,
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11BufferDesc
{
    public uint ByteWidth;
    public D3D11Usage Usage;
    public D3D11BindFlags BindFlags;
    public D3D11CpuAccessFlags CpuAccessFlags;
    public D3D11ResourceMiscFlags MiscFlags;
    public uint StructureByteStride;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DDesc
{
    public uint Width;
    public uint MipLevels;
    public uint ArraySize;
    public D3D11Format Format;
    public D3D11Usage Usage;
    public D3D11BindFlags BindFlags;
    public D3D11CpuAccessFlags CpuAccessFlags;
    public D3D11ResourceMiscFlags MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture3DDesc
{
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint MipLevels;
    public D3D11Format Format;
    public D3D11Usage Usage;
    public D3D11BindFlags BindFlags;
    public D3D11CpuAccessFlags CpuAccessFlags;
    public D3D11ResourceMiscFlags MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDesc
{
    public uint Width;
    public uint Height;
    public uint MipLevels;
    public uint ArraySize;
    public D3D11Format Format;
    public uint SampleCount;
    public uint SampleQuality;
    public D3D11Usage Usage;
    public D3D11BindFlags BindFlags;
    public D3D11CpuAccessFlags CpuAccessFlags;
    public D3D11ResourceMiscFlags MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11SubresourceData
{
    public nint SysMem;
    public uint SysMemPitch;
    public uint SysMemSlicePitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11MappedSubresource
{
    public nint Data;
    public uint RowPitch;
    public uint DepthPitch;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Viewport
{
    public float TopLeftX;
    public float TopLeftY;
    public float Width;
    public float Height;
    public float MinDepth;
    public float MaxDepth;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Box
{
    public uint Left;
    public uint Top;
    public uint Front;
    public uint Right;
    public uint Bottom;
    public uint Back;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DSrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DArraySrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DRtv
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DArrayRtv
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DUav
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DArrayUav
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11TextureCubeSrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11TextureCubeArraySrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
    public uint First2DArrayFace;
    public uint NumCubes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture3DSrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture3DRtv
{
    public uint MipSlice;
    public uint FirstWSlice;
    public uint WSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture3DUav
{
    public uint MipSlice;
    public uint FirstWSlice;
    public uint WSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DSrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DArraySrv
{
    public uint MostDetailedMip;
    public uint MipLevels;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DRtv
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DArrayRtv
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DDsv
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture1DArrayDsv
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DDsv
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DArrayDsv
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DUav
{
    public uint MipSlice;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11Texture2DArrayUav
{
    public uint MipSlice;
    public uint FirstArraySlice;
    public uint ArraySize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11BufferExtendedSrv
{
    public uint FirstElement;
    public uint NumElements;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11BufferUav
{
    public uint FirstElement;
    public uint NumElements;
    public uint Flags;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct D3D11SrvUnion
{
    [FieldOffset(0)] public D3D11BufferExtendedSrv BufferExtended;
    [FieldOffset(0)] public D3D11Texture1DSrv Texture1D;
    [FieldOffset(0)] public D3D11Texture1DArraySrv Texture1DArray;
    [FieldOffset(0)] public D3D11Texture2DSrv Texture2D;
    [FieldOffset(0)] public D3D11Texture2DArraySrv Texture2DArray;
    [FieldOffset(0)] public D3D11TextureCubeSrv TextureCube;
    [FieldOffset(0)] public D3D11TextureCubeArraySrv TextureCubeArray;
    [FieldOffset(0)] public D3D11Texture3DSrv Texture3D;
}

[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct D3D11RtvUnion
{
    [FieldOffset(0)] public D3D11Texture1DRtv Texture1D;
    [FieldOffset(0)] public D3D11Texture1DArrayRtv Texture1DArray;
    [FieldOffset(0)] public D3D11Texture2DRtv Texture2D;
    [FieldOffset(0)] public D3D11Texture2DArrayRtv Texture2DArray;
    [FieldOffset(0)] public D3D11Texture3DRtv Texture3D;
}

[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct D3D11DsvUnion
{
    [FieldOffset(0)] public D3D11Texture1DDsv Texture1D;
    [FieldOffset(0)] public D3D11Texture1DArrayDsv Texture1DArray;
    [FieldOffset(0)] public D3D11Texture2DDsv Texture2D;
    [FieldOffset(0)] public D3D11Texture2DArrayDsv Texture2DArray;
}

[StructLayout(LayoutKind.Explicit, Size = 12)]
internal struct D3D11UavUnion
{
    [FieldOffset(0)] public D3D11BufferUav Buffer;
    [FieldOffset(0)] public D3D11Texture1DUav Texture1D;
    [FieldOffset(0)] public D3D11Texture1DArrayUav Texture1DArray;
    [FieldOffset(0)] public D3D11Texture2DUav Texture2D;
    [FieldOffset(0)] public D3D11Texture2DArrayUav Texture2DArray;
    [FieldOffset(0)] public D3D11Texture3DUav Texture3D;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11ShaderResourceViewDesc
{
    public D3D11Format Format;
    public D3D11SrvDimension ViewDimension;
    public D3D11SrvUnion View;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11RenderTargetViewDesc
{
    public D3D11Format Format;
    public D3D11RtvDimension ViewDimension;
    public D3D11RtvUnion View;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11DepthStencilViewDesc
{
    public D3D11Format Format;
    public D3D11DsvDimension ViewDimension;
    public D3D11DsvUnion View;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11UnorderedAccessViewDesc
{
    public D3D11Format Format;
    public D3D11UavDimension ViewDimension;
    public D3D11UavUnion View;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11SamplerDesc
{
    public D3D11Filter Filter;
    public D3D11TextureAddressMode AddressU;
    public D3D11TextureAddressMode AddressV;
    public D3D11TextureAddressMode AddressW;
    public float MipLODBias;
    public uint MaxAnisotropy;
    public D3D11ComparisonFunc ComparisonFunc;
    public float BorderColor0;
    public float BorderColor1;
    public float BorderColor2;
    public float BorderColor3;
    public float MinLOD;
    public float MaxLOD;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11RasterizerDesc
{
    public D3D11FillMode FillMode;
    public D3D11CullMode CullMode;
    public int FrontCounterClockwise;
    public int DepthBias;
    public float DepthBiasClamp;
    public float SlopeScaledDepthBias;
    public int DepthClipEnable;
    public int ScissorEnable;
    public int MultisampleEnable;
    public int AntialiasedLineEnable;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11RenderTargetBlendDesc
{
    public int BlendEnable;
    public D3D11Blend SrcBlend;
    public D3D11Blend DestBlend;
    public D3D11BlendOp BlendOp;
    public D3D11Blend SrcBlendAlpha;
    public D3D11Blend DestAlpha;
    public D3D11BlendOp BlendOpAlpha;
    public byte ColorWriteMask;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11BlendDesc
{
    public int AlphaToCoverageEnable;
    public int IndependentBlendEnable;
    public D3D11RenderTargetBlendDesc RenderTarget0;
    public D3D11RenderTargetBlendDesc RenderTarget1;
    public D3D11RenderTargetBlendDesc RenderTarget2;
    public D3D11RenderTargetBlendDesc RenderTarget3;
    public D3D11RenderTargetBlendDesc RenderTarget4;
    public D3D11RenderTargetBlendDesc RenderTarget5;
    public D3D11RenderTargetBlendDesc RenderTarget6;
    public D3D11RenderTargetBlendDesc RenderTarget7;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11DepthStencilOpDesc
{
    public D3D11StencilOp StencilFailOp;
    public D3D11StencilOp StencilDepthFailOp;
    public D3D11StencilOp StencilPassOp;
    public D3D11ComparisonFunc StencilFunc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11DepthStencilDesc
{
    public int DepthEnable;
    public D3D11DepthWriteMask DepthWriteMask;
    public D3D11ComparisonFunc DepthFunc;
    public int StencilEnable;
    public byte StencilReadMask;
    public byte StencilWriteMask;
    public D3D11DepthStencilOpDesc FrontFace;
    public D3D11DepthStencilOpDesc BackFace;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11InputElementDesc
{
    public nint SemanticName;
    public uint SemanticIndex;
    public D3D11Format Format;
    public uint InputSlot;
    public uint AlignedByteOffset;
    public D3D11InputClassification InputSlotClass;
    public uint InstanceDataStepRate;
}

internal static unsafe class D3D11Com
{
    public static nint VTableEntry(nint instance, int index)
    {
        nint vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    public static T GetDelegate<T>(nint instance, int index) where T : Delegate
    {
        nint entry = VTableEntry(instance, index);
        if (DelegateCache<T>.LastEntry == entry && DelegateCache<T>.Last is T last) return last;
        T value = DelegateCache<T>.Entries.GetOrAdd(entry, static address => Marshal.GetDelegateForFunctionPointer<T>(address));
        DelegateCache<T>.LastEntry = entry;
        DelegateCache<T>.Last = value;
        return value;
    }

    // Cache function addresses/signatures, never devices or context instances.
    private static class DelegateCache<T> where T : Delegate
    {
        internal static readonly ConcurrentDictionary<nint, T> Entries = new();
        [ThreadStatic] internal static nint LastEntry;
        [ThreadStatic] internal static T Last;
    }

    public static void Release(nint instance)
    {
        if (instance != 0)
        {
            GetDelegate<ReleaseDelegate>(instance, 2)(instance);
        }
    }

    public static nint AddRef(nint instance)
    {
        if (instance == 0)
        {
            return 0;
        }

        GetDelegate<AddRefDelegate>(instance, 1)(instance);
        return instance;
    }

    public static void ThrowIfFailed(int hr, string operation)
    {
        if (hr < 0)
        {
            throw new D3D11Exception(operation, hr);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint AddRefDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseDelegate(nint self);
}

internal sealed class D3D11Exception : Exception
{
    public int HResultCode { get; }

    public D3D11Exception(string operation, int hResult) : base($"{operation} failed with HRESULT 0x{hResult:X8}.")
    {
        HResultCode = hResult;
        HResult = hResult;
    }
}

internal sealed unsafe class D3D11Device
{
    private static readonly Guid InfoQueueGuid = new("6543DBB6-1B48-42F5-AB82-E97EC74326F6");
    private readonly nint _self;

    public D3D11Device(nint self)
    {
        _self = self;
    }

    public uint FeatureLevel => D3D11Com.GetDelegate<GetFeatureLevelDelegate>(_self, 37)(_self);

    public int GetDeviceRemovedReason()
    {
        return D3D11Com.GetDelegate<GetDeviceRemovedReasonDelegate>(_self, 39)(_self);
    }

    public string DrainDebugMessages()
    {
        Guid iid = InfoQueueGuid;
        int queryResult = Marshal.QueryInterface(_self, ref iid, out nint infoQueue);
        if (queryResult < 0 || infoQueue == 0)
        {
            return string.Empty;
        }

        try
        {
            ulong count = D3D11Com.GetDelegate<GetNumStoredMessagesDelegate>(infoQueue, 8)(infoQueue);
            if (count == 0)
            {
                return string.Empty;
            }

            StringBuilder output = new();
            ulong readableCount = Math.Min(count, 256UL);
            for (ulong index = 0; index < readableCount; index++)
            {
                nuint messageSize = 0;
                int result = D3D11Com.GetDelegate<GetMessageDelegate>(infoQueue, 5)(infoQueue, index, 0, ref messageSize);
                if (result < 0 || messageSize == 0 || messageSize > 1024 * 1024)
                {
                    continue;
                }

                byte[] storage = new byte[(int)messageSize];
                fixed (byte* pointer = storage)
                {
                    result = D3D11Com.GetDelegate<GetMessageDelegate>(infoQueue, 5)(infoQueue, index, (nint)pointer, ref messageSize);
                    if (result < 0)
                    {
                        continue;
                    }

                    D3D11DebugMessage message = Marshal.PtrToStructure<D3D11DebugMessage>((nint)pointer);
                    int textLength = (int)Math.Min(message.DescriptionByteLength, 2048u);
                    if (textLength > 0 && Marshal.ReadByte(message.Description, textLength - 1) == 0)
                    {
                        textLength--;
                    }

                    string description = textLength > 0
                        ? Marshal.PtrToStringAnsi(message.Description, textLength)
                        : string.Empty;
                    output.Append("severity=").Append(message.Severity)
                        .Append(", id=").Append(message.Id)
                        .Append(": ").AppendLine(description);
                }
            }

            if (count > readableCount)
            {
                output.Append("messagesTruncated=").Append(count - readableCount).AppendLine();
            }

            D3D11Com.GetDelegate<ClearStoredMessagesDelegate>(infoQueue, 4)(infoQueue);
            return output.ToString();
        }
        finally
        {
            D3D11Com.Release(infoQueue);
        }
    }

    public D3D11FormatSupport CheckFormatSupport(D3D11Format format)
    {
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CheckFormatSupportDelegate>(_self, 29)(_self, format, out D3D11FormatSupport support),
            "ID3D11Device::CheckFormatSupport");
        return support;
    }

    public nint CreateBuffer(in D3D11BufferDesc desc, nint initialData = 0)
    {
        nint result;
        D3D11SubresourceData data = default;
        nint dataPointer = 0;

        if (initialData != 0)
        {
            data.SysMem = initialData;
            dataPointer = (nint)(&data);
        }

        int hr = D3D11Com.GetDelegate<CreateBufferDelegate>(_self, 3)(_self, in desc, dataPointer, out result);
        if (hr < 0)
        {
            string removalReason = string.Empty;
            if (hr == unchecked((int)0x887A0005))
            {
                removalReason = $", deviceRemovedReason=0x{GetDeviceRemovedReason():X8}";
            }
            throw new D3D11Exception(
                $"ID3D11Device::CreateBuffer failed with HRESULT 0x{hr:X8}. " +
                $"BufferDesc: ByteWidth={desc.ByteWidth}, BindFlags={desc.BindFlags}, Usage={desc.Usage}, " +
                $"CPUAccessFlags={desc.CpuAccessFlags}, MiscFlags={desc.MiscFlags}, StructureByteStride={desc.StructureByteStride}, " +
                $"workingSet={Environment.WorkingSet}{removalReason}",
                hr);
        }

        return result;
    }

    public unsafe nint CreateTexture1D(in D3D11Texture1DDesc desc, nint initialData, uint rowPitch)
    {
        nint result;
        D3D11SubresourceData data = default;
        nint dataPointer = 0;

        if (initialData != 0)
        {
            data.SysMem = initialData;
            data.SysMemPitch = rowPitch;
            dataPointer = (nint)(&data);
        }

        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateTexture1DDelegate>(_self, 4)(_self, in desc, dataPointer, out result),
            "ID3D11Device::CreateTexture1D");

        return result;
    }

    public unsafe nint CreateTexture3D(in D3D11Texture3DDesc desc, nint initialData, uint rowPitch, uint depthPitch)
    {
        nint result;
        D3D11SubresourceData data = default;
        nint dataPointer = 0;

        if (initialData != 0)
        {
            data.SysMem = initialData;
            data.SysMemPitch = rowPitch;
            data.SysMemSlicePitch = depthPitch;
            dataPointer = (nint)(&data);
        }

        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateTexture3DDelegate>(_self, 6)(_self, in desc, dataPointer, out result),
            "ID3D11Device::CreateTexture3D");

        return result;
    }

    public nint CreateTexture2D(in D3D11Texture2DDesc desc, nint initialData, uint rowPitch)
    {
        nint result;
        D3D11SubresourceData data = default;
        nint dataPointer = 0;

        if (initialData != 0)
        {
            data.SysMem = initialData;
            data.SysMemPitch = rowPitch;
            dataPointer = (nint)(&data);
        }

        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateTexture2DDelegate>(_self, 5)(_self, in desc, dataPointer, out result),
            $"ID3D11Device::CreateTexture2D " +
            $"(size={desc.Width}x{desc.Height}, mips={desc.MipLevels}, layers={desc.ArraySize}, " +
            $"format={desc.Format}, samples={desc.SampleCount}/{desc.SampleQuality}, usage={desc.Usage}, " +
            $"bind={desc.BindFlags}, cpu={desc.CpuAccessFlags}, misc={desc.MiscFlags}, initialData={initialData != 0})");

        return result;
    }

    public nint CreateShaderResourceView(nint resource)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateShaderResourceViewDelegate>(_self, 7)(_self, resource, 0, out result),
            "ID3D11Device::CreateShaderResourceView");
        return result;
    }

    public unsafe nint CreateShaderResourceView(nint resource, in D3D11ShaderResourceViewDesc desc)
    {
        nint result;
        D3D11ShaderResourceViewDesc local = desc;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateShaderResourceViewDelegate>(_self, 7)(_self, resource, (nint)(&local), out result),
            "ID3D11Device::CreateShaderResourceView");
        return result;
    }

    public nint CreateRenderTargetView(nint resource)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateRenderTargetViewDelegate>(_self, 9)(_self, resource, 0, out result),
            "ID3D11Device::CreateRenderTargetView");
        return result;
    }

    public nint CreateDepthStencilView(nint resource)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateDepthStencilViewDelegate>(_self, 10)(_self, resource, 0, out result),
            "ID3D11Device::CreateDepthStencilView");
        return result;
    }

    public unsafe nint CreateDepthStencilView(nint resource, in D3D11DepthStencilViewDesc desc)
    {
        nint result;
        D3D11DepthStencilViewDesc local = desc;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateDepthStencilViewDelegate>(_self, 10)(_self, resource, (nint)(&local), out result),
            "ID3D11Device::CreateDepthStencilView");
        return result;
    }

    public nint CreateUnorderedAccessView(nint resource)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateUnorderedAccessViewDelegate>(_self, 8)(_self, resource, 0, out result),
            "ID3D11Device::CreateUnorderedAccessView");
        return result;
    }

    public unsafe nint CreateUnorderedAccessView(nint resource, in D3D11UnorderedAccessViewDesc desc)
    {
        nint result;
        D3D11UnorderedAccessViewDesc local = desc;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateUnorderedAccessViewDelegate>(_self, 8)(_self, resource, (nint)(&local), out result),
            "ID3D11Device::CreateUnorderedAccessView");
        return result;
    }

    public unsafe nint CreateRenderTargetView(nint resource, in D3D11RenderTargetViewDesc desc)
    {
        nint result;
        D3D11RenderTargetViewDesc local = desc;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateRenderTargetViewDelegate>(_self, 9)(_self, resource, (nint)(&local), out result),
            "ID3D11Device::CreateRenderTargetView");
        return result;
    }

    public nint CreateQuery(D3D11QueryType type)
    {
        D3D11QueryDesc desc = new() { Query = type, MiscFlags = 0 };
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateQueryDelegate>(_self, 24)(_self, in desc, out result),
            "ID3D11Device::CreateQuery");
        return result;
    }

    public nint CreateSamplerState(in D3D11SamplerDesc desc)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateSamplerStateDelegate>(_self, 23)(_self, in desc, out result),
            "ID3D11Device::CreateSamplerState");
        return result;
    }

    public nint CreateRasterizerState(in D3D11RasterizerDesc desc)
    {
        nint result;
        int hr = D3D11Com.GetDelegate<CreateRasterizerStateDelegate>(_self, 22)(_self, in desc, out result);
        if (hr < 0)
        {
            string removalReason = string.Empty;
            if (hr == unchecked((int)0x887A0005))
            {
                removalReason = $", deviceRemovedReason=0x{GetDeviceRemovedReason():X8}";
            }
            throw new D3D11Exception($"ID3D11Device::CreateRasterizerState failed with HRESULT 0x{hr:X8}{removalReason}", hr);
        }
        return result;
    }

    public nint CreateBlendState(in D3D11BlendDesc desc)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateBlendStateDelegate>(_self, 20)(_self, in desc, out result),
            "ID3D11Device::CreateBlendState");
        return result;
    }

    public nint CreateDepthStencilState(in D3D11DepthStencilDesc desc)
    {
        nint result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<CreateDepthStencilStateDelegate>(_self, 21)(_self, in desc, out result),
            "ID3D11Device::CreateDepthStencilState");
        return result;
    }

    public nint CreateInputLayout(ReadOnlySpan<D3D11InputElementDesc> elements, byte[] shaderBytecode)
    {
        if (shaderBytecode is null || shaderBytecode.Length == 0)
        {
            throw new ArgumentException("Shader bytecode is required to create an input layout.");
        }

        fixed (D3D11InputElementDesc* pointer = elements)
        {
            GCHandle pin = GCHandle.Alloc(shaderBytecode, GCHandleType.Pinned);
            try
            {
                nint result;
                int hr = D3D11Com.GetDelegate<CreateInputLayoutDelegate>(_self, 11)(
                    _self,
                    pointer,
                    (uint)elements.Length,
                    pin.AddrOfPinnedObject(),
                    (nuint)shaderBytecode.Length,
                    out result);
                if (hr < 0)
                {
                    string removalReason = string.Empty;
                    if (hr == unchecked((int)0x887A0005))
                    {
                        removalReason = $", deviceRemovedReason=0x{GetDeviceRemovedReason():X8}";
                    }
                    throw new D3D11Exception($"ID3D11Device::CreateInputLayout failed with HRESULT 0x{hr:X8}{removalReason}", hr);
                }
                return result;
            }
            finally
            {
                pin.Free();
            }
        }
    }

    public nint CreateVertexShader(byte[] bytecode) => CreateShader(bytecode, 12, "ID3D11Device::CreateVertexShader");
    public nint CreateGeometryShader(byte[] bytecode) => CreateShader(bytecode, 13, "ID3D11Device::CreateGeometryShader");
    public nint CreatePixelShader(byte[] bytecode) => CreateShader(bytecode, 15, "ID3D11Device::CreatePixelShader");
    public nint CreateHullShader(byte[] bytecode) => CreateShader(bytecode, 16, "ID3D11Device::CreateHullShader");
    public nint CreateDomainShader(byte[] bytecode) => CreateShader(bytecode, 17, "ID3D11Device::CreateDomainShader");
    public nint CreateComputeShader(byte[] bytecode) => CreateShader(bytecode, 18, "ID3D11Device::CreateComputeShader");

    private nint CreateShader(byte[] bytecode, int vtableIndex, string operation)
    {
        if (bytecode is null || bytecode.Length == 0)
        {
            throw new ArgumentException("Shader bytecode cannot be empty.", nameof(bytecode));
        }

        GCHandle pin = GCHandle.Alloc(bytecode, GCHandleType.Pinned);
        try
        {
            nint result;
            D3D11Com.ThrowIfFailed(
                D3D11Com.GetDelegate<CreateShaderDelegate>(_self, vtableIndex)(
                    _self,
                    pin.AddrOfPinnedObject(),
                    (nuint)bytecode.Length,
                    0,
                    out result),
                operation);
            return result;
        }
        finally
        {
            pin.Free();
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBufferDelegate(nint self, in D3D11BufferDesc desc, nint initialData, out nint result);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11DebugMessage
    {
        public int Category;
        public int Severity;
        public int Id;
        public nint Description;
        public nuint DescriptionByteLength;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearStoredMessagesDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetMessageDelegate(nint self, ulong index, nint message, ref nuint messageByteLength);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate ulong GetNumStoredMessagesDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture1DDelegate(nint self, in D3D11Texture1DDesc desc, nint initialData, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture3DDelegate(nint self, in D3D11Texture3DDesc desc, nint initialData, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(nint self, in D3D11Texture2DDesc desc, nint initialData, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderResourceViewDelegate(nint self, nint resource, nint desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateRenderTargetViewDelegate(nint self, nint resource, nint desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDepthStencilViewDelegate(nint self, nint resource, nint desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateUnorderedAccessViewDelegate(nint self, nint resource, nint desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateQueryDelegate(nint self, in D3D11QueryDesc desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateSamplerStateDelegate(nint self, in D3D11SamplerDesc desc, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateShaderDelegate(nint self, nint bytecode, nuint bytecodeLength, nint classLinkage, out nint result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint GetFeatureLevelDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDeviceRemovedReasonDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFormatSupportDelegate(nint self, D3D11Format format, out D3D11FormatSupport support);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateRasterizerStateDelegate(nint self, in D3D11RasterizerDesc desc, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBlendStateDelegate(nint self, in D3D11BlendDesc desc, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDepthStencilStateDelegate(nint self, in D3D11DepthStencilDesc desc, out nint result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateInputLayoutDelegate(nint self, D3D11InputElementDesc* elements, uint elementCount, nint bytecode, nuint bytecodeLength, out nint result);
}
internal sealed unsafe class D3D11DeviceContext

{
    private readonly nint _self;

    public D3D11DeviceContext(nint self)
    {
        _self = self;
    }

        public D3D11MappedSubresource Map(nint resource, D3D11MapType mapType, D3D11MapFlags mapFlags = D3D11MapFlags.None, uint subresource = 0)
    {
        D3D11MappedSubresource result;
        D3D11Com.ThrowIfFailed(
            D3D11Com.GetDelegate<MapDelegate>(_self, 14)(_self, resource, subresource, mapType, mapFlags, out result),
            "ID3D11DeviceContext::Map");
        return result;
    }
    public void Unmap(nint resource, uint subresource = 0)
    {
        D3D11Com.GetDelegate<UnmapDelegate>(_self, 15)(_self, resource, subresource);
    }

    public void Begin(nint query)
    {
        D3D11Com.GetDelegate<BeginDelegate>(_self, 27)(_self, query);
    }

    public void End(nint query)
    {
        D3D11Com.GetDelegate<EndDelegate>(_self, 28)(_self, query);
    }

    public unsafe int GetData(nint query, Span<byte> destination, bool doNotFlush = false)
    {
        fixed (byte* pointer = destination)
        {
            int result = D3D11Com.GetDelegate<GetDataDelegate>(_self, 29)(
                _self,
                query,
                (nint)pointer,
                (uint)destination.Length,
                doNotFlush ? 1u : 0u);
            if (result < 0)
            {
                throw new D3D11Exception("ID3D11DeviceContext::GetData", result);
            }
            return result;
        }
    }

    public unsafe void UpdateSubresource(nint resource, ReadOnlySpan<byte> data, uint subresource = 0, uint rowPitch = 0, uint depthPitch = 0)
    {
        fixed (byte* pointer = data)
        {
            D3D11Com.GetDelegate<UpdateSubresourceDelegate>(_self, 48)(
                _self,
                resource,
                subresource,
                0,
                (nint)pointer,
                rowPitch,
                depthPitch);
        }
    }

    public unsafe void UpdateSubresource(nint resource, ReadOnlySpan<byte> data, in D3D11Box box, uint subresource = 0, uint rowPitch = 0, uint depthPitch = 0)
    {
        D3D11Box localBox = box;
        fixed (byte* pointer = data)
        {
            D3D11Com.GetDelegate<UpdateSubresourceDelegate>(_self, 48)(
                _self,
                resource,
                subresource,
                (nint)(&localBox),
                (nint)pointer,
                rowPitch,
                depthPitch);
        }
    }

    public void CopyResource(nint destination, nint source)
    {
        D3D11Com.GetDelegate<CopyResourceDelegate>(_self, 47)(_self, destination, source);
    }

    public void ResolveSubresource(nint destination, uint destinationSubresource, nint source, uint sourceSubresource, D3D11Format format)
    {
        // ID3D11DeviceContext slot 56 is GetResourceMinLOD. ResolveSubresource
        // is slot 57 in every D3D11 ABI revision. Calling slot 56 with this
        // signature corrupts the native call frame and can terminate UWP.
        D3D11Com.GetDelegate<ResolveSubresourceDelegate>(_self, 57)(
            _self,
            destination,
            destinationSubresource,
            source,
            sourceSubresource,
            format);
    }

    public void Flush()
    {
        D3D11Com.GetDelegate<FlushDelegate>(_self, 111)(_self);
    }

    public void ClearRenderTargetView(nint renderTargetView, float r, float g, float b, float a)
    {
        float* color = stackalloc float[4] { r, g, b, a };
        D3D11Com.GetDelegate<ClearRenderTargetViewDelegate>(_self, 50)(_self, renderTargetView, color);
    }

    public void ClearDepthStencilView(nint depthStencilView, uint clearFlags, float depth, byte stencil)
    {
        D3D11Com.GetDelegate<ClearDepthStencilViewDelegate>(_self, 53)(_self, depthStencilView, clearFlags, depth, stencil);
    }

    public void ClearUnorderedAccessViewUint(nint unorderedAccessView, uint value)
    {
        uint[] values = { value, value, value, value };
        fixed (uint* pointer = values)
        {
            D3D11Com.GetDelegate<ClearUnorderedAccessViewUintDelegate>(_self, 51)(_self, unorderedAccessView, pointer);
        }
    }

    public void GenerateMips(nint shaderResourceView)
    {
        D3D11Com.GetDelegate<GenerateMipsDelegate>(_self, 54)(_self, shaderResourceView);
    }

    public void OMSetRenderTargets(nint renderTargetView, nint depthStencilView)
    {
        nint* renderTargets = stackalloc nint[1] { renderTargetView };
        D3D11Com.GetDelegate<OMSetRenderTargetsDelegate>(_self, 33)(_self, 1, renderTargets, depthStencilView);
    }

    public void OMSetRenderTargets(ReadOnlySpan<nint> renderTargetViews, nint depthStencilView)
    {
        fixed (nint* renderTargets = renderTargetViews)
        {
            D3D11Com.GetDelegate<OMSetRenderTargetsDelegate>(_self, 33)(
                _self,
                (uint)renderTargetViews.Length,
                renderTargets,
                depthStencilView);
        }
    }

    public void OMSetRenderTargetsAndUnorderedAccessViews(
        ReadOnlySpan<nint> renderTargetViews,
        nint depthStencilView,
        uint uavStartSlot,
        ReadOnlySpan<nint> unorderedAccessViews)
    {
        uint[] initialCounts = new uint[unorderedAccessViews.Length];
        Array.Fill(initialCounts, uint.MaxValue);
        fixed (nint* renderTargets = renderTargetViews)
        fixed (nint* unorderedViews = unorderedAccessViews)
        fixed (uint* counts = initialCounts)
        {
            D3D11Com.GetDelegate<OMSetRenderTargetsAndUnorderedAccessViewsDelegate>(_self, 34)(
                _self,
                (uint)renderTargetViews.Length,
                renderTargets,
                depthStencilView,
                uavStartSlot,
                (uint)unorderedAccessViews.Length,
                unorderedViews,
                counts);
        }
    }

    public void RSSetViewports(ReadOnlySpan<D3D11Viewport> viewports)
    {
        fixed (D3D11Viewport* pointer = viewports)
        {
            D3D11Com.GetDelegate<RSSetViewportsDelegate>(_self, 44)(_self, (uint)viewports.Length, pointer);
        }
    }

    public void RSSetScissorRects(ReadOnlySpan<D3D11Rect> rects)
    {
        fixed (D3D11Rect* pointer = rects)
        {
            D3D11Com.GetDelegate<RSSetScissorRectsDelegate>(_self, 45)(_self, (uint)rects.Length, pointer);
        }
    }

    public void IASetPrimitiveTopology(D3D11PrimitiveTopology topology)
    {
        D3D11Com.GetDelegate<IASetPrimitiveTopologyDelegate>(_self, 24)(_self, topology);
    }

    public void Draw(uint vertexCount, uint startVertex)
    {
        D3D11Com.GetDelegate<DrawDelegate>(_self, 13)(_self, vertexCount, startVertex);
    }

    public void DrawIndexed(uint indexCount, uint startIndex, int baseVertex)
    {
        D3D11Com.GetDelegate<DrawIndexedDelegate>(_self, 12)(_self, indexCount, startIndex, baseVertex);
    }

    public void DrawInstanced(uint vertexCount, uint instanceCount, uint startVertex, uint startInstance)
    {
        D3D11Com.GetDelegate<DrawInstancedDelegate>(_self, 21)(_self, vertexCount, instanceCount, startVertex, startInstance);
    }

    public void DrawIndexedInstanced(uint indexCount, uint instanceCount, uint startIndex, int baseVertex, uint startInstance)
    {
        D3D11Com.GetDelegate<DrawIndexedInstancedDelegate>(_self, 20)(_self, indexCount, instanceCount, startIndex, baseVertex, startInstance);
    }

    public void DrawIndirect(nint argumentBuffer, uint offset)
    {
        D3D11Com.GetDelegate<DrawIndirectDelegate>(_self, 40)(_self, argumentBuffer, offset);
    }

    public void DrawIndexedIndirect(nint argumentBuffer, uint offset)
    {
        D3D11Com.GetDelegate<DrawIndexedIndirectDelegate>(_self, 39)(_self, argumentBuffer, offset);
    }

    public void DrawAuto()
    {
        D3D11Com.GetDelegate<DrawAutoDelegate>(_self, 38)(_self);
    }

    public void SOSetTargets(ReadOnlySpan<nint> buffers, ReadOnlySpan<uint> offsets)
    {
        if (buffers.Length != offsets.Length)
        {
            throw new ArgumentException("Stream-output buffers and offsets must have equal lengths.");
        }

        fixed (nint* bufferPointer = buffers)
        fixed (uint* offsetPointer = offsets)
        {
            D3D11Com.GetDelegate<SOSetTargetsDelegate>(_self, 37)(
                _self,
                (uint)buffers.Length,
                bufferPointer,
                offsetPointer);
        }
    }

    public void DispatchIndirect(nint argumentBuffer, uint offset)
    {
        D3D11Com.GetDelegate<DispatchIndirectDelegate>(_self, 42)(_self, argumentBuffer, offset);
    }

    public void CopySubresourceRegion(nint destination, uint destinationSubresource, uint destinationX, uint destinationY, uint destinationZ, nint source, uint sourceSubresource, in D3D11Box sourceBox)
    {
        D3D11Com.GetDelegate<CopySubresourceRegionDelegate>(_self, 46)(
            _self,
            destination,
            destinationSubresource,
            destinationX,
            destinationY,
            destinationZ,
            source,
            sourceSubresource,
            in sourceBox);
    }

    public void Dispatch(uint groupsX, uint groupsY, uint groupsZ)
    {
        D3D11Com.GetDelegate<DispatchDelegate>(_self, 41)(_self, groupsX, groupsY, groupsZ);
    }

    public void SetShader(Ryujinx.Graphics.Shader.ShaderStage stage, nint shader)
    {
        int index = stage switch
        {
            Ryujinx.Graphics.Shader.ShaderStage.Vertex => 11,
            Ryujinx.Graphics.Shader.ShaderStage.Fragment => 9,
            Ryujinx.Graphics.Shader.ShaderStage.Geometry => 23,
            Ryujinx.Graphics.Shader.ShaderStage.TessellationControl => 60,
            Ryujinx.Graphics.Shader.ShaderStage.TessellationEvaluation => 64,
            Ryujinx.Graphics.Shader.ShaderStage.Compute => 69,
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        D3D11Com.GetDelegate<SetShaderDelegate>(_self, index)(_self, shader, 0, 0);
    }

    public void SetConstantBuffers(Ryujinx.Graphics.Shader.ShaderStage stage, uint startSlot, ReadOnlySpan<nint> buffers)
    {
        SetViews(GetConstantBufferIndex(stage), startSlot, buffers);
    }

    public void SetShaderResources(Ryujinx.Graphics.Shader.ShaderStage stage, uint startSlot, ReadOnlySpan<nint> views)
    {
        SetViews(GetShaderResourceIndex(stage), startSlot, views);
    }

    public void SetSamplers(Ryujinx.Graphics.Shader.ShaderStage stage, uint startSlot, ReadOnlySpan<nint> samplers)
    {
        SetViews(GetSamplerIndex(stage), startSlot, samplers);
    }

    // Getters AddRef the returned COM object; the caller must release it.
    internal nint GetBlitVertexConstantBuffer() => GetSingleView(72, 13); // VSGetConstantBuffers
    internal nint GetBlitPixelConstantBuffer() => GetSingleView(77, 13); // PSGetConstantBuffers
    internal nint GetBlitPixelSampler() => GetSingleView(75, 15); // PSGetSamplers
    private nint GetSingleView(int index, uint slot)
    {
        nint view = 0;
        D3D11Com.GetDelegate<SetViewsDelegate>(_self, index)(_self, slot, 1, &view);
        return view;
    }

    public void SetUnorderedAccessViews(uint startSlot, ReadOnlySpan<nint> views)
    {
        fixed (nint* pointer = views)
        {
            D3D11Com.GetDelegate<SetUnorderedAccessViewsDelegate>(_self, 68)(
                _self,
                startSlot,
                (uint)views.Length,
                pointer,
                0);
        }
    }

    public void IASetVertexBuffers(uint startSlot, ReadOnlySpan<nint> buffers, ReadOnlySpan<uint> strides, ReadOnlySpan<uint> offsets)
    {
        if (buffers.Length != strides.Length || buffers.Length != offsets.Length)
        {
            throw new ArgumentException("Vertex buffer arrays must have equal lengths.");
        }

        fixed (nint* bufferPointer = buffers)
        fixed (uint* stridePointer = strides)
        fixed (uint* offsetPointer = offsets)
        {
            D3D11Com.GetDelegate<IASetVertexBuffersDelegate>(_self, 18)(
                _self,
                startSlot,
                (uint)buffers.Length,
                bufferPointer,
                stridePointer,
                offsetPointer);
        }
    }

    public void IASetIndexBuffer(nint buffer, D3D11Format format, uint offset)
    {
        D3D11Com.GetDelegate<IASetIndexBufferDelegate>(_self, 19)(_self, buffer, format, offset);
    }

    public void IASetInputLayout(nint inputLayout)
    {
        D3D11Com.GetDelegate<IASetInputLayoutDelegate>(_self, 17)(_self, inputLayout);
    }

    public void RSSetState(nint rasterizerState)
    {
        D3D11Com.GetDelegate<RSSetStateDelegate>(_self, 43)(_self, rasterizerState);
    }

    public void OMSetBlendState(nint blendState, float blendFactorR, float blendFactorG, float blendFactorB, float blendFactorA, uint sampleMask)
    {
        float[] factor = { blendFactorR, blendFactorG, blendFactorB, blendFactorA };
        fixed (float* pointer = factor)
        {
            D3D11Com.GetDelegate<OMSetBlendStateDelegate>(_self, 35)(_self, blendState, pointer, sampleMask);
        }
    }

    public void OMSetDepthStencilState(nint depthStencilState, uint stencilRef)
    {
        D3D11Com.GetDelegate<OMSetDepthStencilStateDelegate>(_self, 36)(_self, depthStencilState, stencilRef);
    }

    private void SetViews(int index, uint startSlot, ReadOnlySpan<nint> views)
    {
        fixed (nint* pointer = views)
        {
            D3D11Com.GetDelegate<SetViewsDelegate>(_self, index)(
                _self,
                startSlot,
                (uint)views.Length,
                pointer);
        }
    }

    private static int GetConstantBufferIndex(Ryujinx.Graphics.Shader.ShaderStage stage) => stage switch
    {
        Ryujinx.Graphics.Shader.ShaderStage.Vertex => 7,
        Ryujinx.Graphics.Shader.ShaderStage.Fragment => 16,
        Ryujinx.Graphics.Shader.ShaderStage.Geometry => 22,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationControl => 62,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationEvaluation => 66,
        Ryujinx.Graphics.Shader.ShaderStage.Compute => 71,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static int GetShaderResourceIndex(Ryujinx.Graphics.Shader.ShaderStage stage) => stage switch
    {
        Ryujinx.Graphics.Shader.ShaderStage.Vertex => 25,
        Ryujinx.Graphics.Shader.ShaderStage.Fragment => 8,
        Ryujinx.Graphics.Shader.ShaderStage.Geometry => 31,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationControl => 59,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationEvaluation => 63,
        Ryujinx.Graphics.Shader.ShaderStage.Compute => 67,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static int GetSamplerIndex(Ryujinx.Graphics.Shader.ShaderStage stage) => stage switch
    {
        Ryujinx.Graphics.Shader.ShaderStage.Vertex => 26,
        Ryujinx.Graphics.Shader.ShaderStage.Fragment => 10,
        Ryujinx.Graphics.Shader.ShaderStage.Geometry => 32,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationControl => 61,
        Ryujinx.Graphics.Shader.ShaderStage.TessellationEvaluation => 65,
        Ryujinx.Graphics.Shader.ShaderStage.Compute => 70,
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(nint self, nint resource, uint subresource, D3D11MapType mapType, D3D11MapFlags mapFlags, out D3D11MappedSubresource result);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(nint self, nint resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void BeginDelegate(nint self, nint query);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void EndDelegate(nint self, nint query);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDataDelegate(nint self, nint query, nint data, uint dataSize, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UpdateSubresourceDelegate(nint self, nint resource, uint subresource, nint box, nint sourceData, uint rowPitch, uint depthPitch);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(nint self, nint destination, nint source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearRenderTargetViewDelegate(nint self, nint view, float* color);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearDepthStencilViewDelegate(nint self, nint view, uint flags, float depth, byte stencil);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ClearUnorderedAccessViewUintDelegate(nint self, nint view, uint* values);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GenerateMipsDelegate(nint self, nint view);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsDelegate(nint self, uint count, nint* renderTargets, nint depthStencilView);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetRenderTargetsAndUnorderedAccessViewsDelegate(
        nint self,
        uint renderTargetCount,
        nint* renderTargets,
        nint depthStencilView,
        uint uavStartSlot,
        uint uavCount,
        nint* unorderedAccessViews,
        uint* initialCounts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RSSetViewportsDelegate(nint self, uint count, D3D11Viewport* viewports);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RSSetScissorRectsDelegate(nint self, uint count, D3D11Rect* rects);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetPrimitiveTopologyDelegate(nint self, D3D11PrimitiveTopology topology);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawDelegate(nint self, uint vertexCount, uint startVertex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedDelegate(nint self, uint indexCount, uint startIndex, int baseVertex);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DispatchDelegate(nint self, uint groupsX, uint groupsY, uint groupsZ);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawInstancedDelegate(nint self, uint vertexCount, uint instanceCount, uint startVertex, uint startInstance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedInstancedDelegate(nint self, uint indexCount, uint instanceCount, uint startIndex, int baseVertex, uint startInstance);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndirectDelegate(nint self, nint argumentBuffer, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawIndexedIndirectDelegate(nint self, nint argumentBuffer, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DispatchIndirectDelegate(nint self, nint argumentBuffer, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void DrawAutoDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SOSetTargetsDelegate(nint self, uint count, nint* buffers, uint* offsets);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void ResolveSubresourceDelegate(nint self, nint destination, uint destinationSubresource, nint source, uint sourceSubresource, D3D11Format format);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void FlushDelegate(nint self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopySubresourceRegionDelegate(nint self, nint destination, uint destinationSubresource, uint destinationX, uint destinationY, uint destinationZ, nint source, uint sourceSubresource, in D3D11Box sourceBox);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetShaderDelegate(nint self, nint shader, nint classInstances, uint classInstanceCount);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetViewsDelegate(nint self, uint startSlot, uint count, nint* views);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void SetUnorderedAccessViewsDelegate(nint self, uint startSlot, uint count, nint* views, nint initialCounts);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetVertexBuffersDelegate(nint self, uint startSlot, uint count, nint* buffers, uint* strides, uint* offsets);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetIndexBufferDelegate(nint self, nint buffer, D3D11Format format, uint offset);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void IASetInputLayoutDelegate(nint self, nint inputLayout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RSSetStateDelegate(nint self, nint rasterizerState);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetBlendStateDelegate(nint self, nint blendState, float* blendFactor, uint sampleMask);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void OMSetDepthStencilStateDelegate(nint self, nint depthStencilState, uint stencilRef);
}
