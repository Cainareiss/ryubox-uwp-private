using Ryujinx.Common.Logging;
using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Ryujinx.Graphics.Nvdec.FFmpeg
{
    internal static class FFmpegDiagnostics
    {
        private static readonly object Sync = new();

        public static void Write(string message)
        {
            string path = Environment.GetEnvironmentVariable("RYUBOX_NVDEC_LOG_PATH");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                lock (Sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(
                        path,
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "[{0:O}] [T{1}] {2}{3}",
                            DateTime.UtcNow,
                            Environment.CurrentManagedThreadId,
                            message,
                            Environment.NewLine),
                        Encoding.UTF8);
                }
            }
            catch
            {
                // Video diagnostics must never alter decoder control flow.
            }
        }
    }

    unsafe class FFmpegContext : IDisposable
    {
        private static readonly FFmpegApi.av_log_set_callback_callback _logFunc;
        private AVCodec* _codec;
        private AVPacket* _packet;
        private AVCodecContext* _context;

        private static bool _nativeAvailable;

        public static bool IsNativeAvailable => _nativeAvailable;

        public bool IsAvailable => _nativeAvailable && _codec != null && _packet != null && _context != null;

        public FFmpegContext(AVCodecID codecId)
        {
            if (!IsNativeAvailable)
            {
                return;
            }

            try
            {
                _codec = FFmpegApi.avcodec_find_decoder(codecId);
                if (_codec == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, $"Codec wasn't found. Make sure you have the {codecId} codec present in your FFmpeg installation.");
                    return;
                }

                _context = FFmpegApi.avcodec_alloc_context3(_codec);
                if (_context == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec context couldn't be allocated.");
                    return;
                }

                if (FFmpegApi.avcodec_open2(_context, _codec, null) != 0)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Codec couldn't be opened.");
                    return;
                }

                _packet = FFmpegApi.av_packet_alloc();
                if (_packet == null)
                {
                    Logger.Error?.PrintMsg(LogClass.FFmpeg, "Packet couldn't be allocated.");
                    return;
                }

                int avCodecRawVersion = FFmpegApi.avcodec_version();
                int avCodecMajorVersion = avCodecRawVersion >> 16;
                int avCodecMinorVersion = (avCodecRawVersion >> 8) & 0xFF;
                FFmpegDiagnostics.Write($"decoder initialized codec={codecId}, avcodec={avCodecMajorVersion}.{avCodecMinorVersion}, public-send-receive=true");
            }
            catch (Exception exception)
            {
                FFmpegDiagnostics.Write($"decoder initialization failed: {exception}");
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"FFmpeg codec initialization failed; video decode will be skipped: {exception.GetType().Name}: {exception.Message}");
                Dispose();
            }
        }

        static FFmpegContext()
        {
            _logFunc = Log;

            try
            {
                // Redirect log output. FFmpeg is optional for titles which do not use NVDEC video.
                FFmpegApi.av_log_set_level(AVLog.MaxOffset);
                FFmpegApi.av_log_set_callback(_logFunc);
                _nativeAvailable = true;
                FFmpegDiagnostics.Write($"native libraries available; avcodecVersion=0x{FFmpegApi.avcodec_version():X8}");
            }
            catch (Exception exception)
            {
                _nativeAvailable = false;
                FFmpegDiagnostics.Write($"native libraries unavailable: {exception}");
                Logger.Warning?.PrintMsg(LogClass.FFmpeg, $"Optional FFmpeg libraries are unavailable; NVDEC video fallback enabled: {exception.GetType().Name}: {exception.Message}");
            }
        }

        private static void Log(void* ptr, AVLog level, string format, byte* vl)
        {
            if (level > FFmpegApi.av_log_get_level())
            {
                return;
            }

            int lineSize = 1024;
            byte* lineBuffer = stackalloc byte[lineSize];
            int printPrefix = 1;

            FFmpegApi.av_log_format_line(ptr, level, format, vl, lineBuffer, lineSize, &printPrefix);

            string line = Marshal.PtrToStringAnsi((nint)lineBuffer).Trim();

            switch (level)
            {
                case AVLog.Panic:
                case AVLog.Fatal:
                case AVLog.Error:
                    Logger.Error?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Warning:
                    Logger.Warning?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Info:
                    Logger.Info?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Verbose:
                case AVLog.Debug:
                    Logger.Debug?.Print(LogClass.FFmpeg, line);
                    break;
                case AVLog.Trace:
                    Logger.Trace?.Print(LogClass.FFmpeg, line);
                    break;
            }
        }

        public int DecodeFrame(Surface output, ReadOnlySpan<byte> bitstream)
        {
            if (!IsAvailable)
            {
                return -1;
            }

            FFmpegApi.av_frame_unref(output.Frame);

            int sendResult;

            fixed (byte* ptr = bitstream)
            {
                _packet->Data = ptr;
                _packet->Size = bitstream.Length;
                sendResult = FFmpegApi.avcodec_send_packet(_context, _packet);
            }

            FFmpegApi.av_packet_unref(_packet);
            if (sendResult < 0)
            {
                FFmpegDiagnostics.Write($"avcodec_send_packet failed result={sendResult}, bytes={bitstream.Length}");
                return sendResult;
            }

            int receiveResult = FFmpegApi.avcodec_receive_frame(_context, output.Frame);
            if (receiveResult < 0)
            {
                // EAGAIN is expected while the decoder accumulates enough input for
                // the first delayed frame. Keep the frame unreferenced for callers.
                FFmpegApi.av_frame_unref(output.Frame);
                return receiveResult;
            }

            return 0;
        }

        public void Dispose()
        {
            if (!_nativeAvailable)
            {
                _packet = null;
                _context = null;
                _codec = null;
                return;
            }

            if (_packet != null)
            {
                fixed (AVPacket** ppPacket = &_packet)
                {
                    FFmpegApi.av_packet_free(ppPacket);
                }
            }

            if (_context != null)
            {
                _ = FFmpegApi.avcodec_close(_context);

                fixed (AVCodecContext** ppContext = &_context)
                {
                    FFmpegApi.avcodec_free_context(ppContext);
                }
            }

            _packet = null;
            _context = null;
            _codec = null;
        }
    }
}
