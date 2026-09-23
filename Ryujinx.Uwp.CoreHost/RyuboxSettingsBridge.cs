using System;
using System.Text.Json;
using System.Runtime.InteropServices;
using Ryujinx.Ava.Systems.Configuration; // Or wherever ConfigurationState is
using Ryujinx.Common.Configuration;

namespace Ryujinx.Uwp.CoreHost
{
    public static class RyuboxSettingsBridge
    {
        [DllImport("Ryujinx.Uwp.Native.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern void Native_UpdateSettingsJson([MarshalAs(UnmanagedType.LPStr)] string json);

        public static void SyncSettings()
        {
            var config = ConfigurationState.Instance;
            
            var settings = new
            {
                system = new
                {
                    language = (int)config.System.Language.Value,
                    region = (int)config.System.Region.Value,
                    timeZone = config.System.TimeZone.Value,
                    systemTimeOffsetMinutes = (int)config.System.SystemTimeOffset.Value,
                    ignoreMissingServices = config.System.IgnoreMissingServices.Value,
                    fsIntegrityChecks = config.System.FsIntegrityChecks.Value,
                    expandedDram = config.System.MemoryManagerMode.Value == MemoryManagerMode.SoftwarePageTable
                },
                graphics = new
                {
                    backend = (int)config.Graphics.GraphicsBackend.Value,
                    preferredGpu = config.Graphics.PreferredGpu.Value,
                    resolutionScale = config.Graphics.ResScale.Value,
                    customResolutionScale = config.Graphics.ResScaleCustom.Value,
                    aspectRatio = (int)config.Graphics.AspectRatio.Value,
                    vsync = config.Graphics.EnableVsync.Value,
                    shaderCache = config.Graphics.EnableShaderCache.Value,
                    gpuThreading = config.Graphics.BackendThreading.Value == BackendThreading.On,
                    macroHle = config.Graphics.EnableMacroHLE.Value,
                    colorSpacePassthrough = config.Graphics.EnableColorSpacePassthrough.Value
                },
                audio = new
                {
                    backend = (int)config.System.AudioBackend.Value,
                    masterVolume = config.System.AudioVolume.Value
                },
                network = new
                {
                    guestInternetAccess = config.System.EnableInternetAccess.Value,
                    multiplayerMode = (int)config.Multiplayer.Mode.Value,
                    networkInterface = config.Multiplayer.LanInterfaceId.Value
                },
                logs = new
                {
                    fileLogging = config.Logger.EnableFileLog.Value,
                    trace = config.Logger.EnableTrace.Value,
                    debug = config.Logger.EnableDebug.Value,
                    information = config.Logger.EnableInfo.Value,
                    warning = config.Logger.EnableWarn.Value,
                    error = config.Logger.EnableError.Value,
                    stub = config.Logger.EnableStub.Value,
                    guestLogs = config.Logger.EnableGuest.Value,
                    fsAccessLogs = config.Logger.EnableFsAccessLog.Value
                }
            };

            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            };

            string json = JsonSerializer.Serialize(settings, options);
            Native_UpdateSettingsJson(json);
        }
    }
}
