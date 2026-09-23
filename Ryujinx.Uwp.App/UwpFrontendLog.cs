using System;
using System.Globalization;
using System.IO;
using System.Text;
using Windows.Storage;

namespace Ryujinx.Uwp.App
{
    internal static class UwpFrontendLog
    {
        private static readonly object Sync = new object();

        public static void Write(string category, string message)
        {
            try
            {
                string directory = Path.Combine(ApplicationData.Current.LocalFolder.Path, "Logs", "Frontend");
                Directory.CreateDirectory(directory);
                string safeCategory = string.IsNullOrWhiteSpace(category) ? "session" : category;
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    safeCategory = safeCategory.Replace(invalid, '_');
                }

                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0:O}] {1}{2}",
                    DateTime.UtcNow,
                    message ?? string.Empty,
                    Environment.NewLine);

                lock (Sync)
                {
                    File.AppendAllText(Path.Combine(directory, safeCategory + ".log"), line, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never bring down the XAML frontend.
            }
        }

        public static void Error(string category, Exception exception)
        {
            Write("errors", "[" + category + "]" + Environment.NewLine + exception);
        }
    }
}
