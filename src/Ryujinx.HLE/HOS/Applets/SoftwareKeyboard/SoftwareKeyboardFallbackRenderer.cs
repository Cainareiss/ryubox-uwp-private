using Ryujinx.HLE.UI;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Applets.SoftwareKeyboard
{
    /// <summary>
    /// Dependency-free inline-keyboard surface used when SkiaSharp cannot be
    /// loaded in an AppContainer. Input is still handled by the applet; this
    /// renderer only supplies a stable, simple panel instead of failing draws.
    /// </summary>
    internal sealed class SoftwareKeyboardFallbackRenderer : ISoftwareKeyboardRenderer
    {
        private const uint BytesPerPixel = 4;
        private const uint PanelHeight = 240;

        private readonly object _bufferLock = new();
        private byte[] _buffer;

        public void SetSurfaceInfo(RenderingSurfaceInfo surfaceInfo)
        {
            if (surfaceInfo == null || surfaceInfo.Size > int.MaxValue || surfaceInfo.Pitch < BytesPerPixel)
            {
                return;
            }

            byte[] buffer = new byte[(int)surfaceInfo.Size];
            uint visibleWidth = Math.Min(surfaceInfo.Width, surfaceInfo.Pitch / BytesPerPixel);
            uint firstPanelRow = surfaceInfo.Height > PanelHeight ? surfaceInfo.Height - PanelHeight : 0;
            uint availableRows = surfaceInfo.Pitch == 0 ? 0 : surfaceInfo.Size / surfaceInfo.Pitch;
            uint lastRow = Math.Min(surfaceInfo.Height, availableRows);

            // A8B8G8R8 is byte-compatible with RGBA8888 in the existing Skia
            // renderer. Keep the rest transparent and draw a neutral panel.
            for (uint y = firstPanelRow; y < lastRow; y++)
            {
                int row = checked((int)(y * surfaceInfo.Pitch));
                for (uint x = 0; x < visibleWidth; x++)
                {
                    int pixel = checked(row + (int)(x * BytesPerPixel));
                    buffer[pixel] = 32;
                    buffer[pixel + 1] = 32;
                    buffer[pixel + 2] = 32;
                    buffer[pixel + 3] = 230;
                }
            }

            lock (_bufferLock)
            {
                _buffer = buffer;
            }
        }

        public bool DrawTo(IVirtualMemoryManager destination, ulong position)
        {
            lock (_bufferLock)
            {
                if (_buffer == null)
                {
                    return false;
                }

                try
                {
                    destination.Write(position, _buffer);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void UpdateTextState(string inputText, int? cursorBegin, int? cursorEnd, bool? overwriteMode, bool? typingEnabled)
        {
        }

        public void UpdateCommandState(bool? acceptPressed, bool? cancelPressed, bool? controllerEnabled)
        {
        }

        public void Dispose()
        {
            lock (_bufferLock)
            {
                _buffer = null;
            }
        }
    }
}
