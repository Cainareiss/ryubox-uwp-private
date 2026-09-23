using Ryujinx.Common.Configuration;
using System;
using System.IO;

namespace Ryujinx.Uwp
{
    /// <summary>
    /// Initializes the emulator's writable data area from the UWP host's
    /// ApplicationData.Current.LocalFolder.Path.
    ///
    /// This project intentionally does not reference Windows.* APIs. The
    /// eventual UWP/Xbox app supplies the LocalFolder path, which keeps the
    /// emulator core buildable and testable with the regular .NET SDK.
    /// </summary>
    public sealed class UwpRuntimeBootstrap
    {
        private UwpRuntimeBootstrap(string localFolderPath)
        {
            LocalFolderPath = localFolderPath;
        }

        public string LocalFolderPath { get; }

        public string BaseDirectory => AppDataManager.BaseDirPath;

        public string GamesDirectory => AppDataManager.GamesDirPath;

        public string ProfilesDirectory => AppDataManager.ProfilesDirPath;

        public string SystemDirectory => AppDataManager.KeysDirPath;

        public string LogsDirectory => AppDataManager.LogsDirPath;

        /// <summary>
        /// Initializes the shared Ryujinx data layout under a writable UWP
        /// LocalFolder path.
        /// </summary>
        public static UwpRuntimeBootstrap Initialize(string localFolderPath)
        {
            if (string.IsNullOrWhiteSpace(localFolderPath))
            {
                throw new ArgumentException("A writable UWP LocalFolder path is required.", nameof(localFolderPath));
            }

            string fullPath = Path.GetFullPath(localFolderPath);
            Directory.CreateDirectory(fullPath);

            AppDataManager.Initialize(fullPath);

            return new UwpRuntimeBootstrap(fullPath);
        }
    }
}
