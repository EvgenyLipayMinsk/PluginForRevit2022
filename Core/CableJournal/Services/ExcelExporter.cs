using ARBM.CableJournalExporter.Models;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace ARBM.CableJournalExporter.Services
{
    public class ExcelExporter
    {
        private readonly string[] _cols = { "A", "B", "C", "D", "E", "F", "G", "H", "I", "J" };

        public void ExportCableRecords(
            string templatePath,
            string savePath,
            List<CableRecord> records)
        {
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");

            // Проверяем, что файл не открыт и доступен для записи
            if (!CanAccessFile(savePath))
            {
                throw new IOException(
                    $"Не удалось получить доступ к файлу '{Path.GetFileName(savePath)}'.\n\n" +
                    "Возможные причины:\n" +
                    "• Файл открыт в другом приложении (Excel, LibreOffice и т.д.)\n" +
                    "• Файл заблокирован другим процессом\n" +
                    "• Недостаточно прав доступа\n\n" +
                    "Решение: Закройте файл и повторите попытку.");
            }

            try
            {
                // 1. Копируем шаблон → создаём новый файл
                File.Copy(templatePath, savePath, true);
            }
            catch (IOException ex)
            {
                throw new IOException(
                    $"Ошибка при создании файла '{Path.GetFileName(savePath)}':\n{ex.Message}\n\n" +
                    "Убедитесь, что:\n" +
                    "• Путь доступен для записи\n" +
                    "• Файл не открыт в другом приложении\n" +
                    "• Достаточно свободного место на диске",
                    ex);
            }

            using (var package = new ExcelPackage(new FileInfo(savePath)))
            {
                var ws = package.Workbook.Worksheets[0];

                // 2. Читаем эталонные стили строки 4 (включая A и B)
                var row2Styles = ReadRowStyles(ws, 4);

                // 3. Читаем эталонные нижние границы строки 244
                var bottomBorderStyles = ReadBottomBorderStyles(ws, 244);

                // 4. Заполняем строки начиная с 3-й
                int row = 3;
                int recordNumber = 1; // Счётчик для автонумерации

                foreach (var rec in records)
                {
                    // Копируем стиль строки 4 в новую строку (включая A и B)
                    CopyRowStyle(ws, row, row2Styles);

                    // Записываем данные с автонумерацией и формулой
                    WriteRecord(ws, row, rec, recordNumber);

                    row++;
                    recordNumber++;
                }

                // 5. Применяем нижнюю границу строки 244 к последней строке данных
                ApplyBottomBorderFromTemplate(ws, row - 1, bottomBorderStyles);

                try
                {
                    package.Save();
                }
                catch (IOException ex)
                {
                    throw new IOException(
                        $"Ошибка при сохранении файла '{Path.GetFileName(savePath)}':\n{ex.Message}",
                        ex);
                }
            }

            // 6. Открываем итоговый файл
            try
            {
                Process.Start(new ProcessStartInfo(savePath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                // Файл создан, но не удалось открыть — не критично
                System.Diagnostics.Debug.WriteLine($"Не удалось открыть файл: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверяет, доступен ли файл для записи (не открыт в других приложениях).
        /// </summary>
        private bool CanAccessFile(string filePath)
        {
            // Если файл не существует — доступ гарантирован
            if (!File.Exists(filePath))
                return true;

            try
            {
                // Пытаемся открыть файл в режиме исключительного доступа
                using (FileStream stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.RandomAccess))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Файл заблокирован — другое приложение его использует
                return false;
            }
        }

        // --------------------------------------------------------------------
        // Чтение стилей строки (включая границы)
        // --------------------------------------------------------------------
        private Dictionary<string, int> ReadRowStyles(ExcelWorksheet ws, int row)
        {
            var result = new Dictionary<string, int>();

            foreach (var col in _cols)
            {
                result[col] = ws.Cells[$"{col}{row}"].StyleID;
            }

            return result;
        }

        // --------------------------------------------------------------------
        // Чтение нижних границ строки 244
        // --------------------------------------------------------------------
        private Dictionary<string, ExcelBorderStyle> ReadBottomBorderStyles(ExcelWorksheet ws, int row)
        {
            var result = new Dictionary<string, ExcelBorderStyle>();

            foreach (var col in _cols)
            {
                var cell = ws.Cells[$"{col}{row}"];
                result[col] = cell.Style.Border.Bottom.Style;
            }

            return result;
        }

        // --------------------------------------------------------------------
        // Копирование стиля строки в новую строку
        // --------------------------------------------------------------------
        private void CopyRowStyle(ExcelWorksheet ws, int targetRow, Dictionary<string, int> styleMap)
        {
            foreach (var col in _cols)
            {
                ws.Cells[$"{col}{targetRow}"].StyleID = styleMap[col];
            }
        }

        // --------------------------------------------------------------------
        // Запись данных CableRecord с автонумерацией и формулой
        // --------------------------------------------------------------------
        private void WriteRecord(ExcelWorksheet ws, int row, CableRecord rec, int recordNumber)
        {
            // Колонка A: Автонумерация (№ пп)
            ws.Cells[$"A{row}"].Value = recordNumber;

            // Колонка B: Формула =СЦЕПИТЬ(D{row};"-";F{row})
            ws.Cells[$"B{row}"].Formula = $"CONCATENATE(D{row},\"-\",F{row})";

            // Запись основных данных
            ws.Cells[$"{rec.ColumnMap.StartLevelColumn}{row}"].Value = rec.StartLevel;
            ws.Cells[$"{rec.ColumnMap.StartPointColumn}{row}"].Value = rec.StartPoint;
            ws.Cells[$"{rec.ColumnMap.EndLevelColumn}{row}"].Value = rec.EndLevel;
            ws.Cells[$"{rec.ColumnMap.EndPointColumn}{row}"].Value = rec.EndPoint;
            ws.Cells[$"{rec.ColumnMap.CableTypeColumn}{row}"].Value = rec.CableType;
            ws.Cells[$"{rec.ColumnMap.CableCategoryColumn}{row}"].Value = rec.CableCategory;
            ws.Cells[$"{rec.ColumnMap.PurposeColumn}{row}"].Value = rec.Purpose;
            ws.Cells[$"{rec.ColumnMap.LengthColumn}{row}"].Value = rec.Length;

            // 🔥 Подсветка fallback-длин
            if (rec.IsFallbackForLength)
            {
                ws.Cells[$"K{row}"].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[$"K{row}"].Style.Fill.BackgroundColor.SetColor(System.Drawing.Color.Yellow);
            }
        }

        // --------------------------------------------------------------------
        // Применение нижней границы строки 244 к последней строке данных
        // --------------------------------------------------------------------
        private void ApplyBottomBorderFromTemplate(
            ExcelWorksheet ws,
            int lastRow,
            Dictionary<string, ExcelBorderStyle> bottomBorderStyles)
        {
            foreach (var kvp in bottomBorderStyles)
            {
                string col = kvp.Key;
                var style = kvp.Value;

                ws.Cells[$"{col}{lastRow}"].Style.Border.Bottom.Style = style;
            }
        }
    }
}
