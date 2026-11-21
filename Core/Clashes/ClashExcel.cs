using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace PluginForRevit2022.Core.Clashes
{
    /// <summary>
    /// Выгрузка коллизий в Excel: лист «Коллизии» — все, кроме исправленных (статус, ID, данные отчёта),
    /// лист «Исправлено» — исправленные, лист «Сводка» — счёт по проверкам и статусам.
    /// </summary>
    internal static class ClashExcel
    {
        private const int ScreenshotColumn = 4;
        private static readonly string[] Headers =
        {
            "№", "Статус", "Комментарий", "Скриншот", "Проверка / папка", "Наименование",
            "Эл. 1 ID", "Эл. 1 новый ID", "Эл. 1 ID объекта Navisworks", "Эл. 1 Категория", "Эл. 1 Файл",
            "Эл. 2 ID", "Эл. 2 новый ID", "Эл. 2 ID объекта Navisworks", "Эл. 2 Категория", "Эл. 2 Файл",
            "Изменил", "Когда", "Отчёт",
        };

        /// <summary>Выгружает переданные коллизии (весь отчёт или только коллизии активной модели).</summary>
        public static void Write(List<ClashItem> items, string path)
        {
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            using (var package = new ExcelPackage())
            {
                Sheet(package, "Коллизии", items.Where(i => i.Status != ClashStatus.Fixed).ToList());
                Sheet(package, "Исправлено", items.Where(i => i.Status == ClashStatus.Fixed).ToList());
                Summary(package, items);
                // Ошибка картинки или блокировка файла не должна уничтожать предыдущий отчёт.
                byte[] bytes = package.GetAsByteArray();
                using (var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    stream.SetLength(0);
                    stream.Write(bytes, 0, bytes.Length);
                }
            }
        }

        private static void Sheet(ExcelPackage package, string name, List<ClashItem> items)
        {
            ExcelWorksheet ws = package.Workbook.Worksheets.Add(name);
            for (int c = 0; c < Headers.Length; c++) ws.Cells[1, c + 1].Value = Headers[c];
            int row = 2;
            foreach (ClashItem i in items.OrderBy(i => i.Test).ThenBy(i => i.Group).ThenBy(i => Natural(i.Name)))
            {
                object[] values =
                {
                    row - 1, ClashStatusText.Of(i.Status), i.Comment, null, i.Test, i.Name,
                    (object)i.Item1.Id ?? "", NewIds(i.Item1), (object)i.Item1.ItemId ?? "", i.Item1.Category, i.Item1.File,
                    (object)i.Item2.Id ?? "", NewIds(i.Item2), (object)i.Item2.ItemId ?? "", i.Item2.Category, i.Item2.File,
                    i.ChangedBy, i.ChangedAt, Path.GetFileName(i.Source),
                };
                for (int c = 0; c < values.Length; c++) ws.Cells[row, c + 1].Value = values[c];
                Color? fill = FillOf(i.Status);
                if (fill != null)
                {
                    ws.Cells[row, 2].Style.Fill.PatternType = ExcelFillStyle.Solid;
                    ws.Cells[row, 2].Style.Fill.BackgroundColor.SetColor(fill.Value);
                }
                row++;
            }
            Format(ws, Headers.Length, row - 1);
            ws.Column(ScreenshotColumn).Width = 48;
            if (items.Count > 0) ws.Cells[2, 1, row - 1, Headers.Length].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
            int imageRow = 2;
            foreach (ClashItem item in items.OrderBy(i => i.Test).ThenBy(i => i.Group).ThenBy(i => Natural(i.Name)))
                AddImage(ws, imageRow++, item);
        }

        private static void AddImage(ExcelWorksheet ws, int row, ClashItem item)
        {
            string path = item.EffectiveImagePath;
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path))
            {
                ws.Cells[row, ScreenshotColumn].Value = "Изображение не найдено";
                return;
            }
            try
            {
                int width, height;
                using (var source = Image.FromFile(path))
                {
                    double scale = Math.Min(320.0 / source.Width, 180.0 / source.Height);
                    width = Math.Max(1, (int)Math.Round(source.Width * scale));
                    height = Math.Max(1, (int)Math.Round(source.Height * scale));
                }
                var picture = ws.Drawings.AddPicture("Clash_" + row, new FileInfo(path));
                picture.SetSize(width, height);
                picture.SetPosition(row - 1, 6, ScreenshotColumn - 1, 6);
                picture.EditAs = OfficeOpenXml.Drawing.eEditAs.OneCell;
                ws.Row(row).Height = Math.Max(45, (height + 12) * 72.0 / 96);
                ws.Cells[row, 3].Style.WrapText = true;
                ws.Cells[row, 6].Style.WrapText = true;
            }
            catch (Exception ex)
            {
                if (ws.Drawings["Clash_" + row] != null) ws.Drawings.Remove("Clash_" + row);
                ws.Cells[row, ScreenshotColumn].Value = "Не удалось загрузить изображение. Вставьте новый скриншот.";
                System.Diagnostics.Debug.WriteLine(ex.ToString());
                ws.Cells[row, ScreenshotColumn].Style.WrapText = true;
            }
        }

        private static void Summary(ExcelPackage package, List<ClashItem> items)
        {
            ExcelWorksheet ws = package.Workbook.Worksheets.Add("Сводка");
            var statuses = ClashStatusText.All;
            ws.Cells[1, 1].Value = "Проверка / папка";
            for (int s = 0; s < statuses.Length; s++) ws.Cells[1, s + 2].Value = ClashStatusText.Of(statuses[s]);
            ws.Cells[1, statuses.Length + 2].Value = "Всего";
            int row = 2;
            foreach (var test in items.GroupBy(i => i.Test).OrderBy(g => g.Key))
            {
                ws.Cells[row, 1].Value = test.Key;
                for (int s = 0; s < statuses.Length; s++) ws.Cells[row, s + 2].Value = test.Count(i => i.Status == statuses[s]);
                ws.Cells[row, statuses.Length + 2].Value = test.Count();
                row++;
            }
            ws.Cells[row, 1].Value = "Итого";
            for (int s = 0; s < statuses.Length; s++) ws.Cells[row, s + 2].Value = items.Count(i => i.Status == statuses[s]);
            ws.Cells[row, statuses.Length + 2].Value = items.Count;
            ws.Cells[row, 1, row, statuses.Length + 2].Style.Font.Bold = true;
            Format(ws, statuses.Length + 2, row);
        }

        private static void Format(ExcelWorksheet ws, int columns, int lastRow)
        {
            using (ExcelRange header = ws.Cells[1, 1, 1, columns])
            {
                header.Style.Font.Bold = true;
                header.Style.Fill.PatternType = ExcelFillStyle.Solid;
                header.Style.Fill.BackgroundColor.SetColor(Color.FromArgb(230, 230, 230));
                header.Style.WrapText = true;
            }
            ws.View.FreezePanes(2, 1);
            if (lastRow >= 1) ws.Cells[1, 1, Math.Max(lastRow, 1), columns].AutoFilter = true;
            ws.Cells[1, 1, Math.Max(lastRow, 1), columns].AutoFitColumns(6, 60);
        }

        private static Color? FillOf(ClashStatus s)
        {
            switch (s)
            {
                case ClashStatus.InWork: return Color.FromArgb(250, 238, 218);
                case ClashStatus.Passed: return Color.FromArgb(230, 241, 251);
                case ClashStatus.Fake: return Color.FromArgb(238, 237, 254);
                case ClashStatus.Fixed: return Color.FromArgb(234, 243, 222);
                default: return null;
            }
        }

        /// <summary>ID после соединения/разделения лотков по журналу замен; пусто, если элемент не менялся.</summary>
        private static string NewIds(ClashSide s) =>
            s.Id == null ? "" : string.Join(", ", ElementIdJournal.Resolve(s.File, s.Id.Value));

        /// <summary>«Конфликт2» раньше «Конфликт10».</summary>
        private static string Natural(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"\d+", m => m.Value.PadLeft(10, '0'));
    }
}
