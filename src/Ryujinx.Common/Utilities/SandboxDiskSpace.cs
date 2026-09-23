using Ryujinx.Common.Configuration;
using Ryujinx.Common.Logging;
using System;
using System.Runtime.InteropServices;

namespace Ryujinx.Common.Utilities
{
    public static class SandboxDiskSpace
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out ulong available, out ulong total, out ulong free);

        public static bool TryGet(out long available, out long total)
        {
            available = total = 0;
            string directory = AppDataManager.BaseDirPath;
            if (!OperatingSystem.IsWindows() || string.IsNullOrEmpty(directory)) return false;
            bool success = GetDiskFreeSpaceExW(directory, out ulong bytes, out ulong capacity, out _);
            if (success) { available = checked((long)bytes); total = checked((long)capacity); }
            RuntimeDiagnosticLog.Write("filesystem-uwp", $"scope=app-data success={success} available={available} total={total} win32={(success ? 0 : Marshal.GetLastWin32Error())}");
            return success;
        }
    }
}
