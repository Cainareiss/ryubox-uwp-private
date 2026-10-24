using Ryujinx.HLE.UI;
using Ryujinx.HLE.HOS.Services.SurfaceFlinger;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;

// CPU-only emergency renderer. No native library, desktop font or graphics
// backend is needed. Input/events remain owned by the existing applet.
internal sealed class ManagedKeyboardSurface : IKeyboardSurface
{
    private readonly object _sync = new();
    private RenderingSurfaceInfo _info;
    private byte[] _pixels;

    public void CreateSurface(RenderingSurfaceInfo info)
    {
        if (info.ColorFormat != ColorFormat.A8B8G8R8) throw new NotSupportedException("Inline keyboard requires RGBA8.");
        KeyboardBitmap.Validate(info.Width, info.Height, info.Pitch, info.Size);
        lock (_sync) _info = info;
    }

    public void DrawMutableElements(SoftwareKeyboardUIState state)
    {
        lock (_sync)
        {
            if (_info == null) return;
            _pixels = KeyboardBitmap.Render(_info.Width, _info.Height, _info.Pitch, _info.Size,
                state.InputText, state.CursorBegin, state.CursorEnd,
                state.TextBoxBlinkCounter < SoftwareKeyboardRendererBase.TextBoxBlinkThreshold,
                state.AcceptPressed, state.CancelPressed, state.TypingEnabled);
        }
    }

    public void CopyImageToBuffer() { } // Render already publishes a complete RGBA buffer under the lock.

    public bool WriteBufferToMemory(IVirtualMemoryManager destination, ulong position)
    {
        lock (_sync)
        {
            if (_pixels == null) return false;
            try { destination.Write(position, _pixels); return true; }
            catch (Exception) { return false; }
        }
    }

    public void Dispose() { lock (_sync) { _pixels = null; _info = null; } }
}
