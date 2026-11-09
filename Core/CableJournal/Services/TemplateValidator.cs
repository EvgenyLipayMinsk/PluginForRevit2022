using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Collections.Generic;
using System.IO;
using ARBM.CableJournalExporter.Models;

namespace ARBM.CableJournalExporter.Services
{
    public class TemplateValidator
    {
        public TemplateValidationResult Validate(string templatePath, TemplateStructure expected)
        {
            if (!File.Exists(templatePath))
                return TemplateValidationResult.Fail("Файл шаблона не найден.");

            ExcelPackage.License.SetNonCommercialPersonal("ARBM");

            using (var package = new ExcelPackage(new FileInfo(templatePath)))
            {
                if (package.Workbook.Worksheets.Count == 0)
                    return TemplateValidationResult.Fail("В шаблоне отсутствуют листы.");

                var ws = package.Workbook.Worksheets[0];

                // ---------------------------------------------------------
                // 1. Проверка количества колонок
                // ---------------------------------------------------------
                if (expected.HeaderRow1.Count != 8 || expected.HeaderRow2.Count != 8)
                {
                    return TemplateValidationResult.Fail(
                        "TemplateStructure должен содержать ровно 8 колонок в HeaderRow1 и HeaderRow2.");
                }

                // Колонки C..J
                string[] cols = { "C", "D", "E", "F", "G", "H", "I", "J" };

                // ---------------------------------------------------------
                // 2. Проверка первой строки (HeaderRow1)
                // ---------------------------------------------------------
                for (int i = 0; i < cols.Length; i++)
                {
                    string col = cols[i];
                    string expectedValue = expected.HeaderRow1[i];
                    string actualValue = ws.Cells[$"{col}1"].Text?.Trim() ?? "";

                    if (expectedValue != actualValue)
                    {
                        return TemplateValidationResult.Fail(
                            $"Ошибка в строке 1, колонка {col}: ожидается '{expectedValue}', найдено '{actualValue}'.");
                    }
                }

                // ---------------------------------------------------------
                // 3. Проверка второй строки (HeaderRow2)
                // ---------------------------------------------------------
                for (int i = 0; i < cols.Length; i++)
                {
                    string col = cols[i];
                    string expectedValue = expected.HeaderRow2[i];
                    string actualValue = ws.Cells[$"{col}2"].Text?.Trim() ?? "";

                    if (expectedValue != actualValue)
                    {
                        return TemplateValidationResult.Fail(
                            $"Ошибка в строке 2, колонка {col}: ожидается '{expectedValue}', найдено '{actualValue}'.");
                    }
                }

                // ---------------------------------------------------------
                // 4. Проверка и автоматическое исправление нижних границ
                // ---------------------------------------------------------
                foreach (var col in cols)
                {
                    var cell = ws.Cells[$"{col}2"];
                    var border = cell.Style.Border.Bottom.Style;

                    if (border == ExcelBorderStyle.None)
                    {
                        // Автоматически добавляем тонкую границу
                        cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                    }
                }


                // ---------------------------------------------------------
                // 5. Проверка merged‑ячеек в первой строке
                // ---------------------------------------------------------
                // "Откуда" занимает C1:D1
                if (!ws.Cells["C1:D1"].Merge)
                    return TemplateValidationResult.Fail("Ячейки C1:D1 должны быть объединены для заголовка 'Откуда'.");

                // "Куда" — F1
                if (ws.Cells["F1"].Merge)
                    return TemplateValidationResult.Fail("Ячейка F1 не должна быть объединена.");

                // "Кабель" занимает G1:H1
                if (!ws.Cells["G1:H1"].Merge)
                    return TemplateValidationResult.Fail("Ячейки G1:H1 должны быть объединены для заголовка 'Кабель'.");

                // ---------------------------------------------------------
                // Всё хорошо
                // ---------------------------------------------------------
                return TemplateValidationResult.Ok();
            }
        }
    }
}

