using Ryujinx.HLE.UI;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Applets.SoftwareKeyboard;

internal interface IKeyboardSurface : IDisposable
{
    void CreateSurface(RenderingSurfaceInfo info);
    void DrawMutableElements(SoftwareKeyboardUIState state);
    void CopyImageToBuffer();
    bool WriteBufferToMemory(IVirtualMemoryManager destination, ulong position);
}
