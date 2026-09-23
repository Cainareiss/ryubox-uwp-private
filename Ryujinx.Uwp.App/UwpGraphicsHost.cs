using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Gaming.Input;
using Windows.UI.Xaml.Controls;

namespace Ryujinx.Uwp.App
{
    internal sealed class UwpGraphicsProbeResult
    {
        public bool Direct3DInteropApiPresent { get; set; }
        public bool NativeHostPackaged { get; set; }
        public bool D3D11HostAvailable { get; set; }
        public bool SpirvCrossPackaged { get; set; }
        public bool DozenDriverPackaged { get; set; }
        public bool VulkanLoaderPackaged { get; set; }
        public bool DozenEntryPointAvailable { get; set; }
        public bool CoreRendererBound { get; set; }
        public string SelectedBackend { get; set; }
        public string Summary { get; set; }
        public List<string> Diagnostics { get; } = new List<string>();
    }

    internal static class UwpGraphicsHost
    {
        private const string NativeLibraryName = "Ryujinx.Uwp.Native.dll";
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_Initialize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_Initialize(IntPtr window, IntPtr panel, uint width, uint height, uint flags);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_Shutdown", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_Shutdown();
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_Render", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_Render();
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_GetDeviceStatus", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_GetDeviceStatus();
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_Resize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_Resize(uint width, uint height);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetCompositionScale", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_SetCompositionScale(float x, float y);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetInput", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_SetInput(uint buttons, float lx, float ly, float rx, float ry, float lt, float rt);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetPointer", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_SetPointer(float x, float y, float wheel, int left, int right);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetCatalogue", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int Native_D3D12_SetCatalogue(string titles);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetStatus", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int Native_D3D12_SetStatus(string keys, string firmware);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetOperation", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern int Native_D3D12_SetOperation(string message, float progress, int state);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetOnboardingState", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_SetOnboardingState(int completed, int language, int theme,
            int keysReady, int firmwareReady, int gamesReady);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_SetCover", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_SetCover(int index, byte[] pixels, uint width, uint height, uint pitch);
        [DllImport(NativeLibraryName, EntryPoint = "RyuboxD3D12Frontend_ConsumeAction", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D12_ConsumeAction(out int action);
        private static bool _started;
        private static bool _usingD3D11;
        private static bool _ptcBridgeUnavailable;
        private static SwapChainPanel _boundPanel;
        private const int HostBusy = unchecked((int)0x800700AA);

        private static void CheckFrontendResult(int result)
        {
            // The game owns the context while loading/running. UI work must not
            // submit commands to it, nor report normal lease contention as loss.
            if (result != HostBusy) Marshal.ThrowExceptionForHR(result);
        }

        public static string ActiveBackend
        {
            get { return _usingD3D11 ? "D3D11" : "D3D12"; }
        }

        public static UwpGraphicsProbeResult Probe()
        {
            string packagePath = Package.Current.InstalledLocation.Path;
            bool nativeHost = File.Exists(Path.Combine(packagePath, NativeLibraryName));
            bool dozenDriver = File.Exists(Path.Combine(packagePath, "vulkan_dzn.dll"))
                && File.Exists(Path.Combine(packagePath, "spirv_to_dxil.dll"))
                && File.Exists(Path.Combine(packagePath, "dzn_icd.x86_64.json"));
            bool dozenRuntime = File.Exists(Path.Combine(packagePath, "DXIL.dll"))
                || File.Exists(Path.Combine(packagePath, "dxcompiler_x.dll"))
                || File.Exists(Path.Combine(packagePath, "dxcompiler_xs.dll"));
            bool vulkanLoader = File.Exists(Path.Combine(packagePath, "vulkan-1.dll"));
            bool spirvCross = File.Exists(Path.Combine(packagePath, "spirv-cross-c-shared.dll"));
            bool direct3D = ApiInformation.IsTypePresent("Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice");
            // Do not create a D3D11 device merely to probe a D3D12 session.
            bool d3d11Host = _usingD3D11 && nativeHost && TryProbeD3D11();

            UwpFrontendLog.Write("graphics", string.Format(
                "Probe package='{0}', nativeHost={1}, d3d11Host={2}, spirvCross={3}, dozen={4}, dozenRuntime={5}, vulkanLoader={6}, direct3D={7}",
                packagePath, nativeHost, d3d11Host, spirvCross, dozenDriver, dozenRuntime, vulkanLoader, direct3D));

            UwpGraphicsProbeResult result = new UwpGraphicsProbeResult
            {
                Direct3DInteropApiPresent = direct3D,
                NativeHostPackaged = nativeHost,
                D3D11HostAvailable = d3d11Host,
                SpirvCrossPackaged = spirvCross,
                DozenDriverPackaged = dozenDriver,
                VulkanLoaderPackaged = vulkanLoader,
                SelectedBackend = "Unsupported",
                CoreRendererBound = false
            };

            result.Diagnostics.Add(direct3D
                ? "A API de interoperabilidade Direct3D esta presente."
                : "A API de interoperabilidade Direct3D nao foi encontrada.");
            result.Diagnostics.Add(nativeHost
                ? "Host nativo D3D12 e D3D11 empacotado."
                : "Host nativo D3D12/D3D11 ausente do pacote; compile a DLL C++.");
            result.Diagnostics.Add(d3d11Host && direct3D && spirvCross
                ? "Backend GAL Direct3D 11 nativo e compilador SPIR-V encontrados."
                : "D3D11 nao ativado nesta sessao; permanece no codigo como backend separado.");
            if (dozenDriver || vulkanLoader || dozenRuntime)
            {
                result.Diagnostics.Add(dozenDriver
                    ? "Mesa Dozen completo encontrado: vulkan_dzn.dll, spirv_to_dxil.dll e ICD presentes."
                    : "Mesa Dozen ausente/incompleto; nao e necessario para D3D12 nativo.");
                result.Diagnostics.Add(vulkanLoader
                    ? "vulkan-1.dll encontrado no pacote."
                    : "Loader Vulkan ausente; nao e necessario para Direct3D nativo.");
                result.Diagnostics.Add(dozenRuntime
                    ? "Runtime DXIL do Mesa Dozen encontrado no pacote."
                    : "Runtime DXIL ausente; o caminho HLSL/DXBC D3D12 nao depende dele.");
            }
            else
            {
                result.Diagnostics.Add("Direct3D nativo: Vulkan/Mesa Dozen nao participam do caminho de renderizacao.");
            }

            if (nativeHost && direct3D && dozenDriver && dozenRuntime && vulkanLoader)
            {
                result.DozenEntryPointAvailable = TryProbeDozen();
            }

            if (_started && !_usingD3D11 && spirvCross)
            {
                result.SelectedBackend = "D3D12";
                result.Summary = "Frontend Direct3D 12 nativo inicializado; CoreHost usa lease da mesma superficie, sem D3D11/Dozen.";
            }
            else if (nativeHost && direct3D && d3d11Host && spirvCross)
            {
                result.SelectedBackend = "D3D11";
                result.Summary = "Direct3D 11 nativo pronto; Vulkan e Mesa Dozen nao participam deste caminho.";
            }
            else if (nativeHost && direct3D && dozenDriver && dozenRuntime && vulkanLoader)
            {
                result.SelectedBackend = "D3D12-Dozen";
                result.Summary = result.DozenEntryPointAvailable
                    ? "D3D12 + Mesa Dozen detectados; CoreHost Ryujinx pronto para iniciar."
                    : "D3D12 + Mesa Dozen empacotados; probe opcional indisponivel, CoreHost tentara iniciar diretamente.";
            }
            else if (nativeHost && direct3D)
            {
                result.SelectedBackend = "D3D12";
                result.Summary = "Host Direct3D encontrado, mas o compilador SPIR-V do backend D3D11 esta ausente.";
            }
            else if (direct3D)
            {
                result.SelectedBackend = "D3D12 host pendente";
                result.Summary = "A API Direct3D esta disponivel, mas o host nativo ainda nao foi empacotado.";
            }
            else
            {
                result.Summary = "Nenhum host grafico UWP utilizavel foi detectado.";
            }

            UwpFrontendLog.Write("graphics", "Probe result: backend='" + result.SelectedBackend + "', summary='" + result.Summary + "'");
            return result;
        }

        public static bool TryInitialize(SwapChainPanel panel, uint width, uint height, out string error)
        {
            return TryInitialize(panel, width, height, "D3D12", out error);
        }

        public static bool TryInitialize(
            SwapChainPanel panel,
            uint width,
            uint height,
            string requestedBackend,
            out string error)
        {
            error = string.Empty;
            bool useD3D11 = string.Equals(requestedBackend, "D3D11", StringComparison.OrdinalIgnoreCase);

            if (_started && _usingD3D11 == useD3D11 && ReferenceEquals(_boundPanel, panel))
            {
                return SetCompositionScale(panel, out error);
            }

            if (_started)
            {
                Shutdown();
                if (_started)
                {
                    error = "O contexto grafico ainda pertence ao jogo; troca de backend recusada.";
                    return false;
                }
            }

            IntPtr panelUnknown = IntPtr.Zero;
            try
            {
                UwpFrontendLog.Write("graphics", string.Format(
                    "Initialize backend={0}, width={1}, height={2}",
                    useD3D11 ? "D3D11" : requestedBackend,
                    width,
                    height));
                panelUnknown = Marshal.GetIUnknownForObject(panel);
                int hresult = useD3D11
                    ? Native_D3D11_Initialize(panelUnknown, width, height)
                    : Native_D3D12_Initialize(IntPtr.Zero, panelUnknown, width, height, 0);
                if (hresult < 0)
                {
                    error = FormatHResult(useD3D11 ? "D3D11 nao inicializou" : "D3D12 nao inicializou", hresult);
                    UwpFrontendLog.Write("errors", "Graphics initialize failed: " + error);
                    return false;
                }

                _usingD3D11 = useD3D11;
                _started = true;
                _boundPanel = panel;
                UwpFrontendLog.Write("graphics", (useD3D11 ? "D3D11" : "D3D12") + " graphics host initialized");
                return SetCompositionScale(panel, out error);
            }
            catch (DllNotFoundException exception)
            {
                error = "Ryujinx.Uwp.Native.dll nao esta no pacote.";
                UwpFrontendLog.Error("graphics initialize / DLL ausente", exception);
                return false;
            }
            catch (EntryPointNotFoundException exception)
            {
                error = "A DLL nativa nao expoe a API de host " + (useD3D11 ? "D3D11" : "D3D12") + ".";
                UwpFrontendLog.Error("graphics initialize / entrypoint ausente", exception);
                return false;
            }
            catch (Exception exception)
            {
                error = "Falha ao criar a superficie " + (useD3D11 ? "D3D11" : "D3D12") + ": " + exception.Message;
                UwpFrontendLog.Error("graphics initialize", exception);
                return false;
            }
            finally
            {
                if (panelUnknown != IntPtr.Zero)
                {
                    Marshal.Release(panelUnknown);
                }
            }
        }

        public static void Resize(uint width, uint height)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                int hresult = _usingD3D11
                    ? Native_D3D11_Resize(width, height)
                    : Native_D3D12_Resize(width, height);
                if (hresult < 0 && hresult != HostBusy)
                {
                    UwpFrontendLog.Write("errors", "Graphics resize failed: " + FormatHResult(
                        _usingD3D11 ? "D3D11 resize" : "D3D12 resize", hresult));
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("graphics resize", exception);
            }
        }

        public static void Render()
        {
            if (!_started)
            {
                return;
            }

            try
            {
                int hresult = _usingD3D11
                    ? Native_D3D11_Render()
                    : Native_D3D12_Render();
                if (hresult < 0 && hresult != HostBusy)
                {
                    UwpFrontendLog.Write("errors", "Graphics render failed: " + FormatHResult(
                        _usingD3D11 ? "D3D11 render" : "D3D12 render", hresult));
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("graphics render", exception);
            }
        }

        public static void SetNativeImGuiInput(UwpInputSnapshot snapshot, GamepadButtons additionalButtons = GamepadButtons.None)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (!_usingD3D11)
                {
                    CheckFrontendResult(Native_D3D12_SetInput(ToNativeButtons(snapshot.Buttons | additionalButtons),
                        (float)snapshot.LeftThumbstickX, (float)snapshot.LeftThumbstickY,
                        (float)snapshot.RightThumbstickX, (float)snapshot.RightThumbstickY,
                        (float)snapshot.LeftTrigger, (float)snapshot.RightTrigger));
                    return;
                }
                Native_D3D11_ImGuiSetInput(
                    ToNativeButtons(snapshot.Buttons | additionalButtons),
                    (float)snapshot.LeftThumbstickX,
                    (float)snapshot.LeftThumbstickY,
                    (float)snapshot.RightThumbstickX,
                    (float)snapshot.RightThumbstickY,
                    (float)snapshot.LeftTrigger,
                    (float)snapshot.RightTrigger);
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui input", exception);
            }
        }

        public static void SetNativeImGuiPointer(float x, float y, bool leftDown, bool rightDown, float wheel)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_usingD3D11) Native_D3D11_ImGuiSetPointer(x, y, leftDown ? 1 : 0, rightDown ? 1 : 0, wheel);
                else CheckFrontendResult(Native_D3D12_SetPointer(x, y, wheel, leftDown ? 1 : 0, rightDown ? 1 : 0));
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui pointer", exception);
            }
        }

        public static void SetNativeImGuiCatalogue(IEnumerable<string> titles)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                string payload = string.Join("\n", titles ?? Enumerable.Empty<string>());
                if (_usingD3D11) Native_D3D11_ImGuiSetCatalogue(payload);
                else CheckFrontendResult(Native_D3D12_SetCatalogue(payload));
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui catalogue", exception);
            }
        }

        public static void SetNativeImGuiStatus(bool keysReady, bool firmwareReady)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (!_usingD3D11)
                {
                    CheckFrontendResult(Native_D3D12_SetStatus(keysReady ? "configuradas" : "ausentes",
                        firmwareReady ? "instalado" : "ausente"));
                    return;
                }
                Native_D3D11_ImGuiSetStatus(
                    keysReady ? "configuradas" : "ausentes",
                    firmwareReady ? "instalado" : "ausente");
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui status", exception);
            }
        }

        public static void SetNativeImGuiOperation(string message, float progress, int state)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_usingD3D11) Native_D3D11_ImGuiSetOperation(message ?? string.Empty, progress, state);
                else CheckFrontendResult(Native_D3D12_SetOperation(message ?? string.Empty, progress, state));
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui operation", exception);
            }
        }

        public static void SetNativeImGuiOnboardingState(bool completed, int language, int theme,
            bool keysReady, bool firmwareReady, bool gamesReady)
        {
            if (!_started)
            {
                return;
            }

            try
            {
                if (_usingD3D11)
                {
                    Native_D3D11_ImGuiSetOnboardingState(completed ? 1 : 0, language, theme,
                        keysReady ? 1 : 0, firmwareReady ? 1 : 0, gamesReady ? 1 : 0);
                }
                else
                {
                    CheckFrontendResult(Native_D3D12_SetOnboardingState(completed ? 1 : 0, language, theme,
                        keysReady ? 1 : 0, firmwareReady ? 1 : 0, gamesReady ? 1 : 0));
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui onboarding state", exception);
            }
        }

        public static void SetNativeImGuiCover(int index, byte[] rgbaPixels, uint width, uint height, uint rowPitch)
        {
            if (!_started || rgbaPixels == null || rgbaPixels.Length == 0)
            {
                return;
            }

            try
            {
                if (width == 0 || height == 0 || rowPitch < (ulong)width * 4 ||
                    (ulong)rgbaPixels.LongLength < (ulong)rowPitch * height)
                    throw new ArgumentException("Invalid cover pixel buffer.");
                int hresult = _usingD3D11
                    ? Native_D3D11_ImGuiSetCover(index, rgbaPixels, width, height, rowPitch)
                    : Native_D3D12_SetCover(index, rgbaPixels, width, height, rowPitch);
                if (hresult < 0)
                {
                    UwpFrontendLog.Write("errors", "Native ImGui cover upload failed: " + FormatHResult("cover", hresult));
                }
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui cover", exception);
            }
        }

        public static int ConsumeNativeImGuiAction()
        {
            if (!_started)
            {
                return -1;
            }

            try
            {
                if (_usingD3D11) return Native_D3D11_ImGuiConsumeAction();
                CheckFrontendResult(Native_D3D12_ConsumeAction(out int action));
                return action;
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native imgui action", exception);
                return -1;
            }
        }

        public static void SetNativePtcEnabled(bool enabled)
        {
            if (_started && !_ptcBridgeUnavailable)
            {
                try { Native_ImGuiSetPtcEnabled(enabled ? 1 : 0); }
                catch (Exception exception)
                {
                    _ptcBridgeUnavailable = true;
                    UwpFrontendLog.Error("native PTC settings bridge unavailable", exception);
                }
            }
        }

        private static bool _fpsBridgeUnavailable;
        public static void SetNativeFpsEnabled(bool enabled)
        {
            if (!_started || _fpsBridgeUnavailable) return;
            try { Native_ImGuiSetFpsEnabled(enabled ? 1 : 0); }
            catch (Exception exception)
            {
                _fpsBridgeUnavailable = true;
                UwpFrontendLog.Error("native FPS bridge unavailable", exception);
            }
        }

        public static int ConsumeNativeFpsChange()
        {
            if (!_started || _fpsBridgeUnavailable) return -1;
            try { return Native_ImGuiConsumeFpsChange(); }
            catch (Exception exception)
            {
                _fpsBridgeUnavailable = true;
                UwpFrontendLog.Error("native FPS bridge unavailable", exception);
                return -1;
            }
        }

        public static int ConsumeNativePtcChange()
        {
            if (!_started || _ptcBridgeUnavailable) return -1;
            try { return Native_ImGuiConsumePtcChange(); }
            catch (Exception exception)
            {
                _ptcBridgeUnavailable = true;
                UwpFrontendLog.Error("native PTC settings bridge unavailable", exception);
                return -1;
            }
        }

        private static uint ToNativeButtons(GamepadButtons buttons)
        {
            uint result = 0;
            if ((buttons & GamepadButtons.DPadUp) != 0) result |= 1u << 0;
            if ((buttons & GamepadButtons.DPadDown) != 0) result |= 1u << 1;
            if ((buttons & GamepadButtons.DPadLeft) != 0) result |= 1u << 2;
            if ((buttons & GamepadButtons.DPadRight) != 0) result |= 1u << 3;
            if ((buttons & GamepadButtons.A) != 0) result |= 1u << 4;
            if ((buttons & GamepadButtons.B) != 0) result |= 1u << 5;
            if ((buttons & GamepadButtons.Y) != 0) result |= 1u << 6;
            if ((buttons & GamepadButtons.X) != 0) result |= 1u << 7;
            if ((buttons & GamepadButtons.Menu) != 0) result |= 1u << 8;
            if ((buttons & GamepadButtons.View) != 0) result |= 1u << 9;
            if ((buttons & GamepadButtons.LeftShoulder) != 0) result |= 1u << 10;
            if ((buttons & GamepadButtons.RightShoulder) != 0) result |= 1u << 11;
            return result;
        }

        public static bool SetCompositionScale(SwapChainPanel panel, out string error)
        {
            error = string.Empty;
            if (!_started || panel == null)
            {
                return true;
            }

            double scaleX = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : 1.0;
            double scaleY = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : 1.0;
            GetPhysicalPanelSize(panel, out uint backBufferWidth, out uint backBufferHeight);
            float inverseX = (float)(1.0 / scaleX);
            float inverseY = (float)(1.0 / scaleY);

            try
            {
                int hresult = _usingD3D11
                    ? Native_D3D11_SetCompositionScale((float)scaleX, (float)scaleY)
                    : Native_D3D12_SetCompositionScale((float)scaleX, (float)scaleY);
                if (hresult == HostBusy) return true;
                UwpFrontendLog.Write("graphics", string.Format(
                    "SwapChainPanel logical={0:F0}x{1:F0} CompositionScale={2:F3}x{3:F3} BackBuffer={4}x{5} MatrixTransform={6:F3}x{7:F3} FinalExpected={8}x{9}",
                    panel.ActualWidth,
                    panel.ActualHeight,
                    scaleX,
                    scaleY,
                    backBufferWidth,
                    backBufferHeight,
                    inverseX,
                    inverseY,
                    backBufferWidth,
                    backBufferHeight));

                if (hresult < 0)
                {
                    error = FormatHResult("SetMatrixTransform falhou", hresult);
                    UwpFrontendLog.Write("errors", error);
                    return false;
                }

                UwpFrontendLog.Write("graphics", string.Format(
                    "SetMatrixTransform succeeded scaleX={0:F3} scaleY={1:F3}",
                    inverseX,
                    inverseY));
                return true;
            }
            catch (Exception exception)
            {
                error = "Falha ao aplicar compensação de CompositionScale: " + exception.Message;
                UwpFrontendLog.Error("graphics composition scale", exception);
                return false;
            }
        }

        public static void Shutdown()
        {
            if (!_started)
            {
                return;
            }

            int hresult = 0;
            try
            {
                if (_usingD3D11)
                {
                    Native_D3D11_Shutdown();
                }
                else
                {
                    hresult = Native_D3D12_Shutdown();
                    if (hresult == HostBusy)
                    {
                        // The CoreHost still owns the shared context. Keep the
                        // managed ownership flags so the UI cannot initialize a
                        // second frontend over a live game session.
                        Marshal.ThrowExceptionForHR(hresult);
                    }
                    if (hresult < 0)
                    {
                        // Device removal/cleanup failure destroys the native
                        // context deliberately. Do not retain a stale managed
                        // "started" flag that would block recovery forever.
                        UwpFrontendLog.Write("errors", "D3D12 graphics shutdown completed with native error: " +
                            FormatHResult("D3D12 shutdown", hresult));
                    }
                }
                UwpFrontendLog.Write("graphics", (_usingD3D11 ? "D3D11" : "D3D12") + " graphics host stopped");
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("graphics shutdown", exception);
                return; // Keep ownership tracked if shutdown was refused.
            }

            _started = false;
            _usingD3D11 = false;
            _boundPanel = null;
        }

        public static bool TryRecoverDevice(
            SwapChainPanel panel,
            out bool recovered,
            out string error)
        {
            recovered = false;
            error = string.Empty;
            if (!_started || panel == null)
            {
                return true;
            }

            int deviceStatus;
            try
            {
                deviceStatus = _usingD3D11 ? Native_D3D11_GetDeviceStatus() : Native_D3D12_GetDeviceStatus();
            }
            catch (Exception exception)
            {
                error = "Falha ao consultar o estado do dispositivo " + ActiveBackend + ": " + exception.Message;
                UwpFrontendLog.Error(ActiveBackend + " device status", exception);
                return false;
            }

            if (deviceStatus >= 0)
            {
                return true;
            }
            if (deviceStatus == HostBusy)
            {
                error = "CoreHost ainda possui a superficie; aguarde o encerramento ou reinicie o aplicativo.";
                return false;
            }

            string backend = ActiveBackend;
            UwpFrontendLog.Write(
                "graphics",
                string.Format("{0} device lost; GetDeviceRemovedReason=0x{1:X8}; rebuilding host", backend, deviceStatus));
            if (_usingD3D11) Native_D3D11_Shutdown();
            else Native_D3D12_Shutdown(); // Frees context even when Drain reports device loss.
            _started = false;
            _usingD3D11 = false;
            _boundPanel = null;

            GetPhysicalPanelSize(panel, out uint width, out uint height);
            if (!TryInitialize(panel, width, height, backend, out error))
            {
                return false;
            }

            recovered = true;
            UwpFrontendLog.Write("graphics", "NewDeviceCreated after device loss; frontend graphics restored");
            return true;
        }

        private static bool TryProbeDozen()
        {
            try
            {
                bool available = Native_ProbeDozen() != 0;
                UwpFrontendLog.Write("graphics", "Native Dozen probe result: " + available);
                UwpFrontendLog.Write(
                    "graphics",
                    string.Format(
                        "Native Dozen probe details: flags=0x{0:X8}, win32Error=0x{1:X8} (1=driver, 2=entrypoint, 4=DXIL, 8/16=Xbox compiler)",
                        Native_GetProbeFlags(),
                        Native_GetProbeError()));
                return available;
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Write("graphics", "Optional native Dozen probe unavailable; packaged files remain usable: " + exception);
                return false;
            }
        }

        private static bool TryProbeD3D11()
        {
            try
            {
                bool available = Native_D3D11_Probe() != 0;
                UwpFrontendLog.Write("graphics", "Native D3D11 host probe result: " + available);
                return available;
            }
            catch (EntryPointNotFoundException)
            {
                UwpFrontendLog.Write("graphics", "Native D3D11 host probe unavailable; the packaged DLL is an older host build.");
                return false;
            }
            catch (Exception exception)
            {
                UwpFrontendLog.Error("native D3D11 probe", exception);
                return false;
            }
        }

        private static string FormatHResult(string message, int hresult)
        {
            return string.Format("{0} (HRESULT 0x{1:X8}).", message, hresult);
        }

        public static void GetPhysicalPanelSize(SwapChainPanel panel, out uint width, out uint height)
        {
            double scaleX = panel.CompositionScaleX > 0 ? panel.CompositionScaleX : 1.0;
            double scaleY = panel.CompositionScaleY > 0 ? panel.CompositionScaleY : 1.0;
            width = (uint)Math.Clamp(Math.Round(panel.ActualWidth * scaleX), 1, 3840);
            height = (uint)Math.Clamp(Math.Round(panel.ActualHeight * scaleY), 1, 2160);

            if (panel.ActualWidth <= 0 || panel.ActualHeight <= 0)
            {
                width = 1280;
                height = 720;
            }
        }

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_Initialize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_Initialize(IntPtr panel, uint width, uint height);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_Resize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_Resize(uint width, uint height);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_Render", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_Render();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_Shutdown", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_Shutdown();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_Initialize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Initialize(IntPtr panel, uint width, uint height);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_Probe", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Probe();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_Resize", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Resize(uint width, uint height);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_SetCompositionScale", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_SetCompositionScale(float scaleX, float scaleY);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_Render", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_Render();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_GetDeviceStatus", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_GetDeviceStatus();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_Shutdown", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_Shutdown();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetInput", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_ImGuiSetInput(
            uint buttons,
            float leftX,
            float leftY,
            float rightX,
            float rightY,
            float leftTrigger,
            float rightTrigger);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetPointer", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_ImGuiSetPointer(
            float x,
            float y,
            int leftDown,
            int rightDown,
            float wheel);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetCatalogue", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern void Native_D3D11_ImGuiSetCatalogue(string newlineSeparatedTitles);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetStatus", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern void Native_D3D11_ImGuiSetStatus(string keysStatus, string firmwareStatus);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetOperation", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private static extern void Native_D3D11_ImGuiSetOperation(string message, float progress, int state);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetOnboardingState", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_D3D11_ImGuiSetOnboardingState(int completed, int language, int theme,
            int keysReady, int firmwareReady, int gamesReady);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiSetCover", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_ImGuiSetCover(
            int index,
            [In] byte[] rgbaPixels,
            uint width,
            uint height,
            uint rowPitch);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphicsD3D11_ImGuiConsumeAction", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_D3D11_ImGuiConsumeAction();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpFrontend_SetPtcEnabled", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_ImGuiSetPtcEnabled(int enabled);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpFrontend_ConsumePtcChange", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_ImGuiConsumePtcChange();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpFrontend_SetFpsEnabled", CallingConvention = CallingConvention.StdCall)]
        private static extern void Native_ImGuiSetFpsEnabled(int enabled);

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpFrontend_ConsumeFpsChange", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_ImGuiConsumeFpsChange();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_ProbeDozen", CallingConvention = CallingConvention.StdCall)]
        private static extern int Native_ProbeDozen();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_GetProbeFlags", CallingConvention = CallingConvention.StdCall)]
        private static extern uint Native_GetProbeFlags();

        [DllImport(NativeLibraryName, EntryPoint = "RyujinxUwpGraphics_GetProbeError", CallingConvention = CallingConvention.StdCall)]
        private static extern uint Native_GetProbeError();
    }
}
