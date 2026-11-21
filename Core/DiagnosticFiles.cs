using System;
using System.IO;

namespace PluginForRevit2022.Core
{
    internal static class DiagnosticFiles
    {
        public static string Target(string path, string extension, bool overwrite)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Укажите savePath.");
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Для результата нужен файл " + extension + ".");
            if (!Directory.Exists(Path.GetDirectoryName(path))) throw new DirectoryNotFoundException("Папка результата не найдена.");
            if (!overwrite && File.Exists(path)) throw new IOException("Файл уже существует: " + path + ". Выберите другое имя или overwrite=true.");
            return path;
        }

        public static void Write(string path, byte[] bytes, bool overwrite)
        {
            using (var file = new FileStream(path, overwrite ? FileMode.OpenOrCreate : FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.SetLength(0);
                file.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
