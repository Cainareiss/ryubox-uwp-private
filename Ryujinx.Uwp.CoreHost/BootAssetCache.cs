using System;
using System.IO;

namespace Ryujinx.Uwp.CoreHost;

// Fixed public filenames are read by the frontend. Never truncate a published
// image while its decoder is reading, or accept an old partial file as success.
internal static class BootAssetCache
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly object PublishLock = new();

    internal static void Publish(Stream input, string destination)
    {
        string staging = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream output = new(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[64 * 1024];
                int total = 0;
                int count;
                while ((count = input.Read(buffer)) > 0)
                {
                    total = checked(total + count);
                    if (total > MaximumBytes) throw new InvalidDataException("Boot asset exceeds 16 MiB.");
                    output.Write(buffer, 0, count);
                }
                if (total == 0) throw new InvalidDataException("Empty boot asset.");
                output.Flush(true);
            }

            lock (PublishLock)
            {
                if (File.Exists(destination) && EqualFiles(staging, destination)) return;
                // Replace atomically; if an old decoder forbids replacement,
                // report failure to the caller instead of claiming stale bytes
                // are the requested asset. The previous complete file survives.
                File.Move(staging, destination, overwrite: true);
            }
        }
        finally
        {
            File.Delete(staging);
        }
    }

    private static bool EqualFiles(string leftPath, string rightPath)
    {
        using FileStream left = File.OpenRead(leftPath);
        using FileStream right = new(rightPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (left.Length != right.Length) return false;
        Span<byte> a = stackalloc byte[4096];
        Span<byte> b = stackalloc byte[4096];
        int read;
        while ((read = left.Read(a)) != 0)
        {
            right.ReadExactly(b[..read]);
            if (!a[..read].SequenceEqual(b[..read])) return false;
        }
        return true;
    }
}
