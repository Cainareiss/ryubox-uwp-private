using Ryujinx.Common.Memory;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Common.Logging;

// Wall-clock categories are explicitly inclusive: FIFO busy includes compilation
// and waits performed by that thread. They must not be summed as GPU timings.
public static class RuntimeGpuTelemetry
{
    private static long _fifoTicks, _queryTicks, _syncpointTicks, _compilerTicks, _frames;
    private static int _compilers;
    public static void RecordFifo(long start) => Interlocked.Add(ref _fifoTicks, Stopwatch.GetTimestamp() - start);
    public static void RecordQuery(long start) => Interlocked.Add(ref _queryTicks, Stopwatch.GetTimestamp() - start);
    public static void RecordSyncpoint(long start) => Interlocked.Add(ref _syncpointTicks, Stopwatch.GetTimestamp() - start);
    public static void CompilerBegin() => Interlocked.Increment(ref _compilers);
    public static void CompilerEnd(long start)
    {
        Interlocked.Add(ref _compilerTicks, Stopwatch.GetTimestamp() - start);
        Interlocked.Decrement(ref _compilers);
    }
    public static void Frame() => Interlocked.Increment(ref _frames);
    public static string Snapshot()
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        return $"gpuThreadBusyInclusiveMs={Interlocked.Read(ref _fifoTicks) * ms:F2} " +
            $"hostGpuQueryWaitMs={Interlocked.Read(ref _queryTicks) * ms:F2} " +
            $"guestSyncpointWaitMs={Interlocked.Read(ref _syncpointTicks) * ms:F2} " +
            $"shaderCompileWallMs={Interlocked.Read(ref _compilerTicks) * ms:F2} " +
            $"shaderCompileActive={Volatile.Read(ref _compilers)} frameId={Interlocked.Read(ref _frames)} " +
            $"memoryPressureState={RuntimeMemoryBudget.Pressure}";
    }
    public static void Reset()
    {
        Interlocked.Exchange(ref _fifoTicks, 0);
        Interlocked.Exchange(ref _queryTicks, 0);
        Interlocked.Exchange(ref _syncpointTicks, 0);
        Interlocked.Exchange(ref _compilerTicks, 0);
        Interlocked.Exchange(ref _frames, 0);
    }
}
