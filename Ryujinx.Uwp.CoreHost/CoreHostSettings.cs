using System;
using System.Text.Json;

namespace Ryujinx.Uwp.CoreHost;

internal sealed class CoreHostSettings
{
    public int SchemaVersion = 1;
    public string Backend = "D3D11";
    public string ConsoleMode = "Handheld";
    public string Language = "AmericanEnglish";
    public string Region = "USA";
    public string TimeZone = "UTC";
    public int SystemTimeOffsetMinutes;
    public bool IgnoreMissingServices = true;
    public bool FsIntegrityChecks = true;
    public bool Ptc;
    public bool ShaderCache;
    public string MemoryManagerMode = "SoftwarePageTable";
    public bool Hypervisor;
    public bool FastMemory = true;
    public bool VSync = true;
    public string AspectRatio = "Fixed16x9";
    public float ResolutionScale = 1.0f;
    public float AudioVolume = 1.0f;
    public bool AudioEnabled = true;
    public bool GuestInternetAccess;
    public string MultiplayerMode = "Disabled";
    public bool GpuThreading;
    public bool FileLogging = true;
    public bool GuestLogs;
    public bool FsAccessLogs;
    public bool GraphicsDebugLogging;
    public bool ShaderTelemetry;
    public bool PresentationLogs;
    public bool SyncStalls;

    public static CoreHostSettings CreateDefault() => new CoreHostSettings();

    public static string NormalizeBackend(string backend)
    {
        return string.Equals(backend, "D3D12", StringComparison.OrdinalIgnoreCase)
            ? "D3D12"
            : "D3D11";
    }

    public static CoreHostSettings FromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return CreateDefault();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return CreateDefault();

            // No reflection or dynamic serializers: missing members retain
            // their defaults, and NativeAOT cannot trim the binding metadata.
            CoreHostSettings value = CreateDefault();
            value.SchemaVersion = ReadInt32(root, "schemaVersion", value.SchemaVersion);
            value.Backend = ReadString(root, "backend", value.Backend);
            value.ConsoleMode = ReadString(root, "consoleMode", value.ConsoleMode);
            value.Language = ReadString(root, "language", value.Language);
            value.Region = ReadString(root, "region", value.Region);
            value.TimeZone = ReadString(root, "timeZone", value.TimeZone);
            value.SystemTimeOffsetMinutes = ReadInt32(root, "systemTimeOffsetMinutes", value.SystemTimeOffsetMinutes);
            value.IgnoreMissingServices = ReadBoolean(root, "ignoreMissingServices", value.IgnoreMissingServices);
            value.FsIntegrityChecks = ReadBoolean(root, "fsIntegrityChecks", value.FsIntegrityChecks);
            value.Ptc = ReadBoolean(root, "ptc", value.Ptc);
            value.ShaderCache = ReadBoolean(root, "shaderCache", value.ShaderCache);
            value.MemoryManagerMode = ReadString(root, "memoryManagerMode", value.MemoryManagerMode);
            value.Hypervisor = ReadBoolean(root, "hypervisor", value.Hypervisor);
            value.FastMemory = ReadBoolean(root, "fastMemory", value.FastMemory);
            value.VSync = ReadBoolean(root, "vsync", value.VSync);
            value.AspectRatio = ReadString(root, "aspectRatio", value.AspectRatio);
            value.ResolutionScale = ReadSingle(root, "resolutionScale", value.ResolutionScale);
            value.AudioVolume = ReadSingle(root, "audioVolume", value.AudioVolume);
            value.AudioEnabled = ReadBoolean(root, "audioEnabled", value.AudioEnabled);
            value.GuestInternetAccess = ReadBoolean(root, "guestInternetAccess", value.GuestInternetAccess);
            value.MultiplayerMode = ReadString(root, "multiplayerMode", value.MultiplayerMode);
            value.GpuThreading = ReadBoolean(root, "gpuThreading", value.GpuThreading);
            value.FileLogging = ReadBoolean(root, "fileLogging", value.FileLogging);
            value.GuestLogs = ReadBoolean(root, "guestLogs", value.GuestLogs);
            value.FsAccessLogs = ReadBoolean(root, "fsAccessLogs", value.FsAccessLogs);
            value.GraphicsDebugLogging = ReadBoolean(root, "graphicsDebugLogging", value.GraphicsDebugLogging);
            value.ShaderTelemetry = ReadBoolean(root, "shaderTelemetry", value.ShaderTelemetry);
            value.PresentationLogs = ReadBoolean(root, "presentationLogs", value.PresentationLogs);
            value.SyncStalls = ReadBoolean(root, "syncStalls", value.SyncStalls);

            value.SchemaVersion = 1;
            value.Backend = NormalizeBackend(value.Backend);
            value.ConsoleMode = "Handheld";
            value.MemoryManagerMode = "SoftwarePageTable";
            value.Hypervisor = false;
            value.FastMemory = true;
            // Preserve the requested scale; forcing 1x made this setting
            // decorative. Bound it to a range that remains viable on Xbox.
            value.ResolutionScale = Math.Clamp(value.ResolutionScale, 0.5f, 2.0f);
            value.AudioVolume = Math.Clamp(value.AudioVolume, 0.0f, 1.0f);
            value.TimeZone = string.IsNullOrWhiteSpace(value.TimeZone) ? "UTC" : value.TimeZone;
            value.Language = string.IsNullOrWhiteSpace(value.Language) ? "AmericanEnglish" : value.Language;
            value.Region = string.IsNullOrWhiteSpace(value.Region) ? "USA" : value.Region;
            value.AspectRatio = string.IsNullOrWhiteSpace(value.AspectRatio) ? "Fixed16x9" : value.AspectRatio;
            value.MultiplayerMode = string.IsNullOrWhiteSpace(value.MultiplayerMode) ? "Disabled" : value.MultiplayerMode;
            return value;
        }
        catch (JsonException) { return CreateDefault(); }
        catch (InvalidOperationException) { return CreateDefault(); }
        catch (FormatException) { return CreateDefault(); }
        catch (OverflowException) { return CreateDefault(); }
    }

    private static bool ReadBoolean(JsonElement root, string name, bool fallback) =>
        root.TryGetProperty(name, out JsonElement value) ? value.GetBoolean() : fallback;
    private static string ReadString(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? fallback : fallback;
    private static int ReadInt32(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : fallback;
    private static float ReadSingle(JsonElement root, string name, float fallback)
    {
        if (!root.TryGetProperty(name, out JsonElement value)) return fallback;
        float result = value.GetSingle();
        if (!float.IsFinite(result)) throw new JsonException("Setting must be finite.");
        return result;
    }
}
