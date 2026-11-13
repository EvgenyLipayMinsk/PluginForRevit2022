using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace PluginForRevit2022.Core.Clashes
{
    /// <summary>
    /// Статусы и комментарии коллизий. Хранятся рядом с отчётом («отчёт.html.aerbim.json»), чтобы коллеги,
    /// открывшие тот же отчёт, видели отметки; если папка только для чтения — в профиле пользователя.
    /// Сохраняется только изменённая коллизия: файл перечитывается и в него вносится одна запись,
    /// поэтому отметки коллег, сделанные после открытия отчёта, не стираются.
    /// </summary>
    internal static class ClashStatusStore
    {
        private sealed class Entry
        {
            public string Status { get; set; }
            public string Comment { get; set; }
            public string By { get; set; }
            public string At { get; set; }
        }

        public static string SidecarOf(string report) => report + ".aerbim.json";

        private static string FallbackOf(string report)
        {
            string hash;
            using (var sha = SHA1.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(report.ToLowerInvariant()))).Replace("-", "").Substring(0, 16);
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AerBIM", "clashes", hash + ".json");
        }

        /// <summary>Подставляет сохранённые статусы: из файла рядом с отчётом и из профиля (новее — главнее).</summary>
        public static void Load(ClashReport report)
        {
            foreach (string file in report.Files)
            {
                var saved = new Dictionary<string, Entry>();
                foreach (string path in new[] { SidecarOf(file), FallbackOf(file) })
                {
                    Dictionary<string, Entry> part = Read(path, out string error);
                    if (error != null) report.Warnings.Add($"Статусы «{Path.GetFileName(path)}» не прочитаны: {error}");
                    if (part == null) continue;
                    foreach (var pair in part)
                        if (!saved.TryGetValue(pair.Key, out Entry have) || string.CompareOrdinal(pair.Value.At ?? "", have.At ?? "") > 0) saved[pair.Key] = pair.Value;
                }
                foreach (ClashItem item in report.Items.Where(i => i.Source == file))
                {
                    if (!saved.TryGetValue(item.Key, out Entry e)) continue;
                    item.Status = Enum.TryParse(e.Status, out ClashStatus s) ? s : ClashStatus.New;
                    item.Comment = e.Comment ?? "";
                    item.ChangedBy = e.By ?? "";
                    item.ChangedAt = e.At ?? "";
                }
            }
        }

        /// <summary>Сохраняет отметку одной коллизии. Возвращает путь файла статусов.</summary>
        public static string Save(ClashItem item)
        {
            string sidecar = SidecarOf(item.Source);
            try
            {
                Merge(sidecar, item);
                return sidecar;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || (ex is IOException && !File.Exists(sidecar) && !CanWrite(Path.GetDirectoryName(sidecar))))
            {
                // Папка отчёта только для чтения — в профиль
                string fallback = FallbackOf(item.Source);
                Directory.CreateDirectory(Path.GetDirectoryName(fallback));
                Merge(fallback, item);
                return fallback;
            }
        }

        /// <summary>Перечитать файл (с повтором, если он на мгновение занят коллегой), заменить одну запись, записать.</summary>
        private static void Merge(string path, ClashItem item)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    {
                        Dictionary<string, Entry> data;
                        using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, leaveOpen: true))
                        {
                            string text = reader.ReadToEnd();
                            data = string.IsNullOrWhiteSpace(text) ? new Dictionary<string, Entry>()
                                : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, Entry>>(text) ?? new Dictionary<string, Entry>();
                        }
                        if (item.Status == ClashStatus.New && item.Comment.Length == 0) data.Remove(item.Key);
                        else data[item.Key] = new Entry { Status = item.Status.ToString(), Comment = item.Comment, By = item.ChangedBy, At = item.ChangedAt };
                        byte[] bytes = new UTF8Encoding(false).GetBytes(new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Serialize(data));
                        stream.SetLength(0);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    return;
                }
                catch (IOException) when (attempt < 10 && File.Exists(path))
                {
                    Thread.Sleep(150); // файл занят коллегой — подождать и повторить
                }
            }
        }

        private static bool CanWrite(string folder)
        {
            try
            {
                string probe = Path.Combine(folder, ".aerbim-write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static Dictionary<string, Entry> Read(string path, out string error)
        {
            error = null;
            if (!File.Exists(path)) return null;
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        return string.IsNullOrWhiteSpace(text) ? new Dictionary<string, Entry>()
                            : new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, Entry>>(text);
                    }
                }
                catch (IOException) when (attempt < 10)
                {
                    Thread.Sleep(150);
                }
                catch (Exception ex)
                {
                    error = ex.Message; // сообщить, а не считать пустым (иначе следующая запись затёрла бы файл)
                    return null;
                }
            }
        }
    }
}
