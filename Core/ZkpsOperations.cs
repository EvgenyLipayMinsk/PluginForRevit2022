using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Group = Autodesk.Revit.DB.Group;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Зоны контроля пожарной сигнализации (ЗКПС) с плана в параметр «ARBM_ЗКПС» устройств линий.
    /// Зоны нарисованы группами узлов (имя содержит «ЗКСПС»/«ЗКПС»): линии границ и подписи «ЗКСПС_3.21».
    /// Границы растеризуются в сетку (шаг ~150 мм, линии утолщаются, чтобы закрыть мелкие разрывы),
    /// от каждой подписи область заливается до границ; устройство на уровне вида группы относится к зоне,
    /// в чью область попадает его точка. Номер — число из подписи целиком (ЗКСПС_3.21 → 3.21).
    /// </summary>
    internal static class ZkpsOperations
    {
        private const string Param = "ARBM_ЗКПС";

        public static Dictionary<string, object> Assign(Document doc, string groupContains, double cellMm, bool dryRun, string commentsContains = null)
        {
            string pattern = string.IsNullOrWhiteSpace(groupContains) ? "ЗК" : groupContains.Trim();
            double cell = (cellMm <= 0 ? 150 : cellMm) / 304.8;
            List<Group> groups = new FilteredElementCollector(doc).OfClass(typeof(Group)).Cast<Group>()
                .Where(g => g.OwnerViewId != ElementId.InvalidElementId &&
                            (g.GroupType?.Name ?? g.Name).IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0 &&
                            Regex.IsMatch(g.GroupType?.Name ?? g.Name, "ЗК", RegexOptions.IgnoreCase))
                .ToList();
            if (groups.Count == 0) throw new InvalidOperationException($"Не найдено групп узлов с «{pattern}» в имени.");

            List<FamilyInstance> devices;
            if (!string.IsNullOrWhiteSpace(commentsContains))
            {
                // Устройства любой категории с нужным текстом в «Комментариях», кроме приборов (электрооборудование)
                // и трасс (семейства кабеля, «Оборудование») — у трасс тоже бывает «АПС» в комментариях.
                string needle = commentsContains.Trim();
                long electrical = (long)BuiltInCategory.OST_ElectricalEquipment, routes = (long)BuiltInCategory.OST_MechanicalEquipment;
                devices = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                    .Where(d => d.Category != null && RevitIds.IdOf(d.Category.Id) != electrical && RevitIds.IdOf(d.Category.Id) != routes &&
                                d.Location is LocationPoint && d.LookupParameter(Param) != null &&
                                (d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString() ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
            }
            else
            {
                MarkingSettings settings = MarkingSettings.Load();
                devices = new FilteredElementCollector(doc)
                    .WherePasses(new ElementMulticategoryFilter(new List<BuiltInCategory> { BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_DataDevices }))
                    .WhereElementIsNotElementType().OfType<FamilyInstance>()
                    .Where(d => settings.IsInLine(d.Symbol.FamilyName, "aps") && d.Location is LocationPoint)
                    .ToList();
            }

            var changes = new List<(FamilyInstance device, string value, string zone)>();
            var report = new List<Dictionary<string, object>>();
            var warnings = new List<string>();
            var assigned = new HashSet<long>();

            foreach (Group group in groups)
            {
                View view = doc.GetElement(group.OwnerViewId) as View;
                ElementId levelId = (view as ViewPlan)?.GenLevel?.Id ?? ElementId.InvalidElementId;
                var segments = new List<(XYZ a, XYZ b)>();
                var labels = new List<(string text, XYZ point)>();
                CollectMembers(doc, group, segments, labels);
                List<(string text, XYZ point)> zones = labels.Where(l => Regex.IsMatch(l.text, @"ЗК\w*[_\s]*[\d.]+", RegexOptions.IgnoreCase)).ToList();
                if (segments.Count == 0 || zones.Count == 0)
                {
                    warnings.Add($"группа «{group.Name}»: линий {segments.Count}, подписей зон {zones.Count} — пропущена");
                    continue;
                }

                // Сетка по охвату границ
                double minX = segments.Min(s => Math.Min(s.a.X, s.b.X)) - 2 * cell, minY = segments.Min(s => Math.Min(s.a.Y, s.b.Y)) - 2 * cell;
                double maxX = segments.Max(s => Math.Max(s.a.X, s.b.X)) + 2 * cell, maxY = segments.Max(s => Math.Max(s.a.Y, s.b.Y)) + 2 * cell;
                int w = (int)Math.Ceiling((maxX - minX) / cell) + 1, h = (int)Math.Ceiling((maxY - minY) / cell) + 1;
                if ((long)w * h > 20_000_000) throw new InvalidOperationException($"группа «{group.Name}»: слишком большая сетка, увеличьте cellMm.");
                var wall = new bool[w, h];
                (int x, int y) Cell(XYZ p) => ((int)Math.Floor((p.X - minX) / cell), (int)Math.Floor((p.Y - minY) / cell));
                foreach (var (a, b) in segments)
                {
                    double length = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
                    int steps = Math.Max(1, (int)Math.Ceiling(length / (cell / 3)));
                    for (int i = 0; i <= steps; i++)
                    {
                        var (cx, cy) = Cell(a + (b - a) * ((double)i / steps));
                        for (int dx = -1; dx <= 1; dx++)      // утолщение: закрывает разрывы до ~1,5 клетки
                            for (int dy = -1; dy <= 1; dy++)
                                if (cx + dx >= 0 && cy + dy >= 0 && cx + dx < w && cy + dy < h) wall[cx + dx, cy + dy] = true;
                    }
                }

                // Заливка от каждой подписи
                var owner = new int[w, h];
                var zoneInfo = new List<Dictionary<string, object>>();
                for (int z = 0; z < zones.Count; z++)
                {
                    var (sx, sy) = Cell(zones[z].point);
                    (int x, int y)? seed = NearestFree(wall, owner, sx, sy, w, h);
                    int filled = 0;
                    bool leaked = false;
                    var claimed = new List<(int x, int y)>();
                    if (seed != null)
                    {
                        var queue = new Queue<(int x, int y)>();
                        queue.Enqueue(seed.Value);
                        owner[seed.Value.x, seed.Value.y] = z + 1;
                        while (queue.Count > 0)
                        {
                            var (x, y) = queue.Dequeue();
                            claimed.Add((x, y));
                            filled++;
                            if (x == 0 || y == 0 || x == w - 1 || y == h - 1) leaked = true;
                            foreach (var (nx, ny) in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
                            {
                                if (nx < 0 || ny < 0 || nx >= w || ny >= h || wall[nx, ny] || owner[nx, ny] != 0) continue;
                                owner[nx, ny] = z + 1;
                                queue.Enqueue((nx, ny));
                            }
                        }
                    }
                    if (leaked)
                    {
                        foreach (var (x, y) in claimed) owner[x, y] = 0; // вне плана — освободить для соседних подписей
                    }
                    zoneInfo.Add(new Dictionary<string, object>
                    {
                        ["label"] = zones[z].text,
                        ["value"] = ValueOf(zones[z].text),
                        ["areaM2"] = Math.Round(filled * cell * cell * 0.092903, 1),
                        ["open"] = leaked || seed == null,
                    });
                    if (leaked) warnings.Add($"зона «{zones[z].text}» ({group.Name}): граница не замкнута — область вытекла за пределы плана, зона не используется");
                }

                // Устройства уровня
                var counts = new int[zones.Count];
                foreach (FamilyInstance device in devices.Where(d => levelId == ElementId.InvalidElementId || d.LevelId == levelId))
                {
                    var (cx, cy) = Cell(((LocationPoint)device.Location).Point);
                    int z = ZoneAt(owner, cx, cy, w, h);
                    if (z <= 0 || (bool)zoneInfo[z - 1]["open"]) continue;
                    long id = RevitIds.IdOf(device.Id);
                    if (!assigned.Add(id)) continue;
                    changes.Add((device, (string)zoneInfo[z - 1]["value"], zones[z - 1].text));
                    counts[z - 1]++;
                }
                for (int z = 0; z < zones.Count; z++) zoneInfo[z]["devices"] = counts[z];
                report.Add(new Dictionary<string, object>
                {
                    ["group"] = group.Name,
                    ["view"] = view?.Name,
                    ["lines"] = segments.Count,
                    ["zones"] = zoneInfo,
                });
            }

            int changed = 0;
            var failures = new List<string>();
            if (!dryRun && changes.Count > 0)
            {
                using (var transaction = new Transaction(doc, "AerBIM: ЗКПС устройств"))
                {
                    transaction.Start();
                    foreach (var (device, value, _) in changes)
                    {
                        if (device.LookupParameter(Param)?.AsString() == value) continue;
                        string error = RevitText.TrySet(device, Param, value);
                        if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}: {error}");
                        else changed++;
                    }
                    if (failures.Count > 0) transaction.RollBack();
                    else transaction.Commit();
                }
            }

            int outside = devices.Count(d => !assigned.Contains(RevitIds.IdOf(d.Id)) && groups.Any(g => (doc.GetElement(g.OwnerViewId) as ViewPlan)?.GenLevel?.Id == d.LevelId));
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["groups"] = report,
                ["devicesInZones"] = changes.Count,
                ["devicesOutsideZonesOnTheseLevels"] = outside,
                ["changed"] = changed,
                ["failures"] = failures.Take(50).ToList(),
                ["warnings"] = warnings,
                ["summary"] = dryRun
                    ? $"Предпросмотр: устройств в зонах ЗКПС: {changes.Count}, вне зон на этих уровнях: {outside}. Для записи повторите с dryRun=false."
                    : failures.Count > 0 ? $"ЗКПС не записаны: ошибок {failures.Count}." : $"Записано ARBM_ЗКПС: {changed}. Отменить можно через Ctrl+Z.",
            };
        }

        /// <summary>Номер зоны из подписи: «ЗКСПС_3.21» → «3.21».</summary>
        private static string ValueOf(string label)
        {
            MatchCollection numbers = Regex.Matches(label, @"\d+(?:[.,]\d+)*");
            return numbers.Count == 0 ? "" : numbers[numbers.Count - 1].Value.Replace(',', '.');
        }

        private static void CollectMembers(Document doc, Group group, List<(XYZ, XYZ)> segments, List<(string, XYZ)> labels)
        {
            foreach (ElementId id in group.GetMemberIds())
            {
                Element member = doc.GetElement(id);
                switch (member)
                {
                    case Group nested:
                        CollectMembers(doc, nested, segments, labels);
                        break;
                    case CurveElement curveElement when curveElement.GeometryCurve != null:
                        IList<XYZ> points = curveElement.GeometryCurve.Tessellate();
                        for (int i = 1; i < points.Count; i++) segments.Add((points[i - 1], points[i]));
                        break;
                    case TextNote note:
                        labels.Add((note.Text.Trim(), note.Coord));
                        break;
                    case FilledRegion region:
                        foreach (CurveLoop loop in region.GetBoundaries())
                            foreach (Curve c in loop)
                            {
                                IList<XYZ> pts = c.Tessellate();
                                for (int i = 1; i < pts.Count; i++) segments.Add((pts[i - 1], pts[i]));
                            }
                        break;
                    case IndependentTag tag:
                        labels.Add((tag.TagText ?? "", tag.TagHeadPosition));
                        break;
                }
            }
        }

        /// <summary>Ближайшая к подписи свободная клетка (подпись может стоять на линии).</summary>
        private static (int x, int y)? NearestFree(bool[,] wall, int[,] owner, int sx, int sy, int w, int h)
        {
            for (int r = 0; r <= 10; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int x = sx + dx, y = sy + dy;
                        if (x >= 0 && y >= 0 && x < w && y < h && !wall[x, y] && owner[x, y] == 0) return (x, y);
                    }
            return null;
        }

        /// <summary>Зона клетки; если устройство стоит на линии границы — ближайшая зона в радиусе 2 клеток.</summary>
        private static int ZoneAt(int[,] owner, int cx, int cy, int w, int h)
        {
            for (int r = 0; r <= 2; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x >= 0 && y >= 0 && x < w && y < h && owner[x, y] > 0) return owner[x, y];
                    }
            return 0;
        }
    }
}
