using Ryujinx.Common.Configuration;
using System;
using System.IO;

namespace Ryujinx.Uwp
{
    /// <summary>
    /// Initializes Ryujinx writable data from the UWP host's
    /// ApplicationData.Current.LocalFolder.Path.
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
