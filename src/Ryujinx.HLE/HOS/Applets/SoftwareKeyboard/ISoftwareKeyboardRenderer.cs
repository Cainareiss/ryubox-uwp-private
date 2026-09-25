using Ryujinx.HLE.UI;
using Ryujinx.Memory;
using System;

namespace Ryujinx.HLE.HOS.Applets.SoftwareKeyboard
{
    internal interface ISoftwareKeyboardRenderer : IDisposable
    {
        void UpdateTextState(string inputText, int? cursorBegin, int? cursorEnd, bool? overwriteMode, bool? typingEnabled);
        void UpdateCommandState(bool? acceptPressed, bool? cancelPressed, bool? controllerEnabled);
        void SetSurfaceInfo(RenderingSurfaceInfo surfaceInfo);
        bool DrawTo(IVirtualMemoryManager destination, ulong position);
    }
}
