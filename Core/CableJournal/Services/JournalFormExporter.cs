using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using ARBM.CableJournalExporter.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace ARBM.CableJournalExporter.Services
{
    public enum CableJournalForm { AerBim, A3 }

    // Выбор формы не меняет сбор данных и расчёт длин существующего журнала.
    public sealed class JournalFormExporter
    {
        public const string BuiltInA3 = "builtin:a3";
        private const string ResourceName = "PluginForRevit2022.CableJournal.A3.xlsx";
        private readonly CableJournalForm _form;
        public JournalFormExporter(CableJournalForm form) { _form = form; }

        public TemplateValidationResult Validate(string templatePath, TemplateStructure expected)
        {
            if (_form == CableJournalForm.AerBim)
                return new TemplateValidator().Validate(templatePath, expected);
            using (var stream = typeof(JournalFormExporter).Assembly.GetManifestResourceStream(ResourceName))
                return stream == null ? TemplateValidationResult.Fail("Встроенная форма КЖ А3 не найдена. Переустановите плагин.")
                    : TemplateValidationResult.Ok();
        }

        public void ExportCableRecords(string templatePath, string savePath, List<CableRecord> records)
        {
            // Защита шаблона от случайного выбора того же имени в окне сохранения.
            if (_form == CableJournalForm.AerBim &&
                string.Equals(Path.GetFullPath(templatePath), Path.GetFullPath(savePath), StringComparison.OrdinalIgnoreCase))
                throw new IOException("Выберите для результата другой файл: перезаписывать шаблон нельзя.");
            if (_form == CableJournalForm.AerBim)
            {
                new ExcelExporter().ExportCableRecords(templatePath, savePath, records);
                return;
            }
            ExportA3(savePath, records);
            try { Process.Start(new ProcessStartInfo(savePath) { UseShellExecute = true }); }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }

        // Отдельный метод без открытия Excel используется и для проверки формы вне Revit.
        public static void ExportA3(string savePath, IList<CableRecord> records)
        {
            if (records == null || records.Count == 0)
                throw new ArgumentException("Нет записей для выгрузки кабельного журнала.");
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            using (var stream = typeof(JournalFormExporter).Assembly.GetManifestResourceStream(ResourceName))
            {
                if (stream == null) throw new InvalidOperationException("Встроенная форма КЖ А3 не найдена.");
                using (var template = new ExcelPackage(stream))
                using (var output = new ExcelPackage())
                {
                    var full = output.Workbook.Worksheets.Add("Полный КЖ");
                    string[] headers = { "Обозначение кабеля", "Начало трассы", "Конец трассы", "Скрыто в трубе, м", "Открыто в трубе, м", "По стояку в стальной трубе, м", "В лотке, м", "Марка по проекту", "Число и сечение жил по проекту", "Длина по проекту, м", "Марка проложенного кабеля", "Число и сечение жил проложенного кабеля", "Проложенная длина, м" };
                    for (int col = 1; col <= headers.Length; col++) full.Cells[1, col].Value = headers[col - 1];
                    for (int index = 0; index < records.Count; index++)
                    {
                        var record = records[index];
                        int row = index + 2;
                        double length = ParseLength(record.Length);
                        full.Cells[row, 1].Value = record.StartPoint + "-" + record.EndPoint;
                        full.Cells[row, 2].Value = record.StartPoint;
                        full.Cells[row, 3].Value = record.EndPoint;
                        var installation = (record.InstallationLengths ?? new CableInstallationLengths(0, 0)).Allocate(length);
                        if (installation.Concealed > 0 || length == 0) full.Cells[row, 4].Value = installation.Concealed;
                        if (installation.Tray > 0) full.Cells[row, 7].Value = installation.Tray;
                        full.Cells[row, 8].Value = record.CableType;
                        full.Cells[row, 9].Value = record.CableCategory;
                        full.Cells[row, 10].Value = length;
                        if (record.IsFallbackForLength)
                        {
                            Highlight(full.Cells[row, 4]);
                            Highlight(full.Cells[row, 10]);
                        }
                    }
                    full.Tables.Add(full.Cells[1, 1, records.Count + 1, 13], "CableJournal");
                    full.Cells[1, 1, records.Count + 1, 13].Style.Font.Name = "Calibri";
                    full.Cells[1, 1, records.Count + 1, 13].Style.Font.Size = 11;
                    full.Cells[1, 1, 1, 13].Style.WrapText = true;
                    full.Row(1).Height = 48;
                    for (int col = 1; col <= 13; col++) full.Column(col).Width = col <= 3 ? 42 : (col == 8 || col == 11 ? 26 : 20);
                    full.View.FreezePanes(2, 1);
                    full.View.ShowGridLines = false;
                    full.Cells[2, 4, records.Count + 1, 7].Style.Numberformat.Format = "General";
                    full.Cells[2, 10, records.Count + 1, 10].Style.Numberformat.Format = "General";
                    full.Cells[2, 13, records.Count + 1, 13].Style.Numberformat.Format = "General";

                    const int firstPageRows = 44;
                    const int continuationRows = 49;
                    int pages = 1 + (Math.Max(0, records.Count - firstPageRows) + continuationRows - 1) / continuationRows;
                    int[] targetColumns = { 4, 9, 21, 33, 34, 35, 39, 43, 48, 53, 58, 63, 68 };
                    string[] sourceColumns = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M" };
                    for (int page = 0; page < pages; page++)
                    {
                        bool firstPage = page == 0;
                        int rowsPerPage = firstPage ? firstPageRows : continuationRows;
                        int firstRecord = firstPage ? 0 : firstPageRows + (page - 1) * continuationRows;
                        var ws = output.Workbook.Worksheets.Add("КЖ " + (page + 1),
                            template.Workbook.Worksheets[firstPage ? "Первый лист" : "Продолжение"]);
                        ws.Names.Remove("Print_Area");
                        if (firstPage)
                        {
                            ws.Cells["BN57"].Value = page + 1;
                            ws.Cells["BQ57"].Value = pages;
                        }
                        else ws.Cells["BS59"].Value = page + 1;
                        for (int slot = 0; slot < rowsPerPage; slot++)
                        {
                            int index = firstRecord + slot;
                            if (index >= records.Count) break;
                            int row = 8 + slot;
                            for (int col = 0; col < targetColumns.Length; col++)
                            {
                                var cell = ws.Cells[row, targetColumns[col]];
                                string source = "'Полный КЖ'!" + sourceColumns[col] + (index + 2);
                                // Прямое равенство пустой ячейке в Excel дало бы 0.
                                cell.Formula = "IF(" + source + "=\"\",\"\"," + source + ")";
                                cell.Style.Numberformat.Format = "General";
                                cell.Style.ShrinkToFit = true;
                                cell.Style.WrapText = false;
                            }
                            if (records[index].IsFallbackForLength)
                            {
                                Highlight(ws.Cells[row, 33]);
                                Highlight(ws.Cells[row, 53]);
                            }
                        }
                        int lastDataRow = 7 + Math.Min(rowsPerPage, records.Count - firstRecord);
                        var bottom = ws.Cells[lastDataRow, 4, lastDataRow, 72].Style.Border.Bottom;
                        bottom.Style = ExcelBorderStyle.Medium;
                        bottom.Color.SetColor(System.Drawing.Color.Black);
                        ws.View.ShowGridLines = false;
                        ws.PrinterSettings.LeftMargin = 0;
                        ws.PrinterSettings.RightMargin = 0;
                        ws.PrinterSettings.TopMargin = 0;
                        ws.PrinterSettings.BottomMargin = 0;
                        ws.PrinterSettings.HeaderMargin = 0;
                        ws.PrinterSettings.FooterMargin = 0;
                        ws.PrinterSettings.PaperSize = ePaperSize.A3;
                        ws.PrinterSettings.Orientation = eOrientation.Landscape;
                        ws.PrinterSettings.PrintArea = ws.Cells["A1:BU61"];
                        ws.PrinterSettings.FitToPage = true;
                        ws.PrinterSettings.FitToWidth = 1;
                        ws.PrinterSettings.FitToHeight = 1;
                    }
                    output.Workbook.Calculate();
                    output.Workbook.CalcMode = ExcelCalcMode.Automatic;
                    // Сначала формируем весь файл; ошибка в данных не повреждает существующий результат.
                    byte[] bytes = output.GetAsByteArray();
                    using (var file = new FileStream(savePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                    {
                        file.SetLength(0);
                        file.Write(bytes, 0, bytes.Length);
                    }
                }
            }
        }

        private static void Highlight(ExcelRange cell)
        {
            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
            cell.Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.Yellow);
        }
        private static double ParseLength(string text)
        {
            double value;
            if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                throw new ArgumentException("Некорректная длина кабеля: «" + text + "».");
            return value;
        }
    }
}
