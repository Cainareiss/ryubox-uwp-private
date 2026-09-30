using System;
using System.IO;
using Ryujinx.HLE.FileSystem;

// Register a path that does not exist: success must come from the handle,
// never from reopening a path with desktop permissions.
string directory = Path.Combine(Path.GetTempPath(), "RyuboxHandleProbe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
string original = Path.Combine(directory, "source.bin");
string brokered = Path.Combine(directory, "authorized-only.nsp");
byte[] expected = { 0x50, 0x46, 0x53, 0x30, 1, 2, 3, 4 };
try
{
    File.WriteAllBytes(original, expected);
    using (var caller = File.OpenHandle(original, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        HostFileStream.RegisterBrokeredReadHandle(brokered, caller.DangerousGetHandle());
    }
    using (var first = HostFileStream.OpenRead(brokered))
    {
        byte[] actual = new byte[expected.Length];
        first.ReadExactly(actual);
        if (!actual.AsSpan().SequenceEqual(expected)) throw new Exception("Read after caller close failed");
        first.Position = 0;
    }
    using (var second = HostFileStream.OpenRead(brokered))
    {
        HostFileStream.ClearBrokeredReadHandle();
        second.Position = 0;
        if (second.ReadByte() != expected[0]) throw new Exception("Open stream lost its owned handle");
    }
    try
    {
        using var unexpected = HostFileStream.OpenRead(brokered);
        throw new Exception("Session handle remained registered after cleanup");
    }
    catch (FileNotFoundException) { }
    Console.WriteLine("PASS: brokered-only read, caller lifetime, reopen, live stream after cleanup, registry cleanup");
}
finally
{
    HostFileStream.ClearBrokeredReadHandle();
    File.Delete(original);
    Directory.Delete(directory);
}
