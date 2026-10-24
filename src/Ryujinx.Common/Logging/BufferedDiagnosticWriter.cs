using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace Ryujinx.Common.Logging;

/// <summary>Bounded, process-wide batching for diagnostic files. Fatal writes
/// are synchronous barriers; paths and context are captured by the producer.</summary>
public static class BufferedDiagnosticWriter
{
    private static readonly object QueueLock = new();
    private static readonly object DrainLock = new();
    private static readonly Queue<(string Path, string Line, bool Rotate)> Pending = new();
    private static readonly Timer DrainTimer = new(_ => Flush(), null, 250, 250);
    private static int _characters;
    private static long _dropped;
    public static long DroppedRecords => Interlocked.Read(ref _dropped);

    static BufferedDiagnosticWriter()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
    }

    public static void Write(string path, string line, bool rotate = false, bool critical = false)
    {
        if (critical)
        {
            lock (DrainLock)
            {
                Flush();
                Append(path, line, rotate);
            }
            return;
        }
        lock (QueueLock)
        {
            // <=2 MiB of UTF-16 text plus a bounded 1024-record metadata set.
            if (Pending.Count >= 1024 || line.Length > 1024 * 1024 - _characters)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            Pending.Enqueue((path, line, rotate));
            _characters += line.Length;
        }
    }

    public static void Flush()
    {
        lock (DrainLock)
        {
            Dictionary<(string Path, bool Rotate), StringBuilder> batches = new();
            lock (QueueLock)
            {
                while (Pending.TryDequeue(out var item))
                {
                    var key = (item.Path, item.Rotate);
                    if (!batches.TryGetValue(key, out var text)) batches[key] = text = new StringBuilder();
                    text.Append(item.Line);
                }
                _characters = 0;
            }
            foreach (var batch in batches) Append(batch.Key.Path, batch.Value.ToString(), batch.Key.Rotate);
        }
    }

    private static void Append(string path, string line, bool rotate)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (rotate && File.Exists(path) && new FileInfo(path).Length > 4 * 1024 * 1024)
                File.Move(path, path + ".previous", true);
            File.AppendAllText(path, line, Encoding.UTF8);
        }
        catch { Interlocked.Increment(ref _dropped); /* Diagnostics must not fault guest work. */ }
    }
}
