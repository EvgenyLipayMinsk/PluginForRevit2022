using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using OfficeOpenXml;
using ARBM.CableJournalExporter.Models;

class ReflectionSmoke
{
    static Assembly plugin;
    static string folder;
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    static object Args(Dictionary<string, object> values) => Activator.CreateInstance(plugin.GetType("PluginForRevit2022.Mcp.McpArgs"), new object[] { values });
    static object Call(string type, string method, params object[] args)
    {
        try { return plugin.GetType("PluginForRevit2022.Core." + type).GetMethod(method, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, args); }
        catch (TargetInvocationException e) { throw e.InnerException; }
    }
    static Dictionary<string, object> Manage(Dictionary<string, object> values) => (Dictionary<string, object>)Call("ClashReportFileOperations", "Manage", "Test", Args(values));
    static int Main(string[] args)
    {
        try
        {
            folder = args[0]; Directory.CreateDirectory(folder);
            plugin = typeof(CableRecord).Assembly;
            ExcelPackage.License.SetNonCommercialPersonal("ARBM");
            string source = Path.Combine(folder, "report.xml");
            File.WriteAllText(source, "<exchange units='m'><clashtest name='Test'><clashresult name='Conflict1' guid='id-1' status='new'/></clashtest></exchange>");
            var update = new Dictionary<string, object> { ["reportPaths"] = new[] { source }, ["action"] = "update", ["key"] = "id-1", ["status"] = "Fixed", ["changes"] = new Dictionary<string, object> { ["comment"] = "done" } };
            var preview = Manage(update);
            Check((bool)preview["dryRun"], "Update default must be dryRun");
            Check(!File.Exists(source + ".aerbim.json") && !Directory.Exists(source + ".aerbim.json.images"), "Preview wrote sidecars/images");
            update["dryRun"] = false; Manage(update);
            var list = Manage(new Dictionary<string, object> { ["reportPaths"] = new[] { source } });
            Check((int)list["total"] == 1, "List count");
            var items = (List<Dictionary<string, object>>)list["items"];
            Check((string)items[0]["status"] == "Fixed" && (string)items[0]["comment"] == "done", "Status/comment not saved");
            Check(!(bool)items[0]["hasScreenshot"], "Screenshot became mandatory");
            string xlsx = Path.Combine(folder, "report.xlsx");
            var export = new Dictionary<string, object> { ["reportPaths"] = new[] { source }, ["action"] = "export", ["savePath"] = xlsx };
            Manage(export); Check(!File.Exists(xlsx), "Export preview wrote a file");
            export["dryRun"] = false; Manage(export); Check(File.Exists(xlsx), "Actual export missing");
            byte[] old = File.ReadAllBytes(xlsx);
            try { Manage(export); throw new Exception("Overwrite without permission"); } catch (IOException) { }
            Check(File.ReadAllBytes(xlsx).SequenceEqual(old), "Existing output damaged");
            update["changes"] = new Dictionary<string, object> { ["comment"] = "" }; Manage(update);
            items = (List<Dictionary<string, object>>)Manage(new Dictionary<string, object> { ["reportPaths"] = new[] { source } })["items"];
            Check((string)items[0]["comment"] == "", "Explicit comment clear failed");
            try { Manage(new Dictionary<string, object> { ["reportPaths"] = new[] { source }, ["action"] = "update", ["key"] = "absent" }); throw new Exception("Unknown clash key accepted"); } catch (ArgumentException) { }
            Check(!Directory.GetFiles(folder, "*.png").Any(), "Optional screenshot unexpectedly created");
            Console.WriteLine("PASS: report MCP list/update/export, dryRun, optional image, overwrite guard and optional-image behaviour");

            var records = new List<CableRecord> { new CableRecord { StartPoint = "A", EndPoint = "B", Length = "10", CableType = "Brand", CableCategory = "Cores", InstallationLengths = new CableInstallationLengths(7, 3) } };
            using (var book = new ExcelPackage())
            {
                var sheet = book.Workbook.Worksheets.Add("Полный КЖ");
                sheet.Cells[2, 2].Value = "A"; sheet.Cells[2, 3].Value = "B"; sheet.Cells[2, 4].Value = 7;
                sheet.Cells[2, 7].Value = 3; sheet.Cells[2, 8].Value = "Brand"; sheet.Cells[2, 9].Value = "Cores"; sheet.Cells[2, 10].Value = 10;
                Dictionary<string, object> Compare() => (Dictionary<string, object>)Call("JournalReviewOperations", "CompareRows", sheet, records, 0.01);
                Check((bool)Compare()["ok"], "Equal journal rejected");
                sheet.Cells[2, 4].Value = 6; Check(!(bool)Compare()["ok"], "Installation difference missed");
                sheet.Cells[2, 4].Value = 7; sheet.Cells[2, 10].Value = "bad"; Check(!(bool)Compare()["ok"], "Invalid numeric length accepted");
                sheet.Cells[2, 10].Value = 10; sheet.Cells[2, 8].Value = "Other"; Check(!(bool)Compare()["ok"], "Cable type difference missed");
                sheet.Cells[2, 8].Value = "Brand"; sheet.Cells[2, 2].Value = "Renamed"; Check((int)Compare()["missingInExcelCount"] == 1, "Renamed device matched silently");
                sheet.Cells[2, 2].Value = "A"; records.Add(new CableRecord { StartPoint = "A", EndPoint = "B", Length = "10" });
                Check(!(bool)Compare()["ok"], "Ambiguous model endpoints matched"); records.RemoveAt(1);
                sheet.Cells[2, 1, 2, 13].Copy(sheet.Cells[3, 1, 3, 13]); Check(!(bool)Compare()["ok"], "Duplicate Excel endpoints missed");
                sheet.Cells[3, 1, 3, 13].Clear(); Check((bool)Compare()["ok"], "Empty formatted row counted as cable");
            }
            Console.WriteLine("PASS: journal comparison exact/changed/renamed/duplicate/ambiguous/invalid rows");

            var report = new Dictionary<string, object> { ["ok"] = false, ["checks"] = new[] { new Dictionary<string, object> { ["check"] = "routes", ["issues"] = new[] { new Dictionary<string, object> { ["id"] = 123, ["message"] = "Разрыв" } } } } };
            string json = Path.Combine(folder, "checks.json"), excel = Path.Combine(folder, "checks.xlsx");
            Call("ProjectCheckOperations", "Write", json, report, false);
            Check(new JavaScriptSerializer().DeserializeObject(File.ReadAllText(json)) != null, "Invalid check JSON");
            Call("ProjectCheckOperations", "Write", excel, report, false);
            using (var book = new ExcelPackage(new FileInfo(excel))) Check(book.Workbook.Worksheets["Проверки"].Cells.Any(c => c.Text == "123"), "Element ID lost in check Excel");
            try { Call("DiagnosticFiles", "Target", json, ".json", false); throw new Exception("Existing target accepted"); } catch (IOException) { }
            try { Call("DiagnosticFiles", "Target", Path.Combine(folder, "file.txt"), ".json", false); throw new Exception("Wrong extension accepted"); } catch (ArgumentException) { }
            Console.WriteLine("PASS: combined check JSON/XLSX retain IDs and protect output files");
            Console.WriteLine("Output: " + folder);
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
