using System;
using System.Runtime.InteropServices;
using Windows.Storage;

namespace Ryujinx.Uwp.App
{
    // FutureAccessList preserves the StorageFile permission, but passing only
    // StorageFile.Path to NativeAOT loses the broker. IStorageItemHandleAccess
    // turns the already-authorized StorageFile into a real read handle that the
    // CoreHost can duplicate and retain for the duration of LoadNsp.
    internal static class StorageFileHandleAccess
    {
        private const uint HandleAccessRead = 0x00120089;
        private const uint ShareReadWriteDelete = 0x00000007;
        private const uint DefaultCaching = 0;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [ComImport]
        [Guid("5CA296B2-2C25-4D22-B785-B885C8201E6A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IStorageItemHandleAccess
        {
            [PreserveSig]
            int Create(
                uint accessOptions,
                uint sharingOptions,
                uint options,
                IntPtr oplockBreakingHandler,
                out IntPtr interopHandle);
        }

        internal static bool TryOpenRead(StorageFile file, out IntPtr handle, out string error)
        {
            handle = IntPtr.Zero;
            error = string.Empty;
            if (file == null)
            {
                error = "O seletor UWP nÃ£o retornou um arquivo de jogo.";
                return false;
            }

            try
            {
                IStorageItemHandleAccess access = (IStorageItemHandleAccess)(object)file;
                int hresult = access.Create(
                    HandleAccessRead,
                    ShareReadWriteDelete,
                    // Match HostFileStream's default caching: ROM asset loads
                    // contain long sequential reads as well as seeks. Forcing
                    // RANDOM_ACCESS suppresses read-ahead on external disks.
                    DefaultCaching,
                    IntPtr.Zero,
                    out handle);
                if (hresult < 0 || handle == IntPtr.Zero || handle == InvalidHandleValue)
                {
                    error = string.Format(
                        "O Windows nÃ£o liberou um handle do jogo (HRESULT 0x{0:X8}).",
                        hresult);
                    handle = IntPtr.Zero;
                    return false;
                }

                return true;
            }
            catch (Exception exception)
            {
                error = "Falha ao obter o handle autorizado pelo seletor UWP: " + exception.Message;
                handle = IntPtr.Zero;
                return false;
            }
        }

        internal static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != InvalidHandleValue)
            {
                CloseHandle(handle);
            }
        }

        [DllImport("api-ms-win-core-handle-l1-1-0.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
