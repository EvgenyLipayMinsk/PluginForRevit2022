using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PluginForRevit2022.Core.Clashes
{
    /// <summary>Статус коллизии, который проставляет проектировщик в окне плагина.</summary>
    internal enum ClashStatus
    {
        New,        // Не рассмотрено
        InWork,     // В работе
        Passed,     // Передано смежному разделу
        Fake,       // Фиктивная
        Fixed,      // Исправлено
    }

    internal static class ClashStatusText
    {
        public static string Of(ClashStatus s)
        {
            switch (s)
            {
                case ClashStatus.InWork: return "В работе";
                case ClashStatus.Passed: return "Передано смежному разделу";
                case ClashStatus.Fake: return "Фиктивная";
                case ClashStatus.Fixed: return "Исправлено";
                default: return "Не рассмотрено";
            }
        }

        public static readonly ClashStatus[] All = { ClashStatus.New, ClashStatus.InWork, ClashStatus.Passed, ClashStatus.Fake, ClashStatus.Fixed };
    }

    /// <summary>Элемент коллизии из отчёта.</summary>
    internal sealed class ClashSide
    {
        public long? Id;            // Revit ElementId («Объект Id»)
        public long? ItemId;        // «Идентификатор элемента» Navisworks («ID объекта: N»)
        public string File = "";    // файл-источник (.rvt)
        public string Category = "", Family = "", Type = "", Name = "", ItemName = "", System = "", Workset = "", Layer = "";

        public bool IsEmpty => Id == null && ItemId == null && string.IsNullOrEmpty(Category) && string.IsNullOrEmpty(Name) && string.IsNullOrEmpty(File);

        public override string ToString()
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Category)) parts.Add(Category);
            string typeName = string.Join(" : ", new[] { Family, Type }.Where(x => !string.IsNullOrEmpty(x)));
            if (typeName.Length > 0) parts.Add(typeName);
            else if (!string.IsNullOrEmpty(Name)) parts.Add(Name);
            if (Id != null) parts.Add($"ID {Id}");
            if (!string.IsNullOrEmpty(File)) parts.Add(File);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>Одна коллизия (или точка обзора) отчёта.</summary>
    internal sealed class ClashItem
    {
        public string Key = "";         // стабильный ключ для статусов
        public string Source = "";      // файл отчёта
        public string Test = "";        // проверка / папка точек обзора
        public string Group = "";
        public string Name = "";
        public string NavisStatus = "";
        public string Date = "";
        public string Distance = "";
        public string Description = "";
        public double[] Point;          // точка конфликта, в единицах отчёта
        public double UnitToFeet = 1;   // множитель единиц отчёта → футы
        public ClashSide Item1 = new ClashSide(), Item2 = new ClashSide();

        // Точка обзора (XML точек обзора Navisworks): камера и границы секущих плоскостей
        public double[] CameraEye, CameraForward, CameraUp;
        public double CameraFocal;
        public double? ClipTop, ClipBottom;
        public string ImagePath;        // картинка из папки отчёта, если есть

        public ClashStatus Status = ClashStatus.New;
        public string Comment = "";
        public string ChangedBy = "", ChangedAt = "";

        public bool IsViewpoint => CameraEye != null;

        public string PointText => Point == null ? "" :
            string.Format(CultureInfo.InvariantCulture, "X:{0:0.###}, Y:{1:0.###}, Z:{2:0.###}", Point[0], Point[1], Point[2]);
    }

    internal sealed class ClashReport
    {
        public List<ClashItem> Items = new List<ClashItem>();
        public List<string> Files = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// Разбор отчётов Navisworks:
    /// • HTML Clash Detective (табличный): колонки берутся из строки заголовков, элементы 1 и 2 — по классам ячеек item1/item2;
    /// • XML Clash Detective (clashtest/clashresult);
    /// • XML точек обзора (viewpoints/viewfolder/view): камера, секущие плоскости, без ID элементов.
    /// </summary>
    internal static class NavisReportReader
    {
        public static ClashReport Read(IEnumerable<string> paths)
        {
            var report = new ClashReport();
            foreach (string path in paths)
            {
                report.Files.Add(path);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                List<ClashItem> items;
                if (ext == ".xml") items = ReadXml(path, report.Warnings);
                else if (ext == ".html" || ext == ".htm") items = ReadHtml(path, report.Warnings);
                else throw new ArgumentException($"«{Path.GetFileName(path)}»: нужен отчёт Navisworks в формате HTML или XML.");
                if (items.Count == 0) report.Warnings.Add($"«{Path.GetFileName(path)}»: коллизий не найдено.");
                report.Items.AddRange(items);
            }
            // Одинаковые ключи из разных файлов (повторно загруженный отчёт) — оставляем первый
            report.Items = report.Items.GroupBy(i => i.Key).Select(g => g.First()).ToList();
            return report;
        }

        // ===================== HTML =====================

        private static readonly Regex Row = new Regex(@"<tr(?<attr>[^>]*)>(?<body>.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex Cell = new Regex(@"<td(?<attr>[^>]*)>(?<body>.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        private static readonly Regex ClassAttr = new Regex(@"class\s*=\s*""(?<c>[^""]*)""", RegexOptions.IgnoreCase);
        private static readonly Regex ImgSrc = new Regex(@"<img[^>]*src\s*=\s*""(?<s>[^""]*)""", RegexOptions.IgnoreCase);

        private static List<ClashItem> ReadHtml(string path, List<string> warnings)
        {
            string html = File.ReadAllText(path);
            var rows = Row.Matches(html).Cast<Match>().Select(m => new
            {
                Class = ClassAttr.Match(m.Groups["attr"].Value).Groups["c"].Value,
                Cells = Cell.Matches(m.Groups["body"].Value).Cast<Match>().Select(c => new
                {
                    Class = ClassAttr.Match(c.Groups["attr"].Value).Groups["c"].Value,
                    Raw = c.Groups["body"].Value,
                    Text = Text(c.Groups["body"].Value),
                }).ToList(),
            }).ToList();

            // Имя проверки и единицы (из «Допуск»: 0.010m)
            string test = rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.Class.Contains("testName"))?.Text ?? Path.GetFileNameWithoutExtension(path);
            // Единицы координат — как у допуска в сводке проверки («0.010m»)
            string tolerance = rows.SelectMany(r => r.Cells).Select(c => c.Text)
                .FirstOrDefault(t => Regex.IsMatch(t, @"^[\d.,]+\s*(mm|m|ft|cm|in)$")) ?? "m";
            double unit = UnitFactor(Regex.Match(tolerance, @"[a-z]+$").Value);

            if (!rows.Any(r => r.Cells.Any(c => c.Text == "Наименование конфликта" || c.Text == "Clash Name")))
            {
                warnings.Add($"«{Path.GetFileName(path)}»: не найдена таблица коллизий (колонка «Наименование конфликта»). Нужен отчёт Clash Detective в формате HTML (табличный).");
                return new List<ClashItem>();
            }
            string folder = Path.GetDirectoryName(path);

            // В объединённом отчёте проверок несколько: у каждой своя сводка (testName) и своя строка заголовков
            var columns = new List<(int side, string name)>();
            var items = new List<ClashItem>();
            foreach (var r in rows)
            {
                var testCell = r.Cells.FirstOrDefault(c => c.Class.Contains("testName"));
                if (testCell != null) test = testCell.Text;
                if (r.Cells.Any(c => c.Text == "Наименование конфликта" || c.Text == "Clash Name"))
                {
                    columns = r.Cells.Select(c => (side: c.Class.Contains("item1") ? 1 : c.Class.Contains("item2") ? 2 : 0, name: c.Text)).ToList();
                    continue;
                }
                // Строки коллизий; строка группы (clashGroupRow) — заголовок группы, её коллизии идут ниже отдельными строками
                bool clashRow = r.Class.Contains("contentRow") || r.Class.Contains("childRow");
                if (!clashRow || r.Cells.Count != columns.Count) continue;
                var item = new ClashItem { Source = path, Test = test, UnitToFeet = unit };
                for (int i = 0; i < columns.Count; i++)
                {
                    string value = r.Cells[i].Text;
                    var (side, name) = columns[i];
                    if (side == 0)
                    {
                        switch (name)
                        {
                            case "Наименование конфликта": case "Clash Name": item.Name = value; break;
                            case "Статус": case "Status": item.NavisStatus = value; break;
                            case "Группа конфликтов": case "Clash Group": item.Group = value; break;
                            case "Дата обнаружения": case "Date Found": item.Date = value; break;
                            case "Расстояние": case "Distance": item.Distance = value; break;
                            case "Описание": case "Description": item.Description = value; break;
                            case "Точка конфликта": case "Clash Point": item.Point = ParsePoint(value); break;
                            case "Изображение": case "Image":
                                string src = ImgSrc.Match(r.Cells[i].Raw).Groups["s"].Value;
                                if (src.Length > 0)
                                {
                                    string full = Path.Combine(folder, Uri.UnescapeDataString(src).Replace('/', '\\'));
                                    if (File.Exists(full)) item.ImagePath = full;
                                }
                                break;
                        }
                    }
                    else
                    {
                        Fill(side == 1 ? item.Item1 : item.Item2, name, value);
                    }
                }
                if (string.IsNullOrEmpty(item.Name)) continue;
                item.Key = $"{test}|{item.Name}";
                items.Add(item);
            }
            return items;
        }

        private static void Fill(ClashSide side, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            string n = name.ToLowerInvariant();
            if (n == "объект id" || n == "element id" || n == "элемент id" || n == "element id (revit)")
            {
                if (long.TryParse(Regex.Match(value, @"-?\d+").Value, out long id)) side.Id = id;
            }
            else if (n == "идентификатор элемента" || n == "item id")
            {
                if (long.TryParse(Regex.Match(value, @"\d+").Value, out long itemId)) side.ItemId = itemId;
            }
            else if (n.Contains("файл источника") || n.Contains("source file")) { if (side.File.Length == 0) side.File = value; }
            else if (n == "объект категория" || n == "element category") side.Category = value;
            else if (n == "объект семейство" || n == "element family") side.Family = value;
            else if (n == "объект тип" || n == "element type") side.Type = value;
            else if (n == "объект имя" || n == "element name") side.Name = value;
            else if (n == "элемент имя" || n == "item name") side.ItemName = value;
            else if (n == "объект имя системы" || n == "element system name") side.System = value;
            else if (n == "объект рабочий набор" || n == "element workset") side.Workset = value;
            else if (n == "элемент слой" || n == "item layer") side.Layer = value;
        }

        private static string Text(string html)
        {
            string s = Regex.Replace(html, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"<[^>]+>", "");
            s = WebUtility.HtmlDecode(s);
            return Regex.Replace(s, @"\s+", " ").Trim();
        }

        private static double[] ParsePoint(string text)
        {
            // «X:292876.284, Y:243181.947, Z:2.233» (дробная часть — точка или запятая)
            var m = Regex.Match(text, @"X\s*:\s*(?<x>-?\d+(?:[.,]\d+)?)\s*[,;]?\s*Y\s*:\s*(?<y>-?\d+(?:[.,]\d+)?)\s*[,;]?\s*Z\s*:\s*(?<z>-?\d+(?:[.,]\d+)?)", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            return new[] { Num(m.Groups["x"].Value), Num(m.Groups["y"].Value), Num(m.Groups["z"].Value) };
        }

        private static double Num(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

        private static double Attr(XElement e, string name) =>
            double.TryParse((string)e?.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

        /// <summary>Множитель единиц отчёта в футы.</summary>
        private static double UnitFactor(string unit)
        {
            switch ((unit ?? "").Trim().ToLowerInvariant())
            {
                case "ft": return 1;
                case "in": return 1 / 12.0;
                case "mm": return 1 / 304.8;
                case "cm": return 1 / 30.48;
                default: return 1 / 0.3048; // m
            }
        }

        // ===================== XML =====================

        private static List<ClashItem> ReadXml(string path, List<string> warnings)
        {
            XDocument xml = XDocument.Load(path);
            double unit = UnitFactor((string)xml.Root?.Attribute("units"));
            var results = xml.Descendants().Where(e => e.Name.LocalName == "clashresult").ToList();
            if (results.Count > 0) return ReadClashXml(path, results, unit);
            var views = xml.Descendants().Where(e => e.Name.LocalName == "view").ToList();
            if (views.Count > 0) return ReadViewpoints(path, views, unit);
            warnings.Add($"«{Path.GetFileName(path)}»: в XML нет ни коллизий (clashresult), ни точек обзора (view).");
            return new List<ClashItem>();
        }

        private static List<ClashItem> ReadClashXml(string path, List<XElement> results, double unit)
        {
            var items = new List<ClashItem>();
            foreach (XElement r in results)
            {
                string test = (string)r.Ancestors().FirstOrDefault(a => a.Name.LocalName == "clashtest")?.Attribute("name") ?? Path.GetFileNameWithoutExtension(path);
                var item = new ClashItem
                {
                    Source = path,
                    Test = test,
                    Group = (string)r.Ancestors().FirstOrDefault(a => a.Name.LocalName == "clashgroup")?.Attribute("name") ?? "",
                    Name = (string)r.Attribute("name") ?? "",
                    NavisStatus = Child(r, "resultstatus") ?? (string)r.Attribute("status") ?? "",
                    Distance = (string)r.Attribute("distance") ?? "",
                    Description = Child(r, "description") ?? "",
                    UnitToFeet = unit,
                };
                XElement pos = r.Elements().FirstOrDefault(e => e.Name.LocalName == "clashpoint")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "pos3f");
                if (pos != null) item.Point = new[] { Attr(pos, "x"), Attr(pos, "y"), Attr(pos, "z") };
                XElement date = r.Elements().FirstOrDefault(e => e.Name.LocalName == "createddate")?.Elements().FirstOrDefault();
                if (date != null) item.Date = $"{(string)date.Attribute("year")}/{(string)date.Attribute("month")}/{(string)date.Attribute("day")} {(string)date.Attribute("hour")}:{((string)date.Attribute("minute") ?? "").PadLeft(2, '0')}";
                var objects = r.Descendants().Where(e => e.Name.LocalName == "clashobject").ToList();
                for (int i = 0; i < objects.Count && i < 2; i++) ReadClashObject(objects[i], i == 0 ? item.Item1 : item.Item2);
                string guid = (string)r.Attribute("guid");
                item.Key = !string.IsNullOrEmpty(guid) ? guid : $"{test}|{item.Name}";
                items.Add(item);
            }
            return items;
        }

        private static void ReadClashObject(XElement o, ClashSide side)
        {
            foreach (XElement a in o.Descendants().Where(e => e.Name.LocalName == "objectattribute" || e.Name.LocalName == "smarttag"))
            {
                string name = Child(a, "name") ?? "";
                string value = Child(a, "value") ?? "";
                if (name == "ID объекта" || name == "Item ID") { if (long.TryParse(Regex.Match(value, @"\d+").Value, out long itemId)) side.ItemId = itemId; continue; }
                Fill(side, name, value);
            }
            if (side.Layer.Length == 0) side.Layer = Child(o, "layer") ?? "";
            if (side.File.Length == 0)
            {
                var nodes = o.Descendants().Where(e => e.Name.LocalName == "node").Select(e => e.Value).ToList();
                side.File = nodes.FirstOrDefault(n => n.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".nwc", StringComparison.OrdinalIgnoreCase)) ?? "";
            }
        }

        private static string Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value?.Trim();

        private static List<ClashItem> ReadViewpoints(string path, List<XElement> views, double unit)
        {
            var items = new List<ClashItem>();
            foreach (XElement v in views)
            {
                XElement camera = v.Descendants().FirstOrDefault(e => e.Name.LocalName == "camera");
                XElement pos = camera?.Descendants().FirstOrDefault(e => e.Name.LocalName == "position")?.Descendants().FirstOrDefault(e => e.Name.LocalName == "pos3f");
                XElement q = camera?.Descendants().FirstOrDefault(e => e.Name.LocalName == "quaternion");
                if (pos == null || q == null) continue;
                // Кватернион Navisworks (a, b, c, d) = (x, y, z, w); камера смотрит вдоль −Z, вверх — +Y
                double[] quat = { Attr(q, "a"), Attr(q, "b"), Attr(q, "c"), Attr(q, "d") };
                var item = new ClashItem
                {
                    Source = path,
                    Test = string.Join(" / ", v.Ancestors().Where(a => a.Name.LocalName == "viewfolder").Reverse().Select(a => (string)a.Attribute("name"))),
                    Name = (string)v.Attribute("name") ?? "",
                    UnitToFeet = unit,
                    CameraEye = new[] { Attr(pos, "x"), Attr(pos, "y"), Attr(pos, "z") },
                    CameraForward = Rotate(quat, new[] { 0.0, 0, -1 }),
                    CameraUp = Rotate(quat, new[] { 0.0, 1, 0 }),
                    CameraFocal = Attr(v.Descendants().FirstOrDefault(e => e.Name.LocalName == "viewpoint"), "focal"),
                };
                if (string.IsNullOrEmpty(item.Test)) item.Test = Path.GetFileNameWithoutExtension(path);
                // Горизонтальные секущие плоскости: нормаль ±Z, остаётся сторона n·p ≥ d
                    XElement clipSet = v.Descendants().FirstOrDefault(e => e.Name.LocalName == "clipplaneset");
                bool clipOn = clipSet != null && (string)clipSet.Attribute("enabled") != "0" && (string)clipSet.Attribute("mode") != "box";
                foreach (XElement clip in v.Descendants().Where(e => clipOn && e.Name.LocalName == "clipplane" && (string)e.Attribute("state") == "enabled"))
                {
                    XElement plane = clip.Elements().FirstOrDefault(e => e.Name.LocalName == "plane");
                    XElement normal = plane?.Elements().FirstOrDefault(e => e.Name.LocalName == "vec3f");
                    if (normal == null) continue;
                    double nz = Attr(normal, "z"), d = Attr(plane, "distance");
                    if (nz < -0.99) item.ClipTop = -d;
                    else if (nz > 0.99) item.ClipBottom = d;
                }
                item.Point = new[]
                {
                    item.CameraEye[0] + item.CameraForward[0] * Math.Max(item.CameraFocal, 1),
                    item.CameraEye[1] + item.CameraForward[1] * Math.Max(item.CameraFocal, 1),
                    item.CameraEye[2] + item.CameraForward[2] * Math.Max(item.CameraFocal, 1),
                };
                string guid = (string)v.Attribute("guid");
                item.Key = !string.IsNullOrEmpty(guid) ? guid : $"{item.Test}|{item.Name}|{items.Count}";
                items.Add(item);
            }
            return items;
        }

        private static double[] Rotate(double[] q, double[] v)
        {
            double x = q[0], y = q[1], z = q[2], w = q[3];
            double ix = w * v[0] + y * v[2] - z * v[1];
            double iy = w * v[1] + z * v[0] - x * v[2];
            double iz = w * v[2] + x * v[1] - y * v[0];
            double iw = -x * v[0] - y * v[1] - z * v[2];
            return new[]
            {
                ix * w + iw * -x + iy * -z - iz * -y,
                iy * w + iw * -y + iz * -x - ix * -z,
                iz * w + iw * -z + ix * -y - iy * -x,
            };
        }
    }
}
