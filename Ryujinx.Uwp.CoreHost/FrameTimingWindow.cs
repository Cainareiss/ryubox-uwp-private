using System;
using System.Globalization;

namespace Ryujinx.Uwp.CoreHost;

// GPU-owner-only aggregate; fixed storage, no allocations on the per-frame path.
internal sealed class FrameTimingWindow
{
    private readonly long _frequency;
    private readonly int[] _intervals = new int[1025];
    private long _lastFrame;
    private bool _hasFrame;
    private long _intervalCount;
    private long _waitTicks, _processTicks, _presentTicks;
    private long _maxProcessTicks, _maxPresentTicks, _maxIntervalTicks;
    private long _processCount, _presentCount;

    public FrameTimingWindow(long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
    }

    public void AddFifoWait(long ticks) => _waitTicks += Math.Max(0, ticks);
    public void AddProcess(long ticks)
    {
        _processTicks += ticks; _processCount++;
        _maxProcessTicks = Math.Max(_maxProcessTicks, ticks);
    }
    public void AddPresent(long ticks, long timestamp)
    {
        _presentTicks += ticks; _presentCount++;
        _maxPresentTicks = Math.Max(_maxPresentTicks, ticks);
        if (_hasFrame)
        {
            long interval = Math.Max(0, timestamp - _lastFrame);
            int bucket = (int)Math.Min(1024, Math.Ceiling(interval * 1000.0 / _frequency));
            _intervals[bucket]++; _intervalCount++;
            _maxIntervalTicks = Math.Max(_maxIntervalTicks, interval);
        }
        _lastFrame = timestamp; _hasFrame = true;
    }

    public string SampleAndReset()
    {
        long target = (long)Math.Ceiling(_intervalCount * 0.95);
        long count = 0; int p95 = 0;
        if (target > 0)
            for (; p95 < 1024; p95++)
            {
                count += _intervals[p95];
                if (count >= target) break;
            }
        string result = string.Format(CultureInfo.InvariantCulture,
            "fifoWaitMs={0:F3}; processTotalMs={1:F3}; processCount={2}; processMaxMs={3:F3}; " +
            "presentCallTotalMs={4:F3}; presentCallCount={5}; presentCallMaxMs={6:F3}; " +
            "guestCallbackIntervalP95UpperMs={7}; guestCallbackIntervalMaxMs={8:F3}; intervalSamples={9}",
            Milliseconds(_waitTicks), Milliseconds(_processTicks), _processCount, Milliseconds(_maxProcessTicks),
            Milliseconds(_presentTicks), _presentCount, Milliseconds(_maxPresentTicks),
            p95 == 1024 ? ">1023" : p95.ToString(CultureInfo.InvariantCulture),
            Milliseconds(_maxIntervalTicks), _intervalCount);
        Array.Clear(_intervals);
        _intervalCount = _waitTicks = _processTicks = _presentTicks = 0;
        _maxProcessTicks = _maxPresentTicks = _maxIntervalTicks = 0;
        _processCount = _presentCount = 0;
        // Preserve last callback across sample boundaries, including long stalls.
        return result;
    }

    private double Milliseconds(long ticks) => ticks * 1000.0 / _frequency;
}
