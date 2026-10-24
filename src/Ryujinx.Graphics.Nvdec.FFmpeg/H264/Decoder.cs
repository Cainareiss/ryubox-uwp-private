using Ryujinx.Graphics.Nvdec.FFmpeg.Native;
using Ryujinx.Graphics.Video;
using Ryujinx.Common.Logging;
using System;

namespace Ryujinx.Graphics.Nvdec.FFmpeg.H264
{
    public sealed class Decoder : IH264Decoder
    {
        public bool IsHardwareAccelerated => false;

        private const int WorkBufferSize = 0x200;

        private readonly byte[] _workBuffer = new byte[WorkBufferSize];

        private FFmpegContext _context;
        private bool _missingNativeLogged;
        private long _decodeAttempts;
        private long _decodedFrames;

        private int _oldOutputWidth;
        private int _oldOutputHeight;

        public ISurface CreateSurface(int width, int height)
        {
            return new Surface(width, height);
        }

        public bool Decode(ref H264PictureInfo pictureInfo, ISurface output, ReadOnlySpan<byte> bitstream)
        {
            Surface outSurf = (Surface)output;

            if (!FFmpegContext.IsNativeAvailable)
            {
                if (!_missingNativeLogged)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "H.264 decode skipped because the optional FFmpeg native libraries are unavailable. Continuing without the video frame.");
                    _missingNativeLogged = true;
                }

                return false;
            }

            if (_context == null || outSurf.RequestedWidth != _oldOutputWidth || outSurf.RequestedHeight != _oldOutputHeight)
            {
                _context?.Dispose();
                _context = new FFmpegContext(AVCodecID.AV_CODEC_ID_H264);

                _oldOutputWidth = outSurf.RequestedWidth;
                _oldOutputHeight = outSurf.RequestedHeight;
            }

            if (!_context.IsAvailable)
            {
                if (!_missingNativeLogged)
                {
                    Logger.Warning?.PrintMsg(LogClass.FFmpeg, "H.264 decoder initialization failed. Continuing without the video frame.");
                    _missingNativeLogged = true;
                }

                return false;
            }

            Span<byte> bs = Prepend(bitstream, SpsAndPpsReconstruction.Reconstruct(ref pictureInfo, _workBuffer));

            long attempt = ++_decodeAttempts;
            int result = _context.DecodeFrame(outSurf, bs);
            bool decoded = result == 0;
            if (decoded)
            {
                _decodedFrames++;
            }

            if (attempt <= 8 || attempt % 120 == 0)
            {
                FFmpegDiagnostics.Write(
                    $"h264 attempt={attempt}, decoded={decoded}, result={result}, frames={_decodedFrames}, " +
                    $"bitstream={bitstream.Length}, output={outSurf.Width}x{outSurf.Height}, " +
                    $"stride={outSurf.Stride}, uvStride={outSurf.UvStride}");
            }

            return decoded;
        }

        private static byte[] Prepend(ReadOnlySpan<byte> data, ReadOnlySpan<byte> prep)
        {
            byte[] output = new byte[data.Length + prep.Length];

            prep.CopyTo(output);
            data.CopyTo(new Span<byte>(output)[prep.Length..]);

            return output;
        }

        public void Dispose() => _context?.Dispose();
    }
}
