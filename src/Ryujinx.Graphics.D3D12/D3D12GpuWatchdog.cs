using System;
using System.Diagnostics;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Graphics.D3D12;

public enum GpuStallReason
{
    Unknown,
    CpuWait,
    GuestSyncpointWait,
    GpuQueueWait,
    FenceWait,
    PresentWait,
}

public readonly record struct GpuOperationRecord(
    long Timestamp,
    string CommandType,
    int Subchannel,
    int Method,
    int Argument,
    ulong GuestAddress,
    ulong ResourceId);

/// <summary>
/// D3D12 GPU hang watchdog. Monitors for no-progress stalls (no present >= 5s,
/// no completed dispatch >= 3s, sync waiting), captures a lock-free ring buffer
/// of the last 256 operations, and dumps diagnostic stall classifications.
/// </summary>
public sealed class D3D12GpuWatchdog : IDisposable
{
    private const int RingCapacity = 256;
    private readonly GpuOperationRecord[] _ring = new GpuOperationRecord[RingCapacity];
    private long _operationIndex;
    private long _lastPresentTime = Stopwatch.GetTimestamp();
    private long _lastDispatchTime = Stopwatch.GetTimestamp();
    private volatile bool _waitingOnSync;
    private volatile GpuStallReason _currentWaitReason = GpuStallReason.Unknown;
    private ulong _currentWaitFence;
    private uint _currentWaitSyncpoint;
    private uint _currentWaitThreshold;

    private readonly Thread _watchdogThread;
    private volatile bool _running = true;
    private bool _triggered;
    private readonly D3D12Renderer _renderer;

    public D3D12GpuWatchdog(D3D12Renderer renderer)
    {
        _renderer = renderer;
        _watchdogThread = new Thread(WatchdogLoop)
        {
            Name = "D3D12 GPU Watchdog",
            IsBackground = true,
        };
        _watchdogThread.Start();
    }

    public void RecordOperation(string commandType, int subchannel = 0, int method = 0, int argument = 0, ulong address = 0, ulong resourceId = 0)
    {
        long index = Interlocked.Increment(ref _operationIndex) - 1;
        int slot = (int)(index % RingCapacity);
        _ring[slot] = new GpuOperationRecord(Stopwatch.GetTimestamp(), commandType, subchannel, method, argument, address, resourceId);
    }

    public void NotifyPresent()
    {
        Volatile.Write(ref _lastPresentTime, Stopwatch.GetTimestamp());
    }

    public void NotifyDispatch()
    {
        Volatile.Write(ref _lastDispatchTime, Stopwatch.GetTimestamp());
    }

    public void EnterWait(GpuStallReason reason, ulong fence = 0, uint syncpoint = 0, uint threshold = 0)
    {
        _currentWaitReason = reason;
        _currentWaitFence = fence;
        _currentWaitSyncpoint = syncpoint;
        _currentWaitThreshold = threshold;
        _waitingOnSync = true;
    }

    public void ExitWait()
    {
        _waitingOnSync = false;
        _currentWaitReason = GpuStallReason.Unknown;
    }

    private void WatchdogLoop()
    {
        while (_running)
        {
            Thread.Sleep(1000);
            if (!_running || !D3D12Configuration.GpuWatchdog) continue;

            long now = Stopwatch.GetTimestamp();
            double secondsSincePresent = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastPresentTime), now).TotalSeconds;
            double secondsSinceDispatch = Stopwatch.GetElapsedTime(Volatile.Read(ref _lastDispatchTime), now).TotalSeconds;

            // NO-PROGRESS condition: No present >= 5s AND no completed dispatch >= 3s AND queue/syncpoint still waiting
            if (secondsSincePresent >= 5.0 && secondsSinceDispatch >= 3.0 && _waitingOnSync && !_triggered)
            {
                _triggered = true;
                DumpStallDiagnostics(secondsSincePresent, secondsSinceDispatch);
            }
            else if (_triggered && (!_waitingOnSync || secondsSincePresent < 2.0))
            {
                // Reset trigger if progress resumed
                _triggered = false;
            }
        }
    }

    private void DumpStallDiagnostics(double secondsSincePresent, double secondsSinceDispatch)
    {
        try
        {
            var devInfo = _renderer.Device.GetInfo();
            GpuStallReason stallReason = _currentWaitReason;
            string stallName = stallReason switch
            {
                GpuStallReason.CpuWait => "CPU_WAIT",
                GpuStallReason.GuestSyncpointWait => "GUEST_SYNCPOINT_WAIT",
                GpuStallReason.GpuQueueWait => "GPU_QUEUE_WAIT",
                GpuStallReason.FenceWait => "FENCE_WAIT",
                GpuStallReason.PresentWait => "PRESENT_WAIT",
                _ => "UNKNOWN",
            };

            string header = $"[D3D12 WATCHDOG] STALL DETECTED! Classification={stallName}; " +
                $"NoPresentFor={secondsSincePresent:F2}s; NoDispatchFor={secondsSinceDispatch:F2}s; " +
                $"FenceSubmitted={devInfo.Submitted}; FenceCompleted={devInfo.Completed}; " +
                $"DeviceRemovedReason=0x{devInfo.DeviceRemovedReason:X8}; " +
                $"WaitFence={_currentWaitFence}; Syncpoint={_currentWaitSyncpoint}; Threshold={_currentWaitThreshold}";

            RuntimeDiagnosticLog.Write("gpu-watchdog", header);
            OutputDebugString(header + "\n");

            // Dump ring buffer
            long currentOp = Volatile.Read(ref _operationIndex);
            long startOp = Math.Max(0, currentOp - RingCapacity);
            RuntimeDiagnosticLog.Write("gpu-watchdog", $"--- Last {currentOp - startOp} GPU Operations ---");
            for (long i = startOp; i < currentOp; i++)
            {
                var op = _ring[i % RingCapacity];
                if (op.Timestamp == 0) continue;
                double msAgo = Stopwatch.GetElapsedTime(op.Timestamp).TotalMilliseconds;
                RuntimeDiagnosticLog.Write("gpu-watchdog",
                    $"  [-{msAgo:F1}ms] Cmd={op.CommandType} Subchannel={op.Subchannel} Method=0x{op.Method:X3} Arg=0x{op.Argument:X8} Addr=0x{op.GuestAddress:X16} ResId={op.ResourceId}");
            }
            RuntimeDiagnosticLog.Write("gpu-watchdog", "--- End Ring Buffer Dump ---");
        }
        catch (Exception ex)
        {
            RuntimeDiagnosticLog.Write("gpu-watchdog", $"Watchdog dump error: {ex.Message}");
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Ansi)]
    private static extern void OutputDebugString(string lpOutputString);

    public void Dispose()
    {
        _running = false;
    }
}
