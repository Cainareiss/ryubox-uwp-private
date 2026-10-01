using ARMeilleure.Translation;
using LibHac.Common;
using LibHac.Fs;
using LibHac.Fs.Fsa;
using LibHac.FsSystem;
using LibHac.Tools.Fs;
using LibHac.Tools.FsSystem;
using LibHac.Ncm;
using LibHac.Tools.FsSystem.NcaUtils;
using Ryujinx.Audio.Backends.XAudio2Uwp;
using Ryujinx.Audio.Renderer.Dsp;
using Ryujinx.Common;
using Ryujinx.Common.Configuration;
using Ryujinx.Common.Configuration.Multiplayer;
using Ryujinx.Common.Logging;
using Ryujinx.Common.Logging.Targets;
using Ryujinx.Common.Memory;
using Ryujinx.Cpu;
using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Gpu;
using Ryujinx.Graphics.D3D11;
using Ryujinx.Graphics.D3D12;
using Ryujinx.Graphics.Gpu.Engine.GPFifo;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Vulkan;
using Ryujinx.HLE;
using Ryujinx.HLE.FileSystem;
using Ryujinx.HLE.HOS;
using Ryujinx.HLE.HOS.Applets;
using Ryujinx.HLE.HOS.Services.Account.Acc;
using Ryujinx.HLE.HOS.Services.Hid;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;
using Ryujinx.HLE.HOS.SystemState;
using Ryujinx.HLE.HOS.Kernel.Threading;
using Ryujinx.HLE.HOS.Kernel.Common;
using Ryujinx.HLE.HOS.Kernel.SupervisorCall;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using System;
using System.Collections.Generic;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Path = System.IO.Path;

namespace Ryujinx.Uwp.CoreHost
{
    public static class CoreHostExports
    {
        public enum CoreHostSessionState
        {
            Idle = 0,
            Starting = 1,
            Running = 2,
            Stopping = 3,
            Faulted = 4,
        }

        private enum GameContainerKind
        {
            Unknown,
            Nsp,
            Xci,
            Nro,
            Nca,
        }

        private static readonly object Sync = new object();
        // These values are sampled by the UWP composition thread while the
        // emulation thread updates them. A stale value can hand a held A
        // button back to the frontend for one frame.
        private static volatile bool _started;
        private static volatile bool _coreBound;
        private static Switch _switch;
        private static VirtualFileSystem _virtualFileSystem;
        private static CancellationTokenSource _runStop;
        private static Thread _runThread;
        private static ManualResetEventSlim _runLoopReady;
        private static string _runLoopStartupError;
        private static bool _d3d11CoreSessionActive;
        private static bool _d3d11LocalWindowSession;
        private static CancellationTokenSource _startupWatchdogStop;
        private static Thread _startupWatchdogThread;
        private static long _startupStartedAt;
        private static long _runLoopStartedAt;
        private static long _runLoopLastProgressAt;
        private static long _runLoopIterationCount;
        private static long _processedFrameCount;
        private static long _presentedFrameCount;
        private static int _sessionState = (int)CoreHostSessionState.Idle;
        private static string _lastError = string.Empty;
        private static CoreHostSettings _activeSettings = CoreHostSettings.CreateDefault();
        private static bool _d3d11PresentErrorLogged;
        private static int _d3d11ShaderSequence;
        private static readonly object D3D11ShaderCacheSync = new object();
        private static readonly Dictionary<string, byte[]> D3D11ShaderBytecodeCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private const int D3D11ShaderBytecodeCacheLimit = 1024;
        private static long _d3d11CompatibilityFrameCount;
        private static int _d3d11FrameCaptureCompleted;
        private static int _d3d12FrameCaptureCompleted;
        private static int _d3d12FrameCaptureRequested;
        private static int _d3d12FrameCaptureAttempts;
        private static D3D12Renderer _d3d12CaptureRenderer;
        private static ulong _d3d12FrontendLease;
        private static D3D12Renderer _d3d12SessionRenderer;
        private static bool _d3d12HandoffFailed;
        private static byte[] _d3d11PresentationFrame = Array.Empty<byte>();
        private static long _pokemonNextPresentationTimestamp;
        private static long _pokemonPacedFrameCount;
        private static long _pokemonRepeatedFrameCount;
        private static long _lastNativePresentSuccess;
        private static long _lastNativePresentAttempts;
        private static long _lastD3D12PresentSuccess;
        private static long _lastD3D12PresentAttempts;
        private static long _d3d12SessionPresentBaseline;
        private static long _lastNativeCopySuccess;
        private static long _lastNativeUploadSuccess;
        private static long _nextSessionId;
        private static long _activeSessionId;
        private static long _sessionStartedUtcTicks;
        private static int _deviceLost;
        private static readonly long D3D11CaptureMinimumSequence = ReadD3D11CaptureMinimumSequence();
        private static readonly long D3D12CaptureMinimumSequence = ReadD3D12CaptureMinimumSequence();
        private static readonly long D3D12CaptureSeriesInterval = ReadD3D12CaptureSeriesInterval();
        private static D3D11SpirvCrossCompiler _d3d11SpirvCompiler;
        private static ulong _activeProgramId;
        private static int _bootAssetMask;
        private const int BootAssetNintendoLogo = 1;
        private const int BootAssetStartupMovie = 2;
        private static bool _useHandheldController;
        private static int _crashTelemetryInstalled;
        private static readonly object LocalWindowSync = new object();
        private static IntPtr _localWindowHandle;
        private static IntPtr _localWindowInstance;
        private static string _localWindowClassName;
        private static NativeWindowProc _localWindowProc = LocalWindowProc;
        private static readonly HashSet<uint> LocalWindowHeldKeys = new();

        private const uint HeadlessSurfaceStructureType = 1000256000;
        private const ulong GiB = 1024UL * 1024UL * 1024UL;

        public static CoreHostSessionState SessionState =>
            (CoreHostSessionState)Volatile.Read(ref _sessionState);

        public static long ActiveSessionId => Interlocked.Read(ref _activeSessionId);

        static CoreHostExports()
        {
            RuntimeMemoryBudget.TrimRequested += HandleMemoryTrimRequested;
        }

        private static long ReadD3D11CaptureMinimumSequence()
        {
            const long DefaultSequence = 1800;

            string value = Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_FRAME_MIN_SEQUENCE");
            return long.TryParse(value, out long sequence)
                ? Math.Clamp(sequence, 1, 100000)
                : DefaultSequence;
        }

        private static long ReadD3D12CaptureMinimumSequence()
        {
            // D3D12 owns its back buffer and can read it directly. A small
            // delay avoids recording a startup clear as evidence of a game.
            const long DefaultSequence = 60;
            string value = Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_CAPTURE_FRAME_MIN_SEQUENCE");
            return long.TryParse(value, out long sequence)
                ? Math.Clamp(sequence, 1, 100000)
                : DefaultSequence;
        }

        private static long ReadD3D12CaptureSeriesInterval()
        {
            const long DefaultInterval = 300;
            string value = Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_CAPTURE_SERIES_INTERVAL");
            return long.TryParse(value, out long interval)
                ? Math.Clamp(interval, 1, 1800)
                : DefaultInterval;
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_Start",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int Start(
            IntPtr gamePath,
            IntPtr keysPath,
            IntPtr firmwarePath,
            IntPtr backend,
            IntPtr settingsJson,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit)
        {
            return StartInternal(gamePath, IntPtr.Zero, keysPath, firmwarePath, backend,
                settingsJson, appMemoryUsageLimit, expectedAppMemoryUsageLimit);
        }

        private static int StartInternal(
            IntPtr gamePath,
            IntPtr gameReadHandle,
            IntPtr keysPath,
            IntPtr firmwarePath,
            IntPtr backend,
            IntPtr settingsJson,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit)
        {
            lock (Sync)
            {
                if (Volatile.Read(ref _sessionState) != (int)CoreHostSessionState.Idle || _started)
                {
                    CoreHostLog.Write("session", "Previous session not idle; performing cleanup before starting new session");
                    StopCore();
                    if (Volatile.Read(ref _sessionState) != (int)CoreHostSessionState.Idle)
                    {
                        _lastError = "A sessao anterior ainda esta encerrando; novo inicio bloqueado para proteger a memoria.";
                        return unchecked((int)0x80004005);
                    }
                }

                Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Starting);
                long sessionId = Interlocked.Increment(ref _nextSessionId);
                Interlocked.Exchange(ref _activeSessionId, sessionId);
                Interlocked.Exchange(ref _sessionStartedUtcTicks, DateTime.UtcNow.Ticks);
                Interlocked.Exchange(ref _deviceLost, 0);
                _lastError = string.Empty;
                Volatile.Write(ref _bootAssetMask, 0);
                if (gamePath == IntPtr.Zero || keysPath == IntPtr.Zero || backend == IntPtr.Zero)
                {
                    Interlocked.CompareExchange(ref _activeSessionId, 0, sessionId);
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Idle);
                    return unchecked((int)0x80070057);
                }

                string game = Marshal.PtrToStringUni(gamePath);
                string keys = Marshal.PtrToStringUni(keysPath);
                string selectedBackend = CoreHostSettings.NormalizeBackend(Marshal.PtrToStringUni(backend));
                string serializedSettings = settingsJson == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringUni(settingsJson) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(game)
                    || string.IsNullOrWhiteSpace(keys)
                    || string.IsNullOrWhiteSpace(selectedBackend))
                {
                    Interlocked.CompareExchange(ref _activeSessionId, 0, sessionId);
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Idle);
                    return unchecked((int)0x80070057);
                }

                CoreHostLog.SetSessionContext(sessionId, Path.GetFileName(game));

                // Match the proven 0.2.0.99 UWP bootstrap. LibHac must use the
                // package-aware Win32 open path, and the brokered handle is
                // validated directly before it becomes the global session handle.
                AppContext.SetSwitch("Ryujinx.Uwp.UseCreateFileFromApp", true);
                _activeSettings = CoreHostSettings.FromJson(serializedSettings);
                GraphicsConfig.ResScale = _activeSettings.ResolutionScale;
                // The upstream per-title shader cache is separate from the
                // native DXBC cache. Opt in only for D3D12 diagnostics until
                // its loading and memory cost are validated on Xbox.
                GraphicsConfig.EnableShaderCache =
                    string.Equals(selectedBackend, "D3D12", StringComparison.OrdinalIgnoreCase) &&
                    _activeSettings.ShaderCache;
                CoreHostLog.Write("graphics",
                    $"Guest shader cache enabled={GraphicsConfig.EnableShaderCache}; backend={selectedBackend}");
                CoreHostLog.Write(
                    "graphics",
                    string.Format("Internal resolution scale applied before game GPU creation: {0:F2}x", GraphicsConfig.ResScale));
                // The ABI argument is the renderer selected by the frontend
                // for this exact session.  Keep it authoritative: older
                // snapshots always serialized D3D11 and used to silently
                // redirect an explicit D3D12 CoreHost test to Vulkan.
                _activeSettings.Backend = selectedBackend;
                Environment.SetEnvironmentVariable("RYUJINX_UWP_ENABLE_PTC", _activeSettings.Ptc ? "1" : "0");
                RuntimeMemoryBudget.ResetSession();
                RuntimeMemoryBudget.Configure(appMemoryUsageLimit, expectedAppMemoryUsageLimit);

                string firmware = firmwarePath == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(firmwarePath) ?? string.Empty;

                // A previous live/faulted session was already cleaned at the
                // top of StartInternal. Do not run the full modern teardown on
                // a fresh process: unlike the much smaller 0.2.0.99 teardown,
                // it resets JIT/commit/GPU state and can block before the first
                // CoreHost log is initialized.
                Ryujinx.Graphics.D3D11.D3D11DebugTelemetry.Enabled =
                    _activeSettings.GraphicsDebugLogging ||
                    string.Equals(Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D11_DEBUG"), "1", StringComparison.OrdinalIgnoreCase);
                Ryujinx.Graphics.D3D11.D3D11DebugTelemetry.Reset();
                Ryujinx.Graphics.D3D11.UwpD3D11WindowTelemetry.BeginSession(sessionId);
                _d3d11PresentErrorLogged = false;
                _d3d11CompatibilityFrameCount = 0;
                _d3d11FrameCaptureCompleted = 0;
                _d3d12FrameCaptureCompleted = 0;
                _d3d12FrameCaptureRequested = 0;
                _d3d12FrameCaptureAttempts = 0;
                _d3d12CaptureRenderer = null;
                Interlocked.Exchange(ref _pokemonNextPresentationTimestamp, 0);
                Interlocked.Exchange(ref _pokemonPacedFrameCount, 0);
                Interlocked.Exchange(ref _pokemonRepeatedFrameCount, 0);
                Interlocked.Exchange(ref _lastNativePresentSuccess, 0);
                Interlocked.Exchange(ref _lastNativePresentAttempts, 0);
                Interlocked.Exchange(ref _lastD3D12PresentSuccess, 0);
                Interlocked.Exchange(ref _lastD3D12PresentAttempts, 0);
                Interlocked.Exchange(ref _lastNativeCopySuccess, 0);
                Interlocked.Exchange(ref _lastNativeUploadSuccess, 0);
                ResetStartupTelemetry();
                string stage = "preparando o sandbox";
                IRenderer renderer = null;
                bool rendererOwnedBySwitch = false;
                GameContainerKind gameContainer = GameContainerKind.Unknown;

                try
                {
                    string baseDirectory = ResolveBaseDirectory(keys);
                    CoreHostLog.Initialize(baseDirectory);
                    CoreHostLog.Write("session", $"LaunchRequested; containerPath='{game}'");
                    InstallCrashTelemetry();
                    InitializeRyujinxLog(baseDirectory);
                    stage = "validando acesso ao arquivo do jogo";
                    using (FileStream gameProbe = HostFileStream.OpenRead(game, gameReadHandle))
                    {
                        if (gameProbe.Length == 0 || gameProbe.ReadByte() < 0)
                        {
                            throw new InvalidDataException("O arquivo do jogo esta vazio.");
                        }
                        gameProbe.Position = 0;
                        gameContainer = DetectGameContainer(gameProbe, game);
                        CoreHostLog.Write("session", $"Game access validated: brokered={gameReadHandle != IntPtr.Zero}; bytes={gameProbe.Length}");
                        CoreHostLog.Write("loader", $"[Session {sessionId}] Container detected: kind={gameContainer}, path='{game}'");
                    }
                    HostFileStream.RegisterBrokeredReadHandle(game, gameReadHandle);
                    CoreHostLog.Write(
                        "settings",
                        string.Format(
                            "Ryubox snapshot schema={0}; backend={1}; mode={2}; language={3}; region={4}; vsync={5}; ptc={6}; memory={7}; internet={8}; scale={9:F2}; gpuThreading={10}",
                            _activeSettings.SchemaVersion,
                            selectedBackend,
                            _activeSettings.ConsoleMode,
                            _activeSettings.Language,
                            _activeSettings.Region,
                            _activeSettings.VSync,
                            _activeSettings.Ptc,
                            _activeSettings.MemoryManagerMode,
                            _activeSettings.GuestInternetAccess,
                            _activeSettings.ResolutionScale,
                            _activeSettings.GpuThreading));
                    CoreHostLog.Write("bootstrap", "NativeAOT reflection roots revision 2");
                    CoreHostLog.Write(
                        "session",
                        string.Format(
                            "Start game='{0}', keys='{1}', firmware='{2}', backend='{3}', currentMemoryLimit={4}, expectedMemoryLimit={5}",
                            game,
                            keys,
                            firmware,
                            selectedBackend,
                            appMemoryUsageLimit,
                            expectedAppMemoryUsageLimit));

                    stage = "inicializando AppDataManager";
                    AppDataManager.Initialize(baseDirectory);
                    CoreHostLog.Write("bootstrap", "AppDataManager initialized at " + AppDataManager.BaseDirPath);

                    if (string.Equals(selectedBackend, "D3D12", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            string cacheDirectory = D3D12CacheDirectory.Prepare(AppDataManager.BaseDirPath);
                            Environment.SetEnvironmentVariable("RYUJINX_UWP_D3D12_CACHE_DIR", cacheDirectory);
                            CoreHostLog.Write("graphics", "Native DXBC cache writable: " + cacheDirectory +
                                "; independent of guest shaderCache setting");
                        }
                        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException)
                        {
                            // Persistence is optional; never fail game startup for a cache path.
                            Environment.SetEnvironmentVariable("RYUJINX_UWP_D3D12_CACHE_DIR", null);
                            CoreHostLog.Write("graphics", "Native DXBC cache path setup failed: " + exception.Message);
                        }
                    }

                    stage = "copiando e carregando prod.keys/title.keys";
                    CopyKeysIntoAppData(keys);
                    CoreHostLog.Write("keys", "Keyset reloaded from " + AppDataManager.KeysDirPath);

                    _virtualFileSystem ??= VirtualFileSystem.CreateInstance();
                    _virtualFileSystem.ReloadKeySet();

                    stage = "inicializando serviços Horizon";
                    CoreHostLog.Write("horizon", "Initializing Horizon filesystem and system clients");
                    LibHacHorizonManager horizonManager = new();
                    horizonManager.InitializeFsServer(_virtualFileSystem);
                    horizonManager.InitializeArpServer();
                    horizonManager.InitializeBcatServer();
                    horizonManager.InitializeSystemClients();

                    ContentManager contentManager = new(_virtualFileSystem);
                    AccountManager accountManager = new(horizonManager.RyujinxClient);
                    UserChannelPersistence persistence = new();

                    bool firmwareAlreadyInstalled = HasInstalledFirmwareContent();
                    if (!string.IsNullOrWhiteSpace(firmware) && !firmwareAlreadyInstalled)
                    {
                        stage = "instalando firmware: " + firmware;
                        if (!HasInstallableFirmwarePayload(firmware))
                        {
                            throw new InvalidDataException(
                                "A origem do firmware não contém arquivos NCA instaláveis. "
                                + "Selecione um ZIP/XCI válido ou a pasta Contents/registered com os NCAs.");
                        }

                        CoreHostLog.Write("firmware", "Installing firmware from " + firmware);
                        contentManager.InstallFirmware(firmware);
                        CoreHostLog.Write("firmware", "Firmware installation finished");
                    }
                    else if (firmwareAlreadyInstalled)
                    {
                        CoreHostLog.Write(
                            "firmware",
                            string.IsNullOrWhiteSpace(firmware)
                                ? "Firmware already installed; reusing sandbox content"
                                : "Firmware already installed; skipped redundant installation for this game");
                    }
                    else
                    {
                        stage = "validando firmware instalado";
                        throw new FileNotFoundException(
                            "Nenhum firmware válido está instalado no sandbox UWP. "
                            + "Instale o firmware antes de iniciar o jogo.");
                    }

                    // Horizon services can query installed system content as
                    // soon as Switch starts. Prime the index before creating
                    // the emulation context so this does not race service
                    // initialization on the UWP worker threads.
                    stage = "indexando conteúdo do firmware";
                    contentManager.LoadEntries();
                    CoreHostLog.Write("firmware", "Content index loaded");

                    const ulong FontStandardTitleId = 0x0100000000000811;
                    string fontStandardPath = contentManager.GetInstalledContentPath(
                        FontStandardTitleId,
                        StorageId.BuiltInSystem,
                        NcaContentType.Data);
                    if (string.IsNullOrWhiteSpace(fontStandardPath))
                    {
                        throw new InvalidDataException(
                            "Firmware incompleto: o título de sistema FontStandard (0100000000000811) não foi encontrado. "
                            + "Reinstale um dump completo de firmware antes de iniciar jogos 3D.");
                    }
                    CoreHostLog.Write("firmware", "Required system title FontStandard indexed at " + fontStandardPath);

                    if (string.Equals(selectedBackend, "D3D11", StringComparison.OrdinalIgnoreCase))
                    {
                        stage = "criando renderer Direct3D 11 nativo";
                        if (UseLocalWin32Surface())
                        {
                            EnsureLocalWin32Window();
                            int localHostResult = Native_D3D11_InitializeForWindow(
                                _localWindowHandle,
                                1920,
                                1080);
                            if (localHostResult < 0)
                            {
                                throw new InvalidOperationException(string.Format(
                                    "Falha ao inicializar host D3D11 local (HRESULT 0x{0:X8}).",
                                    localHostResult));
                            }

                            _d3d11LocalWindowSession = true;
                            CoreHostLog.Write("d3d11", "Local visible D3D11 host initialized at 1920x1080");
                        }
                        EnsureD3D11Surface();
                        // 0.2.0.99 transferred the swap-chain exactly once,
                        // before the GAL queried its native objects. Keeping
                        // the frontend as owner until after LoadNsp introduced
                        // a second, late ownership transition and could leave
                        // real guest frames hidden behind the launch overlay.
                        int ownershipResult = Native_D3D11_BeginCoreSessionV2(unchecked((ulong)sessionId));
                        if (ownershipResult < 0)
                        {
                            throw new InvalidOperationException(string.Format(
                                "Falha ao transferir o host D3D11 para a sessao do emulador (HRESULT 0x{0:X8}).",
                                ownershipResult));
                        }
                        _d3d11CoreSessionActive = true;
                        CoreHostLog.Write("d3d11", "Native graphics ownership transferred before GAL initialization (0.2.0.99 startup contract)");

                        int nativeResult = Native_D3D11_GetNativeObjects(
                            out IntPtr device,
                            out IntPtr deviceContext,
                            out IntPtr swapChain);
                        if (nativeResult < 0 || device == IntPtr.Zero || deviceContext == IntPtr.Zero)
                        {
                            throw new InvalidOperationException(string.Format(
                                "Falha ao obter objetos D3D11 nativos (HRESULT 0x{0:X8}).",
                                nativeResult));
                        }

                        Native_D3D11_GetSurfaceSize(out uint surfaceWidth, out uint surfaceHeight);
                        string packageDirectory = Environment.GetEnvironmentVariable("RYUJINX_UWP_PACKAGE_DIR");
                        if (string.IsNullOrWhiteSpace(packageDirectory))
                        {
                            packageDirectory = AppContext.BaseDirectory;
                        }

                        string spirvCrossPath = Path.Combine(packageDirectory, "spirv-cross-c-shared.dll");
                        if (!File.Exists(spirvCrossPath))
                        {
                            throw new FileNotFoundException(
                                "SPIRV-Cross para o renderer D3D11 nao foi encontrado no pacote.",
                                spirvCrossPath);
                        }

                        _d3d11SpirvCompiler = new D3D11SpirvCrossCompiler(
                            spirvCrossPath,
                            CompileD3D11Hlsl,
                            message => CoreHostLog.Write("d3d11-spirv", message));
                        D3D11HostContext hostContext = new(
                            device,
                            deviceContext,
                            swapChain,
                            checked((int)surfaceWidth),
                            checked((int)surfaceHeight),
                            presentWithSyncIntervalCallback: interval => PresentD3D11RendererFrameForSession(sessionId, interval),
                            deviceLostCallback: result => RecordD3D11DeviceLoss(sessionId, result),
                            swapChainResizeCallback: (width, height) =>
                                IsActiveSession(sessionId) ? ResizeD3D11SwapChain(width, height) : IntPtr.Zero,
                            shaderCompiler: CompileD3D11Hlsl,
                            spirvCompiler: _d3d11SpirvCompiler.Compile,
                            presentTextureCallback: (resource, crop) =>
                            {
                                if (IsActiveSession(sessionId)) CopyD3D11RendererTexture(resource, crop);
                            },
                            presentTextureTransformCallback: (resource, crop, flipY) =>
                                IsActiveSession(sessionId)
                                    ? CopyD3D11RendererTextureTransformed(resource, crop, flipY)
                                    : unchecked((int)0x80004004),
                            spirvCompilerWithLayout: _d3d11SpirvCompiler.Compile,
                            compatibilityPresentCallback: PresentD3D11CompatibilityFrame,
                            // Preserve the title profile for diagnostics and
                            // fallback decisions. Native GPU presentation is
                            // still preferred; compatibility is used only
                            // when a frame cannot be presented directly.
                            forceCompatibilityPresentCallback: () => IsCompatibilityPresentationTitle(_activeProgramId),
                            // Titles on the compatibility list stop presenting
                            // after their first native swap-chain frames. The
                            // compositor is top-down and now applies the same
                            // global crop/orientation contract as the direct
                            // presenter; this is not a per-title flip hack.
                            preferNativeTransformCallback: () => !IsCompatibilityPresentationTitle(_activeProgramId));

                        renderer = D3D11Renderer.Create(hostContext);
                        renderer.Window.ChangeVSyncMode(_activeSettings.VSync ? VSyncMode.Switch : VSyncMode.Unbounded);
                        CoreHostLog.Write("presentation", $"[Session {sessionId}] D3D11 VSync={_activeSettings.VSync}, SyncInterval={hostContext.PresentSyncInterval}, PresentFlags=0");
                        CoreHostLog.Write("graphics", string.Format(
                            "Native D3D11 GAL selected: device=0x{0:X}, context=0x{1:X}, surface={2}x{3}; ownership=0x{4:X}; Vulkan/Dozen bypassed",
                            device.ToInt64(),
                            deviceContext.ToInt64(),
                            surfaceWidth,
                            surfaceHeight,
                            Native_D3D11_GetOwnershipState()));
                    }
                    else if (string.Equals(selectedBackend, "D3D12", StringComparison.OrdinalIgnoreCase))
                    {
                        bool localD3D12 = UseLocalWin32Surface();
                        stage = "criando renderer Direct3D 12 nativo";
                        if (localD3D12) EnsureLocalWin32Window();
                        const uint LocalD3D12Width = 1280;
                        const uint LocalD3D12Height = 720;
                        string packageDirectory = Environment.GetEnvironmentVariable("RYUJINX_UWP_PACKAGE_DIR");
                        if (string.IsNullOrWhiteSpace(packageDirectory))
                        {
                            packageDirectory = AppContext.BaseDirectory;
                        }

                        string spirvCrossPath = Path.Combine(packageDirectory, "spirv-cross-c-shared.dll");
                        if (!File.Exists(spirvCrossPath))
                        {
                            throw new FileNotFoundException(
                                "SPIRV-Cross para o renderer D3D12 nao foi encontrado no pacote.",
                                spirvCrossPath);
                        }

                        D3D12DeviceFlags d3d12Flags = _activeSettings.GraphicsDebugLogging
                            ? D3D12DeviceFlags.DebugLayer
                            : D3D12DeviceFlags.Hardware;
                        // The desktop smoke host can opt into WARP when an
                        // installed vendor D3D12 driver crashes inside its
                        // user-mode component. This is deliberately local
                        // and opt-in: packaged/UWP and Xbox paths retain the
                        // hardware adapter.
                        bool localWarp = localD3D12 && string.Equals(
                            Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_D3D12_WARP"),
                            "1",
                            StringComparison.Ordinal);
                        if (localWarp)
                        {
                            d3d12Flags |= D3D12DeviceFlags.Warp;
                        }
                        D3D12Renderer d3d12Renderer;
                        if (localD3D12)
                        {
                            d3d12Renderer = new(LocalD3D12Width, LocalD3D12Height,
                                spirvCrossPath, _localWindowHandle, flags: d3d12Flags);
                        }
                        else
                        {
                            int acquireResult = Native_D3D12Frontend_Acquire((ulong)sessionId, out IntPtr context);
                            Marshal.ThrowExceptionForHR(acquireResult);
                            _d3d12FrontendLease = (ulong)sessionId;
                            d3d12Renderer = D3D12Renderer.FromFrontendLease(context, spirvCrossPath);
                            CoreHostLog.Write("presentation", $"[Session {sessionId}] D3D12 frontend lease acquired; shared SwapChainPanel; no D3D11 fallback");
                        }
                        renderer = d3d12Renderer;
                        // The shared context already contains frontend presents.
                        // Snapshot before publishing the renderer to UI readers.
                        var initialPresentation = d3d12Renderer.Device.GetPerformanceStats();
                        Interlocked.Exchange(ref _d3d12SessionPresentBaseline, checked((long)initialPresentation.PresentSuccess));
                        Interlocked.Exchange(ref _lastD3D12PresentSuccess, checked((long)initialPresentation.PresentSuccess));
                        Interlocked.Exchange(ref _lastD3D12PresentAttempts, checked((long)initialPresentation.PresentAttempts));
                        _d3d12SessionRenderer = d3d12Renderer;
                        _d3d12HandoffFailed = false;
                        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_FRAME_PATH")))
                        {
                            d3d12Renderer.ScreenCaptured += CaptureD3D12Frame;
                            _d3d12CaptureRenderer = d3d12Renderer;
                            CoreHostLog.Write("d3d12-frame", "D3D12 frame capture enabled for this local session.");
                        }
                        renderer.Window.ChangeVSyncMode(_activeSettings.VSync ? VSyncMode.Switch : VSyncMode.Unbounded);
                        D3D12DeviceInfo surfaceInfo = d3d12Renderer.Device.GetInfo();
                        CoreHostLog.Write(
                            "graphics",
                            $"Native D3D12 GAL selected: surface={surfaceInfo.Width}x{surfaceInfo.Height}; " +
                            $"window=0x{_localWindowHandle.ToInt64():X}; debug={_activeSettings.GraphicsDebugLogging}; " +
                            $"warp={localWarp}; UWP panel lease={!localD3D12}");
                    }
                    else
                    {
                        bool useLocalWin32Surface = UseLocalWin32Surface();
                        stage = useLocalWin32Surface
                            ? "configurando Vulkan local com superficie Win32"
                            : "configurando ICD Vulkan Mesa Dozen";
                        ConfigureVulkanDriver(selectedBackend, useLocalWin32Surface);

                        stage = useLocalWin32Surface
                            ? "criando renderer Vulkan local"
                            : "criando renderer Vulkan/Mesa Dozen";
                        CoreHostLog.Write(
                            "vulkan",
                            useLocalWin32Surface
                                ? "Creating VulkanRenderer with VK_KHR_win32_surface for local validation"
                                : "Creating VulkanRenderer with VK_EXT_headless_surface");
                        VulkanRenderer vulkanRenderer = VulkanRenderer.Create(
                            string.Empty,
                            useLocalWin32Surface ? CreateLocalWin32Surface : CreateHeadlessSurface,
                            useLocalWin32Surface
                                ? static () => new[] { "VK_KHR_surface", "VK_KHR_win32_surface" }
                                : static () => new[] { "VK_KHR_surface", "VK_EXT_headless_surface" });
                        vulkanRenderer.InitializationStageCallback = message => CoreHostLog.Write("vulkan", message);
                        renderer = vulkanRenderer;
                    }

                    // The supplied ImGui model exposes this as a host setting.
                    // Apply it only at renderer construction time; changing it
                    // while a game is running would invalidate GPU ownership.
                    // D3D12 command recording is already bound to the HLE GPU
                    // thread.  Do not introduce a second GAL worker until its
                    // ownership/interrupt path is covered by a CoreHost smoke
                    // test. D3D11/Vulkan retain their user-selected behavior.
                    bool enableBackendThreading = _activeSettings.GpuThreading &&
                        !string.Equals(selectedBackend, "D3D12", StringComparison.OrdinalIgnoreCase);
                    renderer = renderer.TryMakeThreaded(
                        enableBackendThreading ? BackendThreading.On : BackendThreading.Off);
                    CoreHostLog.Write(
                        "graphics",
                        "BackendThreading=" + (enableBackendThreading ? "On" : "Off") +
                        (string.Equals(selectedBackend, "D3D12", StringComparison.OrdinalIgnoreCase)
                            ? " (D3D12 local CoreHost safety policy)"
                            : " (applied at launch)"));

                    // Initialize the renderer before reserving the Switch DRAM
                    // block. Xbox Dev Mode has only about 5 GiB available.
                    stage = "inicializando renderer " + selectedBackend;
                    CoreHostLog.Write("memory", string.Format(
                        "Before renderer init: workingSet={0} bytes, managed={1} bytes",
                        Environment.WorkingSet,
                        GC.GetTotalMemory(false)));
                    renderer.Initialize(_activeSettings.GraphicsDebugLogging
                        ? GraphicsDebugLevel.Error
                        : GraphicsDebugLevel.None);
                    CoreHostLog.Write("graphics", selectedBackend + " renderer initialized before Switch memory reservation");

                    // Preserve the retail Switch memory arrangement (3285 MiB
                    // application pool). A local validation with AppletDev proved
                    // that Shovel Knight requests virtually the entire reduced
                    // 2 GiB pool and then triggers a guest SVC Break. The Xbox fix
                    // is the private SoftwarePageTable backing below, not changing
                    // the memory contract visible to games.
                    MemoryConfiguration memoryConfiguration = MemoryConfiguration.MemoryConfiguration4GiB;
                    // Do not infer expanded guest DRAM from a title/path or the
                    // host budget override. Games size their heaps from this
                    // arrangement: advertising 6 GiB can turn a retail-sized
                    // allocation into a multi-GiB commit that cannot fit UWP.
                    string constrainedLocalTestMarker = Path.Combine(
                        AppDataManager.BaseDirPath,
                        "local-test-2gb-memory.txt");
                    if (File.Exists(constrainedLocalTestMarker))
                    {
                        // A desktop test machine can have less commit available
                        // than Xbox Dev Mode even when it has more physical RAM.
                        // Keep this opt-in and local: the normal Xbox package has
                        // no marker and therefore retains the retail 4 GiB layout.
                        memoryConfiguration = MemoryConfiguration.MemoryConfiguration4GiBAppletDev;
                        CoreHostLog.Write(
                            "memory",
                            "Local constrained-memory marker detected; using the 2 GiB application pool for this run");
                    }

                    CoreHostLog.Write(
                        "memory",
                        string.Format(
                            "Selected guest memory arrangement={0}; currentHostLimit={1} MiB, expectedHostLimit={2} MiB",
                            memoryConfiguration,
                            appMemoryUsageLimit / (1024UL * 1024UL),
                            expectedAppMemoryUsageLimit / (1024UL * 1024UL)));

                    bool localPerformanceTest = string.Equals(
                        Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_PERFORMANCE_TEST"),
                        "1",
                        StringComparison.OrdinalIgnoreCase);
                    bool rivalsPerformanceProfile = IsRivalsOfAetherPath(game);
                    // Pinned dispatch deliberately returns to the host after
                    // every guest call/jump so code can be reclaimed immediately.
                    // That is safe but catastrophically expensive for games with
                    // dense control flow (Rivals crossed the host boundary more
                    // than 235 million times before its first useful frame).
                    //
                    // Epoch dispatch can only reclaim cold translations after
                    // every native guest reader crosses a safe boundary. Under
                    // Xbox's tight process commit budget, allocation can fail
                    // inside that reader, so recovery cannot immediately reuse
                    // the retired code. Prefer pinned dispatch on device builds;
                    // retain an explicit opt-out for controlled perf diagnostics.
                    bool usePinnedSafeJit = rivalsPerformanceProfile || !string.Equals(
                        Environment.GetEnvironmentVariable("RYUJINX_UWP_FAST_EPOCH_JIT"),
                        "1",
                        StringComparison.OrdinalIgnoreCase);
                    bool useSafeJit = usePinnedSafeJit;
                    // Local diagnostics can isolate block-poll instrumentation
                    // from pin/epoch ownership without changing either default.
                    // This is not a shipping per-title workaround.
                    if (localPerformanceTest)
                    {
                        string safeJitOverride = Environment.GetEnvironmentVariable("RYUJINX_UWP_TEST_SAFE_JIT");
                        if (safeJitOverride == "0" || safeJitOverride == "1")
                        {
                            useSafeJit = safeJitOverride == "1";
                            CoreHostLog.Write("performance", $"Local JIT diagnostic override: safeJit={useSafeJit}, pinnedDispatch={usePinnedSafeJit}");
                        }
                    }
                    bool useNativeDispatch = !string.Equals(
                        Environment.GetEnvironmentVariable("RYUJINX_UWP_MANAGED_PINNED_DISPATCH"),
                        "1", StringComparison.Ordinal);
                    // High-CQ background replacement caused large cold-start
                    // memory growth and competes with the guest on Series S.
                    // Keep it disabled for both the shipping profile and normal
                    // desktop comparisons. It remains an explicit diagnostics-only
                    // opt-in so it can never be enabled accidentally by the old
                    // PerformanceMode flag.
                    bool enableBackgroundHighCq = localPerformanceTest && string.Equals(
                        Environment.GetEnvironmentVariable("RYUJINX_UWP_ALLOW_BACKGROUND_HIGHCQ"),
                        "1",
                        StringComparison.OrdinalIgnoreCase);
                    bool deepTraceEnabled = string.Equals(
                        Environment.GetEnvironmentVariable("RYUJINX_UWP_DEEP_TRACE"),
                        "1",
                        StringComparison.OrdinalIgnoreCase);
                    // Pinned dispatch returns every guest call/jump to the host
                    // dispatcher, so there is no nested native guest call stack
                    // for the recursion guard to protect. A compatibility run
                    // matching this flat call flow, together with stable JIT
                    // retention, restored Pokemon Z-A's initialization path.
                    // Epoch/native dispatch still nests translated calls and
                    // retains the upstream guard.
                    bool enableDeepCallProtection = !usePinnedSafeJit;
                    if (localPerformanceTest)
                    {
                        string deepCallOverride = Environment.GetEnvironmentVariable("RYUJINX_UWP_TEST_DEEP_CALL_PROTECTION");
                        if (deepCallOverride == "0" || deepCallOverride == "1")
                        {
                            enableDeepCallProtection = deepCallOverride == "1";
                        }
                    }

                    AppContext.SetSwitch("Ryujinx.Uwp.SingleAddressSpaceView", false);
                    AppContext.SetSwitch("Ryujinx.Uwp.LazyGuestCommit", true);
                    AppContext.SetSwitch("Ryujinx.Uwp.ConstrainedScheduling", true);
                    AppContext.SetSwitch("Ryujinx.Uwp.PinnedJitDispatch", usePinnedSafeJit);
                    AppContext.SetSwitch("Ryujinx.Uwp.SafeJit", useSafeJit);
                    AppContext.SetSwitch("Ryujinx.Uwp.DisableBackgroundTranslation", !enableBackgroundHighCq);
                    AppContext.SetSwitch("Ryujinx.Uwp.BaselineNativeDispatch", useNativeDispatch);
                    // Large titles also exceed the old two-region ceiling.
                    // Reserve additional regions lazily, subject to JitCache's
                    // process-budget admission check. Always assign this per
                    // session so a previous title cannot change the next limit.
                    AppContext.SetData("Ryujinx.Uwp.JitCacheMaxRegions", 8);
                    // Finer JIT reservation steps (same total capacity). Read
                    // once per process by JitCache; later sessions keep it.
                    AppContext.SetData("Ryujinx.Uwp.JitCacheRegionMiB", 128);
                    AppContext.SetSwitch("Ryujinx.Uwp.D3D12DisableGuestCounters", true);
                    AppContext.SetSwitch("Ryujinx.Uwp.SafeAudioRenderer", true);
                    AppContext.SetSwitch("Ryujinx.Uwp.AudioPcmStartupGuard", true);
                    // D3D11 performs implicit resource hazard tracking. The
                    // Xbox profile therefore avoids a costly driver Flush at
                    // every GAL barrier; strict mode remains available via
                    // RYUJINX_UWP_D3D11_STRICT_BARRIERS.
                    AppContext.SetSwitch("Ryujinx.Uwp.D3D11.DeferImplicitBarriers", true);
                    // Environment.TickCount64 only advances every millisecond. That
                    // coarse global replacement caused the kernel timer queue and
                    // condition-variable wakeups to bunch together when the game
                    // created its render/audio threads. Keep the instance-local,
                    // synchronized guest clock, but feed it from QPC like upstream.
                    AppContext.SetSwitch("Ryujinx.Uwp.SafeMonotonicClock", false);
                    AppContext.SetSwitch("Ryujinx.Uwp.PortableTimers", true);
                    AppContext.SetSwitch("Ryujinx.Uwp.DeepTrace", deepTraceEnabled);
                    ARMeilleure.Optimizations.UseUnmanagedDispatchLoop = useNativeDispatch;
                    ARMeilleure.Optimizations.AllowLcqInFunctionTable = !usePinnedSafeJit;
                    ARMeilleure.Optimizations.EnableDeepCallRecursionProtection = enableDeepCallProtection;
                    CoreHostLog.Write(
                        "scheduler",
                        string.Format(
                            "ARMeilleure applied config: safeJit={0}, unmanagedDispatch={1}, allowLcqInTable={2}, backgroundThreads={3}, highCqEnabled={4}; pinnedDispatch={5}; dispatchPolicy={6}; deepCallProtection={7}; flatPinnedCallFlow={8}",
                            useSafeJit,
                            ARMeilleure.Optimizations.UseUnmanagedDispatchLoop,
                            ARMeilleure.Optimizations.AllowLcqInFunctionTable,
                            enableBackgroundHighCq ? 1 : 0,
                            enableBackgroundHighCq,
                            usePinnedSafeJit,
                            usePinnedSafeJit
                                ? "native-pinned-v2"
                                : rivalsPerformanceProfile
                                    ? "rivals-native-epoch-baseline-v2"
                                    : "native-epoch-baseline-v1",
                            ARMeilleure.Optimizations.EnableDeepCallRecursionProtection,
                            usePinnedSafeJit && !ARMeilleure.Optimizations.EnableDeepCallRecursionProtection));
                    if (rivalsPerformanceProfile)
                    {
                        CoreHostLog.Write(
                            "scheduler",
                            "Rivals startup profile applied: pinned native dispatch, background High-CQ disabled, process JIT admission and eviction enabled");
                    }
                    CoreHostLog.Write(
                        "memory",
                        "Xbox safe memory mode enabled: SoftwarePageTable + private reserved DRAM + retail 3285 MiB application pool");
                    CoreHostLog.Write(
                        "memory",
                        "HostMapped mirrored sections disabled; private 64 KiB fault-driven commits enabled (0x5AF structural fix)");
                    CoreHostLog.Write(
                        "scheduler",
                        usePinnedSafeJit
                            ? $"Pinned Safe-JIT recovery profile enabled: {(useNativeDispatch ? "native" : "managed")} pinned baseline dispatch, High-CQ/rejit disabled"
                            : $"Xbox fast epoch-JIT enabled: {(useNativeDispatch ? "native" : "managed")} baseline dispatch, LCQ function-table links active, High-CQ/rejit {(enableBackgroundHighCq ? "enabled for diagnostics" : "disabled")}");
                    CoreHostLog.Write(
                        "audio",
                        "Xbox safe audio renderer enabled: bounded DSP waits, 5 ms external clock, PCM late-fill guard and event fallback");
                    CoreHostLog.Write(
                        "audio",
                        "Native XAudio2 UWP output enabled; PCM buffers use AudioCategory_GameMedia and are copied into the Xbox system audio engine");
                    CoreHostLog.Write(
                        "clock",
                        "High-resolution Stopwatch/QPC clock restored; guest counter state remains instance-local and synchronized; UWP timer waits use sandbox-safe wait handles");
                    CoreHostLog.Write(
                        "deep-trace",
                        deepTraceEnabled
                            ? "Deep trace enabled: correlated clock, guest dispatch/SVC, kernel thread, compositor, GPU and memory samples"
                            : "Deep trace disabled: normal guest logging retained without per-dispatch instrumentation");

                    HleConfiguration configuration = new HleConfiguration(
                        memoryConfiguration,
                        ToSystemLanguage(_activeSettings.Language),
                        ToRegionCode(_activeSettings.Region),
                        _activeSettings.VSync ? VSyncMode.Switch : VSyncMode.Unbounded,
                        // Always emulate the handheld profile. It avoids
                        // docked clocks and keeps the Xbox memory budget lower.
                        false,
                        // PTC remains opt-in on NativeAOT/UWP. The default is
                        // safe JIT; setting RYUJINX_UWP_ENABLE_PTC=1 enables a
                        // diagnostic build/session after the first-frame path
                        // has been validated on the target Xbox.
                        _activeSettings.Ptc && IsPtcOptedIn(),
                        ITickSource.RealityTickScalar,
                        _activeSettings.GuestInternetAccess,
                        _activeSettings.FsIntegrityChecks ? IntegrityCheckLevel.ErrorOnInvalid : IntegrityCheckLevel.None,
                        _activeSettings.FsAccessLogs ? 1 : 0,
                        _activeSettings.SystemTimeOffsetMinutes * TimeSpan.TicksPerMinute,
                        _activeSettings.TimeZone,
                        // The Xbox UWP sandbox repeatedly returned Win32 0x5AF
                        // while committing the HostMapped paging-file section.
                        // SoftwarePageTable stays the default. HostMappedUnsafe is
                        // opt-in (RYUJINX_UWP_MEMORY_MODE=hostunsafe) and falls back
                        // to SoftwarePageTable automatically if DRAM or the address
                        // space cannot be created.
                        ResolveMemoryManagerMode(),
                        _activeSettings.IgnoreMissingServices,
                        ToAspectRatio(_activeSettings.AspectRatio),
                        _activeSettings.AudioEnabled ? _activeSettings.AudioVolume : 0.0f,
                        false,
                        string.Empty,
                        ToMultiplayerMode(_activeSettings.MultiplayerMode),
                        false,
                        string.Empty,
                        string.Empty,
                        false,
                        0,
                        false,
                        _activeSettings.VSync ? 16 : 0)
                        .Configure(
                            _virtualFileSystem,
                            horizonManager,
                            contentManager,
                            accountManager,
                            persistence,
                            renderer,
                            new XAudio2UwpHardwareDeviceDriver(),
                            new UwpHostUiHandler());

                    stage = "criando instância do Switch HLE";
                    _switch = new Switch(configuration);
                    _switch.GuestFaultHandler = exception => RecordSessionFault(sessionId, "guest CPU", exception);
                    rendererOwnedBySwitch = true;
                    CoreHostLog.Write("horizon", "Switch HLE instance created");
                    _useHandheldController = IsPokemonLetsGoPath(game);
                    CoreHostLog.Write(
                        "session",
                        string.Format(
                            "Handheld profile forced: dockedMode=false, standard clocks; controller={0}",
                            _useHandheldController ? "Handheld (Pokemon Let's Go)" : "ProController"));
                    CoreHostLog.Write(
                        "performance",
                        "PTC enabled=" + IsPtcOptedIn() + "; default remains safe JIT unless explicitly opted in");
                    if (_useHandheldController)
                    {
                        _switch.Hid.Npads.Configure(new[]
                        {
                            new ControllerConfig
                            {
                                Player = PlayerIndex.Handheld,
                                Type = ControllerType.Handheld,
                            },
                        });
                    }
                    else
                    {
                        _switch.Hid.Npads.Configure(new[]
                        {
                            new ControllerConfig
                            {
                                Player = PlayerIndex.Player1,
                                Type = ControllerType.ProController,
                            },
                        });
                    }
                    CoreHostLog.Write("memory", string.Format(
                        "After Switch creation: workingSet={0} bytes, managed={1} bytes",
                        Environment.WorkingSet,
                        GC.GetTotalMemory(false)));

                    stage = "carregando " + gameContainer + ": " + game;
                    CoreHostLog.Write("loader", $"[Session {sessionId}] Loading {gameContainer} {game}");
                    if (_d3d11SpirvCompiler != null || deepTraceEnabled)
                    {
                        StartStartupWatchdog();
                    }

                    bool loadSucceeded = gameContainer switch
                    {
                        GameContainerKind.Xci => _switch.LoadXci(game),
                        GameContainerKind.Nro => _switch.LoadProgram(game),
                        GameContainerKind.Nca => _switch.LoadNca(game),
                        _ => _switch.LoadNsp(game),
                    };

                    if (!loadSucceeded)
                    {
                        // Container loading can fail after the firmware has already been
                        // validated.  Returning only E_INVALIDARG made the
                        // frontend present this as a missing firmware error,
                        // which was especially confusing for update/DLC-only packages.
                        _lastError =
                            $"Falha ao carregar o container {gameContainer} depois da validacao do firmware. " +
                            "O arquivo pode ser uma atualizacao/DLC sem o jogo base, estar corrompido " +
                            "ou não conter um Main NCA executavel. Consulte o log detalhado do Ryujinx.";
                        CoreHostLog.Write("loader", $"[Session {sessionId}] {_lastError}");
                        TryWriteErrorLog(_lastError);
                        StopCore();
                        return unchecked((int)0x80004005);
                    }

                    _activeProgramId = _switch.Processes.ActiveApplication?.ProgramId ?? 0;
                    TryCacheBootAssets(game, gameReadHandle, gameContainer, keys, _activeProgramId);
                    CoreHostLog.SetSessionContext(sessionId, $"{_activeProgramId:X16}");
                    CoreHostLog.Write(
                        "graphics",
                        string.Format(
                            "Presentation profile selected for title 0x{0:X16}: legacyVerticalOrder={1}, forceCompatibility={2}, preferNativeTransform={3}, orientation=single-global-complement-v2",
                            _activeProgramId,
                            false,
                            IsCompatibilityPresentationTitle(_activeProgramId),
                            true));

                    if (IsPokemonLetsGoTitle(_activeProgramId))
                    {
                        CoreHostLog.Write(
                            "performance",
                            "Pokemon frame pacing enabled: new frames=30 FPS, swap-chain VSync=60 Hz, handheld profile=true");
                    }

                    _started = true;
                    _coreBound = true;
                    StartRunLoop();
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Running);
                    CoreHostLog.Write("session", $"[Session {sessionId}] CoreHostStartSuccess; state=Running");
                    return 0;
                }
                catch (Exception exception)
                {
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                    _lastError = "Etapa: " + stage + Environment.NewLine + exception;
                    CoreHostLog.Error(stage, exception);
                    if (!rendererOwnedBySwitch)
                    {
                        try
                        {
                            renderer?.Dispose();
                        }
                        catch (Exception rendererException)
                        {
                            CoreHostLog.Error("liberando renderer antes do Switch", rendererException);
                        }
                    }
                    TryWriteErrorLog(_lastError);
                    try
                    {
                        StopCore();
                    }
                    catch (Exception disposeException)
                    {
                        _lastError += Environment.NewLine + "Falha ao liberar recursos: " + disposeException;
                        TryWriteErrorLog(_lastError);
                    }
                    return unchecked((int)0x80004005);
                }
            }
        }

        // Keep an owned duplicate of the authorized StorageFile handle for
        // the session: a brokered path alone does not grant Win32 file access.
        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_StartWithHandle",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int StartWithHandle(
            IntPtr gamePath,
            IntPtr gameReadHandle,
            IntPtr keysPath,
            IntPtr firmwarePath,
            IntPtr backend,
            IntPtr settingsJson,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit)
        {
            if (gameReadHandle == IntPtr.Zero || gameReadHandle == new IntPtr(-1))
            {
                return unchecked((int)0x80070057);
            }

            return StartInternal(
                gamePath,
                gameReadHandle,
                keysPath,
                firmwarePath,
                backend,
                settingsJson,
                appMemoryUsageLimit,
                expectedAppMemoryUsageLimit);
        }

        private static SystemLanguage ToSystemLanguage(string value)
        {
            return Enum.TryParse(value, true, out SystemLanguage result)
                ? result
                : SystemLanguage.AmericanEnglish;
        }

        private static RegionCode ToRegionCode(string value)
        {
            // The front end uses "USA"; Brazil is not a Switch RegionCode,
            // therefore it intentionally falls back to USA.
            if (string.Equals(value, "Brazil", StringComparison.OrdinalIgnoreCase)) return RegionCode.USA;
            return Enum.TryParse(value, true, out RegionCode result) ? result : RegionCode.USA;
        }

        private static AspectRatio ToAspectRatio(string value)
        {
            return Enum.TryParse(value, true, out AspectRatio result)
                ? result
                : AspectRatio.Fixed16x9;
        }

        private static MultiplayerMode ToMultiplayerMode(string value)
        {
            return Enum.TryParse(value, true, out MultiplayerMode result)
                ? result
                : MultiplayerMode.Disabled;
        }


        private static void InitializeRyujinxLog(string baseDirectory)
        {
            const string targetName = "uwp-corehost-file";
            string logDirectory = Path.Combine(baseDirectory, "Logs", "Ryujinx");
            try
            {
                // Trace includes every guest SVC and IPC call.  FileLogTarget
                // flushes every line, which can generate tens of megabytes and
                // starve the Xbox presentation thread during cold JIT startup.
                // Keep actionable diagnostics while moving file I/O off the
                // emulation threads and discarding overflow under heavy load.
                Logger.SetEnable(LogLevel.Trace, _activeSettings.ShaderTelemetry || _activeSettings.PresentationLogs);
                Logger.SetEnable(LogLevel.Debug, _activeSettings.GraphicsDebugLogging || _activeSettings.SyncStalls);
                Logger.SetEnable(LogLevel.Stub, false);
                Logger.SetEnable(LogLevel.Guest, _activeSettings.GuestLogs);
                Logger.SetEnable(LogLevel.AccessLog, _activeSettings.FsAccessLogs);
                Logger.SetEnable(LogLevel.NetLog, _activeSettings.GuestInternetAccess);
                Logger.SetEnable(LogLevel.Info, true);
                Logger.SetEnable(LogLevel.Warning, true);
                Logger.SetEnable(LogLevel.Error, true);
                Logger.SetConsoleTargetEnabled(false);
                Logger.RemoveTarget(targetName);

                Directory.CreateDirectory(logDirectory);
                if (!_activeSettings.FileLogging)
                {
                    CoreHostLog.Write("bootstrap", "Ryujinx file logging disabled by snapshot; CoreHost diagnostic logs remain enabled");
                    return;
                }
                string logPath = Path.Combine(
                    logDirectory,
                    $"Ryujinx_UWP_{DateTime.UtcNow:yyyy-MM-dd_HH-mm-ss-fff}.log");
                FileStream stream = File.Open(logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                Logger.AddTarget(new AsyncLogTargetWrapper(
                    new FileLogTarget(targetName, stream),
                    2048,
                    AsyncLogTargetOverflowAction.Discard));
                CoreHostLog.Write("bootstrap", "Ryujinx detailed log initialized at " + logPath);
                CoreHostLog.Write("bootstrap", "Ryujinx logging profile: Info/Warning/Error, asynchronous bounded queue");
            }
            catch (Exception exception)
            {
                // Diagnostics must never prevent the emulator from starting.
                CoreHostLog.Error("inicializando log detalhado do Ryujinx", exception);
            }
        }

        // Firmware installation is a standalone CoreHost ABI. The UWP
        // frontend installs a selected ZIP/XCI before any game session exists;
        // it must not have to call Start with a fake title.
        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_InstallFirmware",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int InstallFirmware(IntPtr keysPath, IntPtr firmwarePath)
        {
            lock (Sync)
            {
                _lastError = string.Empty;

                if (keysPath == IntPtr.Zero || firmwarePath == IntPtr.Zero || _started || _coreBound)
                {
                    return unchecked((int)0x80070057);
                }

                string keys = Marshal.PtrToStringUni(keysPath) ?? string.Empty;
                string firmware = Marshal.PtrToStringUni(firmwarePath) ?? string.Empty;

                if (string.IsNullOrWhiteSpace(keys) || string.IsNullOrWhiteSpace(firmware))
                {
                    return unchecked((int)0x80070057);
                }

                string stage = "preparando a instalacao de firmware";

                try
                {
                    string baseDirectory = ResolveBaseDirectory(keys);
                    CoreHostLog.Initialize(baseDirectory);
                    InstallCrashTelemetry();
                    InitializeRyujinxLog(baseDirectory);

                    stage = "inicializando AppDataManager";
                    AppDataManager.Initialize(baseDirectory);

                    stage = "copiando e carregando prod.keys/title.keys";
                    CopyKeysIntoAppData(keys);
                    // VirtualFileSystem is a process-wide singleton by design:
                    // Dispose does not reset its creation guard. Keep the same
                    // instance for firmware installation and the next game,
                    // while reloading the key set before each operation.
                    _virtualFileSystem ??= VirtualFileSystem.CreateInstance();
                    _virtualFileSystem.ReloadKeySet();

                    stage = "validando arquivo de firmware";
                    if (!HasInstallableFirmwarePayload(firmware))
                    {
                        throw new InvalidDataException(
                            "A origem do firmware nao contem arquivos NCA instalaveis. "
                            + "Selecione um ZIP/XCI valido ou uma pasta Contents/registered.");
                    }

                    stage = "instalando firmware: " + firmware;
                    CoreHostLog.Write("firmware", "Standalone firmware installation started from " + firmware);
                    ContentManager contentManager = new(_virtualFileSystem);
                    contentManager.InstallFirmware(firmware);

                    stage = "validando firmware instalado";
                    if (!HasInstalledFirmwareContent())
                    {
                        throw new InvalidDataException(
                            "O instalador terminou sem gravar NCAs em bis/system/Contents/registered.");
                    }

                    // Prove that a new in-process content index can consume the
                    // installation now. StartInternal reuses this VFS, reloads
                    // its keys and constructs another fresh ContentManager, so
                    // the first game no longer requires an app restart.
                    stage = "indexando firmware instalado na mesma sessao";
                    contentManager.LoadEntries();
                    const ulong FontStandardTitleId = 0x0100000000000811;
                    string fontStandardPath = contentManager.GetInstalledContentPath(
                        FontStandardTitleId,
                        StorageId.BuiltInSystem,
                        NcaContentType.Data);
                    if (string.IsNullOrWhiteSpace(fontStandardPath))
                    {
                        throw new InvalidDataException(
                            "Firmware incompleto: o titulo de sistema FontStandard (0100000000000811) nao foi encontrado apos a instalacao.");
                    }

                    CoreHostLog.Write(
                        "firmware",
                        "Standalone firmware installation finished, indexed in-process, and is ready for immediate launch");
                    return 0;
                }
                catch (Exception exception)
                {
                    _lastError = "Etapa: " + stage + Environment.NewLine + exception;
                    CoreHostLog.Error(stage, exception);
                    TryWriteErrorLog(_lastError);
                    return unchecked((int)0x80004005);
                }
            }
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_Stop",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static void Stop()
        {
            lock (Sync)
            {
                StopCore();
            }
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_SetInput",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static void SetInput(
            uint buttons,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            float leftTrigger,
            float rightTrigger)
        {
            lock (Sync)
            {
                UpdateInputUnsafe(buttons, leftX, leftY, rightX, rightY, leftTrigger, rightTrigger);
            }
        }

        // Caller must hold Sync. Both the exported controller bridge and the
        // desktop-only D3D12 keyboard route use this exact path, keeping the
        // Player1/Handheld compatibility update identical.
        private static void UpdateInputUnsafe(
            uint buttons,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            float leftTrigger,
            float rightTrigger)
        {
            if (!_started || _switch == null)
            {
                return;
            }

            ControllerKeys mappedButtons = MapButtons(buttons, leftTrigger, rightTrigger);
            GamepadInput input = new()
            {
                PlayerId = PlayerIndex.Player1,
                Buttons = mappedButtons,
                LStick = new JoystickPosition
                {
                    Dx = ToStickValue(leftX),
                    Dy = ToStickValue(leftY),
                },
                RStick = new JoystickPosition
                {
                    Dx = ToStickValue(rightX),
                    Dy = ToStickValue(rightY),
                },
            };

            GamepadInput handheldInput = input;
            handheldInput.PlayerId = PlayerIndex.Handheld;
            _switch.Hid.Npads.Update(new[] { input, handheldInput });
        }

        internal static void AutoConfigureControllers(ControllerAppletUIArgs args)
        {
            if (_switch == null)
            {
                return;
            }

            CoreHostLog.Write("hid", string.Format(
                "Auto-configuring controllers for ControllerApplet: min={0}, max={1}, styles={2}, isDocked={3}",
                args.PlayerCountMin,
                args.PlayerCountMax,
                args.SupportedStyles,
                args.IsDocked));

            ControllerType selectedType = ControllerType.ProController;
            PlayerIndex selectedPlayer = PlayerIndex.Player1;

            if ((args.SupportedStyles & ControllerType.ProController) != 0)
            {
                selectedType = ControllerType.ProController;
                selectedPlayer = PlayerIndex.Player1;
            }
            else if ((args.SupportedStyles & ControllerType.Handheld) != 0 && !args.IsDocked)
            {
                selectedType = ControllerType.Handheld;
                selectedPlayer = PlayerIndex.Handheld;
            }
            else if ((args.SupportedStyles & ControllerType.JoyconPair) != 0)
            {
                selectedType = ControllerType.JoyconPair;
                selectedPlayer = PlayerIndex.Player1;
            }
            else if ((args.SupportedStyles & ControllerType.JoyconLeft) != 0)
            {
                selectedType = ControllerType.JoyconLeft;
                selectedPlayer = PlayerIndex.Player1;
            }
            else if ((args.SupportedStyles & ControllerType.JoyconRight) != 0)
            {
                selectedType = ControllerType.JoyconRight;
                selectedPlayer = PlayerIndex.Player1;
            }

            _switch.Hid.Npads.Configure(new[]
            {
                new ControllerConfig
                {
                    Player = selectedPlayer,
                    Type = selectedType,
                },
            });
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_IsStarted",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int IsStarted()
        {
            return _started ? 1 : 0;
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_GetSessionState",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int GetSessionState()
        {
            return Volatile.Read(ref _sessionState);
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_GetSessionId",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static ulong GetSessionId()
        {
            return unchecked((ulong)Math.Max(0, ActiveSessionId));
        }

        // The UWP shell uses this as the only authority for dismissing its
        // launch screen. A successful Start() or a running guest thread is not
        // evidence that pixels reached the shared presentation surface.
        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_HasFirstVisiblePresent",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int HasFirstVisiblePresent()
        {
            // A successful DXGI Present is the strongest signal available at
            // this boundary, but it is not proof that a physical display showed
            // non-black pixels. Keep the historical export name for ABI users.
            if (string.Equals(_activeSettings?.Backend, "D3D12", StringComparison.OrdinalIgnoreCase))
            {
                D3D12Renderer renderer = Volatile.Read(ref _d3d12SessionRenderer);
                if (renderer == null) return 0;
                // Guest callback and a new native present are both required;
                // frontend frames must never dismiss the game's boot overlay.
                if (Interlocked.Read(ref _presentedFrameCount) == 0) return 0;
                try { return renderer.Device.GetPerformanceStats().PresentSuccess >
                    (ulong)Math.Max(0, Interlocked.Read(ref _d3d12SessionPresentBaseline)) ? 1 : 0; }
                catch { return 0; }
            }

            return Interlocked.Read(ref _presentedFrameCount) > 0 ? 1 : 0;
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_GetActiveProgramId",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static ulong GetActiveProgramId()
        {
            return Volatile.Read(ref _activeProgramId);
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_GetBootAssetMask",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int GetBootAssetMask()
        {
            return Volatile.Read(ref _bootAssetMask);
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_IsBound",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int IsBound()
        {
            return _coreBound ? 1 : 0;
        }

        [UnmanagedCallersOnly(
            EntryPoint = "RyujinxUwpCoreHost_GetLastError",
            CallConvs = new[] { typeof(CallConvStdcall) })]
        public static int GetLastError(IntPtr buffer, int capacity)
        {
            lock (Sync)
            {
                if (buffer == IntPtr.Zero || capacity <= 0)
                {
                    return _lastError.Length;
                }

                byte[] bytes = Encoding.Unicode.GetBytes(_lastError);
                int copyBytes = Math.Min(bytes.Length, Math.Max(0, (capacity - 1) * sizeof(char)));
                if ((copyBytes & 1) != 0)
                {
                    copyBytes--;
                }

                Marshal.Copy(bytes, 0, buffer, copyBytes);
                Marshal.WriteInt16(buffer, copyBytes, 0);
                return copyBytes / sizeof(char);
            }
        }

        private static void StopCore()
        {
            long sessionId = ActiveSessionId;
            Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Stopping);
            CoreHostLog.Write("session", $"[Session {sessionId}] TeardownBegin; state=Stopping");
            long workingSetBefore = Environment.WorkingSet;
            long managedBefore = GC.GetTotalMemory(false);
            StopStartupWatchdog("CoreHost stopping");
            CancellationTokenSource runStop = _runStop;
            runStop?.Cancel();

            Switch device = _switch;
            try
            {
                device?.Gpu.GPFifo.Interrupt();
            }
            catch (Exception exception)
            {
                CoreHostLog.Error("interrupting graphics FIFO during teardown", exception);
            }

            Thread runThread = _runThread;
            if (runThread != null && runThread != Thread.CurrentThread)
            {
                bool joined = runThread.Join(10000);
                CoreHostLog.Write("session", $"[Session {sessionId}] GraphicsThreadJoined={joined}");
                if (!joined)
                {
                    try { device?.Gpu.GPFifo.Interrupt(); } catch { }
                    joined = runThread.Join(2000);
                    CoreHostLog.Write("session", $"[Session {sessionId}] GraphicsThreadJoinedAfterInterrupt={joined}");
                }
                if (!joined)
                {
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                    _started = false;
                    _coreBound = false;
                    _lastError = "A thread GPU nao encerrou; recursos preservados e relaunch bloqueado.";
                    CoreHostLog.Write("session", $"[Session {sessionId}] TeardownDeferred; GPU still alive; resources retained");
                    return;
                }
            }
            _runThread = null;
            _runStop = null;

            ManualResetEventSlim runLoopReady = _runLoopReady;
            _runLoopReady = null;
            _runLoopStartupError = null;
            runLoopReady?.Dispose();
            runStop?.Dispose();

            _switch = null;
            _activeProgramId = 0;
            Volatile.Write(ref _bootAssetMask, 0);
            _useHandheldController = false;
            Interlocked.Exchange(ref _pokemonNextPresentationTimestamp, 0);
            Interlocked.Exchange(ref _pokemonPacedFrameCount, 0);
            Interlocked.Exchange(ref _pokemonRepeatedFrameCount, 0);
            try
            {
                // Stop Horizon services and all guest CPU threads before the
                // GPFIFO wait handle and renderer are released. Disposing the
                // GPU first allowed an nvservices thread to submit once more
                // and call Set() on an already disposed AutoResetEvent.
                device?.Dispose();
            }
            catch (Exception exception)
            {
                _lastError = string.IsNullOrWhiteSpace(_lastError)
                    ? "Falha ao liberar o Switch: " + exception
                    : _lastError + Environment.NewLine + "Falha ao liberar o Switch: " + exception;
                CoreHostLog.Error("liberando o Switch", exception);
                TryWriteErrorLog(_lastError);
            }

            try
            {
                string beforeReset = Ryujinx.Cpu.Jit.MemoryManager.GetUwpPrivateCommitTelemetry();
                Ryujinx.Cpu.Jit.MemoryManager.ResetUwpPrivateCommitRegistration();
                CoreHostLog.Write(
                    "memory",
                    "Cleared UWP lazy-commit registration after session stop; before=" + beforeReset +
                    "; after=" + Ryujinx.Cpu.Jit.MemoryManager.GetUwpPrivateCommitTelemetry());
            }
            catch (Exception exception)
            {
                CoreHostLog.Error("clearing UWP lazy-commit registration", exception);
            }

            try
            {
                device?.DisposeGpu();
            }
            catch (Exception exception)
            {
                // The renderer may not have completed initialization, but keep
                // the failure recorded so an Xbox shutdown race is diagnosable.
                CoreHostLog.Error("liberando GPU", exception);
                if (_d3d12FrontendLease != 0) _d3d12HandoffFailed = true;
            }
            try
            {
                _d3d11SpirvCompiler?.Dispose();
            }
            catch (Exception exception)
            {
                CoreHostLog.Error("liberando SPIRV-Cross D3D11", exception);
            }
            _d3d11SpirvCompiler = null;

            if (_d3d12FrontendLease != 0)
            {
                if (_d3d12HandoffFailed)
                {
                    _started = false;
                    _coreBound = false;
                    _lastError = "Cleanup D3D12 incompleto; contexto preservado. Reinicie o aplicativo.";
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                    return;
                }
                ulong lease = _d3d12FrontendLease;
                try
                {
                    int result = Native_D3D12Frontend_Release(lease);
                    Marshal.ThrowExceptionForHR(result);
                    _d3d12FrontendLease = 0;
                    CoreHostLog.Write("presentation", $"[Session {lease}] D3D12 context returned to frontend after GPU cleanup");
                }
                catch (Exception exception)
                {
                    CoreHostLog.Error("returning D3D12 frontend ownership", exception);
                    _lastError = "Falha ao devolver a superficie D3D12: " + exception.Message;
                    _started = false;
                    _coreBound = false;
                    Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                    return;
                }
            }

            if (_d3d11CoreSessionActive)
            {
                try
                {
                    int endResult = Native_D3D11_EndCoreSessionV2(unchecked((ulong)Math.Max(0, sessionId)));
                    CoreHostLog.Write(
                        "d3d11",
                        $"[Session {sessionId}] SwapChainDetached=true; ownership returned; GetDeviceRemovedReason=0x{endResult:X8}");
                    if (endResult < 0)
                    {
                        Interlocked.Exchange(ref _deviceLost, 1);
                    }
                }
                catch (Exception exception)
                {
                    CoreHostLog.Error("returning D3D11 ownership", exception);
                }
                _d3d11CoreSessionActive = false;
            }
            if (_d3d11LocalWindowSession)
            {
                try
                {
                    Native_D3D11_Shutdown();
                    CoreHostLog.Write("d3d11", "Local visible D3D11 host shut down");
                }
                catch (Exception exception)
                {
                    CoreHostLog.Error("shutting down local D3D11 host", exception);
                }
                _d3d11LocalWindowSession = false;
            }
            _started = false;
            _coreBound = false;
            _d3d12SessionRenderer = null;
            HostFileStream.ClearBrokeredReadHandle();
            DestroyLocalWin32Window();
            RuntimeMemoryBudget.ResetSession();
            Ryujinx.Graphics.D3D11.UwpD3D11WindowTelemetry.EndSession(sessionId);
            long workingSetAfter = Environment.WorkingSet;
            long managedAfter = GC.GetTotalMemory(false);
            CoreHostLog.Write(
                "memory",
                string.Format(
                    "Session cleanup complete: workingSetBefore={0}MB, workingSetAfter={1}MB (delta={2}MB), managedBefore={3}MB, managedAfter={4}MB",
                    workingSetBefore / (1024 * 1024),
                    workingSetAfter / (1024 * 1024),
                    (workingSetAfter - workingSetBefore) / (1024 * 1024),
                    managedBefore / (1024 * 1024),
                    managedAfter / (1024 * 1024)));
            Interlocked.CompareExchange(ref _activeSessionId, 0, sessionId);
            Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Idle);
            CoreHostLog.Write(
                "session",
                $"[Session {sessionId}] TeardownEnd; state=Idle; deviceLost={Volatile.Read(ref _deviceLost) != 0}; DeviceReleaseRequired={Volatile.Read(ref _deviceLost) != 0}");
            CoreHostLog.ClearSessionContext(sessionId);
        }

        private static void StartRunLoop()
        {
            CancellationTokenSource runStop = new();
            ManualResetEventSlim ready = new(false);
            _runStop = runStop;
            _runLoopReady = ready;
            _runLoopStartupError = null;
            CoreHostLog.Write("session", "Starting emulation loop");
            _runThread = new Thread(() =>
            {
                try { RunLoop(runStop); }
                finally
                {
                    try { _d3d12SessionRenderer?.CompleteGpuThread(); }
                    catch (Exception exception)
                    {
                        _d3d12HandoffFailed = true;
                        CoreHostLog.Error("finishing D3D12 GPU owner thread", exception);
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Ryujinx.Uwp.CoreHost.RunLoop",
                Priority = ThreadPriority.AboveNormal,
            };
            Interlocked.Exchange(ref _runLoopStartedAt, Environment.TickCount64);
            Interlocked.Exchange(ref _runLoopLastProgressAt, Environment.TickCount64);
            _runThread.Start();

            // Do not report a successful launch until the dedicated GPU thread
            // owns D3D11 and the shader cache is ready. This mirrors MTGS::WaitForOpen.
            if (!ready.Wait(TimeSpan.FromSeconds(30)))
            {
                runStop.Cancel();
                throw new TimeoutException(
                    "A thread grafica D3D11 nao concluiu o handshake em 30 segundos.");
            }

            if (!string.IsNullOrWhiteSpace(_runLoopStartupError))
            {
                throw new InvalidOperationException(
                    "A thread grafica D3D11 falhou durante o handshake." + Environment.NewLine +
                    _runLoopStartupError);
            }

            CoreHostLog.Write("session", "Graphics thread handshake completed");
        }

        private static void ResetStartupTelemetry()
        {
            long now = Environment.TickCount64;
            Interlocked.Exchange(ref _startupStartedAt, now);
            Interlocked.Exchange(ref _runLoopStartedAt, 0);
            Interlocked.Exchange(ref _runLoopLastProgressAt, now);
            Interlocked.Exchange(ref _runLoopIterationCount, 0);
            Interlocked.Exchange(ref _processedFrameCount, 0);
            Interlocked.Exchange(ref _presentedFrameCount, 0);
        }

        private static void StartStartupWatchdog()
        {
            StopStartupWatchdog("restarting startup watchdog");

            CancellationTokenSource watchdogStop = new();
            CancellationToken token = watchdogStop.Token;
            long watchdogSessionId = ActiveSessionId;
            // A loading animation can present forever while a guest worker is
            // stuck. Opt-in desktop diagnostics must survive that first frame;
            // keep the normal startup-only behavior for packaged builds.
            bool monitorAfterFirstFrame = UseLocalWin32Surface() &&
                Environment.GetEnvironmentVariable("RYUJINX_UWP_SESSION_WATCHDOG") == "1";
            Thread watchdogThread = new Thread(() =>
            {
                long previousIterations = -1;
                long previousDispatchLookups = -1;
                long previousPerfTicks = -1;
                long previousRawStopwatchTicks = -1;
                long previousEnvironmentMilliseconds = -1;
                long sampleId = 0;
                int consecutiveStalls = 0;

                while (!token.WaitHandle.WaitOne(2500) && ActiveSessionId == watchdogSessionId)
                {
                    long now = Environment.TickCount64;
                    long perfTicks = PerformanceCounter.ElapsedTicks;
                    long rawStopwatchTicks = System.Diagnostics.Stopwatch.GetTimestamp();
                    long currentSampleId = ++sampleId;
                    long iterations = Interlocked.Read(ref _runLoopIterationCount);
                    long processed = Interlocked.Read(ref _processedFrameCount);
                    long presented = Interlocked.Read(ref _presentedFrameCount);
                    long runLoopStarted = Interlocked.Read(ref _runLoopStartedAt);
                    long lastProgress = Interlocked.Read(ref _runLoopLastProgressAt);
                    long stalledFor = Math.Max(0, now - lastProgress);

                    int deviceStatus;
                    uint ownershipState;
                    try
                    {
                        deviceStatus = Native_D3D11_GetDeviceStatus();
                        ownershipState = Native_D3D11_GetOwnershipState();
                    }
                    catch (Exception exception)
                    {
                        CoreHostLog.Error("querying D3D11 device status", exception);
                        deviceStatus = unchecked((int)0x80004005);
                        ownershipState = 0;
                    }

                    string translatorTelemetry = Translator.GetUwpSchedulingTelemetry();
                    string guestThreadTelemetry = UwpGuestThreadTelemetry.GetSnapshot();
                    string svcTelemetry = UwpSvcTelemetry.GetSnapshot();
                    string timeManagerTelemetry = UwpTimeManagerTelemetry.GetSnapshot();
                    string kernelCriticalTelemetry = UwpKernelCriticalSectionTelemetry.GetSnapshot();
                    string gpfifoTelemetry = UwpGpfifoTelemetry.GetSnapshot();
                    string guestClockTelemetry = _switch?.GetUwpTimingTelemetry() ?? "guestClock=unavailable";
                    string surfaceTelemetry = UwpSurfaceFlingerTelemetry.GetSnapshot();
                    string audioTelemetry = AudioProcessor.GetUwpTelemetry();
                    string memoryCommitTelemetry =
                        "memoryMode=SoftwarePageTable, guestPool=3285MiB, mirroredSection=disabled; " +
                        Ryujinx.Cpu.Jit.MemoryManager.GetUwpPrivateCommitTelemetry();
                    string memoryBudgetTelemetry = RuntimeMemoryBudget.Sample(
                        Environment.WorkingSet,
                        GC.GetTotalMemory(false));
                    string jitTelemetry = ARMeilleure.Translation.Cache.JitCache.GetDetailedTelemetry().ToString();
                    string d3d11WindowTelemetry = Ryujinx.Graphics.D3D11.UwpD3D11WindowTelemetry.GetSnapshot();
                    string d3d11DebugMessages = Ryujinx.Graphics.D3D11.D3D11DebugTelemetry.Drain();
                    if (!string.IsNullOrWhiteSpace(d3d11DebugMessages))
                    {
                        CoreHostLog.Write("d3d11-debug", $"sample={currentSampleId}; {d3d11DebugMessages.TrimEnd()}");
                    }
                    string presentationTelemetry;
                    try
                    {
                        int telemetryResult = Native_D3D11_GetPresentationTelemetry(out D3D11PresentationTelemetry nativeTelemetry);
                        presentationTelemetry = telemetryResult < 0
                            ? $"presentationTelemetryResult=0x{telemetryResult:X8}"
                            : $"guestFramesProduced={Interlocked.Read(ref _processedFrameCount)}; {nativeTelemetry.ToLogString()}";
                    }
                    catch (Exception exception)
                    {
                        presentationTelemetry = $"presentationTelemetryException={exception.GetType().Name}: {exception.Message}";
                    }
                    long dispatchLookups = ParseTelemetryCounter(translatorTelemetry, "dispatchLookups=");
                    bool loopAdvanced = iterations != previousIterations;
                    bool guestAdvanced = dispatchLookups < 0 || dispatchLookups != previousDispatchLookups;
                    long perfDelta = previousPerfTicks < 0 ? 0 : perfTicks - previousPerfTicks;
                    long rawStopwatchDelta = previousRawStopwatchTicks < 0 ? 0 : rawStopwatchTicks - previousRawStopwatchTicks;
                    long environmentDelta = previousEnvironmentMilliseconds < 0 ? 0 : now - previousEnvironmentMilliseconds;

                    CoreHostLog.Write(
                        "clock",
                        string.Format(
                            "sample={0}; envDelta={1}ms; perfDelta={2}; rawStopwatchDelta={3}; {4}; {5}",
                            currentSampleId,
                            environmentDelta,
                            perfDelta,
                            rawStopwatchDelta,
                            PerformanceCounter.GetUwpClockTelemetry(),
                            guestClockTelemetry));
                    CoreHostLog.Write(
                        "guest-dispatch",
                        string.Format("sample={0}; advanced={1}; {2}", currentSampleId, guestAdvanced, translatorTelemetry));
                    CoreHostLog.Write(
                        "guest-threads",
                        string.Format("sample={0}; {1}", currentSampleId, guestThreadTelemetry));
                    CoreHostLog.Write(
                        "kernel-sync",
                        string.Format("sample={0}; {1}; {2}; {3}", currentSampleId, svcTelemetry, kernelCriticalTelemetry, timeManagerTelemetry));
                    CoreHostLog.Write(
                        "gpfifo",
                        string.Format("sample={0}; {1}", currentSampleId, gpfifoTelemetry));
                    CoreHostLog.Write(
                        "compositor",
                        string.Format(
                            "sample={0}; {1}; {2}; {3}",
                            currentSampleId,
                            surfaceTelemetry,
                            d3d11WindowTelemetry,
                            presentationTelemetry));
                    CoreHostLog.Write(
                        "audio-trace",
                        string.Format("sample={0}; {1}", currentSampleId, audioTelemetry));
                    CoreHostLog.Write(
                        "resource-trace",
                        string.Format(
                            "sample={0}; workingSet={1}; managed={2}; appMemoryLimit={3}; loopIterations={4}; processed={5}; presented={6}; ownership=0x{7:X}; deviceStatus=0x{8:X8}; {9}",
                            currentSampleId,
                            Environment.WorkingSet,
                            GC.GetTotalMemory(false),
                            GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                            iterations,
                            processed,
                            presented,
                            ownershipState,
                            deviceStatus,
                            memoryCommitTelemetry + "; " + memoryBudgetTelemetry + "; " + jitTelemetry));

                    CoreHostLog.Write(
                        "watchdog",
                        string.Format(
                            "sample={0}; Startup state: phase={1}, uptime={2} ms, loopIterations={3}, loopAdvanced={4}, processed={5}, presented={6}, loopStall={7} ms, ownership=0x{8:X}, deviceStatus=0x{9:X8}; {10}; {11}; {12}; {13}; {14}; {15}; {16}; {17}; {18}",
                            currentSampleId,
                            runLoopStarted == 0 ? "LoadNsp" : "GuestRunning",
                            now - Interlocked.Read(ref _startupStartedAt),
                            iterations,
                            loopAdvanced,
                            processed,
                            presented,
                            stalledFor,
                            ownershipState,
                            deviceStatus,
                            translatorTelemetry,
                            guestThreadTelemetry,
                            guestClockTelemetry,
                            surfaceTelemetry,
                            audioTelemetry,
                            svcTelemetry,
                            kernelCriticalTelemetry,
                            gpfifoTelemetry,
                            d3d11WindowTelemetry));

                    if (presented > 0 && !monitorAfterFirstFrame)
                    {
                        CoreHostLog.Write("watchdog", "Startup watchdog completed after first guest frame");
                        return;
                    }

                    if (runLoopStarted != 0 && !loopAdvanced && !guestAdvanced && stalledFor >= 5000)
                    {
                        consecutiveStalls++;
                        CoreHostLog.Write(
                            "stall-analysis",
                            string.Format(
                                "sample={0}; Scheduling stall confirmed ({1} consecutive samples): GPU loop and guest dispatch made no progress; process remains alive; {2}; {3}; {4}; {5}; {6}; {7}; {8}; {9}",
                                currentSampleId,
                                consecutiveStalls,
                                guestClockTelemetry,
                                translatorTelemetry,
                                guestThreadTelemetry,
                                surfaceTelemetry,
                                audioTelemetry,
                                svcTelemetry,
                                kernelCriticalTelemetry,
                                gpfifoTelemetry));
                    }
                    else
                    {
                        consecutiveStalls = 0;
                    }

                    previousIterations = iterations;
                    previousDispatchLookups = dispatchLookups;
                    previousPerfTicks = perfTicks;
                    previousRawStopwatchTicks = rawStopwatchTicks;
                    previousEnvironmentMilliseconds = now;
                }
            })
            {
                IsBackground = true,
                Name = "Ryujinx.Uwp.CoreHost.StartupWatchdog",
                Priority = ThreadPriority.AboveNormal,
            };

            _startupWatchdogStop = watchdogStop;
            _startupWatchdogThread = watchdogThread;
            watchdogThread.Start();
            CoreHostLog.Write("watchdog", "Startup watchdog started before LoadNsp");
        }

        private static string GetNativePresentationDelta(long elapsedMilliseconds, out double acceptedPresentFps)
        {
            acceptedPresentFps = 0;
            try
            {
                if (Volatile.Read(ref _d3d12SessionRenderer) is D3D12Renderer d3d12Renderer)
                {
                    D3D12PerformanceStats d3d12Telemetry = d3d12Renderer.Device.GetPerformanceStats();
                    long previousSuccess = Interlocked.Exchange(ref _lastD3D12PresentSuccess, checked((long)d3d12Telemetry.PresentSuccess));
                    long previousD3D12Attempts = Interlocked.Exchange(ref _lastD3D12PresentAttempts, checked((long)d3d12Telemetry.PresentAttempts));
                    ulong successDelta = d3d12Telemetry.PresentSuccess - (ulong)Math.Max(0, previousSuccess);
                    ulong attemptsDelta = d3d12Telemetry.PresentAttempts - (ulong)Math.Max(0, previousD3D12Attempts);
                    string presentationTarget = d3d12Telemetry.PresentationTarget switch
                    {
                        1 => "hwnd",
                        2 => "composition-panel",
                        _ => "offscreen",
                    };
                    if (elapsedMilliseconds > 0) acceptedPresentFps = successDelta * 1000.0 / elapsedMilliseconds;
                    return string.Format(
                        "d3d12Present={0}/{1}, d3d12PresentSequence={2}, d3d12LastPresent=0x{3:X8}, d3d12LastPresentFence={4}, d3d12PresentQpc={5}, qpcFrequency={6}, d3d12Target={7}, d3d12Surface={8}x{9}",
                        successDelta,
                        attemptsDelta,
                        d3d12Telemetry.PresentSuccess,
                        d3d12Telemetry.LastPresentResult,
                        d3d12Telemetry.LastPresentFence,
                        d3d12Telemetry.LastPresentQpc,
                        System.Diagnostics.Stopwatch.Frequency,
                        presentationTarget,
                        d3d12Telemetry.LastPresentWidth,
                        d3d12Telemetry.LastPresentHeight);
                }

                if (!string.Equals(_activeSettings?.Backend, "D3D11", StringComparison.OrdinalIgnoreCase))
                {
                    return "nativePresentTelemetry=unavailable";
                }

                int result = Native_D3D11_GetPresentationTelemetry(out D3D11PresentationTelemetry telemetry);
                if (result < 0)
                {
                    return string.Format("nativePresentTelemetry=0x{0:X8}", result);
                }

                long previousPresent = Interlocked.Exchange(ref _lastNativePresentSuccess, checked((long)telemetry.PresentSuccess));
                long previousAttempts = Interlocked.Exchange(ref _lastNativePresentAttempts, checked((long)telemetry.PresentAttempts));
                long previousCopy = Interlocked.Exchange(ref _lastNativeCopySuccess, checked((long)telemetry.CopySuccess));
                long previousUpload = Interlocked.Exchange(ref _lastNativeUploadSuccess, checked((long)telemetry.UploadSuccess));
                ulong presentDelta = telemetry.PresentSuccess - (ulong)Math.Max(0, previousPresent);
                if (elapsedMilliseconds > 0) acceptedPresentFps = presentDelta * 1000.0 / elapsedMilliseconds;
                return string.Format(
                    "nativePresent={0}/{1}, nativeCopy={2}, nativeUpload={3}, lastPresent=0x{4:X8}",
                    telemetry.PresentSuccess - (ulong)Math.Max(0, previousPresent),
                    telemetry.PresentAttempts - (ulong)Math.Max(0, previousAttempts),
                    telemetry.CopySuccess - (ulong)Math.Max(0, previousCopy),
                    telemetry.UploadSuccess - (ulong)Math.Max(0, previousUpload),
                    telemetry.LastPresentResult);
            }
            catch (Exception exception)
            {
                return "nativePresentTelemetryException=" + exception.GetType().Name;
            }
        }

        private static long ParseTelemetryCounter(string telemetry, string prefix)
        {
            int start = telemetry.IndexOf(prefix, StringComparison.Ordinal);
            if (start < 0)
            {
                return -1;
            }

            start += prefix.Length;
            int end = telemetry.IndexOf(',', start);
            string value = end < 0 ? telemetry.Substring(start) : telemetry.Substring(start, end - start);
            return long.TryParse(value, out long parsed) ? parsed : -1;
        }

        private static void StopStartupWatchdog(string reason)
        {
            CancellationTokenSource watchdogStop = Interlocked.Exchange(ref _startupWatchdogStop, null);
            Thread watchdogThread = Interlocked.Exchange(ref _startupWatchdogThread, null);

            if (watchdogStop == null)
            {
                return;
            }

            bool watchdogJoined = watchdogThread == null || !watchdogThread.IsAlive;
            try
            {
                watchdogStop.Cancel();
                if (watchdogThread != null && watchdogThread != Thread.CurrentThread)
                {
                    watchdogJoined = watchdogThread.Join(1000);
                }
                CoreHostLog.Write("watchdog", "Startup watchdog cancellation: joined=" + watchdogJoined + "; " + reason);
            }
            finally
            {
                // The cancelled worker can still be reading its WaitHandle.
                // If busy, leave ownership to GC after that worker exits.
                if (watchdogJoined) watchdogStop.Dispose();
            }
        }

        private static void RunLoop(CancellationTokenSource runStop)
        {
            long loopIterationCount = 0;
            long processedFrameCount = 0;
            long presentedFrameCount = 0;
            long intervalProcessedFrames = 0;
            long intervalPresentedFrames = 0;
            long intervalStartMilliseconds = Environment.TickCount64;
            long descriptorSampleMilliseconds = intervalStartMilliseconds;
            D3D12DescriptorStats previousDescriptorStats = default;
            D3D12PerformanceStats previousPerformanceStats = default;
            D3D12PerformanceStats startupPerformanceStats = default;
            const long StartupDrawTraceLimit = 64;

            Switch initializedSwitch = _switch;
            long sessionId = ActiveSessionId;
            try
            {
                if (initializedSwitch == null)
                {
                    throw new InvalidOperationException("Switch HLE ausente ao iniciar a thread GPU.");
                }

                if (_d3d11CoreSessionActive)
                {
                    int bindResult = Native_D3D11_BindGraphicsThread();
                    if (bindResult < 0)
                    {
                        throw new InvalidOperationException(string.Format(
                            "Falha ao vincular a thread GPU ao host D3D11 (HRESULT 0x{0:X8}).",
                            bindResult));
                    }
                    CoreHostLog.Write(
                        "d3d11",
                        string.Format(
                            "GPU thread became the exclusive presentation owner; ownership=0x{0:X}",
                            Native_D3D11_GetOwnershipState()));
                }

                initializedSwitch.Gpu.SetGpuThread();
                if (_d3d12SessionRenderer is D3D12Renderer sessionRenderer)
                {
                    previousPerformanceStats = sessionRenderer.Device.GetPerformanceStats();
                    startupPerformanceStats = previousPerformanceStats;
                    previousDescriptorStats = sessionRenderer.Device.GetDescriptorStats();
                }
                CoreHostLog.Write("session", "GPU thread registered");
                initializedSwitch.Gpu.InitializeShaderCache(runStop.Token);
                CoreHostLog.Write("session", "Shader cache initialized; entering FIFO loop");
            }
            catch (Exception exception)
            {
                _runLoopStartupError = exception.ToString();
                _lastError = "Etapa: handshake da thread grafica" + Environment.NewLine + exception;
                CoreHostLog.Error("handshake da thread grafica", exception);
                TryWriteErrorLog(_lastError);
                _runLoopReady?.Set();
                return;
            }

            _runLoopReady?.Set();

            while (!runStop.IsCancellationRequested && IsActiveSession(sessionId))
            {
                Switch current = _switch;

                if (current == null)
                {
                    return;
                }

                try
                {
                    if (current.WaitFifo())
                    {
                        long frameNumber = processedFrameCount + 1;
                        long processFrameStart = Environment.TickCount64;
                        if (frameNumber <= 32)
                        {
                            CoreHostLog.Write(
                                "session",
                                string.Format("ProcessFrame {0} begin", frameNumber));
                        }

                        current.Statistics.RecordFifoStart();
                        current.ProcessFrame();
                        current.Statistics.RecordFifoEnd();
                        DrainD3D11DebugTelemetry("after-process-frame");
                        processedFrameCount++;
                        intervalProcessedFrames++;

                        long processFrameElapsed = Environment.TickCount64 - processFrameStart;
                        if (presentedFrameCount < 10 &&
                            _d3d12SessionRenderer is D3D12Renderer startupRenderer &&
                            (frameNumber <= StartupDrawTraceLimit || processFrameElapsed >= 250))
                        {
                            // Keep this bounded to startup. It identifies whether a black
                            // screen is spending its time compiling, binding, recording,
                            // submitting, or waiting without forcing a fence/readback.
                            D3D12PerformanceStats startupStats = startupRenderer.Device.GetPerformanceStats();
                            CoreHostLog.Write(
                                "d3d12-first-frame",
                                $"processed={frameNumber}; elapsedMs={processFrameElapsed}; " +
                                $"drawsDelta={startupStats.DrawCalls-startupPerformanceStats.DrawCalls}; passesDelta={startupStats.RenderPasses-startupPerformanceStats.RenderPasses}; " +
                                $"shaderHitDelta={startupStats.ShaderCacheHits-startupPerformanceStats.ShaderCacheHits}; shaderMissDelta={startupStats.ShaderCacheMisses-startupPerformanceStats.ShaderCacheMisses}; " +
                                $"shaderCompileUsDelta={startupStats.ShaderCompileMicroseconds-startupPerformanceStats.ShaderCompileMicroseconds}; " +
                                $"psoHitDelta={startupStats.PsoCacheHits-startupPerformanceStats.PsoCacheHits}; psoCreateDelta={startupStats.PsoCreations-startupPerformanceStats.PsoCreations}; " +
                                $"psoUsDelta={startupStats.PsoMicroseconds-startupPerformanceStats.PsoMicroseconds}; descriptorUsDelta={startupStats.DescriptorBindMicroseconds-startupPerformanceStats.DescriptorBindMicroseconds}; " +
                                $"descriptorTableHitDelta={startupStats.DescriptorTableCacheHits-startupPerformanceStats.DescriptorTableCacheHits}; descriptorTableMissDelta={startupStats.DescriptorTableCacheMisses-startupPerformanceStats.DescriptorTableCacheMisses}; " +
                                $"descriptorCopiesDelta={startupStats.DescriptorCopies-startupPerformanceStats.DescriptorCopies}; " +
                                $"recordUsDelta={startupStats.RecordMicroseconds-startupPerformanceStats.RecordMicroseconds}; submitUsDelta={startupStats.SubmitMicroseconds-startupPerformanceStats.SubmitMicroseconds}; " +
                                $"waitUsDelta={startupStats.FenceWaitMicroseconds-startupPerformanceStats.FenceWaitMicroseconds}; invalidPassDelta={startupStats.InvalidRenderPasses-startupPerformanceStats.InvalidRenderPasses}");
                            startupPerformanceStats = startupStats;
                        }
                        if (frameNumber <= 32 || processFrameElapsed >= 1000)
                        {
                            CoreHostLog.Write(
                                "session",
                                string.Format(
                                    "ProcessFrame {0} end; elapsed={1} ms",
                                    frameNumber,
                                    processFrameElapsed));
                        }
                    }

                    while (current.ConsumeFrameAvailable())
                    {
                        if (presentedFrameCount == 0)
                        {
                            CoreHostLog.Write(
                                "session",
                                $"[Session {sessionId}] FirstGuestFrameProduced on the exclusive graphics thread");
                        }

                        long presentStart = Environment.TickCount64;
                        current.PresentFrame(static () => { });
                        DrainD3D11DebugTelemetry("after-present-frame");
                        presentedFrameCount++;
                        intervalPresentedFrames++;

                        bool captureSeries = Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_CAPTURE_SERIES") == "1";
                        bool captureDue = captureSeries
                            ? presentedFrameCount >= D3D12CaptureMinimumSequence && presentedFrameCount % D3D12CaptureSeriesInterval == 0
                            : presentedFrameCount >= D3D12CaptureMinimumSequence;
                        if (Volatile.Read(ref _d3d12CaptureRenderer) is D3D12Renderer d3d12Renderer &&
                            captureDue &&
                            (captureSeries || Volatile.Read(ref _d3d12FrameCaptureCompleted) == 0) &&
                            Interlocked.CompareExchange(ref _d3d12FrameCaptureRequested, 1, 0) == 0)
                        {
                            try
                            {
                                int attempt = Interlocked.Increment(ref _d3d12FrameCaptureAttempts);
                                if (attempt == 1)
                                {
                                    CoreHostLog.Write("d3d12-frame", $"Requesting D3D12 back-buffer capture at present {presentedFrameCount}.");
                                }
                                d3d12Renderer.Screenshot();
                            }
                            catch (Exception exception)
                            {
                                // A later present can retry. Do not let an
                                // optional diagnostic readback stop the game.
                                Volatile.Write(ref _d3d12FrameCaptureRequested, 0);
                                CoreHostLog.Error("captura interna do framebuffer D3D12", exception);
                            }
                        }

                        if (presentedFrameCount == 1)
                        {
                            CoreHostLog.Write(
                                "session",
                                string.Format(
                                    "[Session {1}] FirstGuestPresentCallback; elapsed={0} ms",
                                    Environment.TickCount64 - presentStart,
                                    sessionId));
                        }
                        else if (presentedFrameCount <= 32 || presentedFrameCount % 60 == 0)
                        {
                            CoreHostLog.Write(
                                "session",
                                string.Format(
                                    "[Session {1}] Present {2} (processed={3}); elapsed={0} ms",
                                    Environment.TickCount64 - presentStart,
                                    sessionId,
                                    presentedFrameCount,
                                    processedFrameCount));
                        }
                    }

                    loopIterationCount++;
                    long nowMilliseconds = Environment.TickCount64;
                    Interlocked.Exchange(ref _runLoopIterationCount, loopIterationCount);
                    Interlocked.Exchange(ref _processedFrameCount, processedFrameCount);
                    Interlocked.Exchange(ref _presentedFrameCount, presentedFrameCount);
                    Interlocked.Exchange(ref _runLoopLastProgressAt, nowMilliseconds);

                    if ((loopIterationCount % 600) == 0)
                    {
                        CoreHostLog.Write(
                            "session",
                            string.Format(
                                "Emulation FIFO iterations {0}; processed={1}; presented={2}",
                                loopIterationCount,
                                processedFrameCount,
                                presentedFrameCount));
                    }

                    long elapsedMilliseconds = nowMilliseconds - intervalStartMilliseconds;
                    if (elapsedMilliseconds >= 1000)
                    {
                        CoreHostLog.Write("memory", RuntimeMemoryBudget.Sample(Environment.WorkingSet, GC.GetTotalMemory(false)) +
                            "; " + ARMeilleure.Translation.Cache.JitCache.GetDetailedTelemetry());
                        double processedFps = intervalProcessedFrames * 1000.0 / elapsedMilliseconds;
                        double presentedFps = intervalPresentedFrames * 1000.0 / elapsedMilliseconds;
                        string nativePresentation = GetNativePresentationDelta(elapsedMilliseconds, out double acceptedPresentFps);
                        // Desktop harness only. Count successful native presents,
                        // not FIFO iterations or guest callbacks. Reuse the GPU
                        // owner's sample; do not query the device from a UI worker.
                        if (_localWindowHandle != IntPtr.Zero &&
                            Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_FPS") == "1")
                        {
                            // Post, rather than synchronously SendMessage via
                            // SetWindowText, so a busy UI never stalls the GPU.
                            Native_PostMessage(_localWindowHandle, 0x8029,
                                (IntPtr)(long)Math.Round(acceptedPresentFps * 10), IntPtr.Zero);
                        }
                        CoreHostLog.Write(
                            "performance",
                            string.Format(
                                "FPS backend={0} callbacks={1:F1}, processed={2:F1}, totalCallbacks={3}, totalProcessed={4}, loopIterations={5}, workingSet={6}, presentAcceptedFps={7:F1}; {8}",
                                _activeSettings?.Backend ?? "unknown",
                                presentedFps,
                                processedFps,
                                presentedFrameCount,
                                processedFrameCount,
                                loopIterationCount,
                                Environment.WorkingSet,
                                acceptedPresentFps,
                                nativePresentation));

                        intervalProcessedFrames = 0;
                        intervalPresentedFrames = 0;
                        intervalStartMilliseconds = nowMilliseconds;
                        if (nowMilliseconds - descriptorSampleMilliseconds >= 5000 &&
                            _d3d12SessionRenderer is D3D12Renderer descriptorRenderer)
                        {
                            // On the GPU owner thread; reads counters without
                            // flushing, waiting, or capturing a framebuffer.
                            var s = descriptorRenderer.Device.GetDescriptorStats();
                            var p = descriptorRenderer.Device.GetPerformanceStats();
                            CoreHostLog.Write("d3d12-descriptors",
                                $"mode={(s.RingEnabled != 0 ? "ring" : "fixed")}; intervalMs={nowMilliseconds-descriptorSampleMilliseconds}; " +
                                $"viewsDelta={s.ViewAllocations-previousDescriptorStats.ViewAllocations}; samplersDelta={s.SamplerAllocations-previousDescriptorStats.SamplerAllocations}; " +
                                $"viewsPerDraw={Ratio(s.ViewAllocations-previousDescriptorStats.ViewAllocations, p.DrawCalls-previousPerformanceStats.DrawCalls)}; " +
                                $"samplersPerDraw={Ratio(s.SamplerAllocations-previousDescriptorStats.SamplerAllocations, p.DrawCalls-previousPerformanceStats.DrawCalls)}; " +
                                $"heapSwitchesPerDraw={Ratio(s.HeapSwitches-previousDescriptorStats.HeapSwitches, p.DrawCalls-previousPerformanceStats.DrawCalls)}; " +
                                $"occupied={s.ViewOccupied}/{s.SamplerOccupied}; peakOccupied={s.ViewPeakOccupied}/{s.SamplerPeakOccupied}; " +
                                $"peakSubmission={s.ViewPeakPerSubmission}/{s.SamplerPeakPerSubmission}; pages={s.ViewPages}/{s.SamplerPages}; " +
                                $"heapSwitchesDelta={s.HeapSwitches-previousDescriptorStats.HeapSwitches}; pressureDelta={s.PressureEvents-previousDescriptorStats.PressureEvents}; " +
                                $"ringWaitsDelta={s.RingWaits-previousDescriptorStats.RingWaits}; ringWaitUsDelta={s.RingWaitMicroseconds-previousDescriptorStats.RingWaitMicroseconds}; " +
                                $"allFenceWaitsDelta={s.CpuFenceWaits-previousDescriptorStats.CpuFenceWaits}; allFenceWaitUsDelta={s.CpuFenceWaitMicroseconds-previousDescriptorStats.CpuFenceWaitMicroseconds}; " +
                                $"maxFenceWaitUs={s.MaxFenceWaitMicroseconds}; wraps={s.Wraps}; failures={s.AllocationFailures}; estimatedHeapBytes={s.EstimatedHeapBytes}");
                            CoreHostLog.Write("d3d12-performance",
                                $"intervalMs={nowMilliseconds-descriptorSampleMilliseconds}; drawsDelta={p.DrawCalls-previousPerformanceStats.DrawCalls}; " +
                                $"passesDelta={p.RenderPasses-previousPerformanceStats.RenderPasses}; shaderCacheHitDelta={p.ShaderCacheHits-previousPerformanceStats.ShaderCacheHits}; " +
                                $"shaderCacheMissDelta={p.ShaderCacheMisses-previousPerformanceStats.ShaderCacheMisses}; shaderCompileUsDelta={p.ShaderCompileMicroseconds-previousPerformanceStats.ShaderCompileMicroseconds}; " +
                                $"psoHitDelta={p.PsoCacheHits-previousPerformanceStats.PsoCacheHits}; " +
                                $"psoCreateDelta={p.PsoCreations-previousPerformanceStats.PsoCreations}; psoUsDelta={p.PsoMicroseconds-previousPerformanceStats.PsoMicroseconds}; " +
                                $"descriptorUsDelta={p.DescriptorBindMicroseconds-previousPerformanceStats.DescriptorBindMicroseconds}; recordUsDelta={p.RecordMicroseconds-previousPerformanceStats.RecordMicroseconds}; " +
                                $"descriptorTableHitDelta={p.DescriptorTableCacheHits-previousPerformanceStats.DescriptorTableCacheHits}; descriptorTableMissDelta={p.DescriptorTableCacheMisses-previousPerformanceStats.DescriptorTableCacheMisses}; " +
                                $"descriptorCopiesDelta={p.DescriptorCopies-previousPerformanceStats.DescriptorCopies}; copiesPerDraw={Ratio(p.DescriptorCopies-previousPerformanceStats.DescriptorCopies, p.DrawCalls-previousPerformanceStats.DrawCalls)}; " +
                                $"submitUsDelta={p.SubmitMicroseconds-previousPerformanceStats.SubmitMicroseconds}; presentUsDelta={p.PresentMicroseconds-previousPerformanceStats.PresentMicroseconds}; " +
                                $"waitUsDelta={p.FenceWaitMicroseconds-previousPerformanceStats.FenceWaitMicroseconds}; depthDetachedDelta={p.DepthAttachmentsDetached-previousPerformanceStats.DepthAttachmentsDetached}; " +
                                $"invalidPassDelta={p.InvalidRenderPasses-previousPerformanceStats.InvalidRenderPasses}; uavBarriersDelta={p.UavBarriers-previousPerformanceStats.UavBarriers}");
                            previousDescriptorStats = s;
                            previousPerformanceStats = p;
                            descriptorSampleMilliseconds = nowMilliseconds;
                        }
                    }
                }
                catch (Exception exception)
                {
                    // StopCore owns Sync while joining us. Never acquire it on
                    // the GPU error path or teardown deadlocks with this thread.
                    RecordSessionFault(sessionId, "loop de emulacao", exception);
                    return;
                }

                Thread.Yield();
            }
        }

        private static string Ratio(ulong numerator, ulong denominator) =>
            denominator == 0 ? "0.000" : ((double)numerator / denominator).ToString("F3", System.Globalization.CultureInfo.InvariantCulture);

        private static int ToStickValue(float value)
        {
            return Math.Clamp((int)(value * short.MaxValue), short.MinValue, short.MaxValue);
        }

        private static ControllerKeys MapButtons(uint buttons, float leftTrigger, float rightTrigger)
        {
            ControllerKeys result = 0;

            // This mask is produced by UwpCoreSession.ToCoreButtonMask. Keep it
            // independent from Windows.Gaming.Input.GamepadButtons enum values.
            AddButton(ref result, buttons, 1u << 0, ControllerKeys.A);
            AddButton(ref result, buttons, 1u << 1, ControllerKeys.B);
            AddButton(ref result, buttons, 1u << 2, ControllerKeys.X);
            AddButton(ref result, buttons, 1u << 3, ControllerKeys.Y);
            AddButton(ref result, buttons, 1u << 4, ControllerKeys.DpadUp);
            AddButton(ref result, buttons, 1u << 5, ControllerKeys.DpadDown);
            AddButton(ref result, buttons, 1u << 6, ControllerKeys.DpadLeft);
            AddButton(ref result, buttons, 1u << 7, ControllerKeys.DpadRight);
            AddButton(ref result, buttons, 1u << 8, ControllerKeys.Minus);
            AddButton(ref result, buttons, 1u << 9, ControllerKeys.Plus);
            AddButton(ref result, buttons, 1u << 10, ControllerKeys.L);
            AddButton(ref result, buttons, 1u << 11, ControllerKeys.R);
            AddButton(ref result, buttons, 1u << 12, ControllerKeys.LStick);
            AddButton(ref result, buttons, 1u << 13, ControllerKeys.RStick);

            if (leftTrigger > 0.5f) result |= ControllerKeys.Zl;
            if (rightTrigger > 0.5f) result |= ControllerKeys.Zr;
            return result;
        }

        private static void AddButton(ref ControllerKeys result, uint buttons, uint mask, ControllerKeys key)
        {
            if ((buttons & mask) != 0)
            {
                result |= key;
            }
        }

        private static void TryWriteErrorLog(string message)
        {
            try
            {
                CoreHostLog.Write("errors", message);
                string baseDirectory = AppDataManager.BaseDirPath;
                if (string.IsNullOrWhiteSpace(baseDirectory))
                {
                    return;
                }

                Directory.CreateDirectory(baseDirectory);
                File.WriteAllText(Path.Combine(baseDirectory, "corehost-error.log"), message, Encoding.UTF8);
            }
            catch
            {
                // Error reporting must never replace the original CoreHost error.
            }
        }

        private static void ConfigureVulkanDriver(string selectedBackend, bool useLocalWin32Surface)
        {
            if (useLocalWin32Surface)
            {
                // The local validation path must use the machine's normal
                // Vulkan ICD. Mesa Dozen is the Xbox package path and is not
                // a substitute for a desktop Win32 presentation surface.
                Environment.SetEnvironmentVariable("VK_DRIVER_FILES", null);
                Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", null);
                Environment.SetEnvironmentVariable("VK_LOADER_DEBUG", "error,warn");
                CoreHostLog.Write("vulkan", "Local Win32 validation uses the system Vulkan ICD");
                return;
            }

            // The packaged UWP process resolves this from AppContext.BaseDirectory.
            // The local phase-1 harness can provide the same package directory
            // explicitly, without changing the Xbox/UWP deployment behavior.
            string packageDirectory = Environment.GetEnvironmentVariable("RYUJINX_UWP_PACKAGE_DIR");
            if (string.IsNullOrWhiteSpace(packageDirectory))
            {
                packageDirectory = AppContext.BaseDirectory;
            }

            packageDirectory = Path.GetFullPath(packageDirectory);
            string driverManifest = Path.Combine(packageDirectory, "dzn_icd.x86_64.json");

            if (!File.Exists(driverManifest))
            {
                throw new FileNotFoundException("ICD Mesa Dozen não encontrado no pacote UWP.", driverManifest);
            }

            string[] validatorNames = { "DXIL.dll", "dxcompiler_x.dll", "dxcompiler_xs.dll" };
            string validator = validatorNames
                .Select(name => Path.Combine(packageDirectory, name))
                .FirstOrDefault(File.Exists);
            if (validator == null)
            {
                throw new FileNotFoundException(
                    "Runtime DXIL do Mesa Dozen não encontrado no pacote UWP. O driver precisa de DXIL.dll, dxcompiler_x.dll ou dxcompiler_xs.dll.",
                    Path.Combine(packageDirectory, "DXIL.dll"));
            }

            foreach (string runtimeName in new[] { "vulkan_dzn.dll", "spirv_to_dxil.dll", "vulkan-1.dll", validatorNames[0], validatorNames[1], validatorNames[2] })
            {
                string runtimePath = Path.Combine(packageDirectory, runtimeName);
                CoreHostLog.Write("vulkan", string.Format("Packaged runtime {0}: {1}", runtimeName, File.Exists(runtimePath) ? "present" : "absent"));
            }

            // The Xbox package ships its own Vulkan loader and Dozen ICD. If
            // these variables are not set, the loader can select an Xbox
            // system ICD and Ryujinx reports ErrorIncompatibleDriver.
            Environment.SetEnvironmentVariable("VK_DRIVER_FILES", driverManifest);
            Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", driverManifest);
            Environment.SetEnvironmentVariable("VK_LOADER_DEBUG", "error,warn,driver");
            CoreHostLog.Write("vulkan", string.Format(
                "Using packaged Mesa Dozen ICD '{0}' with validator '{1}' for backend '{2}'",
                driverManifest,
                validator,
                selectedBackend));
        }

        private static void EnsureD3D11Surface()
        {
            int result = Native_D3D11_GetSurfaceSize(out uint width, out uint height);
            if (result < 0 || width == 0 || height == 0)
            {
                throw new InvalidOperationException(
                    string.Format("Host D3D11 nao esta inicializado (HRESULT 0x{0:X8}).", result));
            }

            CoreHostLog.Write("graphics", string.Format("D3D11 surface ready: {0}x{1}", width, height));
        }

        private static IntPtr ResizeD3D11SwapChain(int width, int height)
        {
            int result = Native_D3D11_Resize((uint)Math.Max(1, width), (uint)Math.Max(1, height));
            if (result < 0)
            {
                throw new InvalidOperationException(string.Format(
                    "Falha ao redimensionar swap chain D3D11 (HRESULT 0x{0:X8}).",
                    result));
            }

            result = Native_D3D11_GetNativeObjects(out _, out _, out IntPtr swapChain);
            if (result < 0 || swapChain == IntPtr.Zero)
            {
                throw new InvalidOperationException(string.Format(
                    "Falha ao atualizar swap chain D3D11 (HRESULT 0x{0:X8}).",
                    result));
            }

            return swapChain;
        }

        private static void CopyD3D11RendererTexture(IntPtr resource, ImageCrop crop)
        {
            int result = Native_D3D11_CopyRendererTexture(resource, false, crop.Left, crop.Top, crop.Right, crop.Bottom, true);
            if (result < 0)
            {
                throw new InvalidOperationException(string.Format(
                    "Falha ao copiar frame do renderer D3D11 para a swap chain (HRESULT 0x{0:X8}, crop={1},{2}-{3},{4}).",
                    result,
                    crop.Left,
                    crop.Top,
                    crop.Right,
                    crop.Bottom));
            }
        }

        private static int CopyD3D11RendererTextureTransformed(IntPtr resource, ImageCrop crop, bool flipY)
        {
            int result = Native_D3D11_CopyRendererTexture(resource, flipY, crop.Left, crop.Top, crop.Right, crop.Bottom, true);
            if (result < 0)
            {
                CoreHostLog.Write(
                    "d3d11-present-path",
                    string.Format(
                        "Native GPU presentation transform failed; resource=0x{0:X}, flipY={1}, crop={2},{3}-{4},{5}, HRESULT=0x{6:X8}; compatibility fallback remains available",
                        resource.ToInt64(),
                        flipY,
                        crop.Left,
                        crop.Top,
                        crop.Right,
                        crop.Bottom,
                        result));
            }
            else if (ShouldLogD3D11Frame(Interlocked.Read(ref _d3d11CompatibilityFrameCount) + 1))
            {
                CoreHostLog.Write(
                    "d3d11-present-path",
                    string.Format("Native GPU presentation transform completed; flipY={0}, crop={1},{2}-{3},{4}", flipY, crop.Left, crop.Top, crop.Right, crop.Bottom));
            }

            return result;
        }

        private static byte[] CompileD3D11Hlsl(ShaderStage stage, string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ArgumentException("O shader HLSL gerado esta vazio.", nameof(source));
            }

            byte[] sourceBytes = Encoding.UTF8.GetBytes(source);
            string shaderHash = Convert.ToHexString(SHA256.HashData(sourceBytes));
            string cacheKey = "v2:" + ((int)stage).ToString() + ":" + shaderHash;
            lock (D3D11ShaderCacheSync)
            {
                if (D3D11ShaderBytecodeCache.TryGetValue(cacheKey, out byte[] cachedBytecode))
                {
                    CoreHostLog.Write(
                        "d3d11-shader",
                        string.Format(
                            "HLSL bytecode cache hit: stage={0}, chars={1}, bytecode={2}, entries={3}",
                            stage,
                            source.Length,
                            cachedBytecode.Length,
                            D3D11ShaderBytecodeCache.Count));
                    return cachedBytecode;
                }
            }

            byte[] diskCachedBytecode = TryLoadD3D11ShaderBytecode(cacheKey);
            if (diskCachedBytecode is not null)
            {
                lock (D3D11ShaderCacheSync)
                {
                    if (D3D11ShaderBytecodeCache.Count < D3D11ShaderBytecodeCacheLimit)
                    {
                        D3D11ShaderBytecodeCache[cacheKey] = diskCachedBytecode;
                    }
                }

                CoreHostLog.Write(
                    "d3d11-shader",
                    string.Format(
                        "HLSL disk cache hit: stage={0}, bytecode={1}, entries={2}",
                        stage,
                        diskCachedBytecode.Length,
                        D3D11ShaderBytecodeCache.Count));
                return diskCachedBytecode;
            }

            int shaderSequence = Interlocked.Increment(ref _d3d11ShaderSequence);
            long compileStartedAt = Environment.TickCount64;
            string compileStatus = "started";
            CoreHostLog.Write(
                "d3d11-shader",
                string.Format(
                    "HLSL compile begin: sequence={0}, hash={1}, stage={2}, chars={3}, thread={4}, workingSet={5}, managed={6}",
                    shaderSequence,
                    shaderHash,
                    stage,
                    source.Length,
                    Environment.CurrentManagedThreadId,
                    Environment.WorkingSet,
                    GC.GetTotalMemory(false)));
            if (shaderSequence <= 32 && RuntimeMemoryBudget.Pressure < MemoryPressure.High)
            {
                CoreHostLog.Write("d3d11-shader-source", string.Format(
                    "===== shader {0}, stage={1}, chars={2} ====={3}{4}",
                    shaderSequence,
                    stage,
                    source.Length,
                    Environment.NewLine,
                    source.Length <= 131072 ? source : source.Substring(0, 131072)));
            }

            using IDisposable sourceLease = RuntimeMemoryBudget.Track(MemoryCategory.HlslSource, sourceBytes.LongLength);
            using IDisposable compilerLease = RuntimeMemoryBudget.EnterCompiler(Math.Max(sourceBytes.LongLength * 4, 4L * 1024 * 1024));
            GCHandle pin = GCHandle.Alloc(sourceBytes, GCHandleType.Pinned);
            IntPtr bytecode = IntPtr.Zero;
            IntPtr errorMessage = IntPtr.Zero;

            try
            {
                int result = Native_D3D11_CompileHlsl(
                    (uint)stage,
                    pin.AddrOfPinnedObject(),
                    checked((uint)sourceBytes.Length),
                    out bytecode,
                    out uint bytecodeLength,
                    out errorMessage);
                string compilerMessage = errorMessage == IntPtr.Zero
                    ? string.Empty
                    : Marshal.PtrToStringAnsi(errorMessage) ?? string.Empty;

                if (result < 0 || bytecode == IntPtr.Zero || bytecodeLength == 0)
                {
                    compileStatus = string.Format("failed:0x{0:X8}", result);
                    string diagnosticSource = RuntimeMemoryBudget.Pressure >= MemoryPressure.High && source.Length > 16384
                        ? source.Substring(0, 16384) + Environment.NewLine + "<truncated under memory pressure>"
                        : source;
                    CoreHostLog.Write("d3d11-shader-failed-source", string.Format(
                        "===== failed shader {0}, hash={1}, stage={2}, chars={3}, HRESULT=0x{4:X8} ====={5}{6}",
                        shaderSequence,
                        shaderHash,
                        stage,
                        source.Length,
                        result,
                        Environment.NewLine,
                        diagnosticSource));
                    CoreHostLog.Write("d3d11-shader", string.Format(
                        "HLSL compile failed: stage={0}, HRESULT=0x{1:X8}, detail={2}",
                        stage,
                        result,
                        compilerMessage));
                    throw new InvalidOperationException(string.Format(
                        "Falha ao compilar shader HLSL {0} (HRESULT 0x{1:X8}): {2}",
                        stage,
                        result,
                        compilerMessage));
                }

                if (!string.IsNullOrWhiteSpace(compilerMessage))
                {
                    CoreHostLog.Write("d3d11-shader", stage + ": " + compilerMessage);
                }

                byte[] resultBytes = new byte[checked((int)bytecodeLength)];
                Marshal.Copy(bytecode, resultBytes, 0, resultBytes.Length);
                lock (D3D11ShaderCacheSync)
                {
                    if (RuntimeMemoryBudget.Pressure < MemoryPressure.High &&
                        D3D11ShaderBytecodeCache.Count < D3D11ShaderBytecodeCacheLimit)
                    {
                        D3D11ShaderBytecodeCache[cacheKey] = resultBytes;
                        RuntimeMemoryBudget.Observe(
                            MemoryCategory.ShaderCache,
                            D3D11ShaderBytecodeCache.Values.Sum(value => (long)value.Length));
                    }
                }
                PersistD3D11ShaderBytecode(cacheKey, resultBytes);
                compileStatus = "success";
                CoreHostLog.Write(
                    "d3d11-shader",
                    string.Format(
                        "HLSL compile end: sequence={0}, hash={1}, stage={2}, chars={3}, bytecode={4}, elapsed={5} ms",
                        shaderSequence,
                        shaderHash,
                        stage,
                        source.Length,
                        resultBytes.Length,
                        Environment.TickCount64 - compileStartedAt));
                return resultBytes;
            }
            finally
            {
                // This is emitted on success, compiler error, interop error,
                // and cancellation/exception paths. A missing end marker is
                // otherwise indistinguishable from a native D3DCompile hang
                // in the Xbox log.
                CoreHostLog.Write(
                    "d3d11-shader",
                    string.Format(
                        "HLSL compile finalized: sequence={0}, hash={1}, stage={2}, status={3}, elapsed={4} ms, workingSet={5}, managed={6}",
                        shaderSequence,
                        shaderHash,
                        stage,
                        compileStatus,
                        Environment.TickCount64 - compileStartedAt,
                        Environment.WorkingSet,
                        GC.GetTotalMemory(false)));
                pin.Free();
                if (bytecode != IntPtr.Zero)
                {
                    Native_D3D11_FreeBuffer(bytecode);
                }
                if (errorMessage != IntPtr.Zero)
                {
                    Native_D3D11_FreeBuffer(errorMessage);
                }
            }
        }

        private static string GetD3D11ShaderCachePath(string cacheKey)
        {
            string baseDirectory = AppDataManager.BaseDirPath;
            if (string.IsNullOrWhiteSpace(baseDirectory))
            {
                return null;
            }

            string safeKey = cacheKey.Replace(':', '-');
            return Path.Combine(baseDirectory, "D3D11ShaderCache", safeKey + ".dxbc");
        }

        private static byte[] TryLoadD3D11ShaderBytecode(string cacheKey)
        {
            try
            {
                string path = GetD3D11ShaderCachePath(cacheKey);
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    return null;
                }

                byte[] bytecode = File.ReadAllBytes(path);
                return bytecode.Length > 0 && bytecode.Length <= 16 * 1024 * 1024
                    ? bytecode
                    : null;
            }
            catch (Exception exception)
            {
                CoreHostLog.Write("d3d11-shader", "HLSL disk cache read skipped: " + exception.Message);
                return null;
            }
        }

        private static void PersistD3D11ShaderBytecode(string cacheKey, byte[] bytecode)
        {
            try
            {
                string path = GetD3D11ShaderCachePath(cacheKey);
                if (string.IsNullOrWhiteSpace(path) || bytecode is null || bytecode.Length == 0)
                {
                    return;
                }

                string directory = Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                string temporaryPath = path + ".tmp-" + Environment.CurrentManagedThreadId + "-" + Environment.TickCount64;
                File.WriteAllBytes(temporaryPath, bytecode);
                File.Move(temporaryPath, path, true);
                CoreHostLog.Write("d3d11-shader", "HLSL bytecode persisted: " + Path.GetFileName(path));
            }
            catch (Exception exception)
            {
                CoreHostLog.Write("d3d11-shader", "HLSL disk cache write skipped: " + exception.Message);
            }
        }

        private static bool PresentD3D11CompatibilityFrame(ITexture texture, ImageCrop crop, long sequence, string description)
        {
            bool traceFrame = ShouldLogD3D11Frame(sequence);
            if (traceFrame)
            {
                CoreHostLog.Write(
                    "d3d11-present-path",
                    string.Format(
                        "Compatibility callback entered; sequence={0}, thread={1}, textureNull={2}",
                        sequence,
                        Environment.CurrentManagedThreadId,
                        texture == null));
            }

            if (texture == null)
            {
                return false;
            }

            try
            {
                int result = Native_D3D11_GetSurfaceSize(out uint targetWidth, out uint targetHeight);
                if (result < 0 || targetWidth == 0 || targetHeight == 0)
                {
                    LogD3D11PresentError("GetSurfaceSize", result);
                    return false;
                }

                // Do not sleep while holding the GPU/FIFO thread. Instead,
                // keep the swap chain at its normal 60 Hz VSync and reuse the
                // last uploaded frame until the 30 FPS presentation interval
                // expires. This reduces the expensive readback/CPU compose /
                // upload path by half for Let's Go without changing the guest
                // clock or introducing a deadlock in the emulation loop.
                if (IsPokemonLetsGoTitle(_activeProgramId) &&
                    ShouldRepeatPokemonPresentation(sequence))
                {
                    return true;
                }

                int sourceWidth = texture.Width;
                int sourceHeight = texture.Height;
                int sourceLength = checked(sourceWidth * sourceHeight * 4);
                bool isLeafGreen = _activeProgramId == 0x010034D02340E000UL;
                ImageCrop composeCrop = crop;

                // The GBA compatibility surface is rendered into a 1020x680
                // viewport inside the 1920x1080 allocation used by this host.
                // Passing the logical 1280x720 crop through unchanged leaves
                // the unused right/bottom allocation visible as black bands.
                // Use the actual viewport for this title, then scale it to the
                // 1280x720 presentation surface while preserving its 3:2
                // aspect ratio.
                if (isLeafGreen &&
                    sourceWidth == 1920 && sourceHeight == 1080 &&
                    crop.Left == 0 && crop.Top == 0 &&
                    crop.Right == 1280 && crop.Bottom == 720)
                {
                    composeCrop = new ImageCrop(
                        0,
                        1020,
                        0,
                        680,
                        crop.FlipX,
                        false,
                        true,
                        1280f,
                        720f);

                    CoreHostLog.Write(
                        "graphics",
                        "Leaf Green presentation viewport normalized from 1920x1080 allocation to 1020x680; aspect=3:2");
                }
                // Some titles render a genuine 1280x720 viewport into the
                // top-left of a 1920x1080 allocation. The crop metadata is
                // identical to the resolution-scaled case, but expanding it
                // here samples the unused allocation and produces a small
                // image with a blue/black remainder on the host surface.
                bool logicalViewportCrop =
                    sourceWidth == 1920 && sourceHeight == 1080 &&
                    composeCrop.Left == 0 && composeCrop.Top == 0 &&
                    composeCrop.Right == 1280 && composeCrop.Bottom == 720 &&
                    description?.IndexOf("firstViewport=1280x720", StringComparison.Ordinal) >= 0;
                D3D11FrameComposer.CropRectangle effectiveCrop = D3D11FrameComposer.NormalizeCrop(
                    sourceWidth,
                    sourceHeight,
                    composeCrop.Left,
                    composeCrop.Top,
                    composeCrop.Right,
                    composeCrop.Bottom,
                    // The 1280x720 metadata is a logical Switch viewport. On
                    // the Xbox host the backing allocation is commonly
                    // 1920x1080, so preserving that rectangle scales only the
                    // upper-left portion and brings the zoom back. Let the
                    // composer expand resolution-scaled crops; its pixel
                    // classifier still rejects transparent/unrendered
                    // padding when the allocation is genuinely larger.
                    true);
                if (traceFrame)
                {
                    CoreHostLog.Write(
                        "d3d11-present-path",
                        string.Format(
                            "Entering texture readback; sequence={0}, source={1}x{2}, target={3}x{4}, crop={5},{6}-{7},{8}, effectiveCrop={9},{10}-{11},{12}, normalized={13}, logicalViewportCrop={14}, flip={15}/{16}, stretched={17}, aspect={18:F3}:{19:F3}, bytes={20}",
                            sequence,
                            sourceWidth,
                            sourceHeight,
                            targetWidth,
                            targetHeight,
                            composeCrop.Left,
                            composeCrop.Top,
                            composeCrop.Right,
                            composeCrop.Bottom,
                            effectiveCrop.Left,
                            effectiveCrop.Top,
                            effectiveCrop.Right,
                            effectiveCrop.Bottom,
                            effectiveCrop.WasNormalized,
                            logicalViewportCrop,
                            composeCrop.FlipX,
                            composeCrop.FlipY,
                            composeCrop.IsStretched,
                            composeCrop.AspectRatioX,
                            composeCrop.AspectRatioY,
                            sourceLength));
                }

                using (PinnedSpan<byte> pinned = texture.GetData())
                {
                    if (traceFrame)
                    {
                        CoreHostLog.Write(
                            "d3d11-present-path",
                            string.Format("Texture readback returned; sequence={0}", sequence));
                    }
                    ReadOnlySpan<byte> source = pinned.Get();
                    if (source.Length < sourceLength)
                    {
                        LogD3D11PresentError("GetData", unchecked((int)0x80070057));
                        return false;
                    }

                    ReadOnlySpan<byte> sourceFrame = source.Slice(0, sourceLength);
                    if (ShouldLogD3D11Frame(sequence))
                    {
                        LogD3D11FrameStatistics(sequence, description, sourceFrame, sourceWidth, sourceHeight);
                    }

                    // Managed readback is top-down. Its coordinate system is
                    // intentionally separate from the native D3D11 presenter,
                    // whose bottom-up fullscreen triangle uses the complement
                    // rule. Do not apply title-specific vertical-flip profiles.
                    bool preserveLegacyVerticalOrder = false;
                    // Match the direct presenter: convert the GAL row-order
                    // convention exactly once for this top-down CPU frame.
                    bool effectiveFlipY = !composeCrop.FlipY;

                    byte[] frame = D3D11FrameComposer.Compose(
                        sourceFrame,
                        sourceWidth,
                        sourceHeight,
                        checked((int)targetWidth),
                        checked((int)targetHeight),
                        effectiveCrop.Left,
                        effectiveCrop.Top,
                        effectiveCrop.Right,
                        effectiveCrop.Bottom,
                        composeCrop.FlipX,
                        effectiveFlipY,
                        composeCrop.IsStretched,
                        composeCrop.AspectRatioX,
                        composeCrop.AspectRatioY,
                        preserveLegacyVerticalOrder,
                        true,
                        ref _d3d11PresentationFrame);
                    TryCaptureD3D11Frame(frame, checked((int)targetWidth), checked((int)targetHeight), sequence);
                    unsafe
                    {
                        fixed (byte* framePointer = frame)
                        {
                            result = Native_D3D11_UploadFrame(
                                (IntPtr)framePointer,
                                targetWidth,
                                targetHeight,
                                targetWidth * 4u);
                        }
                    }

                    if (result < 0)
                    {
                        LogD3D11PresentError("UploadFrame", result);
                        return false;
                    }

                    Interlocked.Exchange(ref _d3d11CompatibilityFrameCount, sequence);
                    return true;
                }
            }
            catch (Exception exception)
            {
                if (!_d3d11PresentErrorLogged)
                {
                    _d3d11PresentErrorLogged = true;
                    CoreHostLog.Error("apresentacao D3D11", exception);
                }
                return false;
            }
        }

        private static bool ShouldLogD3D11Frame(long sequence)
        {
            return sequence <= 3 || sequence == 10 || sequence == 60 || sequence == 300 || sequence % 1800 == 0;
        }

        private static bool ShouldRepeatPokemonPresentation(long sequence)
        {
            long now = Stopwatch.GetTimestamp();
            long interval = Math.Max(1L, Stopwatch.Frequency / 30L);

            while (true)
            {
                long next = Volatile.Read(ref _pokemonNextPresentationTimestamp);
                if (next != 0 && now < next)
                {
                    long repeated = Interlocked.Increment(ref _pokemonRepeatedFrameCount);
                    if (repeated == 1 || repeated % 120 == 0)
                    {
                        CoreHostLog.Write(
                            "performance",
                            string.Format(
                                "Pokemon 30 FPS pacing reused previous frame: sequence={0}, repeated={1}, newFrames={2}",
                                sequence,
                                repeated,
                                Interlocked.Read(ref _pokemonPacedFrameCount)));
                    }
                    return true;
                }

                long scheduled = next == 0 ? now + interval : next + interval;
                if (scheduled <= now)
                {
                    scheduled = now + interval;
                }

                if (Interlocked.CompareExchange(
                        ref _pokemonNextPresentationTimestamp,
                        scheduled,
                        next) == next)
                {
                    Interlocked.Increment(ref _pokemonPacedFrameCount);
                    return false;
                }
            }
        }

        private static void LogD3D11FrameStatistics(
            long sequence,
            string description,
            ReadOnlySpan<byte> frame,
            int width,
            int height)
        {
            const int MaximumSamples = 65536;
            int pixelCount = frame.Length / 4;
            int stridePixels = Math.Max(1, pixelCount / MaximumSamples);
            long rgbEnergy = 0;
            int coloredPixels = 0;
            int opaquePixels = 0;
            byte minimum = byte.MaxValue;
            byte maximum = byte.MinValue;
            ulong checksum = 1469598103934665603UL;

            for (int pixel = 0; pixel < pixelCount; pixel += stridePixels)
            {
                int offset = pixel * 4;
                byte red = frame[offset];
                byte green = frame[offset + 1];
                byte blue = frame[offset + 2];
                byte alpha = frame[offset + 3];
                rgbEnergy += red + green + blue;
                if ((red | green | blue) != 0)
                {
                    coloredPixels++;
                }
                if (alpha != 0)
                {
                    opaquePixels++;
                }
                minimum = Math.Min(minimum, Math.Min(Math.Min(red, green), Math.Min(blue, alpha)));
                maximum = Math.Max(maximum, Math.Max(Math.Max(red, green), Math.Max(blue, alpha)));
                checksum ^= red;
                checksum *= 1099511628211UL;
                checksum ^= green;
                checksum *= 1099511628211UL;
                checksum ^= blue;
                checksum *= 1099511628211UL;
                checksum ^= alpha;
                checksum *= 1099511628211UL;
            }

            int sampledPixels = (pixelCount + stridePixels - 1) / stridePixels;
            CoreHostLog.Write("d3d11-frame", string.Format(
                "frame={0}, size={1}x{2}, bytes={3}, sampled={4}, colored={5}, alpha={6}, energy={7}, range={8}-{9}, checksum=0x{10:X16}, {11}",
                sequence,
                width,
                height,
                frame.Length,
                sampledPixels,
                coloredPixels,
                opaquePixels,
                rgbEnergy,
                minimum,
                maximum,
                checksum,
                description));
        }

        private static void DrainD3D11DebugTelemetry(string context)
        {
            string messages = Ryujinx.Graphics.D3D11.D3D11DebugTelemetry.Drain();
            if (!string.IsNullOrWhiteSpace(messages))
            {
                CoreHostLog.Write("d3d11-debug", $"context={context}; {messages.TrimEnd()}");
            }
        }

        private static void CaptureD3D12Frame(object sender, ScreenCaptureImageInfo image)
        {
            try
            {
                string capturePath = Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_FRAME_PATH");
                byte[] frame = image.Data;
                int width = image.Width;
                int height = image.Height;
                if (string.IsNullOrWhiteSpace(capturePath) || frame == null || width <= 0 || height <= 0 ||
                    frame.Length < checked(width * height * 4))
                {
                    Volatile.Write(ref _d3d12FrameCaptureRequested, 0);
                    return;
                }

                int sampled = 0;
                int colored = 0;
                long energy = 0;
                Span<int> colorBins = stackalloc int[512];
                colorBins.Clear();
                const int PixelStride = 16;
                for (int pixel = 0; pixel < width * height; pixel += PixelStride)
                {
                    int x = pixel % width;
                    int y = pixel / width;
                    // Exclude the emulator's FPS badge; it is not game content.
                    if (x < Math.Min(width / 5, 190) && y < Math.Min(height / 6, 95)) continue;
                    int offset = pixel * 4;
                    byte red = frame[offset];
                    byte green = frame[offset + 1];
                    byte blue = frame[offset + 2];
                    if ((red | green | blue) != 0)
                    {
                        colored++;
                    }
                    energy += red + green + blue;
                    colorBins[((red >> 5) << 6) | ((green >> 5) << 3) | (blue >> 5)]++;
                    sampled++;
                }

                int dominant = 0;
                int secondary = 0;
                foreach (int count in colorBins)
                {
                    if (count > dominant) { secondary = dominant; dominant = count; }
                    else if (count > secondary) secondary = count;
                }

                // Retain the capture request until the real game has composed
                // visible content; startup clears are not useful validation.
                // Some 3D titles begin with a dark splash/logo occupying only
                // a small part of the back buffer. It is still valid visual
                // guest output; requiring ten percent of the sampled pixels
                // incorrectly discarded it as a clear. Reject only an empty
                // or effectively monochrome clear while retaining a minimum
                // amount of energy and colour diversity.
                int currentAttempt = Volatile.Read(ref _d3d12FrameCaptureAttempts);
                int minColored = 1;
                if (int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_MIN_COLORED"), out int customMinColored) && customMinColored > 0)
                {
                    minColored = customMinColored;
                }
                long minEnergy = 1000;
                if (long.TryParse(Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_MIN_ENERGY"), out long customMinEnergy) && customMinEnergy > 0)
                {
                    minEnergy = customMinEnergy;
                }

                int minSecondary = 2;
                if (int.TryParse(Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_MIN_SECONDARY"), out int customMinSecondary) && customMinSecondary >= 0)
                {
                    minSecondary = customMinSecondary;
                }

                if (sampled == 0 || colored < minColored || energy < minEnergy || secondary < minSecondary)
                {
                    if (currentAttempt == 1 || currentAttempt % 120 == 0)
                    {
                        CoreHostLog.Write("d3d12-frame", $"Discarded D3D12 capture #{currentAttempt} as boot content; colored={colored}/{sampled} (min {minColored}), energy={energy} (min {minEnergy}), secondaryColorBin={secondary} (min {minSecondary}).");
                    }
                    Volatile.Write(ref _d3d12FrameCaptureRequested, 0);
                    return;
                }

                bool captureSeries = Environment.GetEnvironmentVariable("RYUJINX_UWP_D3D12_CAPTURE_SERIES") == "1";
                if (!captureSeries && Interlocked.CompareExchange(ref _d3d12FrameCaptureCompleted, 1, 0) != 0)
                {
                    return;
                }

                string fullPath = Path.GetFullPath(capturePath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (captureSeries)
                {
                    string stem = Path.GetFileNameWithoutExtension(fullPath);
                    string extension = Path.GetExtension(fullPath);
                    fullPath = Path.Combine(directory ?? string.Empty,
                        $"{stem}-p{Volatile.Read(ref _d3d12FrameCaptureAttempts):D5}{extension}");
                }

                if (image.IsBgra)
                {
                    byte[] rgba = (byte[])frame.Clone();
                    for (int offset = 0; offset < rgba.Length; offset += 4)
                    {
                        (rgba[offset], rgba[offset + 2]) = (rgba[offset + 2], rgba[offset]);
                    }
                    frame = rgba;
                }

                WriteRgbaBitmap(fullPath, frame, width, height);
                CoreHostLog.Write(
                    "d3d12-frame",
                    $"Captured visible D3D12 frame to '{fullPath}'; colored={colored}/{sampled}, energy={energy}, secondaryColorBin={secondary}, flipX={image.FlipX}, flipY={image.FlipY}");
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _d3d12FrameCaptureCompleted, 0);
                CoreHostLog.Error("captura interna do framebuffer D3D12", exception);
            }
            finally
            {
                // On a dark boot frame this also permits a later retry.
                Volatile.Write(ref _d3d12FrameCaptureRequested, 0);
            }
        }

        private static void TryCaptureD3D11Frame(byte[] frame, int width, int height, long sequence)
        {
            // Wait past the boot/transition frames. Leaf Green often shows a
            // solid color field for its first few hundred presents before the
            // title artwork is composed.
            long captureMaximumSequence = D3D11CaptureMinimumSequence + 1800;
            if (Volatile.Read(ref _d3d11FrameCaptureCompleted) != 0 ||
                sequence < D3D11CaptureMinimumSequence ||
                sequence > captureMaximumSequence)
            {
                return;
            }

            string capturePath = Environment.GetEnvironmentVariable("RYUJINX_UWP_CAPTURE_FRAME_PATH");
            if (string.IsNullOrWhiteSpace(capturePath) || frame == null || frame.Length < width * height * 4)
            {
                return;
            }

            int sampled = 0;
            int colored = 0;
            long energy = 0;
            const int PixelStride = 16;
            for (int pixel = 0; pixel < width * height; pixel += PixelStride)
            {
                int offset = pixel * 4;
                byte red = frame[offset];
                byte green = frame[offset + 1];
                byte blue = frame[offset + 2];
                if ((red | green | blue) != 0)
                {
                    colored++;
                }
                energy += red + green + blue;
                sampled++;
            }

            // Skip boot clears and alpha-only frames. Leaf Green and other
            // retro titles legitimately have mostly-black scenes, so a 25%
            // colored-pixel threshold would discard valid title screens.
            if (colored < sampled / 10 || energy < sampled * 30L)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _d3d11FrameCaptureCompleted, 1, 0) != 0)
            {
                return;
            }

            try
            {
                string fullPath = Path.GetFullPath(capturePath);
                string directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                WriteRgbaBitmap(fullPath, frame, width, height);
                CoreHostLog.Write(
                    "d3d11-frame",
                    string.Format(
                        "Captured visible frame {0} to '{1}'; colored={2}/{3}, energy={4}",
                        sequence,
                        fullPath,
                        colored,
                        sampled,
                        energy));
            }
            catch (Exception exception)
            {
                Volatile.Write(ref _d3d11FrameCaptureCompleted, 0);
                CoreHostLog.Error("captura interna do framebuffer D3D11", exception);
            }
        }

        private static void WriteRgbaBitmap(string path, byte[] rgba, int width, int height)
        {
            int pixelBytes = checked(width * height * 4);
            int fileSize = checked(54 + pixelBytes);
            using FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using BinaryWriter writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

            writer.Write((byte)'B');
            writer.Write((byte)'M');
            writer.Write(fileSize);
            writer.Write(0);
            writer.Write(54);
            writer.Write(40);
            writer.Write(width);
            writer.Write(-height); // top-down DIB, matching the guest framebuffer.
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(0);
            writer.Write(pixelBytes);
            writer.Write(2835);
            writer.Write(2835);
            writer.Write(0);
            writer.Write(0);

            for (int offset = 0; offset < pixelBytes; offset += 4)
            {
                writer.Write(rgba[offset + 2]);
                writer.Write(rgba[offset + 1]);
                writer.Write(rgba[offset]);
                writer.Write(rgba[offset + 3]);
            }
        }

        private static void RecordSessionFault(long sessionId, string stage, Exception exception)
        {
            if (!IsActiveSession(sessionId)) return;
            Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
            _lastError = "Etapa: " + stage + Environment.NewLine + exception;
            _started = false;
            _coreBound = false;
            try { _runStop?.Cancel(); } catch (ObjectDisposedException) { }
            try { _switch?.Gpu.GPFifo.Interrupt(); } catch { }
            CoreHostLog.Error(stage, exception);
            TryWriteErrorLog(_lastError);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (Sync)
                {
                    if (ActiveSessionId == sessionId) StopCore();
                }
            });
        }

        private static void InstallCrashTelemetry()
        {
            if (Interlocked.Exchange(ref _crashTelemetryInstalled, 1) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                _runStop?.Cancel();
                CoreHostLog.Write(
                    "fatal",
                    "Unhandled CoreHost exception; terminating=" + eventArgs.IsTerminating
                    + Environment.NewLine + (eventArgs.ExceptionObject?.ToString() ?? "<null>"));
            };
            TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
            {
                Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                _runStop?.Cancel();
                CoreHostLog.Write("fatal", "Unobserved CoreHost task exception" + Environment.NewLine + eventArgs.Exception);
            };
            CoreHostLog.Write("bootstrap", "Managed fatal exception telemetry installed");
        }

        private static void LogD3D11PresentError(string stage, int result)
        {
            if (_d3d11PresentErrorLogged)
            {
                return;
            }

            _d3d11PresentErrorLogged = true;
            CoreHostLog.Write("errors", string.Format("D3D11 {0} failed (HRESULT 0x{1:X8})", stage, result));
        }

        private static bool IsActiveSession(long sessionId) =>
            sessionId > 0 && sessionId == Interlocked.Read(ref _activeSessionId) &&
            Volatile.Read(ref _sessionState) is (int)CoreHostSessionState.Starting or (int)CoreHostSessionState.Running;

        private static int PresentD3D11RendererFrameForSession(long sessionId, int syncInterval)
        {
            if (!IsActiveSession(sessionId))
            {
                CoreHostLog.Write("presentation", $"[Session {sessionId}] Ignored stale Present callback; active={ActiveSessionId}");
                return unchecked((int)0x80004004);
            }

            int result = Native_D3D11_PresentRendererFrameWithSyncInterval((uint)syncInterval);
            if (result < 0)
            {
                RecordD3D11DeviceLoss(sessionId, result);
            }
            return result;
        }

        private static void RecordD3D11DeviceLoss(long sessionId, int result)
        {
            if (!IsActiveSession(sessionId))
            {
                return;
            }

            int removedReason = result;
            try
            {
                int queried = Native_D3D11_GetDeviceStatus();
                if (queried < 0)
                {
                    removedReason = queried;
                }
            }
            catch
            {
                // Preserve the original failing HRESULT.
            }

            if (removedReason < 0 && Interlocked.Exchange(ref _deviceLost, 1) == 0)
            {
                CoreHostLog.Write(
                    "d3d11",
                    $"[Session {sessionId}] DeviceLostAt={DateTime.UtcNow:O}; GetDeviceRemovedReason=0x{removedReason:X8}");
                Volatile.Write(ref _sessionState, (int)CoreHostSessionState.Faulted);
                _runStop?.Cancel();
            }
        }

        private static void HandleMemoryTrimRequested(MemoryPressure pressure)
        {
            MemoryBudgetSnapshot snapshot = RuntimeMemoryBudget.LastSnapshot;
            CoreHostLog.Write("memory", $"Memory pressure transition={pressure}; {snapshot}");

            if (pressure == MemoryPressure.Warning)
            {
                return;
            }

            lock (D3D11ShaderCacheSync)
            {
                long bytes = D3D11ShaderBytecodeCache.Values.Sum(value => (long)value.Length);
                long target = pressure == MemoryPressure.Emergency ? 0 : pressure >= MemoryPressure.Critical ? 4L << 20 : 8L << 20;
                int entries = 0;
                long released = 0;
                foreach (string key in D3D11ShaderBytecodeCache.Keys.ToArray())
                {
                    if (bytes <= target) break;
                    long length = D3D11ShaderBytecodeCache[key].LongLength;
                    D3D11ShaderBytecodeCache.Remove(key);
                    bytes -= length;
                    released += length;
                    entries++;
                }
                RuntimeMemoryBudget.Observe(MemoryCategory.ShaderCache, bytes);
                CoreHostLog.Write(
                    "memory",
                    $"Memory trim pressure={pressure}: released DXBC entries={entries}, bytes={released}, retainedBytes={bytes}; persistent disk cache preserved");
            }

        }

        // The logo partition is optional and is never required to launch a
        // title. Extract only the two presentation assets, under a strict
        // per-file limit, into LocalState; the game container itself is never
        // copied or retained by the frontend.
        private static void TryCacheBootAssets(
            string gamePath,
            IntPtr gameReadHandle,
            GameContainerKind kind,
            string keysDirectory,
            ulong programId)
        {
            if (programId == 0 || kind is not (GameContainerKind.Nsp or GameContainerKind.Xci or GameContainerKind.Nca))
            {
                return;
            }

            try
            {
                Nca programNca = null;
                using (FileStream gameStream = HostFileStream.OpenRead(gamePath, gameReadHandle))
                {
                    if (kind == GameContainerKind.Nca)
                    {
                        programNca = new Nca(_virtualFileSystem.KeySet, gameStream.AsStorage());
                    }
                    else
                    {
                        IFileSystem container;
                        if (kind == GameContainerKind.Xci)
                        {
                            container = new Xci(_virtualFileSystem.KeySet, gameStream.AsStorage())
                                .OpenPartition(XciPartitionType.Secure);
                        }
                        else
                        {
                            PartitionFileSystem pfs = new();
                            pfs.Initialize(gameStream.AsStorage()).ThrowIfFailure();
                            container = pfs;
                        }

                        foreach (DirectoryEntryEx entry in container.EnumerateEntries("/", "*.nca", SearchOptions.Default))
                        {
                            using UniqueRef<IFile> ncaFile = new();
                            container.OpenFile(ref ncaFile.Ref, entry.FullPath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                            Nca candidate = new(_virtualFileSystem.KeySet, ncaFile.Get.AsStorage());
                            if (candidate.Header.ContentType == NcaContentType.Program &&
                                candidate.CanOpenSection(NcaSectionType.Logo))
                            {
                                programNca = candidate;
                                break;
                            }
                        }
                    }

                    if (programNca == null || !programNca.CanOpenSection(NcaSectionType.Logo))
                    {
                        CoreHostLog.Write("bootscreen", "Logo Partition absent; using Ryubox boot fallback.");
                        return;
                    }

                    string localRoot = Directory.GetParent(keysDirectory)?.FullName;
                    if (string.IsNullOrWhiteSpace(localRoot))
                    {
                        return;
                    }

                    string destination = Path.Combine(localRoot, "boot-assets", programId.ToString("x16"));
                    Directory.CreateDirectory(destination);
                    using IFileSystem logoFs = programNca.OpenFileSystem(NcaSectionType.Logo, IntegrityCheckLevel.ErrorOnInvalid);
                    int assetMask = 0;

                    foreach (DirectoryEntryEx entry in logoFs.EnumerateEntries("/", "*", SearchOptions.Default))
                    {
                        if (entry.Type != DirectoryEntryType.File)
                        {
                            continue;
                        }

                        if (string.Equals(entry.Name, "NintendoLogo.png", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryCopyBootAsset(logoFs, entry.FullPath, Path.Combine(destination, "NintendoLogo.png")))
                            {
                                assetMask |= BootAssetNintendoLogo;
                            }
                        }
                        else if (string.Equals(entry.Name, "StartupMovie.gif", StringComparison.OrdinalIgnoreCase))
                        {
                            if (TryCopyBootAsset(logoFs, entry.FullPath, Path.Combine(destination, "StartupMovie.gif")))
                            {
                                assetMask |= BootAssetStartupMovie;
                            }
                        }
                    }

                    Volatile.Write(ref _bootAssetMask, assetMask);
                    CoreHostLog.Write("bootscreen", $"Logo assets cached for {programId:X16}; mask={assetMask}.");
                }
            }
            catch (Exception exception)
            {
                // Boot artwork must never turn a compatible game into a failed
                // launch. Keep the fallback and capture diagnostics instead.
                Volatile.Write(ref _bootAssetMask, 0);
                CoreHostLog.Error("extraindo assets da Logo Partition", exception);
            }
        }

        private static bool TryCopyBootAsset(IFileSystem sourceFs, string sourcePath, string destinationPath)
        {
            try
            {
                using UniqueRef<IFile> source = new();
                sourceFs.OpenFile(ref source.Ref, sourcePath.ToU8Span(), OpenMode.Read).ThrowIfFailure();
                using Stream input = source.Get.AsStream();
                BootAssetCache.Publish(input, destinationPath);
                return true;
            }
            catch (Exception exception)
            {
                CoreHostLog.Error("copiando asset de boot " + sourcePath, exception);
                return false;
            }
        }

        private static GameContainerKind DetectGameContainer(Stream stream, string path)
        {
            long originalPosition = stream.CanSeek ? stream.Position : 0;
            Span<byte> header = stackalloc byte[0x104];
            int read = 0;
            try
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                while (read < header.Length)
                {
                    int count = stream.Read(header.Slice(read));
                    if (count == 0) break;
                    read += count;
                }
            }
            finally
            {
                if (stream.CanSeek)
                {
                    stream.Position = originalPosition;
                }
            }

            if (read >= 0x104 && header.Slice(0x100, 4).SequenceEqual("HEAD"u8))
            {
                return GameContainerKind.Xci;
            }
            if (read >= 4 && header.Slice(0, 4).SequenceEqual("PFS0"u8))
            {
                return GameContainerKind.Nsp;
            }
            if (read >= 0x14 && header.Slice(0x10, 4).SequenceEqual("NRO0"u8))
            {
                return GameContainerKind.Nro;
            }

            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".xci" or ".xcz" => GameContainerKind.Xci,
                ".nro" => GameContainerKind.Nro,
                ".nca" => GameContainerKind.Nca,
                ".nsp" or ".nsz" => GameContainerKind.Nsp,
                _ => GameContainerKind.Unknown,
            };
        }

        private static bool IsCompatibilityPresentationTitle(ulong programId)
        {
            // Some D3D11 swap-chain paths copy a full-size guest surface with
            // the wrong row order. Use the managed compositor for these titles
            // so the presentation profile can normalize orientation before the
            // swap-chain upload. Pokémon Let's Go is included because its
            // controller-selection surface is the first reliable orientation
            // marker and was previously being sent through the direct path.
            return programId == 0x0100D2700DE7C000UL ||
                   programId == 0x010003F003A34000UL;
        }

        private static bool IsPokemonLetsGoTitle(ulong programId)
        {
            return programId == 0x010003F003A34000UL;
        }

        private static MemoryManagerMode ResolveMemoryManagerMode()

        {

            string value = Environment.GetEnvironmentVariable("RYUJINX_UWP_MEMORY_MODE");

            MemoryManagerMode mode = value?.Trim().ToLowerInvariant() switch

            {

                "hostunsafe" or "hostmappedunsafe" => MemoryManagerMode.HostMappedUnsafe,

                "hostmapped" => MemoryManagerMode.HostMapped,

                _ => MemoryManagerMode.SoftwarePageTable,

            };

            CoreHostLog.Write("memory", "Requested memory manager mode: " + mode +

                (mode == MemoryManagerMode.SoftwarePageTable ? "" : " (falls back to SoftwarePageTable on 0x5AF)"));

            return mode;

        }


        private static bool IsPtcOptedIn()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("RYUJINX_UWP_ENABLE_PTC"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPokemonLetsGoPath(string gamePath)
        {
            if (string.IsNullOrWhiteSpace(gamePath))
            {
                return false;
            }

            // Prefer the title ID because filenames may be localized or
            // renamed. Keep the name check as a fallback for a manually
            // renamed NSP used during local validation.
            return gamePath.IndexOf("010003F003A34000", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   (gamePath.IndexOf("pokemon", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    gamePath.IndexOf("pikachu", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    gamePath.IndexOf("let", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool IsRivalsOfAetherPath(string gamePath)
        {
            return !string.IsNullOrWhiteSpace(gamePath) &&
                   (gamePath.IndexOf("0100D2700DE7C000", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    gamePath.IndexOf("rivals of aether", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool UseLocalWin32Surface()
        {
            return OperatingSystem.IsWindows()
                && string.Equals(
                    Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_TEST"),
                    "1",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool UseVisibleLocalWindow()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_VISIBLE"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        }

        private static unsafe SurfaceKHR CreateLocalWin32Surface(Instance instance, Vk api)
        {
            EnsureLocalWin32Window();

            KhrWin32Surface win32Surface = new(api.Context);
            Win32SurfaceCreateInfoKHR createInfo = new()
            {
                SType = StructureType.Win32SurfaceCreateInfoKhr,
                PNext = null,
                Flags = 0,
                Hinstance = _localWindowInstance,
                Hwnd = _localWindowHandle,
            };

            SurfaceKHR surface;
            Result result = win32Surface.CreateWin32Surface(instance, ref createInfo, null, out surface);
            CoreHostLog.Write("vulkan", "vkCreateWin32SurfaceKHR result: " + result);
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"vkCreateWin32SurfaceKHR falhou: {result}.");
            }

            return surface;
        }

        private static void EnsureLocalWin32Window()
        {
            lock (LocalWindowSync)
            {
                if (_localWindowHandle != IntPtr.Zero)
                {
                    return;
                }

                _localWindowInstance = Native_GetModuleHandle(null);
                if (_localWindowInstance == IntPtr.Zero)
                {
                    throw new InvalidOperationException("GetModuleHandleW falhou ao criar a janela Vulkan local.");
                }

                _localWindowClassName = "RyujinxUwpLocalVulkan_" + Environment.ProcessId;
                NativeWndClassEx windowClass = new()
                {
                    CbSize = (uint)Marshal.SizeOf<NativeWndClassEx>(),
                    LpfnWndProc = Marshal.GetFunctionPointerForDelegate(_localWindowProc),
                    HInstance = _localWindowInstance,
                    LpszClassName = _localWindowClassName,
                };

                ushort registered = Native_RegisterClassEx(ref windowClass);
                if (registered == 0 && Marshal.GetLastWin32Error() != 1410)
                {
                    throw new InvalidOperationException("RegisterClassExW falhou ao criar a janela Vulkan local.");
                }

                string localWindowTitle = Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_TITLE");
                if (string.IsNullOrWhiteSpace(localWindowTitle))
                {
                    localWindowTitle = "Ryujinx local Vulkan validation";
                }

                _localWindowHandle = Native_CreateWindowEx(
                    0,
                    _localWindowClassName,
                    localWindowTitle,
                    0x00CF0000,
                    0,
                    0,
                    1280,
                    720,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    _localWindowInstance,
                    IntPtr.Zero);

                if (_localWindowHandle == IntPtr.Zero)
                {
                    Native_UnregisterClass(_localWindowClassName, _localWindowInstance);
                    _localWindowClassName = null;
                    throw new InvalidOperationException("CreateWindowExW falhou ao criar a janela Vulkan local.");
                }

                // Keep automated smoke tests hidden by default. Interactive
                // desktop validation can opt into a visible game window.
                if (UseVisibleLocalWindow())
                {
                    Native_ShowWindow(_localWindowHandle, 5);
                    Native_UpdateWindow(_localWindowHandle);
                }
            }
        }

        private static void DestroyLocalWin32Window()
        {
            lock (LocalWindowSync)
            {
                if (_localWindowHandle != IntPtr.Zero)
                {
                    Native_DestroyWindow(_localWindowHandle);
                    _localWindowHandle = IntPtr.Zero;
                }

                if (!string.IsNullOrWhiteSpace(_localWindowClassName) && _localWindowInstance != IntPtr.Zero)
                {
                    Native_UnregisterClass(_localWindowClassName, _localWindowInstance);
                }

                _localWindowClassName = null;
                _localWindowInstance = IntPtr.Zero;
            }
        }

        private static IntPtr LocalWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
        {
            // WM_APP + 41: tenths of an FPS, sampled on the GPU owner.
            if (message == 0x8029)
            {
                string title = Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_TITLE") ?? "Ryubox";
                string fps = (wParam.ToInt64() / 10.0).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
                Native_SetWindowText(window, $"{title} | {fps} FPS");
                return IntPtr.Zero;
            }

            // This route exists only for the visible desktop D3D12 harness.
            // The actual swap-chain HWND owns focus, so a hidden WinForms
            // helper cannot reliably receive keyboard messages from a tester.
            if (IsLocalWindowKeyboardEnabled())
            {
                const uint WmKeyDown = 0x0100;
                const uint WmKeyUp = 0x0101;
                const uint WmSysKeyDown = 0x0104;
                const uint WmSysKeyUp = 0x0105;
                const uint WmKillFocus = 0x0008;
                uint key = unchecked((uint)wParam.ToInt64());

                if (message is WmKeyDown or WmSysKeyDown)
                {
                    if (IsLocalMappedKey(key))
                    {
                        lock (Sync)
                        {
                            if (LocalWindowHeldKeys.Add(key))
                            {
                                UpdateLocalWindowKeyboardInputUnsafe();
                            }
                        }
                        return IntPtr.Zero;
                    }
                }
                else if (message is WmKeyUp or WmSysKeyUp)
                {
                    if (IsLocalMappedKey(key))
                    {
                        lock (Sync)
                        {
                            if (LocalWindowHeldKeys.Remove(key))
                            {
                                UpdateLocalWindowKeyboardInputUnsafe();
                            }
                        }
                        return IntPtr.Zero;
                    }
                }
                else if (message == WmKillFocus)
                {
                    lock (Sync)
                    {
                        if (LocalWindowHeldKeys.Count != 0)
                        {
                            LocalWindowHeldKeys.Clear();
                            UpdateInputUnsafe(0, 0, 0, 0, 0, 0, 0);
                        }
                    }
                }
            }

            return Native_DefWindowProc(window, message, wParam, lParam);
        }

        private static bool IsLocalWindowKeyboardEnabled()
        {
            return string.Equals(
                Environment.GetEnvironmentVariable("RYUJINX_UWP_LOCAL_WINDOW_KEYBOARD"),
                "1",
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLocalMappedKey(uint key)
        {
            return key is 0x0D or // Enter = A
                0x31 or 0x33 or // 1/3 = L/R trigger
                0x25 or 0x26 or 0x27 or 0x28 or // directional pad
                0x41 or 0x43 or 0x44 or 0x45 or 0x49 or 0x4A or 0x4B or 0x4C or
                0x4F or 0x50 or 0x51 or 0x53 or 0x56 or 0x57 or 0x58 or 0x5A;
        }

        // Caller must hold Sync. Button values intentionally mirror the
        // direct-host keyboard harness: Z/Enter=A, X=B, C=X, V=Y,
        // arrows=d-pad, WASD=left stick, IJKL=right stick, P=Plus, O=Minus,
        // Q/E=L/R and 1/3=triggers.
        private static void UpdateLocalWindowKeyboardInputUnsafe()
        {
            bool Held(uint key) => LocalWindowHeldKeys.Contains(key);
            uint buttons = 0;
            if (Held(0x5A) || Held(0x0D)) buttons |= 1; // Z / Enter
            if (Held(0x58)) buttons |= 2; // X
            if (Held(0x43)) buttons |= 4; // C
            if (Held(0x56)) buttons |= 8; // V
            if (Held(0x26)) buttons |= 16; // Up
            if (Held(0x28)) buttons |= 32; // Down
            if (Held(0x25)) buttons |= 64; // Left
            if (Held(0x27)) buttons |= 128; // Right
            if (Held(0x50)) buttons |= 512; // P / Plus (CoreHost ABI bit 9)
            if (Held(0x4F)) buttons |= 256; // O / Minus (CoreHost ABI bit 8)
            if (Held(0x51)) buttons |= 1024; // Q / L
            if (Held(0x45)) buttons |= 2048; // E / R

            float leftX = Held(0x44) ? 1f : Held(0x41) ? -1f : 0f; // D/A
            float leftY = Held(0x57) ? 1f : Held(0x53) ? -1f : 0f; // W/S
            float rightX = Held(0x4C) ? 1f : Held(0x4A) ? -1f : 0f; // L/J
            float rightY = Held(0x49) ? 1f : Held(0x4B) ? -1f : 0f; // I/K
            UpdateInputUnsafe(buttons, leftX, leftY, rightX, rightY, Held(0x31) ? 1f : 0f, Held(0x33) ? 1f : 0f);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeWndClassEx
        {
            public uint CbSize;
            public uint Style;
            public IntPtr LpfnWndProc;
            public int CbClsExtra;
            public int CbWndExtra;
            public IntPtr HInstance;
            public IntPtr HIcon;
            public IntPtr HCursor;
            public IntPtr HbrBackground;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string LpszMenuName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string LpszClassName;

            public IntPtr HIconSm;
        }

        private static string ResolveBaseDirectory(string keysDirectory)
        {
            string fullKeysDirectory = Path.GetFullPath(keysDirectory);

            return string.Equals(Path.GetFileName(fullKeysDirectory), "system", StringComparison.OrdinalIgnoreCase)
                ? Directory.GetParent(fullKeysDirectory)!.FullName
                : fullKeysDirectory;
        }

        private static bool HasInstalledFirmwareContent()
        {
            string registeredDirectory = Path.Combine(
                AppDataManager.BaseDirPath,
                "bis",
                "system",
                "Contents",
                "registered");

            return Directory.Exists(registeredDirectory)
                && Directory.EnumerateFiles(
                    registeredDirectory,
                    "*.nca",
                    SearchOption.AllDirectories).Any()
                || Directory.EnumerateDirectories(
                    registeredDirectory,
                    "*.nca",
                    SearchOption.AllDirectories).Any(directory =>
                        File.Exists(Path.Combine(directory, "00")));
        }

        private static bool HasInstallableFirmwarePayload(string firmwarePath)
        {
            if (Directory.Exists(firmwarePath))
            {
                return Directory.EnumerateFiles(
                    firmwarePath,
                    "*.nca",
                    SearchOption.TopDirectoryOnly).Any()
                    || (Directory.Exists(Path.Combine(firmwarePath, "registered"))
                        && Directory.EnumerateFiles(
                            Path.Combine(firmwarePath, "registered"),
                            "*.nca",
                            SearchOption.TopDirectoryOnly).Any());
            }

            if (!File.Exists(firmwarePath))
            {
                return false;
            }

            if (string.Equals(Path.GetExtension(firmwarePath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                using ZipArchive archive = ZipFile.OpenRead(firmwarePath);
                return archive.Entries.Any(entry =>
                    entry.FullName.EndsWith(".nca", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.EndsWith(".nca/00", StringComparison.OrdinalIgnoreCase));
            }

            // ContentManager performs the cryptographic validation for XCI.
            return string.Equals(Path.GetExtension(firmwarePath), ".xci", StringComparison.OrdinalIgnoreCase);
        }

        private static void CopyKeysIntoAppData(string sourceDirectory)
        {
            foreach (string keyFile in Directory.EnumerateFiles(sourceDirectory, "*.keys", SearchOption.TopDirectoryOnly))
            {
                string destination = Path.Combine(AppDataManager.KeysDirPath, Path.GetFileName(keyFile));
                CoreHostLog.Write("keys", "Found key file " + keyFile);
                if (!string.Equals(Path.GetFullPath(keyFile), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(keyFile, destination, true);
                    CoreHostLog.Write("keys", "Copied key file to " + destination);
                }
            }
        }

        private static SurfaceKHR CreateHeadlessSurface(Instance instance, Vk api)
        {
            unsafe
            {
                // Use Silk's generated wrapper. A hand-written delegate is
                // unsafe for dispatchable Vulkan handles on the first call.
                ExtHeadlessSurface headlessSurface = new(api.Context);
                HeadlessSurfaceCreateInfoEXT createInfo = new()
                {
                    SType = (StructureType)HeadlessSurfaceStructureType,
                    PNext = null,
                    Flags = 0,
                };

                SurfaceKHR surface;
                Console.WriteLine("[CoreHost] Creating VK_EXT_headless_surface.");
                CoreHostLog.Write("vulkan", "Calling vkCreateHeadlessSurfaceEXT");
                Result result = headlessSurface.CreateHeadlessSurface(instance, ref createInfo, null, out surface);
                Console.WriteLine($"[CoreHost] Headless surface result: {result}.");
                CoreHostLog.Write("vulkan", "vkCreateHeadlessSurfaceEXT result: " + result);
                if (result != Result.Success)
                {
                    throw new InvalidOperationException($"vkCreateHeadlessSurfaceEXT falhou: {result}.");
                }

                return surface;
            }

#if false
            if (functionPointer == IntPtr.Zero)
            {
                throw new InvalidOperationException("VK_EXT_headless_surface não está disponível no loader Vulkan.");
            }

            unsafe
            {
                CreateHeadlessSurfaceDelegate createSurface = Marshal.GetDelegateForFunctionPointer<CreateHeadlessSurfaceDelegate>(functionPointer);
                HeadlessSurfaceCreateInfo createInfo = new()
                {
                    StructureType = (StructureType)HeadlessSurfaceStructureType,
                    Next = IntPtr.Zero,
                    Flags = 0,
                };

                int result = createSurface((ulong)instance.Handle, &createInfo, IntPtr.Zero, out ulong surface);
                if (result != 0)
                {
                    throw new InvalidOperationException($"vkCreateHeadlessSurfaceEXT falhou: {result}.");
                }

                return new SurfaceKHR(surface);
            }
#endif
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr NativeWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr Native_GetModuleHandle(string moduleName);

        [DllImport("user32.dll", EntryPoint = "RegisterClassExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort Native_RegisterClassEx(ref NativeWndClassEx windowClass);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr Native_CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            IntPtr parent,
            IntPtr menu,
            IntPtr instance,
            IntPtr parameter);

        [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_DestroyWindow(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "ShowWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", EntryPoint = "UpdateWindow", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_UpdateWindow(IntPtr window);

        [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_SetWindowText(IntPtr window, string text);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Native_UnregisterClass(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr Native_DefWindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_GetSurfaceSize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_GetSurfaceSize(out uint width, out uint height);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_InitializeForWindow", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_InitializeForWindow(IntPtr window, uint width, uint height);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_Shutdown", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_Shutdown();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_GetNativeObjects", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_GetNativeObjects(out IntPtr device, out IntPtr deviceContext, out IntPtr swapChain);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_BeginCoreSession", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_BeginCoreSession();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_BeginCoreSessionV2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_BeginCoreSessionV2(ulong sessionId);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_BindGraphicsThread", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_BindGraphicsThread();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_EndCoreSession", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_EndCoreSession();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_EndCoreSessionV2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_EndCoreSessionV2(ulong sessionId);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_Acquire", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12Frontend_Acquire(ulong sessionId, out IntPtr context);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyuboxD3D12Frontend_Release", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12Frontend_Release(ulong sessionId);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_GetOwnershipState", CallingConvention = CallingConvention.StdCall)]
        private static extern uint Native_D3D11_GetOwnershipState();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_Resize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Resize(uint width, uint height);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_CopyRendererTextureV2", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_CopyRendererTexture(
            IntPtr resource, [MarshalAs(UnmanagedType.I1)] bool flipY,
            float left, float top, float right, float bottom,
            [MarshalAs(UnmanagedType.I1)] bool preserveExplicitCrop);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_PresentRendererFrame", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_PresentRendererFrame();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_PresentRendererFrameWithSyncInterval", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_PresentRendererFrameWithSyncInterval(uint syncInterval);

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11PresentationTelemetry
        {
            public ulong BeginCount;
            public ulong BindCount;
            public ulong CopyAttempts;
            public ulong CopySuccess;
            public ulong PresentAttempts;
            public ulong PresentSuccess;
            public ulong UploadAttempts;
            public ulong UploadSuccess;
            public int LastCopyResult;
            public int LastPresentResult;
            public int LastUploadResult;
            public uint GraphicsThreadId;
            public uint Width;
            public uint Height;
            public uint Format;
            public uint BufferCount;
            public ulong SessionId;
            public ulong DeviceGeneration;
            public ulong VisibleGuestFrames;
            public ulong GuestFramesAcceptedByPresenter;
            public ulong GuestFramesSubmittedToSwapChain;
            public ulong FrontendFramesSubmitted;
            public ulong DeviceLostAt;
            public int DeviceRemovedReason;
            public uint PresentationState;
            public uint PresentationOwner;
            public uint Reserved;

            public readonly string ToLogString() =>
                $"sessionId={SessionId}, deviceGeneration={DeviceGeneration}, presentationState={PresentationState}, owner={PresentationOwner}, " +
                $"begin={BeginCount}, bind={BindCount}, graphicsThread={GraphicsThreadId}, " +
                $"copy={CopySuccess}/{CopyAttempts}, lastCopy=0x{LastCopyResult:X8}, " +
                $"present={PresentSuccess}/{PresentAttempts}, lastPresent=0x{LastPresentResult:X8}, " +
                $"guestFramesAcceptedByPresenter={GuestFramesAcceptedByPresenter}, " +
                $"guestFramesSubmittedToSwapChain={GuestFramesSubmittedToSwapChain}, " +
                $"guestFramesVisibleOnAppSurface={VisibleGuestFrames}, frontendFramesSubmitted={FrontendFramesSubmitted}, " +
                $"upload={UploadSuccess}/{UploadAttempts}, lastUpload=0x{LastUploadResult:X8}, " +
                $"swapChain={Width}x{Height}, format={Format}, buffers={BufferCount}, " +
                $"deviceRemovedReason=0x{DeviceRemovedReason:X8}, deviceLostAt={DeviceLostAt}";
        }

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_GetPresentationTelemetry", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_GetPresentationTelemetry(out D3D11PresentationTelemetry telemetry);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_Render", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Render();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_GetDeviceStatus", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_GetDeviceStatus();

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_CompileHlsl", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_CompileHlsl(
            uint stage,
            IntPtr source,
            uint sourceLength,
            out IntPtr bytecode,
            out uint bytecodeLength,
            out IntPtr errorMessage);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_FreeBuffer", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_FreeBuffer(IntPtr buffer);

        [DllImport("Ryujinx.Uwp.Native.dll", EntryPoint = "RyujinxUwpGraphicsD3D11_UploadFrame", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_UploadFrame(IntPtr pixels, uint width, uint height, uint rowPitch);
    }
}
