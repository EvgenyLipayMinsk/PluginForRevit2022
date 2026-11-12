using System;
using System.IO;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Простой потокобезопасный лог MCP-моста:
    /// %AppData%\AerBIM\PluginForRevit\mcp.log
    /// Никогда не показывает диалоги — вызывается в том числе из фоновых потоков.
    /// </summary>
    internal static class McpLog
    {
        private static readonly object _sync = new object();

        public static string LogPath { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AerBIM", "PluginForRevit", "mcp.log");

        public static void Info(string message) => Write("INFO", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + ": " + ex);

        private static void Write(string level, string message)
        {
            try
            {
                lock (_sync)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                    File.AppendAllText(LogPath,
                        $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
                }
            }
            catch
            {
                // Лог не должен ронять Revit.
            }
        }
    }
}
