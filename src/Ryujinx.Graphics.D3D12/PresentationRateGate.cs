using System;
using System.Diagnostics;

namespace Ryujinx.Graphics.D3D12;

/// <summary>Single presentation-owner rate gate; never sleeps or changes the guest clock.</summary>
public sealed class PresentationRateGate
{
    private readonly long _interval;
    private readonly long _tolerance;
    private bool _initialized;
    private long _next;

    public int FramesPerSecond { get; }

    public PresentationRateGate(int framesPerSecond, long frequency = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(framesPerSecond);
        if (frequency == 0) frequency = Stopwatch.Frequency;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        FramesPerSecond = framesPerSecond;
        _interval = framesPerSecond == 0 ? 0 : Math.Max(1, frequency / framesPerSecond);
        // Sub-ms arrival jitter must not turn a nominal 30 Hz producer into 15 Hz.
        _tolerance = Math.Min(frequency / 2000, _interval / 10);
    }

    public void Reset() { _initialized = false; _next = 0; }

    public bool TryAcquire(long now)
    {
        if (_interval == 0) return true;
        if (_initialized && now < _next - _tolerance) return false;
        long scheduled = _initialized ? _next + _interval : now + _interval;
        _next = scheduled <= now ? now + _interval : scheduled;
        _initialized = true;
        return true;
    }
}
