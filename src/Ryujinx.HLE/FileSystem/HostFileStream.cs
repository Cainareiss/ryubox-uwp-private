using Microsoft.Win32.SafeHandles;
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace Ryujinx.HLE.FileSystem
{
    public static class HostFileStream
    {
        private const uint GenericRead = 0x80000000;
        private const uint ShareRead = 0x00000001;
        private const uint ShareWrite = 0x00000002;
        private const uint ShareDelete = 0x00000004;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint DuplicateSameAccess = 0x00000002;
        private static readonly IntPtr InvalidHandleValue = new(-1);
        private static readonly IntPtr CurrentProcessPseudoHandle = new(-1);
        private static readonly object BrokeredHandleLock = new();
        private static string _brokeredPath;
        private static SafeFileHandle _brokeredReadHandle;

        public static FileStream OpenRead(string path)
        {
            if (TryDuplicateBrokeredReadHandle(path, out SafeFileHandle brokeredHandle))
            {
                return CreateReadStream(brokeredHandle);
            }

            if (!AppContext.TryGetSwitch("Ryujinx.Uwp.UseCreateFileFromApp", out bool enabled) || !enabled)
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }

            IntPtr handle = CreateFileFromAppW(
                path,
                GenericRead,
                ShareRead | ShareWrite | ShareDelete,
                IntPtr.Zero,
                OpenExisting,
                // NSP/NCA readers mix seeks with sequential asset reads. Let
                // Windows detect that pattern: RANDOM_ACCESS disables read-ahead
                // even during long sequential loads on external disks.
                FileAttributeNormal,
                IntPtr.Zero);
            if (handle == IntPtr.Zero || handle == InvalidHandleValue)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "CreateFileFromAppW nÃ£o conseguiu abrir '" + path + "'.");
            }

            return CreateReadStream(new SafeFileHandle(handle, ownsHandle: true));
        }

        public static FileStream OpenRead(string path, IntPtr brokeredReadHandle)
        {
            if (IsValidHandle(brokeredReadHandle))
            {
                return CreateReadStream(DuplicateReadHandle(brokeredReadHandle));
            }

            return OpenRead(path);
        }

        public static void RegisterBrokeredReadHandle(string path, IntPtr brokeredReadHandle)
        {
            SafeFileHandle duplicate = IsValidHandle(brokeredReadHandle)
                ? DuplicateReadHandle(brokeredReadHandle)
                : null;

            lock (BrokeredHandleLock)
            {
                _brokeredReadHandle?.Dispose();
                _brokeredReadHandle = duplicate;
                _brokeredPath = duplicate == null ? null : NormalizePath(path);
            }
        }

        public static void ClearBrokeredReadHandle()
        {
            lock (BrokeredHandleLock)
            {
                _brokeredReadHandle?.Dispose();
                _brokeredReadHandle = null;
                _brokeredPath = null;
            }
        }

        private static bool TryDuplicateBrokeredReadHandle(string path, out SafeFileHandle duplicate)
        {
            duplicate = null;
            string normalizedPath = NormalizePath(path);
            lock (BrokeredHandleLock)
            {
                if (_brokeredReadHandle == null || _brokeredReadHandle.IsInvalid ||
                    !string.Equals(_brokeredPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                duplicate = DuplicateReadHandle(_brokeredReadHandle.DangerousGetHandle());
                return true;
            }
        }

        private static FileStream CreateReadStream(SafeFileHandle handle)
        {
            return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false);
        }

        private static SafeFileHandle DuplicateReadHandle(IntPtr sourceHandle)
        {
            if (!DuplicateHandle(
                CurrentProcessPseudoHandle,
                sourceHandle,
                CurrentProcessPseudoHandle,
                out IntPtr duplicate,
                0,
                false,
                DuplicateSameAccess))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "NÃ£o foi possÃ­vel duplicar o handle concedido pelo seletor UWP.");
            }

            return new SafeFileHandle(duplicate, ownsHandle: true);
        }

        private static bool IsValidHandle(IntPtr handle)
        {
            return handle != IntPtr.Zero && handle != InvalidHandleValue;
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path;
            }
        }

        [DllImport("api-ms-win-core-file-fromapp-l1-1-0.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileFromAppW(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("api-ms-win-core-handle-l1-1-0.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DuplicateHandle(
            IntPtr sourceProcessHandle,
            IntPtr sourceHandle,
            IntPtr targetProcessHandle,
            out IntPtr targetHandle,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);
    }
}
