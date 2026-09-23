using System.Reflection;
using ARMeilleure.CodeGen;
using ARMeilleure.Memory;
using ARMeilleure.Translation.Cache;
using Ryujinx.Memory;
using ARMeilleure.Translation;
using ARMeilleure.Common;
using System.Runtime.InteropServices;

int checks = 0;
void Check(bool success, string message)
{
    if (!success) throw new Exception(message);
    Console.WriteLine("PASS " + message);
    checks++;
}
CompiledFunction Function(int bytes)
{
    var constructor = typeof(CompiledFunction).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
    return (CompiledFunction)constructor.Invoke(new object?[] { new byte[bytes], null, null });
}
var admission = typeof(JitCache).GetMethod("CanGrowWithinBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
var commitAdmission = typeof(JitCache).GetMethod("CanCommitWithinBudget", BindingFlags.NonPublic | BindingFlags.Static)!;
bool CanGrow(long usage, long limit) => (bool)admission.Invoke(null, [usage, limit])!;
bool CanCommit(long usage, long limit, int bytes) => (bool)commitAdmission.Invoke(null, [usage, limit, bytes])!;
const long mib = 1024L * 1024;
Check(CanGrow(1024 * mib, 5120 * mib), "normal process budget permits lazy JIT growth beyond 512 MiB");
Check(CanGrow(4352 * mib, 5120 * mib) && !CanGrow(4352 * mib + 1, 5120 * mib),
    "growth reserves a full region plus 512 MiB of process headroom");
Check(!CanGrow(0, 5120 * mib) && !CanGrow(long.MaxValue, 5120 * mib) && !CanGrow(1, 512 * mib),
    "missing samples and exhausted budgets fail closed without overflow");
Check(CanCommit(4672 * mib - 65536, 5120 * mib, 65536) &&
    !CanCommit(4672 * mib - 65535, 5120 * mib, 65536),
    "JIT commit retains an exact 448 MiB process headroom");
var configuredLimit = typeof(JitCache).GetMethod("GetConfiguredMaxRegions", BindingFlags.NonPublic | BindingFlags.Static)!;
object? oldLimit = AppContext.GetData("Ryujinx.Uwp.JitCacheMaxRegions");
try
{
    AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", 6);
    Check((int)configuredLimit.Invoke(null, null)! == 6, "host can admit six lazy regions for large titles");
    AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", 99);
    Check((int)configuredLimit.Invoke(null, null)! == 6, "region cap remains bounded");
}
finally { AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", oldLimit); }
// Actual virtual reservations and executable memory protection. Test code is
// intentionally never executed; failure injection wraps only MEM_COMMIT.
var allocator = new TestAllocator();
var cache = new JitCache(allocator);
var initial = JitCache.GetDetailedTelemetry();
Check(initial.Regions == 1 && initial.ReservedBytes == 256UL * 1024 * 1024,
    "telemetry separates reserved space from committed pages");
var code = Function(128 * 1024);
allocator.FailNextCommit = true;
try { cache.Map(code); throw new Exception("Expected injected failure"); }
catch (OutOfMemoryException) { }
var failed = JitCache.GetDetailedTelemetry();
Check(failed.FreeBytes == initial.FreeBytes && failed.Entries == initial.Entries &&
    failed.AllocationFailures == initial.AllocationFailures + 1,
    "failed commit restores free range without publishing an entry");
nint first = cache.Map(code);
nint second = cache.Map(code);
cache.Unmap(first);
var reclaimed = JitCache.GetDetailedTelemetry();
Check(reclaimed.Unmaps == 1 && reclaimed.ReclaimedBytes == code.Code.Length &&
    reclaimed.FreeBlocks >= 2, "explicit unmap exposes reclaim and fragmentation");
nint reused = cache.Map(code);
Check(reused == first, "freed range is reused without expanding another region");
cache.Unmap(second);
cache.Unmap(reused);
var empty = JitCache.GetDetailedTelemetry();
Check(empty.FreeBytes == initial.FreeBytes && empty.LargestFreeBlock == initial.LargestFreeBlock,
    "adjacent free ranges coalesce after reclamation");
Ryujinx.Common.Memory.RuntimeMemoryBudget.Configure(1, 1);
try
{
    nint pressuredReuse = cache.Map(code);
    Check(JitCache.GetDetailedTelemetry().CommittedBytes == empty.CommittedBytes,
        "memory pressure permits reuse of already committed code pages");
    cache.Unmap(pressuredReuse);
    try { cache.Map(Function(512 * 1024)); throw new Exception("Expected growth refusal"); }
    catch (OutOfMemoryException)
    {
        Check(JitCache.GetDetailedTelemetry().CommittedBytes == empty.CommittedBytes,
            "memory pressure still rejects new executable commit");
    }
}
finally { Ryujinx.Common.Memory.RuntimeMemoryBudget.Configure(5UL * 1024 * 1024 * 1024); }
using var stop = new CancellationTokenSource();
var reader = Task.Run(() => { while (!stop.IsCancellationRequested) JitCache.GetDetailedTelemetry(); });
cache.Dispose();
cache.Dispose();
stop.Cancel();
Check(reader.Wait(TimeSpan.FromSeconds(5)) && JitCache.GetDetailedTelemetry() == default,
    "concurrent telemetry and repeated dispose do not read freed regions");
try { cache.Map(code); throw new Exception("Expected ObjectDisposedException"); }
catch (ObjectDisposedException) { Check(true, "disposed cache rejects new code mappings"); }
Console.WriteLine($"{checks} checks passed");

var freed = new List<int>();
var epochs = new EpochReclaimer<int>(value => freed.Add(value));
epochs.Enter(1);
epochs.Enter(2);
epochs.Retire(10);
epochs.Boundary(1);
Check(freed.Count == 0, "sleeping reader prevents freeing its native return stack");
epochs.Enter(3);
epochs.Boundary(2);
Check(freed.SequenceEqual(new[] { 10 }), "new readers cannot prolong an older grace period");
epochs.Retire(20);
epochs.Boundary(1);
epochs.Boundary(2);
Check(freed.Count == 1, "second generation waits for every potentially exposed reader");
epochs.Exit(3);
Check(freed.SequenceEqual(new[] { 10, 20 }), "reader exit completes pending retirement");
epochs.Exit(1);
epochs.Exit(2);
epochs.Retire(30);
Check(freed.Count == 3 && epochs.Pending == 0, "idle session releases retired code synchronously");
for (int run = 0; run < 100; run++)
{
    int releases = 0;
    var racing = new EpochReclaimer<int>(_ => Interlocked.Increment(ref releases));
    for (ulong id = 1; id <= 4; id++) racing.Enter(id);
    racing.Retire(run);
    Parallel.For(1, 5, id => { racing.Boundary((ulong)id); racing.Exit((ulong)id); });
    if (releases != 1 || racing.Pending != 0) throw new Exception("Retirement race double-free/leak");
}
Check(true, "100 concurrent grace periods release once without stale ownership");
Console.WriteLine($"{checks} allocation and epoch checks passed");

for (int run = 0; run < 300; run++)
{
    var context = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
    Parallel.Invoke(
        () => { for (int i = 0; i < 200; i++) context.RequestInterrupt(); },
        () => { context.StopRunning(); context.Dispose(); },
        () => { for (int i = 0; i < 200; i++) _ = context.Running; });
    context.RequestInterrupt();
    context.Dispose();
    if (context.Running || context.State != ARMeilleure.State.ExecutionContext.LifecycleState.Disposed)
        throw new Exception("Execution context was resurrected");
}
Check(true, "300 interrupt/stop/dispose races never access freed native state");
Check(Ryujinx.Common.Memory.RuntimeMemoryBudget.Classify(96, 100) == Ryujinx.Common.Memory.MemoryPressure.Emergency &&
    Ryujinx.Common.Memory.RuntimeMemoryBudget.Classify(90, 100) == Ryujinx.Common.Memory.MemoryPressure.Critical &&
    Ryujinx.Common.Memory.RuntimeMemoryBudget.Classify(82, 100) == Ryujinx.Common.Memory.MemoryPressure.High &&
    Ryujinx.Common.Memory.RuntimeMemoryBudget.Classify(72, 100) == Ryujinx.Common.Memory.MemoryPressure.Soft &&
    Ryujinx.Common.Memory.RuntimeMemoryBudget.Classify(71, 100) == Ryujinx.Common.Memory.MemoryPressure.Normal,
    "memory pressure thresholds have deterministic boundaries");
var lease = Ryujinx.Common.Memory.RuntimeMemoryBudget.Track(Ryujinx.Common.Memory.MemoryCategory.Ptc, 123);
lease.Dispose();
lease.Dispose();
Check(Ryujinx.Common.Memory.RuntimeMemoryBudget.Sample(0, 0).Contains("Ptc=0 "),
    "memory accounting lease releases exactly once");
Console.WriteLine($"{checks} total checks passed");

string filesystemFixture = Path.Combine(Path.GetTempPath(), "Ryubox-FilesystemRegression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(filesystemFixture);
Ryujinx.Common.Configuration.AppDataManager.Initialize(filesystemFixture);
Check(Ryujinx.Common.Utilities.SandboxDiskSpace.TryGet(out long available, out long total) &&
    available >= 0 && total > 0 && available <= total,
    "actual Windows free-space query uses the chosen app-data directory");
Ryujinx.Common.Memory.RuntimeMemoryBudget.Configure(2UL * 1024 * 1024 * 1024);
Check(Ryujinx.Common.Memory.RuntimeMemoryBudget.Limit == 2L * 1024 * 1024 * 1024,
    "memory budget honors a host-reported limit below the conservative ceiling");
Console.WriteLine($"{checks} final checks passed; filesystem fixture retained at {filesystemFixture}");

// A permanently sleeping guest protects only its own code, not an entire epoch.
var releasedPins = new HashSet<object>();
var pins = new PinnedReclaimer<object>(value =>
{
    if (!releasedPins.Add(value)) throw new Exception("JIT double free");
});
object sleeping = new();
pins.Pin(sleeping);
pins.Pin(sleeping);
pins.Retire(sleeping);
for (int i = 0; i < 10000; i++) pins.Retire(new object());
Check(releasedPins.Count == 10000 && pins.Pending == 1, "sleeping guest no longer blocks 10000 unrelated retirements");
pins.Unpin(sleeping);
Check(!releasedPins.Contains(sleeping), "shared function stays alive until its last executing guest returns");
pins.Unpin(sleeping);
Check(releasedPins.Contains(sleeping) && pins.Pending == 0, "last pin releases retired function exactly once");

AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", true);
AppContext.SetSwitch("Ryujinx.Uwp.SafeJit", true);
foreach (bool nativeDispatch in new[] { false, true })
{
    ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = nativeDispatch;
    var memory = new GuestMemory();
    // main: BL callee; ADD X0, X0, #1; BR X2 (zero = exit).
    // callee: ADD X0, X0, #2; RET. Actual ARM64 -> x64 -> execution.
    memory.Write(0x1000, 0x94000004u);
    memory.Write(0x1004, 0x91000400u);
    memory.Write(0x1008, 0xD61F0040u);
    memory.Write(0x1010, 0x91000800u);
    memory.Write(0x1014, 0xD65F03C0u);
    var translator = new Translator(new TestAllocator(), memory, new TestTable());
    var context = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
    translator.Execute(context, 0x1000);
    Check(context.GetX(0) == 3 && context.GetX(30) == 0x1004,
        $"pinned ARM64 BL/RET/BR preserves registers and PC (nativeDispatch={nativeDispatch})");
    Check(JitCache.GetDetailedTelemetry() == default, "actual guest exit disposes JIT after its last pin");
    context.Dispose();
}
Check(true, "pinned dispatcher executable regressions complete");

foreach (bool nativeDispatch in new[] { false, true })
{
    ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = nativeDispatch;
    var armMemory = new GuestMemory();
    armMemory.Write(0x1000, 0xEB000002u); // ARM BL 0x1010 (PC = address + 8)
    armMemory.Write(0x1004, 0xE2800001u); // ADD R0, R0, #1
    armMemory.Write(0x1008, 0xE12FFF12u); // BX R2 = exit
    armMemory.Write(0x1010, 0xE2800002u); // ADD R0, R0, #2
    armMemory.Write(0x1014, 0xE12FFF1Eu); // BX LR
    var armTranslator = new Translator(new TestAllocator(), armMemory, new TestTable());
    var armContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!) { IsAarch32 = true };
    armTranslator.Execute(armContext, 0x1000);
    Check(armContext.GetX(0) == 3 && armContext.GetX(14) == 0x1004 && JitCache.GetDetailedTelemetry() == default,
        $"ARM32 subroutine return and teardown (native={nativeDispatch})");
    armContext.Dispose();

    foreach (var rounding in new[] { (ARMeilleure.State.FPCR)0, ARMeilleure.State.FPCR.RMode0 })
    {
        var fpMemory = new GuestMemory();
        fpMemory.Write(0x1000, 0x94000008u); // BL 0x1020
        fpMemory.Write(0x1004, 0x1E222823u); // FADD S3, S1, S2
        fpMemory.Write(0x1008, 0xD61F0040u); // BR X2
        fpMemory.Write(0x1020, 0x1E222820u); // FADD S0, S1, S2 (CpuTestSimdReg opcode)
        fpMemory.Write(0x1024, 0xD65F03C0u); // RET
        var fpTranslator = new Translator(new TestAllocator(), fpMemory, new TestTable());
        var fpContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!) { Fpcr = rounding };
        fpContext.SetV(1, new ARMeilleure.State.V128(1.0f));
        fpContext.SetV(2, new ARMeilleure.State.V128(MathF.Pow(2, -24))); // half an ULP at 1.0
        fpTranslator.Execute(fpContext, 0x1000);
        var expectedFp = new ARMeilleure.State.V128(BitConverter.Int32BitsToSingle(
            rounding == 0 ? 0x3f800000 : 0x3f800001));
        Check(fpContext.GetV(0).Equals(expectedFp) && fpContext.GetV(3).Equals(expectedFp) &&
            fpContext.Fpcr == rounding && JitCache.GetDetailedTelemetry() == default,
            $"FP rounding/vector state survives guest subroutine dispatch (native={nativeDispatch}, rounding={rounding})");
        fpContext.Dispose();
    }

    var changingMemory = new GuestMemory();
    changingMemory.Write(0x1000, 0xD4000021u); // SVC #1
    changingMemory.Write(0x1004, 0x91000400u); // ADD #1, replaced with #2
    changingMemory.Write(0x1008, 0xD61F0060u); // BR X3 (same entry, then exit)
    var changingTranslator = new Translator(new TestAllocator(), changingMemory, new TestTable());
    int svcVisits = 0;
    var changingContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!, supervisorCallback: (ctx, _, _) =>
    {
        if (++svcVisits == 1)
        {
            changingMemory.Write(0x1004, 0x91000800u);
            changingTranslator.InvalidateJitCacheRegion(0x1000, 12);
        }
        else ctx.SetX(3, 0);
    });
    changingContext.SetX(3, 0x1000);
    changingTranslator.Execute(changingContext, 0x1000);
    Check(svcVisits == 2 && changingContext.GetX(0) == 3 && JitCache.GetDetailedTelemetry() == default,
        $"self-invalidating same-address loop executes new code on next entry (native={nativeDispatch})");
    changingContext.Dispose();

    using var guestEntered = new ManualResetEventSlim();
    using var guestResume = new ManualResetEventSlim();
    var stopMemory = new GuestMemory();
    stopMemory.Write(0x1000, 0xD4000021u); // SVC #1
    stopMemory.Write(0x1004, 0xD61F0060u); // BR X3
    var stopTranslator = new Translator(new TestAllocator(), stopMemory, new TestTable());
    int interrupts = 0;
    var stopContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!,
        interruptCallback: ctx => { interrupts++; ctx.StopRunning(); },
        supervisorCallback: (_, _, _) => { guestEntered.Set(); guestResume.Wait(TimeSpan.FromSeconds(5)); });
    stopContext.SetX(3, 0x1000);
    var executing = Task.Run(() => stopTranslator.Execute(stopContext, 0x1000));
    bool entered = guestEntered.Wait(TimeSpan.FromSeconds(5));
    stopContext.RequestInterrupt();
    guestResume.Set();
    bool stopped = executing.Wait(TimeSpan.FromSeconds(5));
    Check(entered && stopped && interrupts == 1 && JitCache.GetDetailedTelemetry() == default,
        $"pending scheduler interrupt stops actual guest loop and releases JIT (native={nativeDispatch})");
    stopContext.Dispose();
}

if (args.Contains("--benchmark"))
{
    // Identical ARM64 subroutine loop through the real JIT, no game or window.
    // This includes context switching and pin acquisition, unlike lookup-only tests.
    foreach (bool nativeDispatch in new[] { false, true, false, true })
    {
        ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = nativeDispatch;
        var loopMemory = new GuestMemory();
        loopMemory.Write(0x1000, 0x94000008u); // BL 0x1020
        loopMemory.Write(0x1004, 0x91000400u); // ADD X0, X0, #1
        loopMemory.Write(0x1008, 0xD1000421u); // SUB X1, X1, #1
        loopMemory.Write(0x100c, 0xB5FFFFA1u); // CBNZ X1, 0x1000
        loopMemory.Write(0x1010, 0xD61F0040u); // BR X2 = exit
        loopMemory.Write(0x1020, 0x91000800u); // ADD X0, X0, #2
        loopMemory.Write(0x1024, 0xD65F03C0u); // RET
        var loopTranslator = new Translator(new TestAllocator(), loopMemory, new TestTable());
        var loopContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
        loopContext.SetX(1, 300000);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        loopTranslator.Execute(loopContext, 0x1000);
        timer.Stop();
        Check(loopContext.GetX(0) == 900000 && loopContext.GetX(1) == 0 &&
            JitCache.GetDetailedTelemetry() == default,
            $"ARM64 300000-call benchmark result and teardown (native={nativeDispatch})");
        Console.WriteLine($"BENCH executed ARM64 native={nativeDispatch} elapsedMs={timer.Elapsed.TotalMilliseconds:F2} (not game FPS)");
        loopContext.Dispose();
    }
}

// A supervisor call can sleep or invalidate code while its native return
// address remains live. Reclaim must defer ONLY this translation.
foreach (bool nativeDispatch in new[] { false, true })
{
    ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = nativeDispatch;
    var memory = new GuestMemory();
    memory.Write(0x1000, 0xD4000021u); // SVC #1
    memory.Write(0x1004, 0x91000400u); // ADD X0, X0, #1
    memory.Write(0x1008, 0xD61F0040u); // BR X2
    var translator = new Translator(new TestAllocator(), memory, new TestTable());
    bool pinnedDuringSvc = false;
    var context = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!, supervisorCallback: (_, _, _) =>
    {
        long unmaps = JitCache.GetDetailedTelemetry().Unmaps;
        translator.InvalidateJitCacheRegion(0x1000, 12);
        pinnedDuringSvc = JitCache.GetDetailedTelemetry().Unmaps == unmaps;
    });
    translator.Execute(context, 0x1000);
    Check(pinnedDuringSvc && context.GetX(0) == 1 && JitCache.GetDetailedTelemetry() == default,
        $"self-invalidation inside SVC returns through live code before release (native={nativeDispatch})");
    context.Dispose();

    // Failure from a reverse-P/Invoke JIT lookup must return to managed code,
    // not abort the entire NativeAOT process.
    var failingMemory = new GuestMemory { FailReads = true };
    var failing = new Translator(new TestAllocator(), failingMemory, new TestTable());
    var failingContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
    bool caught = false;
    try { failing.Execute(failingContext, 0x1000); }
    catch (Exception exception) { caught = exception.ToString().Contains("Injected guest read failure"); }
    finally { failingContext.Dispose(); }
    Check(caught && JitCache.GetDetailedTelemetry() == default,
        $"translation failure propagates after dispatcher exit and frees JIT (native={nativeDispatch})");
}

using (var recycling = new JitCache(new TestAllocator()))
{
    var releaseCode = new PinnedReclaimer<CodeHandle>(handle => recycling.Unmap(handle.Pointer));
    var blob = Function(1024 * 1024);
    var sleeper = new CodeHandle(recycling.Map(blob));
    releaseCode.Pin(sleeper);
    releaseCode.Retire(sleeper);
    for (int i = 0; i < 600; i++) releaseCode.Retire(new CodeHandle(recycling.Map(blob)));
    var telemetry = JitCache.GetDetailedTelemetry();
    Check(telemetry.Regions == 1 && telemetry.Unmaps == 600 && telemetry.AllocationFailures == 0,
        "600 MiB cumulative executable mappings reuse one region while a guest stays pinned");
    releaseCode.Unpin(sleeper);
}
Console.WriteLine($"{checks} checks passed");

// Pressure outside the JIT must not purge a cache with ample reusable space.
Check(!JitReclaimPolicy.NeedsReclaim(256UL * 1024 * 1024, 224L * 1024 * 1024),
    "32 MiB live JIT does not purge code to chase unrelated process pressure");
Check(!JitReclaimPolicy.NeedsReclaim(256UL * 1024 * 1024, JitReclaimPolicy.LowFreeBytes) &&
    JitReclaimPolicy.NeedsReclaim(256UL * 1024 * 1024, JitReclaimPolicy.LowFreeBytes - 1) &&
    JitReclaimPolicy.TargetFreeBytes > JitReclaimPolicy.LowFreeBytes,
    "JIT maintenance has separate low/high free-space watermarks");
var reclaimPolicy = new JitReclaimPolicy();
Check(reclaimPolicy.TrySchedule(1000) && !reclaimPolicy.TrySchedule(1001) &&
    !reclaimPolicy.TrySchedule(1999) && reclaimPolicy.TrySchedule(2000),
    "translation bursts cannot schedule more than one maintenance scan per second");

int interruptCalls = 0;
var interruptContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!, interruptCallback: ctx =>
{
    if (++interruptCalls == 1) ctx.RequestInterrupt();
});
int counterOffset = checked((int)ARMeilleure.State.NativeContext.GetCounterOffset());
Marshal.WriteInt32(interruptContext.NativeContextPtr, counterOffset, 123);
interruptContext.CheckPendingInterrupt();
Check(Marshal.ReadInt32(interruptContext.NativeContextPtr, counterOffset) == 123 && interruptCalls == 0,
    "idle managed dispatch does not reset the guest synchronization budget");
interruptContext.RequestInterrupt();
interruptContext.CheckInterrupt();
Check(interruptContext.Interrupted && Marshal.ReadInt32(interruptContext.NativeContextPtr, counterOffset) == 0,
    "interrupt arriving during callback retains immediate native polling");
interruptContext.CheckPendingInterrupt();
Check(interruptCalls == 2 && !interruptContext.Interrupted,
    "second scheduler request is serviced exactly once");
interruptContext.StopRunning();
interruptContext.CheckInterrupt();
Check(Marshal.ReadInt32(interruptContext.NativeContextPtr, counterOffset) == 0,
    "synchronization cannot refill the polling counter after Stop");
interruptContext.Dispose();
interruptContext.CheckPendingInterrupt();
interruptContext.CheckInterrupt();

// Cold compilation must neither block warmed functions nor publish code read
// before a concurrent IC invalidation. A controlled decoder pause is deterministic.
ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = false;
var parallelMemory = new GuestMemory();
parallelMemory.Write(0x1000, 0xD61F0040u); // BR X2 (exit)
parallelMemory.Write(0x1100, 0x91000400u); // ADD X0, X0, #1
parallelMemory.Write(0x1104, 0xD61F0040u);
var parallelTranslator = new Translator(new TestAllocator(), parallelMemory, new TestTable());
parallelTranslator.AcquireFunction(1, 0x1000, ARMeilleure.State.ExecutionMode.Aarch64);
parallelTranslator.GuestDispatchBoundary(1);
using var decoding = new ManualResetEventSlim();
using var resumeDecode = new ManualResetEventSlim();
int coldReads = 0;
parallelMemory.AfterRead = address =>
{
    if (address == 0x1100 && Interlocked.Increment(ref coldReads) == 1)
    {
        decoding.Set();
        if (!resumeDecode.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Decoder test release");
    }
};
var coldLookup = Task.Run(() => parallelTranslator.AcquireFunction(2, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64));
bool warmProgress, invalidationProgress;
Task warmLookup = Task.CompletedTask;
Task invalidation = Task.CompletedTask;
try
{
    if (!decoding.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Decoder test entry");
    warmLookup = Task.Run(() => parallelTranslator.AcquireFunction(3, 0x1000, ARMeilleure.State.ExecutionMode.Aarch64));
    warmProgress = warmLookup.Wait(TimeSpan.FromSeconds(2));
    parallelMemory.Write(0x1100, 0x91000800u); // replace with ADD #2
    invalidation = Task.Run(() => parallelTranslator.InvalidateJitCacheRegion(0x1100, 8));
    invalidationProgress = invalidation.Wait(TimeSpan.FromSeconds(2));
}
finally { resumeDecode.Set(); }
Task.WaitAll(coldLookup, warmLookup, invalidation);
Check(warmProgress, "warm guest lookup progresses while another thread is paused in compilation");
Check(invalidationProgress && coldReads >= 2,
    "invalidation does not wait for compilation and forces obsolete code to be retranslated");
parallelTranslator.GuestDispatchBoundary(2);
parallelTranslator.GuestDispatchBoundary(3);
parallelMemory.AfterRead = null;
for (int i = 0; i < 10000; i++)
    parallelTranslator.AcquireFunction(4, 0x1000, ARMeilleure.State.ExecutionMode.Aarch64, releasePrevious: true);
long unmapsBefore = JitCache.GetDetailedTelemetry().Unmaps;
parallelTranslator.InvalidateJitCacheRegion(0x1000, 4);
Check(JitCache.GetDetailedTelemetry().Unmaps == unmapsBefore,
    "10000 combined dispatch boundaries retain exactly the executing pin");
parallelTranslator.AcquireFunction(4, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64, releasePrevious: true);
Check(JitCache.GetDetailedTelemetry().Unmaps == unmapsBefore + 1,
    "combined dispatch releases invalidated previous code only after its return");
parallelTranslator.GuestDispatchBoundary(4);

// Returning to the SAME address after IC invalidation must miss the retained-pin
// shortcut, even when another thread still executes the old translation.
var oldSameAddress = parallelTranslator.AcquireFunction(6, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64);
parallelTranslator.AcquireFunction(7, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64);
long sameAddressUnmaps = JitCache.GetDetailedTelemetry().Unmaps;
parallelMemory.Write(0x1100, 0x91000C00u); // ADD #3
parallelTranslator.InvalidateJitCacheRegion(0x1100, 8);
var newSameAddress = parallelTranslator.AcquireFunction(6, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64, releasePrevious: true);
Check(newSameAddress != oldSameAddress && JitCache.GetDetailedTelemetry().Unmaps == sameAddressUnmaps,
    "same-address redispatch rejects retired code while another guest keeps its old pin");
parallelTranslator.GuestDispatchBoundary(7);
Check(JitCache.GetDetailedTelemetry().Unmaps == sameAddressUnmaps + 1,
    "last old-code pin releases exactly once after same-address replacement");
parallelTranslator.GuestDispatchBoundary(6);
// Restore the expected instruction for the final execution below.
parallelMemory.Write(0x1100, 0x91000800u);
parallelTranslator.InvalidateJitCacheRegion(0x1100, 8);

if (args.Contains("--benchmark"))
{
    const int iterations = 500000;
    foreach (bool combined in new[] { false, true, false, true })
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
        {
            if (!combined) parallelTranslator.GuestDispatchBoundary(5);
            parallelTranslator.AcquireFunction(5, 0x1100, ARMeilleure.State.ExecutionMode.Aarch64, releasePrevious: combined);
        }
        timer.Stop();
        parallelTranslator.GuestDispatchBoundary(5);
        Console.WriteLine($"BENCH warm lookup combined={combined} iterations={iterations} elapsedMs={timer.Elapsed.TotalMilliseconds:F2} (not game FPS)");
    }
}
var parallelContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
parallelTranslator.Execute(parallelContext, 0x1100);
Check(parallelContext.GetX(0) == 2 && JitCache.GetDetailedTelemetry() == default,
    "post-invalidation ARM64 code executes updated instruction and releases all pins");
parallelContext.Dispose();
// Fill allocator ranges without committing hundreds of MiB just for a test.
// The mappings/protection/region expansion below still use real Windows memory.
AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", 6);
Ryujinx.Common.Memory.RuntimeMemoryBudget.Configure(5UL * 1024 * 1024 * 1024);
try
{
    using var growing = new JitCache(new TestAllocator());
    object ranges = typeof(JitCache).GetField("_cacheAllocator", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(growing)!;
    var reserveRange = ranges.GetType().GetMethod("Allocate")!;
    var mappings = new List<nint>();
    for (int region = 2; region <= 6; region++)
    {
        int remaining = checked((int)JitCache.GetDetailedTelemetry().LargestFreeBlock);
        Check((int)reserveRange.Invoke(ranges, [remaining])! >= 0, "fixture reserves remaining logical range");
        mappings.Add(growing.Map(Function(128)));
        Check(JitCache.GetDetailedTelemetry().Regions == region, $"real executable mapping grows into region {region}");
    }
    int finalRemaining = checked((int)JitCache.GetDetailedTelemetry().LargestFreeBlock);
    reserveRange.Invoke(ranges, [finalRemaining]);
    try { growing.Map(Function(128)); throw new Exception("Expected capacity rejection"); }
    catch (OutOfMemoryException ex) { Check(ex.Message.Contains("configured capacity"), "seventh region rejected with accurate diagnostic"); }
    foreach (nint mapping in mappings) growing.Unmap(mapping);
    Check(JitCache.GetDetailedTelemetry().Entries == 0, "expanded-region mappings release cleanly");
}
finally { AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", oldLimit); }
Console.WriteLine($"{checks} checks passed including Logs6 regressions");

// Exercise the real reverse-P/Invoke memory callback from generated ARM64,
// including a nested translated call. No game or graphics window is involved.
foreach (bool pinned in new[] { false, true })
foreach (bool native in new[] { false, true })
foreach (bool injectFault in new[] { false, true })
foreach (uint instruction in new uint[] {
    0x39400020, 0x79400020, 0xB9400020, 0xF9400020, 0x3DC00020,
    0x39000020, 0x79000020, 0xB9000020, 0xF9000020, 0x3D800020 })
{
    AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", pinned);
    AppContext.SetSwitch("Ryujinx.Uwp.SafeJit", pinned);
    AppContext.SetSwitch("Ryujinx.Uwp.DisableBackgroundTranslation", true);
    ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = native;
    var faultMemory = new GuestMemory();
    using var faultPageTable = new MemoryBlock(4096);
    faultMemory.TestPageTable = faultPageTable.Pointer; // All PTEs unmapped: force callbacks.
    faultMemory.Write(0x1000, 0x94000008u); // BL 0x1020
    faultMemory.Write(0x1004, 0xF9000044u); // STR X4,[X2] (must not execute)
    faultMemory.Write(0x1008, 0xD65F00A0u); // RET X5 = dispatcher exit
    faultMemory.Write(0x1020, instruction); // load/store [X1]
    faultMemory.Write(0x1024, 0xF9000044u); // must not execute in callee either
    faultMemory.Write(0x1028, 0xD65F03C0u); // RET
    faultMemory.Write(0xD0, 0x1234UL);
    if (injectFault) faultMemory.FaultAddress = 0xD0;
    var faultTranslator = new Translator(new TestAllocator(), faultMemory, new TestTable { TestMask = 0 });
    var faultContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
    faultContext.SetX(0, 0x1234);
    faultContext.SetX(1, 0xD0);
    faultContext.SetX(2, 0x2000);
    faultContext.SetX(4, 0xBAD);
    var vectorSentinel = new ARMeilleure.State.V128(123.0f);
    faultContext.SetV(0, vectorSentinel);
    bool reported = false;
    try { faultTranslator.Execute(faultContext, 0x1000); }
    catch (InvalidOperationException error) when (error.InnerException is InvalidMemoryRegionException)
    { reported = true; }
    Check((injectFault
        ? reported && !faultContext.Running && faultMemory.Read<ulong>(0x2000) == 0 &&
            faultContext.GetX(0) == 0x1234 && faultContext.GetV(0).Equals(vectorSentinel)
        : !reported && faultMemory.Read<ulong>(0x2000) == 0xBAD) && JitCache.GetDetailedTelemetry() == default,
        $"memory callback fault={injectFault}: op={instruction:X8}, pinned={pinned}, native={native}");
    faultContext.Dispose();
}
Console.WriteLine($"{checks} checks passed including generated memory-fault exits");

// The unpinned native dispatcher must not tailcall the null pointer returned
// by a failed reverse-P/Invoke translation lookup (Marvel crash at address 0).
AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", false);
ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = true;
var failedLookupMemory = new GuestMemory { FailReads = true };
var failedLookupTranslator = new Translator(new TestAllocator(), failedLookupMemory, new TestTable { TestMask = 0 });
var failedLookupContext = new ARMeilleure.State.ExecutionContext(new TestAllocator(), null!);
bool lookupReported = false;
try { failedLookupTranslator.Execute(failedLookupContext, 0x1000); }
catch (Exception error) { lookupReported = error.ToString().Contains("Injected guest read failure"); }
Check(lookupReported && !failedLookupContext.Running && JitCache.GetDetailedTelemetry() == default,
    "unpinned native lookup failure returns without calling address zero and releases JIT");
failedLookupContext.Dispose();
Console.WriteLine($"{checks} checks passed including null dispatch regression");

sealed record CodeHandle(nint Pointer);

sealed class GuestMemory : IMemoryManager
{
    public bool FailReads;
    public ulong? FaultAddress;
    public Action<ulong>? AfterRead;
    private readonly byte[] _bytes = new byte[65536];
    public int AddressSpaceBits => 16;
    public nint TestPageTable;
    public nint PageTablePointer => TestPageTable;
    public MemoryManagerType Type => MemoryManagerType.SoftwareMmu;
    public event Action<ulong, ulong>? UnmapEvent { add { } remove { } }
    public T Read<T>(ulong va) where T : unmanaged
    {
        if (va == FaultAddress) throw new InvalidMemoryRegionException();
        if (FailReads) throw new OutOfMemoryException("Injected guest read failure");
        T result = MemoryMarshal.Read<T>(_bytes.AsSpan(checked((int)va)));
        AfterRead?.Invoke(va);
        return result;
    }
    public T ReadTracked<T>(ulong va) where T : unmanaged => Read<T>(va);
    public void Write<T>(ulong va, T value) where T : unmanaged
    {
        if (va == FaultAddress) throw new InvalidMemoryRegionException();
        MemoryMarshal.Write(_bytes.AsSpan(checked((int)va)), in value);
    }
    public ReadOnlySpan<byte> GetSpan(ulong va, int size, bool tracked = false) => _bytes.AsSpan(checked((int)va), size);
    public ref T GetRef<T>(ulong va) where T : unmanaged => ref MemoryMarshal.AsRef<T>(_bytes.AsSpan(checked((int)va)));
    public bool IsMapped(ulong va) => va < (ulong)_bytes.Length;
    public void SignalMemoryTracking(ulong va, ulong size, bool write, bool precise = false, int? exemptId = null) { }
}

sealed class TestTable : IAddressTable<ulong>
{
    private ulong _unused;
    public AddressTableType TableType => default;
    public ulong TestMask = 65532;
    public ulong Mask => TestMask;
    public AddressTableLevel[] Levels => [new(2, 14)];
    public ulong Fill { get; set; }
    public nint Base => 0;
    public bool IsValid(ulong address) => false;
    public ref ulong GetValue(ulong address) => ref _unused;
    public void SignalCodeRange(ulong address, ulong size) { }
    public void Dispose() { }
}

sealed class TestAllocator : IJitMemoryAllocator
{
    public bool FailNextCommit;
    public IJitMemoryBlock Allocate(ulong size) => new TestBlock(this, size, MemoryAllocationFlags.Jit);
    public IJitMemoryBlock Reserve(ulong size) => new TestBlock(this, size, MemoryAllocationFlags.Jit | MemoryAllocationFlags.Reserve);
}
sealed class TestBlock(TestAllocator owner, ulong size, MemoryAllocationFlags flags) : IJitMemoryBlock
{
    private readonly MemoryBlock _memory = new(size, flags);
    public nint Pointer => _memory.Pointer;
    public void Commit(ulong offset, ulong bytes)
    {
        if (owner.FailNextCommit)
        {
            owner.FailNextCommit = false;
            throw new OutOfMemoryException("Injected commit failure");
        }
        _memory.Commit(offset, bytes);
    }
    public void MapAsRw(ulong offset, ulong bytes) => _memory.Reprotect(offset, bytes, MemoryPermission.ReadAndWrite);
    public void MapAsRx(ulong offset, ulong bytes) => _memory.Reprotect(offset, bytes, MemoryPermission.ReadAndExecute);
    public void MapAsRwx(ulong offset, ulong bytes) => _memory.Reprotect(offset, bytes, MemoryPermission.ReadWriteExecute);
    public void Dispose() => _memory.Dispose();
}
