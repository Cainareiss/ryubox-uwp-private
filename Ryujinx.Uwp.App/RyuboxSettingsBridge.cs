using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Windows.Storage;

namespace Ryujinx.Uwp.App
{
    // One versioned snapshot is shared by the XAML shell and CoreHost.  The
    // object deliberately contains only portable values; enum conversion and
    // compatibility policy are repeated in CoreHost so a malformed front end
    // cannot select an unsafe renderer or memory mode.
    [DataContract]
    internal sealed class RyuboxCoreSettings
    {
        [DataMember(Name = "schemaVersion")] public int SchemaVersion = 1;
        [DataMember(Name = "backend")] public string Backend = "D3D11";
        [DataMember(Name = "consoleMode")] public string ConsoleMode = "Handheld";
        [DataMember(Name = "language")] public string Language = "AmericanEnglish";
        [DataMember(Name = "region")] public string Region = "USA";
        [DataMember(Name = "timeZone")] public string TimeZone = "UTC";
        [DataMember(Name = "systemTimeOffsetMinutes")] public int SystemTimeOffsetMinutes;
        [DataMember(Name = "ignoreMissingServices")] public bool IgnoreMissingServices = true;
        [DataMember(Name = "fsIntegrityChecks")] public bool FsIntegrityChecks = true;
        [DataMember(Name = "ptc")] public bool Ptc;
        [DataMember(Name = "showFpsCounter")] public bool? ShowFpsCounter;
        [DataMember(Name = "memoryManagerMode")] public string MemoryManagerMode = "SoftwarePageTable";
        [DataMember(Name = "hypervisor")] public bool Hypervisor;
        [DataMember(Name = "fastMemory")] public bool FastMemory = true;
        [DataMember(Name = "vsync")] public bool VSync = true;
        [DataMember(Name = "aspectRatio")] public string AspectRatio = "Fixed16x9";
        [DataMember(Name = "resolutionScale")] public float ResolutionScale = 1.0f;
        [DataMember(Name = "audioVolume")] public float AudioVolume = 1.0f;
        [DataMember(Name = "audioEnabled")] public bool AudioEnabled = true;
        [DataMember(Name = "guestInternetAccess")] public bool GuestInternetAccess;
        [DataMember(Name = "multiplayerMode")] public string MultiplayerMode = "Disabled";
        [DataMember(Name = "gpuThreading")] public bool GpuThreading;
        [DataMember(Name = "fileLogging")] public bool FileLogging = true;
        [DataMember(Name = "guestLogs")] public bool GuestLogs;
        [DataMember(Name = "fsAccessLogs")] public bool FsAccessLogs;
        [DataMember(Name = "graphicsDebugLogging")] public bool GraphicsDebugLogging;
        [DataMember(Name = "shaderTelemetry")] public bool ShaderTelemetry;
        [DataMember(Name = "presentationLogs")] public bool PresentationLogs;
        [DataMember(Name = "syncStalls")] public bool SyncStalls;

        internal static RyuboxCoreSettings CreateDefault()
        {
            return new RyuboxCoreSettings();
        }

        internal static RyuboxCoreSettings Sanitize(RyuboxCoreSettings candidate)
        {
            RyuboxCoreSettings value = candidate ?? CreateDefault();
            value.SchemaVersion = 1;
            value.Backend = "D3D11";
            value.ConsoleMode = "Handheld";
            value.MemoryManagerMode = "SoftwarePageTable";
            value.Hypervisor = false;
            value.FastMemory = true;
            value.ResolutionScale = Math.Max(0.5f, Math.Min(2.0f, value.ResolutionScale));
            value.AudioVolume = Math.Max(0.0f, Math.Min(1.0f, value.AudioVolume));
            value.TimeZone = string.IsNullOrWhiteSpace(value.TimeZone) ? "UTC" : value.TimeZone;
            value.Language = string.IsNullOrWhiteSpace(value.Language) ? "AmericanEnglish" : value.Language;
            value.Region = string.IsNullOrWhiteSpace(value.Region) ? "USA" : value.Region;
            value.AspectRatio = string.IsNullOrWhiteSpace(value.AspectRatio) ? "Fixed16x9" : value.AspectRatio;
            value.MultiplayerMode = string.IsNullOrWhiteSpace(value.MultiplayerMode) ? "Disabled" : value.MultiplayerMode;
            return value;
        }

        internal string ToJson()
        {
            DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RyuboxCoreSettings));
            using (MemoryStream stream = new MemoryStream())
            {
                serializer.WriteObject(stream, Sanitize(this));
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        internal static RyuboxCoreSettings FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return CreateDefault();
            try
            {
                DataContractJsonSerializer serializer = new DataContractJsonSerializer(typeof(RyuboxCoreSettings));
                using (MemoryStream stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                {
                    return Sanitize(serializer.ReadObject(stream) as RyuboxCoreSettings);
                }
            }
            catch
            {
                return CreateDefault();
            }
        }

        internal static RyuboxCoreSettings Load()
        {
            object raw;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue("Ryubox.CoreSettings.v1", out raw))
            {
                return FromJson(raw as string);
            }
            return CreateDefault();
        }

        internal void Save()
        {
            ApplicationData.Current.LocalSettings.Values["Ryubox.CoreSettings.v1"] = Sanitize(this).ToJson();
        }

        internal string GetStatusText()
        {
            return "D3D11 nativo • portátil • V-Sync " + (VSync ? "ativo" : "desativado")
                + " • escala gráfica: compatibilidade pendente";
        }
    }
}
