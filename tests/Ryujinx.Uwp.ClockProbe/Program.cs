using Ryujinx.Cpu;

AppContext.SetSwitch("Ryujinx.Uwp.SafeMonotonicClock", true);

TickSource clock = new(19_200_000);
long regressions = 0;

Parallel.For(0, 8, _ =>
{
    ulong previous = 0;

    for (int index = 0; index < 100_000; index++)
    {
        ulong current = clock.Counter;
        if (current < previous)
        {
            Interlocked.Increment(ref regressions);
        }

        previous = current;
    }
});

if (regressions != 0)
{
    throw new InvalidOperationException($"Guest clock regressed {regressions} times under contention.");
}

ulong beforeSuspend = clock.Counter;
clock.Suspend();
Thread.Sleep(30);
ulong whileSuspended = clock.Counter;

if (whileSuspended != beforeSuspend)
{
    throw new InvalidOperationException("Guest clock advanced while suspended.");
}

clock.Resume();
Thread.Sleep(30);
ulong afterResume = clock.Counter;

if (afterResume <= whileSuspended)
{
    throw new InvalidOperationException("Guest clock did not advance after resume.");
}

Console.WriteLine("CLOCK_PROBE=PASS");
Console.WriteLine(clock.GetUwpTelemetry());
