using System;
using System.IO;
using System.Linq;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Журнал действий плагина: %AppData%\AerBIM\PluginForRevit\logs\plugin-ГГГГ-ММ-ДД.log.
    /// Пишет запуск Revit, команды с ленты и MCP с длительностью и ошибками. Не показывает окон и не роняет Revit.
    /// </summary>
    internal static class PluginLog
    {
        private const int KeepDays = 30;
        private static readonly object Sync = new object();

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AerBIM", "PluginForRevit", "logs");

        public static string CurrentFile => Path.Combine(Folder, $"plugin-{DateTime.Now:yyyy-MM-dd}.log");

        public static void Info(string action, string details = null) => Write("INFO", action, details);

        public static void Warn(string action, string details = null) => Write("WARN", action, details);

        public static void Error(string action, Exception ex, string details = null) =>
            Write("ERROR", action, (details == null ? "" : details + " | ") + ex);

        /// <summary>Удаляет журналы старше 30 дней.</summary>
        public static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                foreach (FileInfo file in new DirectoryInfo(Folder).GetFiles("plugin-*.log").Where(f => f.LastWriteTime < DateTime.Now.AddDays(-KeepDays)))
                {
                    file.Delete();
                }
            }
            catch
            {
                // Очистка не обязательна.
            }
        }

        /// <summary>Укорачивает длинные значения (аргументы MCP, тексты ошибок) для журнала.</summary>
        public static string Short(string text, int max = 400) =>
            string.IsNullOrEmpty(text) || text.Length <= max ? text : text.Substring(0, max) + $"… (+{text.Length - max})";

        private static void Write(string level, string action, string details)
        {
            try
            {
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {action}" +
                              (string.IsNullOrEmpty(details) ? "" : " | " + details.Replace("\r", " ").Replace("\n", " ")) + Environment.NewLine;
                lock (Sync)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(CurrentFile, line);
                }
            }
            catch
            {
                // Журнал не должен мешать работе.
            }
        }
    }
}
