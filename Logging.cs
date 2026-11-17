using System;
using System.IO;
using System.Text;

namespace GladosPdmAddin
{
    /// <summary>
    /// Append-only log at %LOCALAPPDATA%\Glados\pdm-addin.log, one line per Glados call. Per
    /// Windows user on purpose: a file under %ProgramData% belongs to the first user who creates
    /// it, so every other user of the machine could read that user's vault paths but not write
    /// their own lines. Every
    /// public member swallows its own exceptions: a logging failure must never surface in the
    /// PDM client. The personal access token never reaches this class, so there is nothing here
    /// that could leak it into the log file.
    /// </summary>
    internal static class Logging
    {
        private const long MaxSizeBytes = 5 * 1024 * 1024;

        private static readonly object SyncRoot = new object();

        private static string LogDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glados"); }
        }

        private static string LogPath
        {
            get { return Path.Combine(LogDirectory, "pdm-addin.log"); }
        }

        /// <summary>Appends one line: UTC timestamp, hook name, file id, path and a status or
        /// exception summary, tab-separated. Creates the log folder on first use and rotates
        /// the file to pdm-addin.log.1 once it passes 5 MB. Never throws.</summary>
        public static void Log(string hook, int fileId, string path, string detail)
        {
            try
            {
                lock (SyncRoot)
                {
                    Directory.CreateDirectory(LogDirectory);
                    RotateIfNeeded();

                    var line = new StringBuilder()
                        .Append(DateTime.UtcNow.ToString("O"))
                        .Append('\t').Append(hook)
                        .Append('\t').Append(fileId)
                        .Append('\t').Append(path ?? string.Empty)
                        .Append('\t').Append(detail ?? string.Empty)
                        .ToString();

                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception)
            {
                // Logging must never throw into a PDM hook.
            }
        }

        private static void RotateIfNeeded()
        {
            var info = new FileInfo(LogPath);
            if (!info.Exists || info.Length < MaxSizeBytes)
                return;

            var rotatedPath = LogPath + ".1";
            if (File.Exists(rotatedPath))
                File.Delete(rotatedPath);

            File.Move(LogPath, rotatedPath);
        }
    }
}
