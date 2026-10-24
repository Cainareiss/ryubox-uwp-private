using System;
using Ryujinx.Graphics.D3D12;
using Ryujinx.Uwp.CoreHost;

const long frequency = 60_000_000;
static void Require(bool value, string message)
{
    if (!value) throw new InvalidOperationException(message);
}
foreach (int rate in new[] { 15, 30, 60, 120 })
{
    PresentationRateGate gate = new(30, frequency);
    int accepted = 0;
    for (int i = 0; i < rate * 10; i++)
    {
        long now = frequency + i * (frequency / rate) + (i % 2 == 0 ? 10_000 : -10_000);
        if (gate.TryAcquire(now)) accepted++;
    }
    Require(accepted == Math.Min(30, rate) * 10, $"{rate} Hz with jitter: accepted={accepted}");
    Console.WriteLine($"PASS {rate} Hz producer with arrival jitter -> {accepted} new frames/10s");
}

PresentationRateGate stalled = new(30, frequency);
Require(stalled.TryAcquire(0), "First frame at timestamp zero denied");
Require(!stalled.TryAcquire(0), "Duplicate timestamp accepted");
Require(stalled.TryAcquire(frequency * 3), "Late frame denied");
Require(!stalled.TryAcquire(frequency * 3 + 1), "Late frame caused catch-up burst");
stalled.Reset();
Require(stalled.TryAcquire(1), "Reset retained prior session deadline");
PresentationRateGate unlimited = new(0, frequency);
for (int i = 0; i < 100; i++) Require(unlimited.TryAcquire(0), "Unlimited producer throttled");
Console.WriteLine("PASS zero timestamp, no catch-up burst, session reset, unlimited mode");

FrameTimingWindow timings = new(1_000_000);
timings.AddFifoWait(5000);
timings.AddProcess(10_000);
timings.AddPresent(2000, 0);
timings.AddPresent(3000, 33_333);
string first = timings.SampleAndReset();
Require(first.Contains("fifoWaitMs=5.000") && first.Contains("processMaxMs=10.000") &&
    first.Contains("presentCallTotalMs=5.000") && first.Contains("guestCallbackIntervalP95UpperMs=34"), first);
timings.AddPresent(1000, 2_000_000);
string second = timings.SampleAndReset();
Require(second.Contains("processCount=0") && second.Contains("guestCallbackIntervalP95UpperMs=>1023") &&
    second.Contains("guestCallbackIntervalMaxMs=1966.667"), second);
Require(timings.SampleAndReset().Contains("intervalSamples=0"), "Timing totals not reset");
Console.WriteLine("PASS timing aggregates, percentile upper bound, long stall across samples, reset");
Require(PresentationCounter.Delta(10, 7) == 3, "Wrong native counter delta");
Require(PresentationCounter.Delta(2, 1000) == 0, "Context reset generated fictitious frames");
Require(PresentationCounter.Delta(0, ulong.MaxValue) == 0, "Unsigned counter reset underflow");
Console.WriteLine("PASS presentation deltas including context recreation/reset; no fictitious FPS");
