using ARMeilleure.Memory;
using ARMeilleure.State;
using ARMeilleure.Translation.PTC;
using Ryujinx.Common.Configuration;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu.Engine;
using Ryujinx.Graphics.Gpu.Engine.Threed;
using Ryujinx.Graphics.Gpu.Engine.Types;
using System.Reflection;
using System.Runtime.CompilerServices;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
    Console.WriteLine("PASS " + message);
}

string scratch = Path.Combine(AppContext.BaseDirectory, "cache-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
AppDataManager.Initialize(scratch);
AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", true);

PtcCacheInfo Owner(ulong pid, int program, string version = "1.0.0") =>
    new(pid, $"{0x0100000000001000UL + (ulong)program:x16}", "0100000000001000",
        (byte)program, version, "Application", "default");

Ptc Open(PtcCacheInfo owner, bool enabled = true)
{
    // No executable entries are loaded in this test; only on-disk profiling data.
    Ptc cache = new(null!);
    cache.Initialize(owner, enabled, MemoryManagerType.SoftwarePageTable);
    return cache;
}

void Close(Ptc cache)
{
    cache.Profiler.Dispose();
    cache.Dispose();
}

string SaveProfile(PtcCacheInfo owner, ulong address)
{
    Ptc cache = Open(owner);
    try
    {
        cache.Profiler.StaticCodeStart = 0x1000;
        cache.Profiler.StaticCodeSize = 0x1000;
        cache.Profiler.Start();
        cache.Profiler.AddEntry(address, ExecutionMode.Aarch64, false);
        cache.Profiler.Stop();
        Check(File.Exists(cache.CachePathActual + ".info"), "profile persisted for " + owner.TitleIdText);
        return cache.CachePathActual;
    }
    finally { Close(cache); }
}

string program0Path = SaveProfile(Owner(1, 0), 0x1000);
string program1Path = SaveProfile(Owner(2, 1), 0x1100);
Check(program0Path != program1Path, "programs in the same application have separate disk caches");
Check(program0Path.EndsWith("default-ryubox-pinned-v1"), "pinned UWP cache does not alias desktop or nested dispatch");

for (int session = 0; session < 4; session++)
{
    for (int program = 0; program < 2; program++)
    {
        Ptc cache = Open(Owner((ulong)(100 + session * 2 + program), program));
        try
        {
            ulong expectedAddress = program == 0 ? 0x1000UL : 0x1100UL;
            Check(cache.Profiler.ProfiledFuncs.Count == 1 && cache.Profiler.ProfiledFuncs.ContainsKey(expectedAddress),
                $"session {session}, program {program}: own profile survives PID changes without cross-program entries");
        }
        finally { Close(cache); }
    }
}

Ptc versioned = Open(Owner(3, 0, "2.0.0"));
try { Check(versioned.CachePathActual != program0Path && versioned.Profiler.ProfiledFuncs.Count == 0,
    "new program version does not consume an older profile"); }
finally { Close(versioned); }

AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", false);
Ptc nested = Open(Owner(4, 0));
try { Check(nested.CachePathActual != program0Path && nested.Profiler.ProfiledFuncs.Count == 0,
    "nested dispatch does not consume pinned-dispatch profiles"); }
finally { Close(nested); }

Ptc disabled = Open(Owner(5, 0), false);
try { Check(disabled.State == PtcState.Disabled && disabled.CachePathActual.Length == 0 && disabled.CacheInfo.ProcessId == 5,
    "disabled cache keeps diagnostics without selecting a writable cache path"); }
finally { Close(disabled); }

Ptc unknown = Open(new PtcCacheInfo(6, null!, null!, 0, null!, "Unknown", "default"));
try { Check(unknown.State == PtcState.Disabled && unknown.CachePathActual.Length == 0,
    "unknown program does not use a shared default cache"); }
finally { Close(unknown); }

// Exercise the real bulk register update without constructing a GPU/device.
// Only fields accessed by UpdateBlendEnable/ForceDirty are injected.
DeviceStateWithShadow<ThreedClassState> state = new();
int blendCallbacks = 0;
StateUpdateTracker<ThreedClassState> tracker = new([
    new(() => { }), new(() => { }),
    new(() => blendCallbacks++, nameof(ThreedClassState.BlendEnable)),
]);
StateUpdater updater = (StateUpdater)RuntimeHelpers.GetUninitializedObject(typeof(StateUpdater));
ThreedClass threed = (ThreedClass)RuntimeHelpers.GetUninitializedObject(typeof(ThreedClass));
void SetField(object instance, string field, object value) =>
    instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
SetField(updater, "_updateTracker", tracker);
SetField(threed, "_state", state);
SetField(threed, "_stateUpdater", updater);

Boolean32[] pattern = Enumerable.Range(0, 8).Select(i => new Boolean32((uint)(i & 1))).ToArray();
bool Matches(ReadOnlySpan<Boolean32> actual, ReadOnlySpan<Boolean32> expected)
{
    for (int i = 0; i < 8; i++) if ((bool)actual[i] != (bool)expected[i]) return false;
    return true;
}

state.State.SetMmeShadowRamControl = (uint)SetMmeShadowRamControlMode.MethodTrack;
threed.UpdateBlendEnable(pattern);
Check(Matches(state.State.BlendEnable.AsSpan(), pattern) && Matches(state.ShadowState.BlendEnable.AsSpan(), pattern),
    "bulk blend track writes both live and shadow registers");
tracker.Update(1UL << StateUpdater.BlendStateIndex);
Check(blendCallbacks == 1, "blend changes notify the pipeline updater");

state.State.SetMmeShadowRamControl = (uint)SetMmeShadowRamControlMode.MethodPassthrough;
Boolean32[] zeros = new Boolean32[8];
threed.UpdateBlendEnable(zeros);
Check(Matches(state.State.BlendEnable.AsSpan(), zeros) && Matches(state.ShadowState.BlendEnable.AsSpan(), pattern),
    "passthrough changes live blend without destroying the recorded state");
tracker.Update(1UL << StateUpdater.BlendStateIndex);

state.State.SetMmeShadowRamControl = (uint)SetMmeShadowRamControlMode.MethodReplay;
threed.UpdateBlendEnable(zeros);
Check(Matches(zeros, pattern) && Matches(state.State.BlendEnable.AsSpan(), pattern),
    "replay restores recorded blend after live state diverges");
tracker.Update(1UL << StateUpdater.BlendStateIndex);
threed.UpdateBlendEnable(zeros);
Check(!tracker.IsDirty(StateUpdater.BlendStateIndex), "unchanged replay does not dirty the pipeline again");

state.State.SetMmeShadowRamControl = (uint)SetMmeShadowRamControlMode.MethodTrackWithFilter;
Boolean32[] ones = Enumerable.Repeat(new Boolean32(1), 8).ToArray();
threed.UpdateBlendEnable(ones);
Check(Matches(state.ShadowState.BlendEnable.AsSpan(), ones), "filtered tracking records all eight blend targets");
Check(new HardwareInfo("vendor", "gpu", "D3D12").GpuDriverVersion == "" &&
      new HardwareInfo("vendor", "gpu", "Vulkan", "1.2.3").GpuDriverVersion == "1.2.3",
    "GAL hardware info preserves three-argument backend compatibility and explicit driver versions");
Console.WriteLine($"PASS: {checks} checks; profiling evidence: {scratch}");
