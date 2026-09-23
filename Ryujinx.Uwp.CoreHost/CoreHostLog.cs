using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Ryujinx.Common.Logging;

namespace Ryujinx.Uwp.CoreHost
{
    internal static class CoreHostLog
    {
        private static readonly object Sync = new object();
        private static string _logDirectory;
        private static long _sessionId;
        private static string _titleId = string.Empty;

        public static void SetSessionContext(long sessionId, string titleId)
        {
            Interlocked.Exchange(ref _sessionId, sessionId);
            Volatile.Write(ref _titleId, string.IsNullOrWhiteSpace(titleId) ? "unknown" : titleId);
        }

        public static void ClearSessionContext(long sessionId)
        {
            BufferedDiagnosticWriter.Flush();
            if (Interlocked.CompareExchange(ref _sessionId, 0, sessionId) == sessionId)
            {
                Volatile.Write(ref _titleId, string.Empty);
            }
        }

        public static void Initialize(string baseDirectory)
        {
            try
            {
                _logDirectory = Path.Combine(baseDirectory, "Logs", "CoreHost");
                Directory.CreateDirectory(_logDirectory);
                Write("bootstrap", "CoreHost logging initialized at " + _logDirectory);
            }
            catch
            {
                _logDirectory = null;
            }
        }

        public static void Write(string category, string message)
        {
            string directory = _logDirectory;
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string safeCategory = string.IsNullOrWhiteSpace(category) ? "session" : category;
                foreach (char invalid in Path.GetInvalidFileNameChars())
                {
                    safeCategory = safeCategory.Replace(invalid, '_');
                }

                long sessionId = Interlocked.Read(ref _sessionId);
                string titleId = Volatile.Read(ref _titleId);
                string context = sessionId > 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "SessionId={0} TitleId={1}; ",
                        sessionId,
                        string.IsNullOrWhiteSpace(titleId) ? "unknown" : titleId)
                    : string.Empty;
                string line = string.Format(
                    CultureInfo.InvariantCulture,
                    "[{0:O}] [T{1}] {2}{3}{4}",
                    DateTime.UtcNow,
                    Environment.CurrentManagedThreadId,
                    context,
                    message ?? string.Empty,
                    Environment.NewLine);

                bool critical = string.Equals(safeCategory, "errors", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(safeCategory, "fatal", StringComparison.OrdinalIgnoreCase);
                BufferedDiagnosticWriter.Write(Path.Combine(directory, safeCategory + ".log"), line, critical: critical);
                if (critical) BufferedDiagnosticWriter.Write(Path.Combine(directory, "session.log"), line, critical: true);
            }
            catch
            {
                // Logging must never change the emulation control flow.
            }
        }

        public static void Error(string stage, Exception exception)
        {
            Write("errors", "Stage: " + stage + Environment.NewLine + exception);
        }

        public static void ShaderFailure(string hash, string stage, string source, string metadata)
        {
            try
            {
                if (string.IsNullOrEmpty(_logDirectory) || hash.Length != 64 ||
                    !System.Linq.Enumerable.All(hash, Uri.IsHexDigit)) return;
                string directory = Path.Combine(_logDirectory, "shader-failures");
                lock (Sync)
                {
                    Directory.CreateDirectory(directory);
                    string stem = Path.Combine(directory, hash + "-" + stage.ToLowerInvariant());
                    // One immutable source per hash, rather than repeatedly appending megabytes.
                    if (!File.Exists(stem + ".hlsl")) File.WriteAllText(stem + ".hlsl", source, Encoding.UTF8);
                    File.WriteAllText(stem + "-metadata.txt", metadata, Encoding.UTF8);
                }
            }
            catch { /* Diagnostics must not replace the original compiler error. */ }
        }
    }
}
