using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using PluginForRevit2022.Core;

namespace PluginForRevit2022
{
    /// <summary>Собирает журналы плагина и сведения о компьютере в ZIP для передачи разработчику.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdSaveLog : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIApplication uiApp = commandData.Application;
            PluginLog.Info("command.start", "Сохранить лог");
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить лог AerBIM Manager",
                Filter = "Архив ZIP (*.zip)|*.zip",
                FileName = $"AerBIM_log_{Environment.MachineName}_{DateTime.Now:yyyy-MM-dd_HH-mm}.zip",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            };
            if (dialog.ShowDialog() != true) return Result.Cancelled;

            try
            {
                List<string> added = Build(uiApp, dialog.FileName);
                PluginLog.Info("log.saved", $"{dialog.FileName}, файлов: {added.Count}");
                TaskDialog.Show("Сохранить лог", $"Лог сохранён:\n{dialog.FileName}\n\nФайлов в архиве: {added.Count}.\nОтправьте этот файл разработчику.");
                Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                PluginLog.Error("log.failed", ex);
                TaskDialog.Show("Сохранить лог", "Не удалось сохранить лог: " + ex.Message);
                return Result.Failed;
            }
        }

        private static List<string> Build(UIApplication uiApp, string zipPath)
        {
            var added = new List<string>();
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                void AddText(string name, string text)
                {
                    ZipArchiveEntry entry = zip.CreateEntry(name);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true))) writer.Write(text);
                    added.Add(name);
                }
                void AddFile(string path, string name)
                {
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    try
                    {
                        // Файл может быть открыт на запись (журнал Revit, текущий лог): читаем с общим доступом.
                        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        using (Stream target = zip.CreateEntry(name).Open())
                        {
                            source.CopyTo(target);
                        }
                        added.Add(name);
                    }
                    catch (Exception ex)
                    {
                        AddText(name + ".error.txt", ex.Message);
                    }
                }

                AddText("system.txt", SystemInfo(uiApp));
                if (Directory.Exists(PluginLog.Folder))
                {
                    foreach (FileInfo log in new DirectoryInfo(PluginLog.Folder).GetFiles("plugin-*.log").Where(f => f.LastWriteTime > DateTime.Now.AddDays(-14)))
                    {
                        AddFile(log.FullName, "logs/" + log.Name);
                    }
                }
                AddFile(Mcp.McpLog.LogPath, "logs/mcp.log");
                AddFile(ARBM.CableJournalExporter.Services.ExecutionLogger.GetLogPath(), "logs/cable-journal.log");

                string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AerBIM", "PluginForRevit");
                AddFile(Path.Combine(settings, "profiles.json"), "settings/profiles.json");
                try
                {
                    ProfileEntry current = MarkingSettings.Current();
                    AddFile(current.FilePath, "settings/" + Path.GetFileName(current.FilePath));
                }
                catch (Exception ex)
                {
                    AddText("settings/profile.error.txt", ex.Message);
                }

                AddFile(uiApp.Application.RecordingJournalFilename, "revit/" + Path.GetFileName(uiApp.Application.RecordingJournalFilename ?? "journal.txt"));
            }
            return added;
        }

        private static string SystemInfo(UIApplication uiApp)
        {
            var text = new StringBuilder();
            Autodesk.Revit.ApplicationServices.Application app = uiApp.Application;
            Assembly plugin = Assembly.GetExecutingAssembly();
            text.AppendLine($"Дата: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            text.AppendLine($"Компьютер: {Environment.MachineName}");
            text.AppendLine($"Windows: {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "x64" : "x86")})");
            text.AppendLine($".NET: {Environment.Version}");
            text.AppendLine($"Revit: {app.VersionName} {app.VersionBuild}, язык {app.Language}");
            text.AppendLine($"Плагин: {plugin.GetName().Version}, файл {FileVersionInfo.GetVersionInfo(plugin.Location).FileVersion}");
            text.AppendLine($"Папка плагина: {Path.GetDirectoryName(plugin.Location)}");
            text.AppendLine($"MCP: {Mcp.McpHost.GetStatusText()}");
            text.AppendLine($"Активный документ: {uiApp.ActiveUIDocument?.Document?.Title ?? "нет"}");
            text.AppendLine($"Открыто документов: {app.Documents.Size}");

            text.AppendLine();
            text.AppendLine("Библиотеки рядом с плагином:");
            foreach (string dll in Directory.GetFiles(Path.GetDirectoryName(plugin.Location), "*.dll").OrderBy(f => f))
            {
                text.AppendLine($"  {Path.GetFileName(dll)} {FileVersionInfo.GetVersionInfo(dll).FileVersion}");
            }

            text.AppendLine();
            text.AppendLine("Загруженные сборки с тем же именем, что у библиотек плагина (возможные конфликты):");
            var ours = new HashSet<string>(Directory.GetFiles(Path.GetDirectoryName(plugin.Location), "*.dll").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies().Where(a => ours.Contains(a.GetName().Name)))
            {
                string location;
                try { location = loaded.Location; } catch { location = "?"; }
                text.AppendLine($"  {loaded.GetName().Name} {loaded.GetName().Version} — {location}");
            }

            text.AppendLine();
            text.AppendLine("Манифесты надстроек:");
            string year = app.VersionNumber;
            foreach (string folder in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", year),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", year),
            })
            {
                if (!Directory.Exists(folder)) continue;
                foreach (string manifest in Directory.GetFiles(folder, "*.addin")) text.AppendLine($"  {manifest}");
            }
            return text.ToString();
        }
    }
}
