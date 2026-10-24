using System;
using System.IO;
using System.Runtime.InteropServices;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;

namespace Ryujinx.Graphics.D3D12;

/// <summary>
/// Direct SPIR-V to DXIL compiler leveraging Mesa's spirv_to_dxil library via Ryujinx.Uwp.Native.
/// Bypasses intermediate HLSL text generation and D3DCompile when available.
/// </summary>
public static unsafe class D3D12DxilCompiler
{
    private const string Library = "Ryujinx.Uwp.Native.dll";
    [DllImport(Library, EntryPoint = "RyuboxD3D12_GetDxilStatus", CallingConvention = CallingConvention.StdCall)]
    private static extern int GetDxilStatus();

    [DllImport(Library, EntryPoint = "RyuboxD3D12_TranslateSpirvToDxil", CallingConvention = CallingConvention.StdCall)]
    private static extern int TranslateSpirvToDxil(
        uint* words,
        nuint wordCount,
        uint stage,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        out nint outData,
        out ulong outBytes,
        out nint outHandle);

    [DllImport(Library, EntryPoint = "RyuboxD3D12_FreeDxil", CallingConvention = CallingConvention.StdCall)]
    private static extern int FreeDxil(nint handle);

    private static int _isAvailable = -1; // -1: uninitialized, 0: no, 1: yes

    public static bool IsAvailable
    {
        get
        {
            if (_isAvailable == -1)
            {
                try
                {
                    nint lib = NativeLibrary.Load(Library);
                    bool hasExport = NativeLibrary.TryGetExport(lib, "RyuboxD3D12_TranslateSpirvToDxil", out _);
                    NativeLibrary.Free(lib);
                    _isAvailable = hasExport && GetDxilStatus() == 0 ? 1 : 0;
                }
                catch
                {
                    _isAvailable = 0;
                }
            }
            return _isAvailable == 1;
        }
    }

    public static bool TryTranslate(ShaderSource shader, out byte[] dxilBinary)
    {
        dxilBinary = null;
        if (!IsAvailable) return false;
        if (shader.Language != TargetLanguage.Spirv || shader.BinaryCode == null || shader.BinaryCode.Length < 20)
            return false;

        uint stage = shader.Stage switch
        {
            // These values are Mesa's dxil_spirv_shader_stage enum, not
            // Ryujinx's ShaderStage enum. Keep the ABI mapping explicit.
            ShaderStage.Vertex => 0,
            ShaderStage.TessellationControl => 1,
            ShaderStage.TessellationEvaluation => 2,
            ShaderStage.Geometry => 3,
            ShaderStage.Fragment => 4,
            ShaderStage.Compute => 5,
            _ => uint.MaxValue
        };

        if (stage == uint.MaxValue) return false;

        byte[] spv = shader.BinaryCode;
        if (spv.Length % 4 != 0) return false;

        fixed (byte* pSpv = spv)
        {
            int hr = TranslateSpirvToDxil(
                (uint*)pSpv,
                (nuint)(spv.Length / 4),
                stage,
                "main",
                out nint outData,
                out ulong outBytes,
                out nint outHandle);

            if (hr < 0 || outData == 0 || outBytes == 0 || outBytes > 4 * 1024 * 1024)
            {
                if (outHandle != 0) FreeDxil(outHandle);
                return false;
            }

            try
            {
                dxilBinary = new byte[outBytes];
                Marshal.Copy(outData, dxilBinary, 0, (int)outBytes);
                return true;
            }
            finally
            {
                FreeDxil(outHandle);
            }
        }
    }
}
