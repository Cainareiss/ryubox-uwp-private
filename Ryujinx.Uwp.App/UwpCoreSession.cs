using System;
using System.Runtime.InteropServices;
using Windows.Gaming.Input;

namespace Ryujinx.Uwp.App
{
    internal sealed class UwpCoreSession : IDisposable
    {
        private const string NativeLibraryName = "Ryujinx.Uwp.Native.dll";
        private const string CoreHostLibraryName = "Ryujinx.Uwp.CoreHost.dll";
        private const uint CoreButtonA = 1u << 0;
        private const uint CoreButtonB = 1u << 1;
        private const uint CoreButtonX = 1u << 2;
        private const uint CoreButtonY = 1u << 3;
        private const uint CoreButtonDpadUp = 1u << 4;
        private const uint CoreButtonDpadDown = 1u << 5;
        private const uint CoreButtonDpadLeft = 1u << 6;
        private const uint CoreButtonDpadRight = 1u << 7;
        private const uint CoreButtonMinus = 1u << 8;
        private const uint CoreButtonPlus = 1u << 9;
        private const uint CoreButtonL = 1u << 10;
        private const uint CoreButtonR = 1u << 11;
        private const uint CoreButtonLStick = 1u << 12;
        private const uint CoreButtonRStick = 1u << 13;
        private bool _started;
        private bool _usingCoreHost;

        public bool IsGameActive { get; set; }

        public ulong SessionId
        {
            get
            {
                if (!_usingCoreHost)
                {
                    return 0;
                }
                try { return CoreHost_GetSessionId(); }
                catch { return 0; }
            }
        }

        public bool IsRunning
        {
            get
            {
                try
                {
                    bool coreHostStarted = CoreHost_IsStarted() != 0;
                    if (coreHostStarted)
                    {
                        _started = true;
                        _usingCoreHost = true;
                    }
                    else
                    {
                        _started = false;
                    }
                }
                catch
                {
                    // During package startup the NativeAOT DLL may not have
                    // been loaded yet. Preserve the managed state in that case.
                }

                return _started;
            }
        }

        public bool CoreRendererBound
        {
            get
            {
                if (!_started)
                {
                    return false;
                }

                try
                {
                    return _usingCoreHost ? CoreHost_IsBound() != 0 : Native_IsBound() != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool HasFirstVisiblePresent
        {
            get
            {
                if (!_usingCoreHost || !_started)
                {
                    return false;
                }

                try
                {
                    return CoreHost_HasFirstVisiblePresent() != 0;
                }
                catch
                {
                    return false;
                }
            }
        }

        public ulong ActiveProgramId
        {
            get
            {
                if (!_usingCoreHost)
                {
                    return 0;
                }

                try { return CoreHost_GetActiveProgramId(); }
                catch { return 0; }
            }
        }

        public int BootAssetMask
        {
            get
            {
                if (!_usingCoreHost)
                {
                    return 0;
                }

                try { return CoreHost_GetBootAssetMask(); }
                catch { return 0; }
            }
        }

        public bool TryStart(
            string gamePath,
            IntPtr gameReadHandle,
            string keysDirectory,
            string firmwarePath,
            string backend,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit,
            RyuboxCoreSettings settings,
            out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(gamePath))
            {
                error = "O caminho do jogo está vazio.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(keysDirectory))
            {
                error = "O diretório de chaves está vazio.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(backend))
            {
                error = "Nenhum backend gráfico foi selecionado.";
                return false;
            }

            try
            {
                UwpFrontendLog.Write(
                    "session",
                    string.Format(
                        "CoreHost_Start game='{0}', brokeredHandle={1}, keys='{2}', firmware='{3}', backend='{4}', currentMemoryLimit={5}, expectedMemoryLimit={6}",
                        gamePath,
                        gameReadHandle != IntPtr.Zero,
                        keysDirectory,
                        firmwarePath,
                        backend,
                        appMemoryUsageLimit,
                        expectedAppMemoryUsageLimit));
                int hresult = CoreHost_Start(
                    gamePath,
                    gameReadHandle,
                    keysDirectory,
                    firmwarePath ?? string.Empty,
                    backend,
                    appMemoryUsageLimit,
                    expectedAppMemoryUsageLimit,
                    settings == null ? RyuboxCoreSettings.CreateDefault().ToJson() : settings.ToJson());
                if (hresult < 0)
                {
                    string coreError = ReadCoreHostError();
                    error = string.Format("CoreHost Ryujinx falhou (HRESULT 0x{0:X8}).", hresult);
                        if (!string.IsNullOrWhiteSpace(coreError))
                        {
                            error += "\n" + coreError;
                        }
                    else
                    {
                        error += "\nDetalhe não retornado pelo CoreHost; verifique corehost-error.log no sandbox UWP.";
                    }

                    UwpFrontendLog.Write("errors", error);

                    return false;
                }

                _usingCoreHost = true;

                _started = true;
                UwpFrontendLog.Write("session", "CoreHost_Start returned success");
                return true;
            }
            catch (DllNotFoundException)
            {
                error = "Ryujinx.Uwp.CoreHost.dll não está no pacote ou não pôde ser carregado.";
                UwpFrontendLog.Write("errors", error);
                return false;
            }
            catch (EntryPointNotFoundException)
            {
                error = "Ryujinx.Uwp.CoreHost.dll não expõe a API de sessão esperada.";
                UwpFrontendLog.Write("errors", error);
                return false;
            }
            catch (Exception exception)
            {
                error = "Falha ao iniciar o CoreHost Ryujinx: " + exception.Message;
                UwpFrontendLog.Error("CoreHost_Start", exception);
                return false;
            }
        }

        public bool TryInstallFirmware(string keysDirectory, string firmwarePath, out string error)
        {
            error = string.Empty;
            IntPtr keys = IntPtr.Zero;
            IntPtr firmware = IntPtr.Zero;
            try
            {
                keys = Marshal.StringToHGlobalUni(keysDirectory ?? string.Empty);
                firmware = Marshal.StringToHGlobalUni(firmwarePath ?? string.Empty);
                int hresult = CoreHost_InstallFirmware(keys, firmware);
                if (hresult >= 0)
                {
                    UwpFrontendLog.Write("firmware", "CoreHost standalone firmware installation succeeded");
                    return true;
                }
                error = string.Format("Falha ao instalar firmware (HRESULT 0x{0:X8}).", hresult);
                string detail = ReadCoreHostError();
                if (!string.IsNullOrWhiteSpace(detail)) error += "\n" + detail;
                UwpFrontendLog.Write("errors", error);
                return false;
            }
            catch (Exception exception)
            {
                error = "Falha ao chamar o instalador de firmware: " + exception.Message;
                UwpFrontendLog.Error("CoreHost_InstallFirmware", exception);
                return false;
            }
            finally
            {
                if (keys != IntPtr.Zero) Marshal.FreeHGlobal(keys);
                if (firmware != IntPtr.Zero) Marshal.FreeHGlobal(firmware);
            }
        }

        public void SetInput(UwpInputSnapshot snapshot)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                uint coreButtons = ToCoreButtonMask(snapshot.Buttons);

                if (_usingCoreHost)
                {
                    CoreHost_SetInput(
                        coreButtons,
                        (float)snapshot.LeftThumbstickX,
                        (float)snapshot.LeftThumbstickY,
                        (float)snapshot.RightThumbstickX,
                        (float)snapshot.RightThumbstickY,
                        (float)snapshot.LeftTrigger,
                        (float)snapshot.RightTrigger);
                }
                else
                {
                    Native_SetInput(
                        coreButtons,
                        (float)snapshot.LeftThumbstickX,
                        (float)snapshot.LeftThumbstickY,
                        (float)snapshot.RightThumbstickX,
                        (float)snapshot.RightThumbstickY,
                        (float)snapshot.LeftTrigger,
                        (float)snapshot.RightTrigger);
                }
            }
            catch
            {
                // Input polling must not take down the UI if the native host
                // disappears during package shutdown.
            }
        }

        private static uint ToCoreButtonMask(GamepadButtons buttons)
        {
            uint result = 0;

            // Never cast Windows.Gaming.Input.GamepadButtons directly. Its bit
            // order starts with Menu/View before A/B/X/Y, while CoreHost expects
            // a compact Ryubox/Switch mask. A raw cast made Menu act as A, View
            // act as B, and left X/Y without their real actions on Xbox.
            AddButton(ref result, buttons, GamepadButtons.A, CoreButtonA);
            AddButton(ref result, buttons, GamepadButtons.B, CoreButtonB);
            AddButton(ref result, buttons, GamepadButtons.X, CoreButtonX);
            AddButton(ref result, buttons, GamepadButtons.Y, CoreButtonY);
            AddButton(ref result, buttons, GamepadButtons.DPadUp, CoreButtonDpadUp);
            AddButton(ref result, buttons, GamepadButtons.DPadDown, CoreButtonDpadDown);
            AddButton(ref result, buttons, GamepadButtons.DPadLeft, CoreButtonDpadLeft);
            AddButton(ref result, buttons, GamepadButtons.DPadRight, CoreButtonDpadRight);
            AddButton(ref result, buttons, GamepadButtons.View, CoreButtonMinus);
            AddButton(ref result, buttons, GamepadButtons.Menu, CoreButtonPlus);
            AddButton(ref result, buttons, GamepadButtons.LeftShoulder, CoreButtonL);
            AddButton(ref result, buttons, GamepadButtons.RightShoulder, CoreButtonR);
            AddButton(ref result, buttons, GamepadButtons.LeftThumbstick, CoreButtonLStick);
            AddButton(ref result, buttons, GamepadButtons.RightThumbstick, CoreButtonRStick);

            return result;
        }

        private static void AddButton(ref uint result, GamepadButtons buttons, GamepadButtons source, uint targetMask)
        {
            if ((buttons & source) == source)
            {
                result |= targetMask;
            }
        }

        public void Stop()
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_usingCoreHost)
                {
                    CoreHost_Stop();
                }
                else
                {
                    Native_Stop();
                }
            }
            catch
            {
                // The native host may already have been unloaded by UWP.
            }

            UwpFrontendLog.Write("session", "CoreHost stopped");
            _started = false;
            _usingCoreHost = false;
        }

        public void Dispose()
        {
            Stop();
        }

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpCore_Start", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int Native_Start(
            [MarshalAs(UnmanagedType.LPWStr)] string gamePath,
            [MarshalAs(UnmanagedType.LPWStr)] string keysDirectory,
            [MarshalAs(UnmanagedType.LPWStr)] string firmwarePath,
            [MarshalAs(UnmanagedType.LPWStr)] string backend);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpCore_Stop", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_Stop();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpCore_SetInput", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_SetInput(
            uint buttons,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            float leftTrigger,
            float rightTrigger);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpCore_IsBound", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_IsBound();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_Start", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_Start(
            IntPtr gamePath,
            IntPtr keysDirectory,
            IntPtr firmwarePath,
            IntPtr backend,
            IntPtr settingsJson,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit);

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_StartWithHandle", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_StartWithHandle(
            IntPtr gamePath,
            IntPtr gameReadHandle,
            IntPtr keysDirectory,
            IntPtr firmwarePath,
            IntPtr backend,
            IntPtr settingsJson,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit);

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_InstallFirmware", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_InstallFirmware(IntPtr keysDirectory, IntPtr firmwarePath);

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_Stop", CallingConvention = CallingConvention.StdCall)]
        private static extern void CoreHost_Stop();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_IsBound", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_IsBound();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_IsStarted", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_IsStarted();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_SetInput", CallingConvention = CallingConvention.StdCall)]
        private static extern void CoreHost_SetInput(
            uint buttons,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            float leftTrigger,
            float rightTrigger);

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_GetSessionState", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_GetSessionState();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_GetSessionId", CallingConvention = CallingConvention.StdCall)]
        private static extern ulong CoreHost_GetSessionId();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_HasFirstVisiblePresent", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_HasFirstVisiblePresent();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_GetActiveProgramId", CallingConvention = CallingConvention.StdCall)]
        private static extern ulong CoreHost_GetActiveProgramId();

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_GetBootAssetMask", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_GetBootAssetMask();

        public int SessionState
        {
            get
            {
                try
                {
                    return _usingCoreHost ? CoreHost_GetSessionState() : (_started ? 2 : 0);
                }
                catch
                {
                    return _started ? 2 : 0;
                }
            }
        }

        [DllImport(CoreHostLibraryName, EntryPoint = "RyujinxUwpCoreHost_GetLastError", CallingConvention = CallingConvention.StdCall)]
        private static extern int CoreHost_GetLastError(IntPtr buffer, int capacity);

        private static int CoreHost_Start(
            string gamePath,
            IntPtr gameReadHandle,
            string keysDirectory,
            string firmwarePath,
            string backend,
            ulong appMemoryUsageLimit,
            ulong expectedAppMemoryUsageLimit,
            string settingsJson)
        {
            IntPtr game = IntPtr.Zero;
            IntPtr keys = IntPtr.Zero;
            IntPtr firmware = IntPtr.Zero;
            IntPtr selectedBackend = IntPtr.Zero;
            IntPtr serializedSettings = IntPtr.Zero;

            try
            {
                game = Marshal.StringToHGlobalUni(gamePath);
                keys = Marshal.StringToHGlobalUni(keysDirectory);
                firmware = Marshal.StringToHGlobalUni(firmwarePath);
                selectedBackend = Marshal.StringToHGlobalUni(backend);
                serializedSettings = Marshal.StringToHGlobalUni(settingsJson ?? string.Empty);
                return gameReadHandle != IntPtr.Zero
                    ? CoreHost_StartWithHandle(
                        game,
                        gameReadHandle,
                        keys,
                        firmware,
                        selectedBackend,
                        serializedSettings,
                        appMemoryUsageLimit,
                        expectedAppMemoryUsageLimit)
                    : CoreHost_Start(
                        game,
                        keys,
                        firmware,
                        selectedBackend,
                        serializedSettings,
                        appMemoryUsageLimit,
                        expectedAppMemoryUsageLimit);
            }
            finally
            {
                if (game != IntPtr.Zero) Marshal.FreeHGlobal(game);
                if (keys != IntPtr.Zero) Marshal.FreeHGlobal(keys);
                if (firmware != IntPtr.Zero) Marshal.FreeHGlobal(firmware);
                if (selectedBackend != IntPtr.Zero) Marshal.FreeHGlobal(selectedBackend);
                if (serializedSettings != IntPtr.Zero) Marshal.FreeHGlobal(serializedSettings);
            }
        }

        public static string ReadCoreHostError()
        {
            const int Capacity = 16384;
            IntPtr buffer = Marshal.AllocHGlobal(Capacity * sizeof(char));
            try
            {
                CoreHost_GetLastError(buffer, Capacity);
                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
