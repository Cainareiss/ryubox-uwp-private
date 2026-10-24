using Ryujinx.Common.Configuration;
using System;
using System.IO;

namespace Ryujinx.Common.Logging
{
    public static class RuntimeDiagnosticLog
    {
        private static readonly object Sync = new();
        public static void Write(string category, string message)
        {
            try
            {
                string directory = AppDataManager.LogsDirPath;
                if (string.IsNullOrEmpty(directory)) return;
                BufferedDiagnosticWriter.Write(Path.Combine(directory, category + ".log"),
                    $"[{DateTime.UtcNow:O}] [T{Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}",
                    rotate: true, critical: category is "fatal" or "errors");
            }
            catch (Exception) { /* Diagnostics must not alter guest execution. */ }
        }
    }
}
