using System;
using System.IO;

namespace ARMeilleure.Translation.PTC
{
    // Publish only a fully closed stream, including the compression footer.
    // A failed write must leave the previous cache available for the next boot.
    internal static class PtcCacheFile
    {
        public static void Write(string path, Action<Stream> write)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    write(stream);
                    stream.Flush(true);
                }

                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                // Only this invocation's temporary file is disposable.
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
