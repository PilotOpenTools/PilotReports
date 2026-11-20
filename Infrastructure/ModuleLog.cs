using System;
using System.IO;
using System.Text;

namespace Reports.Infrastructure
{
    /// <summary>
    /// Простой файловый лог: %AppData%\ASCON\Units Logs\Logs\Reports.log (там же, где логи NugetPackage).
    /// Без NLog сознательно: модуль живёт в одном AppDomain с другими модулями, и чужая версия NLog
    /// не должна его ломать.
    /// </summary>
    internal static class ModuleLog
    {
        private const long MaxSize = 5 * 1024 * 1024;
        private static readonly object Sync = new object();

        public static string Directory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ASCON", "Units Logs", "Logs");

        private static readonly string LogPath = Path.Combine(Directory, "Reports.log");

        public static void Info(string message) => Write("INFO", message, null);

        public static void Warn(string message, Exception ex = null) => Write("WARN", message, ex);

        public static void Error(string message, Exception ex = null) => Write("ERROR", message, ex);

        private static void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (Sync)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var file = new FileInfo(LogPath);
                    if (file.Exists && file.Length > MaxSize)
                        file.Delete();

                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (ex != null)
                        line += Environment.NewLine + ex;
                    File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Лог не должен ронять отчёт.
            }
        }
    }
}
