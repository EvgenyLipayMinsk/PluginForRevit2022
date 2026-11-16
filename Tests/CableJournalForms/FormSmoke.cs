using System;
using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Services;

class FormSmoke
{
    static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    static int Main(string[] args)
    {
        try
        {
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            Directory.CreateDirectory(args[1]);
            foreach (int count in new[] { 1, 44, 45, 93, 94, 1203 })
            {
                var records = new List<CableRecord>();
                for (int i=0; i<count; i++) records.Add(new CableRecord
                {
                    StartPoint = "A.ARK.1." + i, EndPoint = "A.BTH.1." + (i+1), StartLevel = "1", EndLevel = "2",
                    CableType = "КПСЭнг(А)-FRHF", CableCategory = "1х2х0,75", Length = i == 0 ? "8,00" : "12,5", IsFallbackForLength = i == 0
                });
                string path = Path.Combine(args[1], "a3-" + count + ".xlsx");
                JournalFormExporter.ExportA3(path, records);
                using (var workbook = new ExcelPackage(new FileInfo(path)))
                {
                    Require(workbook.Workbook.Worksheets.Count == 2 + (Math.Max(0, count - 44) + 48) / 49, "Pagination");
                    int actual=0;
                    foreach (var ws in workbook.Workbook.Worksheets)
                    {
                        if (ws.Name == "Полный КЖ") continue;
                        Require(ws.Cells["D2"].Text == "Обозначение кабеля, провода", "Header");
                        bool isFirst = ws.Name == "КЖ 1";
                        Require(ws.Cells[isFirst ? "BN57" : "BS59"].GetValue<int>() == ws.Index, "Page number");
                        if (!isFirst)
                        {
                            Require(ws.Cells["BS58"].Text == "Лист", "Continuation stamp");
                            Require(ws.Cells["D54"].Text.IndexOf("Примечания", StringComparison.Ordinal) < 0, "First-page notes repeated");
                            Require(ws.Cells["AX56"].Text == "", "Large stamp repeated");
                        }
                        double height = 0;
                        for (int r = 1; r <= 61; r++) height += ws.Row(r).Height;
                        Require(height >= 985 && height <= 1010, "A3 height does not fill page: " + height);
                        for (int row=8; row <= (isFirst ? 51 : 56); row++)
                        {
                            if (!string.IsNullOrEmpty(ws.Cells[row,4].Text))
                            {
                                actual++;
                                Require(ws.Cells[row,33].GetValue<double>() == (actual == 1 ? 8 : 12.5), "Hidden length");
                                Require(ws.Cells[row,53].GetValue<double>() == (actual == 1 ? 8 : 12.5), "Design length");
                                if (actual == 1) Require(ws.Cells[row,33].Style.Numberformat.Format == "General" && ws.Cells[row,53].Style.Numberformat.Format == "General", "Integer cell has a decimal separator in its Excel number format");
                                Require(!ws.Cells[row,33].Text.EndsWith(",") && !ws.Cells[row,33].Text.EndsWith(".") && !ws.Cells[row,53].Text.EndsWith(",") && !ws.Cells[row,53].Text.EndsWith("."), "Trailing decimal separator: " + ws.Cells[row,33].Text);
                                Require(ws.Cells[row,43].Text == "КПСЭнг(А)-FRHF", "Cable brand");
                                Require(ws.Cells[row,48].Text == "1х2х0,75", "Cable cores");
                            }
                            foreach (int col in new[] {34,35,39,58,63,68})
                                Require(string.IsNullOrEmpty(ws.Cells[row,col].Text), "Unfilled installation/actual column");
                        }
                        int pageStart = isFirst ? 0 : 44 + (ws.Index - 2) * 49;
                        int lastRow = 7 + Math.Min(isFirst ? 44 : 49, count - pageStart);
                        for (int c = 4; c <= 72; c++)
                        {
                            Require(ws.Cells[lastRow,c].Style.Border.Bottom.Style == ExcelBorderStyle.Medium, "Last row border: " + c);
                            Require(ws.Cells[lastRow,c].Style.Border.Bottom.Color.Rgb == "FF000000", "Last row border color");
                        }
                        Require(ws.Cells[lastRow,73].Style.Border.Bottom.Style != ExcelBorderStyle.Medium, "Border extends beyond table");
                        Require(ws.PrinterSettings.PaperSize == ePaperSize.A3 && ws.PrinterSettings.FitToPage && ws.PrinterSettings.FitToWidth == 1 && ws.PrinterSettings.FitToHeight == 1, "Print setup");
                        Require(ws.Drawings.Count == 0, "Original project drawings");
                    }
                    Require(actual == count, "Missing or stale records");
                    var full = workbook.Workbook.Worksheets["Полный КЖ"];
                    Require(full.Dimension.End.Row == count + 1, "Full journal row count");
                    Require(full.Cells["B2"].Text == records[0].StartPoint && full.Cells["C2"].Text == records[0].EndPoint, "Floor suffix remains");
                    var first = workbook.Workbook.Worksheets["КЖ 1"];
                    Require(first.Cells["AG8"].Formula.Contains("'Полный КЖ'!D2"), "Hidden length is not linked");
                    full.Cells["D2"].Value = 8.75;
                    full.Cells["B2"].Value = "Исправлено вручную";
                    full.Cells["H2"].Value = "Новая марка";
                    full.Cells["E2"].Value = 0;
                    workbook.Workbook.Calculate();
                    Require(first.Cells["AG8"].GetValue<double>() == 8.75, "Manual fractional edit not reflected");
                    Require(first.Cells["I8"].Text == "Исправлено вручную", "Manual text edit not reflected");
                    Require(first.Cells["AQ8"].Text == "Новая марка", "Manual cable edit not reflected");
                    Require(first.Cells["AH8"].GetValue<double>() == 0 && first.Cells["AH8"].Text != "", "Zero hidden as blank");
                    Require(first.Cells["AI8"].Text == "", "Blank rendered as zero");
                }
                Console.WriteLine("PASS: " + count + " records");
            }
            foreach (string name in new[] { "Без засечки", "В ПВХ-канале", "В гибкой трубе", "В жёсткой трубе" })
                Require(CableInstallationLengths.FromTypeName("ARBM_УГО_Засечки : " + name) == CableInstallation.Concealed, "Concealed mapping " + name);
            Require(CableInstallationLengths.FromTypeName("ARBM_УГО_Засечки : В лотке") == CableInstallation.Tray, "Tray mapping");
            try { CableInstallationLengths.FromTypeName("Неизвестно"); throw new Exception("Unknown mapping accepted"); }
            catch (ArgumentException) { }
            var mixed = new List<CableRecord>();
            foreach (var parts in new[] { new CableInstallationLengths(0,10), new CableInstallationLengths(7,3), new CableInstallationLengths(0,0), new CableInstallationLengths(0.1,0.2) })
                mixed.Add(new CableRecord { StartPoint="M2.BTH.2.31(1.7)", EndPoint="M2.ARK.1", StartLevel="2", EndLevel="1", Length="13", InstallationLengths=parts });
            string mixedPath = Path.Combine(args[1], "mixed.xlsx");
            JournalFormExporter.ExportA3(mixedPath, mixed);
            using (var book = new ExcelPackage(new FileInfo(mixedPath)))
            {
                var full = book.Workbook.Worksheets["Полный КЖ"];
                var page = book.Workbook.Worksheets["КЖ 1"];
                for (int r=2;r<=5;r++)
                {
                    Require(full.Cells[r,4].GetValue<double>() + full.Cells[r,7].GetValue<double>() == full.Cells[r,10].GetValue<double>(), "Installation sum mismatch");
                    Require(page.Cells[r+6,33].GetValue<double>() + page.Cells[r+6,39].GetValue<double>() == page.Cells[r+6,53].GetValue<double>(), "Linked sum mismatch");
                    Require(full.Cells[r,5].Text == "" && full.Cells[r,6].Text == "", "Open/riser must stay empty");
                }
                Require(full.Cells["D2"].Text == "" && full.Cells["G2"].GetValue<double>() == 13, "Tray-only route");
                Require(full.Cells["D3"].GetValue<double>() == 9 && full.Cells["G3"].GetValue<double>() == 4, "Mixed route with reserve and rounding");
                Require(full.Cells["D4"].GetValue<double>() == 13 && full.Cells["G4"].Text == "", "No-route default");
                Require(full.Cells["B2"].Text == "M2.BTH.2.31(1.7)", "Device designation damaged");
            }
            foreach (double total in new[] { 0.0, 1.0, 2.2, 13.0, 18.75, 1024.12 })
            {
                var parts = new CableInstallationLengths(7,3).Allocate(total);
                Require(Math.Abs(parts.Concealed + parts.Tray - total) < 1e-9, "Fractional allocation sum");
                Require(parts.Concealed >= 0 && parts.Tray >= 0, "Negative allocation");
            }
            Console.WriteLine("PASS: installation mapping, mixed routes, reserve, sums and floor labels");
            var invalid = new List<CableRecord> { new CableRecord { Length = "invalid" } };
            string existing = Path.Combine(args[1], "existing.txt");
            File.WriteAllText(existing, "keep");
            try { JournalFormExporter.ExportA3(existing, invalid); throw new Exception("Invalid length accepted"); }
            catch (ArgumentException) { Require(File.ReadAllText(existing) == "keep", "Existing file damaged"); }
            string locked = Path.Combine(args[1], "locked.xlsx");
            File.WriteAllText(locked, "keep");
            using (var file = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try { JournalFormExporter.ExportA3(locked, new List<CableRecord> { new CableRecord { Length="1" } }); throw new Exception("Locked file accepted"); }
                catch (IOException) { }
            }
            Require(File.ReadAllText(locked)=="keep", "Locked file damaged");
            Console.WriteLine("PASS: invalid lengths and locked files");
            var legacy = new JournalFormExporter(CableJournalForm.AerBim);
            string standard = Path.Combine(args[2], "Installer", "templ_kzh.xlsx");
            Require(legacy.Validate(standard, new TemplateStructure()).IsValid, "Legacy template rejected");
            try { legacy.ExportCableRecords(standard, standard, new List<CableRecord>()); throw new Exception("Template overwrite accepted"); }
            catch (IOException) { }
            Console.WriteLine("PASS: existing template validation and overwrite protection");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
