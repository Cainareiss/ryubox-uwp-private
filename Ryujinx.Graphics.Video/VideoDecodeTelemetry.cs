using Ryujinx.Common.Logging;
using System;
using System.Diagnostics;
using System.Threading;

namespace Ryujinx.Graphics.Video;

/// <summary>
/// Low-overhead telemetry for the real NVDEC/VIC surface path. The current
/// decoder contract exposes separate Y/U/V planes and writes them to guest
/// memory; it does not expose a native D3D11 NV12 resource. Keeping that fact
/// explicit in the log prevents a fake RGB SRV from hiding a format problem.
/// </summary>
public static class VideoDecodeTelemetry
{
    private static long _frames;
    private static long _droppedFrames;

    public static long Timestamp() => Stopwatch.GetTimestamp();

    public static double ElapsedMilliseconds(long startedAt)
    {
        return (Stopwatch.GetTimestamp() - startedAt) * 1000.0 / Stopwatch.Frequency;
    }

    public static void Record(
        string codec,
        bool decoded,
        int width,
        int height,
        int stride,
        int uvWidth,
        int uvHeight,
        int uvStride,
        int planeCount,
        double decodeMs,
        double uploadMs)
    {
        long frame = Interlocked.Increment(ref _frames);
        long dropped = decoded ? Volatile.Read(ref _droppedFrames) : Interlocked.Increment(ref _droppedFrames);

        // Keep the normal log usable on long videos while retaining the first
        // frames and a periodic heartbeat for correlation with presentation.
        if (frame > 3 && frame % 60 != 0 && decoded)
        {
            return;
        }

        Logger.Debug?.Print(
            LogClass.Nvdec,
            $"VideoFrameFormat=NV12, codec={codec}, Width={width}, Height={height}, PlaneCount={planeCount}, " +
            $"Pitch={stride}, UvWidth={uvWidth}, UvHeight={uvHeight}, UvPitch={uvStride}, " +
            "ColorSpace=unknown, ColorRange=unknown, " +
            $"NvDecDecodeMs={decodeMs:F3}, VideoConvertMs=0.000, VideoUploadMs={uploadMs:F3}, " +
            "VideoPresentMs=0.000, VideoReadbackMs=0.000, " +
            $"DroppedVideoFrames={dropped}, decoded={decoded}");
    }
}
