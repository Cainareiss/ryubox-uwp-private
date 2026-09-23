using Ryujinx.Graphics.Shader;
using System;
using System.Collections.Generic;
using System.IO;

namespace Ryujinx.Graphics.D3D11;

internal static class D3D11ProgramBinary
{
    private const uint Magic = 0x31425844; // DXB1
    private const uint Version = 1;

    public static byte[] Pack(IReadOnlyDictionary<ShaderStage, byte[]> stages)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(stages.Count);

        foreach ((ShaderStage stage, byte[] bytecode) in stages)
        {
            writer.Write((byte)stage);
            writer.Write(bytecode.Length);
            writer.Write(bytecode);
        }

        writer.Flush();
        return stream.ToArray();
    }

    public static bool TryUnpack(ReadOnlySpan<byte> binary, out List<(ShaderStage Stage, byte[] Bytecode)> stages)
    {
        stages = new List<(ShaderStage Stage, byte[] Bytecode)>();

        if (binary.Length < sizeof(uint) * 2 + sizeof(int))
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(binary.ToArray(), writable: false);
            using BinaryReader reader = new(stream);

            if (reader.ReadUInt32() != Magic || reader.ReadUInt32() != Version)
            {
                return false;
            }

            int count = reader.ReadInt32();
            if (count < 0 || count > (int)ShaderStage.Count)
            {
                return false;
            }

            for (int index = 0; index < count; index++)
            {
                ShaderStage stage = (ShaderStage)reader.ReadByte();
                int length = reader.ReadInt32();

                if (stage >= ShaderStage.Count || length <= 0 || length > stream.Length - stream.Position)
                {
                    return false;
                }

                stages.Add((stage, reader.ReadBytes(length)));
            }

            return stream.Position == stream.Length;
        }
        catch (EndOfStreamException)
        {
            stages.Clear();
            return false;
        }
    }
}
