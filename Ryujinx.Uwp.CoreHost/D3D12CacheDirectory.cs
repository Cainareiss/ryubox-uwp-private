using System;
using System.IO;

namespace Ryujinx.Uwp.CoreHost;

internal static class D3D12CacheDirectory
{
    internal static string Prepare(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
            throw new ArgumentException("Missing application data directory.", nameof(baseDirectory));
        string directory = Path.Combine(Path.GetFullPath(baseDirectory), "D3D12ShaderCache");
        Directory.CreateDirectory(directory);
        // Verify access before advertising a cache that silently cannot persist.
        string probe = Path.Combine(directory, ".probe-" + Guid.NewGuid().ToString("N"));
        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.DeleteOnClose))
        {
            stream.WriteByte(0x5a);
            stream.Flush();
            stream.Position = 0;
            if (stream.ReadByte() != 0x5a) throw new IOException("Shader cache probe readback failed.");
        }
        return directory;
    }
}
