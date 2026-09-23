using Ryujinx.Graphics.GAL;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.D3D11;

public static class D3D11DebugTelemetry
{
    // Renderer telemetry is intentionally opt-in. Building hash keys, copying
    // constant buffers and draining text on every draw is useful for a focused
    // GPU investigation, but it is far too expensive for normal Xbox play.
    // One short Fire Red session produced more than 150 MB of trace output and
    // reduced the guest from its 90 FIFO updates / 30 presents target.
    public static bool Enabled { get; set; }

    private static readonly object Lock = new();
    private static readonly StringBuilder Pending = new();
    private static readonly HashSet<string> ObservedTextureConfigurations = new();
    private static readonly HashSet<string> ObservedTextureUploads = new();
    private static readonly HashSet<string> ObservedTextureBinds = new();
    private static readonly HashSet<string> ObservedResourceBinds = new();
    private static readonly HashSet<string> ObservedTextureSwizzles = new();
    private static readonly HashSet<string> ObservedHazards = new();

    /// <summary>
    /// Running count of hazard feedback copies executed since the last <see cref="Reset"/>.
    /// Updated by <see cref="D3D11Texture.TryGetFeedbackCopyView"/> every time a subresource
    /// copy is actually performed (generation-based, so cached hits are not counted).
    /// </summary>
    public static long HazardFeedbackCopyCount => Interlocked.Read(ref _hazardFeedbackCopyCount);
    private static long _hazardFeedbackCopyCount;

    public static long HazardBytesCopiedTotal => Interlocked.Read(ref _hazardBytesCopiedTotal);
    private static long _hazardBytesCopiedTotal;

    public static long HazardCopiesFull => Interlocked.Read(ref _hazardCopiesFull);
    private static long _hazardCopiesFull;

    public static long HazardCopiesPartial => Interlocked.Read(ref _hazardCopiesPartial);
    private static long _hazardCopiesPartial;
    private static long _gpuScaledCopies;
    public static long GpuScaledCopies => Interlocked.Read(ref _gpuScaledCopies);
    internal static void CountGpuScaledCopy() => Interlocked.Increment(ref _gpuScaledCopies);
    private static long _cpuScaledCopyAttempts;
    public static long CpuScaledCopyAttempts => Interlocked.Read(ref _cpuScaledCopyAttempts);
    internal static void CountCpuScaledCopyAttempt() => Interlocked.Increment(ref _cpuScaledCopyAttempts);
    private static long _hazardFeedbackReuses;
    public static long HazardFeedbackReuses => Interlocked.Read(ref _hazardFeedbackReuses);
    internal static void CountHazardFeedbackReuse() => Interlocked.Increment(ref _hazardFeedbackReuses);
    private static long _feedbackCreated, _feedbackInvalidated, _feedbackSubmissionMicroseconds;
    public static long FeedbackCreated => Interlocked.Read(ref _feedbackCreated);
    public static long FeedbackInvalidated => Interlocked.Read(ref _feedbackInvalidated);
    // CPU command submission time, NOT GPU completion time. No Flush/GetData stall.
    public static double FeedbackSubmissionMs => Interlocked.Read(ref _feedbackSubmissionMicroseconds) / 1000.0;
    internal static void CountFeedbackSubmission(double milliseconds, bool created)
    {
        if (created) Interlocked.Increment(ref _feedbackCreated);
        else Interlocked.Increment(ref _feedbackInvalidated);
        Interlocked.Add(ref _feedbackSubmissionMicroseconds, (long)(milliseconds * 1000));
    }

    /// <summary>Called by TryGetFeedbackCopyView when an actual copy is performed.</summary>
    internal static void CountHazardFeedbackCopy(long bytesCopied = 0, bool isFullCopy = true)
    {
        Interlocked.Increment(ref _hazardFeedbackCopyCount);
        if (bytesCopied > 0)
        {
            Interlocked.Add(ref _hazardBytesCopiedTotal, bytesCopied);
        }
        if (isFullCopy)
        {
            Interlocked.Increment(ref _hazardCopiesFull);
        }
        else
        {
            Interlocked.Increment(ref _hazardCopiesPartial);
        }
    }

    internal static void TraceFeedbackCopy(nint source, nint copy, int width, int height, long generation, long bytesCopied = 0, bool isFullCopy = true,
        int mip = 0, int layer = 0, string reason = "source-write")
    {
        if (!Enabled)
        {
            return;
        }

        long count = HazardFeedbackCopyCount;
        if (count <= 16 || (count & (count - 1)) == 0 || count % 4096 == 0)
        {
            Append(
                $"hazard-feedback-copy: count={count}, source=0x{source.ToInt64():X}, copy=0x{copy.ToInt64():X}, " +
                $"size={width}x{height}, bytes={bytesCopied}, full={isFullCopy}, gen={generation}, mip={mip}, layer={layer}, invalidationReason={reason}\n");
        }
    }

    private static readonly string[] OperationRingBuffer = new string[512];
    private static int _ringBufferIndex = 0;
    
    public static void LogOperation(string operation)
    {
        if (!Enabled)
        {
            return;
        }

        int index = Interlocked.Increment(ref _ringBufferIndex) % 512;
        OperationRingBuffer[index] = operation;
    }

    public static void DumpDeviceHungLog()
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("D3D11 DEVICE HUNG - RECENT OPERATIONS:");
            int currentIndex = _ringBufferIndex;
            for (int i = 0; i < 512; i++)
            {
                int index = (currentIndex + i) % 512;
                if (OperationRingBuffer[index] != null)
                {
                    sb.AppendLine(OperationRingBuffer[index]);
                }
            }
            System.IO.File.WriteAllText(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "d3d11-device-hung.log"), sb.ToString());
        }
        catch { }
    }

    public static void Reset()
    {
        lock (Lock)
        {
            Pending.Clear();
            ObservedTextureConfigurations.Clear();
            ObservedTextureUploads.Clear();
            ObservedTextureBinds.Clear();
            ObservedResourceBinds.Clear();
            ObservedTextureSwizzles.Clear();
            ObservedHazards.Clear();
        }
        Interlocked.Exchange(ref _hazardFeedbackCopyCount, 0);
        Interlocked.Exchange(ref _hazardBytesCopiedTotal, 0);
        Interlocked.Exchange(ref _hazardCopiesFull, 0);
        Interlocked.Exchange(ref _hazardCopiesPartial, 0);
        Interlocked.Exchange(ref _gpuScaledCopies, 0);
        Interlocked.Exchange(ref _cpuScaledCopyAttempts, 0);
        Interlocked.Exchange(ref _hazardFeedbackReuses, 0);
        Interlocked.Exchange(ref _feedbackCreated, 0);
        Interlocked.Exchange(ref _feedbackInvalidated, 0);
        Interlocked.Exchange(ref _feedbackSubmissionMicroseconds, 0);
        Array.Clear(OperationRingBuffer, 0, OperationRingBuffer.Length);
        Interlocked.Exchange(ref _ringBufferIndex, 0);
    }

    internal static void TraceTextureConfiguration(string source, TextureCreateInfo info)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{source}|{info.Format}|{info.Target}|{info.Width}x{info.Height}x{info.Depth}|{info.Levels}|{info.Samples}|{info.SwizzleR},{info.SwizzleG},{info.SwizzleB},{info.SwizzleA}";

        lock (Lock)
        {
            if (ObservedTextureConfigurations.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"texture-config: source={source}, GAL={info.Format}, DXGI={D3D11FormatConversion.ToDxgi(info.Format)}, " +
                    $"size={info.Width}x{info.Height}x{info.Depth}, levels={info.Levels}, samples={info.Samples}, " +
                    $"target={info.Target}, swizzle={info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}, " +
                    $"bytesPerPixel={info.BytesPerPixel}, compressed={info.IsCompressed}\n");
            }
        }
    }

    internal static void TraceTextureBind(string stage, int binding, TextureCreateInfo info, nint resource, nint view, nint sampler, bool renderTargetConflict)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{stage}|{binding}|{info.Format}|{info.Target}|{info.Width}x{info.Height}x{info.Depth}|{view}|{sampler}|{renderTargetConflict}";

        lock (Lock)
        {
            if (ObservedTextureBinds.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"texture-bind: stage={stage}, slot={binding}, resource=0x{resource.ToInt64():X}, view=0x{view.ToInt64():X}, " +
                    $"sampler=0x{sampler.ToInt64():X}, size={info.Width}x{info.Height}x{info.Depth}, levels={info.Levels}, " +
                    $"format={info.Format}, target={info.Target}, swizzle={info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}, " +
                    $"renderTargetConflict={renderTargetConflict}\n");
            }
        }
    }

    internal static void TraceTextureArrayBind(string stage, int binding, int index, D3D11Texture texture, nint view, nint sampler, bool renderTargetConflict)
    {
        if (!Enabled)
        {
            return;
        }

        string description = texture is null
            ? "texture=null"
            : $"resource=0x{texture.Resource.ToInt64():X}, size={texture.Info.Width}x{texture.Info.Height}x{texture.Info.Depth}, " +
              $"levels={texture.Info.Levels}, format={texture.Info.Format}, target={texture.Info.Target}, " +
              $"swizzle={texture.Info.SwizzleR}/{texture.Info.SwizzleG}/{texture.Info.SwizzleB}/{texture.Info.SwizzleA}";
        string key = $"{stage}|{binding}|{index}|{description}|{view}|{sampler}|{renderTargetConflict}";

        lock (Lock)
        {
            if (ObservedTextureBinds.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"texture-array-bind: stage={stage}, slot={binding}, index={index}, view=0x{view.ToInt64():X}, " +
                    $"sampler=0x{sampler.ToInt64():X}, renderTargetConflict={renderTargetConflict}, {description}\n");
            }
        }
    }

    internal static void TraceResourceBind(string operation, string stage, int slot, nint resource, nint view, nint uav, string detail)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{operation}|{stage}|{slot}|{resource}|{view}|{uav}|{detail}";

        lock (Lock)
        {
            if (ObservedResourceBinds.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"resource-bind: operation={operation}, stage={stage}, slot={slot}, resource=0x{resource.ToInt64():X}, " +
                    $"srv=0x{view.ToInt64():X}, uav=0x{uav.ToInt64():X}, {detail}\n");
            }
        }
    }

    internal static void TraceTextureUpload(string operation, TextureCreateInfo info, int byteLength)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{operation}|{info.Format}|{info.Width}x{info.Height}x{info.Depth}|{info.Levels}|{info.Samples}|{info.BlockWidth}x{info.BlockHeight}|{info.BytesPerPixel}";

        lock (Lock)
        {
            if (ObservedTextureUploads.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"texture-upload: operation={operation}, GuestFormat={info.Format}, HostFormat={D3D11FormatConversion.ToDxgi(info.Format)}, " +
                    $"ViewFormat={D3D11FormatConversion.ToDxgi(info.Format)}, size={info.Width}x{info.Height}x{info.Depth}, " +
                    $"levels={info.Levels}, layers={info.GetLayers()}, samples={info.Samples}, bytes={byteLength}, " +
                    $"RowPitch={info.GetMipStride(0)}, Mip=operation-specific, Layer=operation-specific, " +
                    $"block={info.BlockWidth}x{info.BlockHeight}, compressed={info.IsCompressed}, " +
                    $"swizzle={info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}\n");
            }
        }
    }

    internal static void TraceTextureSwizzle(TextureCreateInfo info)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{info.Format}|{info.Width}x{info.Height}x{info.Depth}|" +
                     $"{info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}";

        lock (Lock)
        {
            if (ObservedTextureSwizzles.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"texture-swizzle-applied: format={info.Format}, size={info.Width}x{info.Height}x{info.Depth}, " +
                    $"swizzle={info.SwizzleR}/{info.SwizzleG}/{info.SwizzleB}/{info.SwizzleA}\n");
            }
        }
    }

    internal static void TraceRealHazard(D3D11Texture target, D3D11Texture input)
    {
        if (!Enabled)
        {
            return;
        }

        string key = $"{input.Resource}|{target.ViewFirstLayer}|{target.ViewLayerCount}|{target.ViewFirstLevel}|{target.ViewLevelCount}|" +
                     $"{input.ViewFirstLayer}|{input.ViewLayerCount}|{input.ViewFirstLevel}|{input.ViewLevelCount}";

        lock (Lock)
        {
            if (ObservedHazards.Add(key) && Pending.Length < 256 * 1024)
            {
                Pending.Append(
                    $"hazard-real: resource=0x{input.Resource.ToInt64():X}, " +
                    $"targetLayers={target.ViewFirstLayer}+{target.ViewLayerCount}, inputLayers={input.ViewFirstLayer}+{input.ViewLayerCount}, " +
                    $"targetMips={target.ViewFirstLevel}+{target.ViewLevelCount}, inputMips={input.ViewFirstLevel}+{input.ViewLevelCount}\n");
            }
        }
    }

    internal static void TraceDiagnosticPixelShader(string mode)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(mode))
        {
            return;
        }

        lock (Lock)
        {
            if (Pending.Length < 256 * 1024)
            {
                Pending.Append($"diagnostic-pixel-shader: mode={mode}\n");
            }
        }
    }

    internal static void Append(string messages)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(messages))
        {
            return;
        }

        lock (Lock)
        {
            if (Pending.Length < 256 * 1024)
            {
                Pending.Append(messages);
            }
        }
    }

    public static string Drain()
    {
        if (!Enabled)
        {
            return string.Empty;
        }

        lock (Lock)
        {
            if (Pending.Length == 0)
            {
                return string.Empty;
            }

            string messages = Pending.ToString();
            Pending.Clear();
            return messages;
        }
    }
}
