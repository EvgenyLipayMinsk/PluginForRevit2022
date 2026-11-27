using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ComboBox = System.Windows.Controls.ComboBox;

namespace PluginForRevit2022.Core.Clashes
{
    /// <summary>
    /// Показ коллизии в Revit: свой 3D-вид «AerBIM — Коллизии», секущий куб вокруг коллизии, выделение элементов
    /// текущего проекта. Элементы ищутся по ID в файле, имя которого указано в отчёте (проект или связь).
    /// Координаты точки конфликта сверяются с геометрией найденных элементов.
    /// Для сохранённых камер без элементов система координат оценивается по габариту модели.
    /// </summary>
    internal static class ClashRevit
    {
        /// <summary>Вид на пользователя: при совместной работе общий вид был бы занят тем, кто менял его последним.</summary>
        private static string ViewName(Document doc) =>
            "AerBIM — Коллизии" + (doc.IsWorkshared ? " — " + doc.Application.Username : "");
        private const double HalfBoxM = 1.5; // куб 3 × 3 × 3 м вокруг коллизии

        private sealed class Found
        {
            public Element Element;
            public Transform Transform; // из документа элемента в документ проекта
            public bool InHost;
        }

        public static string Show(UIApplication app, ClashItem item, IReadOnlyList<ClashItem> report, CoordinateSession coordinates,
            out ViewFocus focus)
        {
            focus = null;
            UIDocument ui = app.ActiveUIDocument;
            Document doc = ui?.Document;
            if (doc == null || doc.IsFamilyDocument) return "Откройте проект.";
            ElementIdJournal.FlushAll();

            var sources = Candidates(doc);
            var found = new List<Found>();
            var notFound = new List<string>();
            var notes = new List<string>();
            var missing = new List<ClashSide>();
            foreach (ClashSide side in new[] { item.Item1, item.Item2 })
            {
                if (side.Id == null && side.ItemId == null) continue;
                Found f = Find(sources, side) ?? FindReplaced(doc, side, null, notes);
                if (f != null) found.Add(f);
                else missing.Add(side);
            }

            // Габарит найденных элементов в координатах проекта
            BoundingBoxXYZ elements = null;
            foreach (Found f in found) elements = Union(elements, BoxOf(f));

            // Точка коллизии в координатах проекта
            XYZ point = null;
            Transform toProject = Transform.Identity;
            if (item.Point != null)
            {
                XYZ raw = ReportPoint(item);
                if (item.IsViewpoint)
                {
                    toProject = ChooseViewpoint(doc, raw, elements);
                    point = toProject.OfPoint(raw);
                }
                else
                {
                    var anchors = Evidence(found);
                    if (anchors.Count == 0)
                    {
                        string ids = string.Join(", ", missing.Select(Describe));
                        return "Координаты не подтверждены. Геометрия элементов коллизии не найдена" +
                            (ids.Length > 0 ? $" ({ids}). Проверьте исходные модели и загрузку связей." : ". Проверьте состав отчёта.");
                    }

                    CoordinateOption option = coordinates.Cached(doc, item.Source);
                    bool confirmedNow = option == null || !Fits(option, raw, anchors);
                    if (confirmedNow)
                    {
                        coordinates.Forget(doc, item.Source);
                        var options = CoordinateOptions(doc).Where(candidate => Fits(candidate, raw, anchors)).ToList();
                        double variantSpacing = 10 / 304.8;
                        var evidence = report.Where(other => !ReferenceEquals(other, item) && !other.IsViewpoint && other.Point != null &&
                            string.Equals(other.Source, item.Source, StringComparison.OrdinalIgnoreCase))
                            .Where(other => ReportPoint(other).DistanceTo(raw) > variantSpacing);
                        foreach (ClashItem other in evidence.Take(12))
                        {
                            if (options.Count <= 1) break;
                            var otherAnchors = Evidence(new[] { Find(sources, other.Item1), Find(sources, other.Item2) }.Where(f => f != null));
                            if (otherAnchors.Count == 0) continue;
                            XYZ otherPoint = ReportPoint(other);
                            var supported = options.Where(candidate => Fits(candidate, otherPoint, otherAnchors)).ToList();
                            if (supported.Count > 0) options = supported;
                        }

                        if (options.Count != 1)
                        {
                            focus = ShowFound(app, ui, doc, found, elements);
                            if (options.Count == 0)
                                return "Координаты отчёта не подтверждены геометрией. Показаны найденные элементы без секущего куба. Проверьте координаты экспорта Navisworks.";
                            option = ConfirmCoordinates(app.MainWindowHandle, options, raw);
                            if (option == null)
                                return "Координаты не подтверждены. Показаны найденные элементы без секущего куба.";
                        }
                        else option = options[0];

                        coordinates.Save(doc, item.Source, option);
                        PluginLog.Info("clash.coordinates", $"{item.Name} | {option.Label} | опорных элементов: {anchors.Count}");
                        notes.Add($"координаты подтверждены ({option.Label})");
                    }
                    point = option.Transform.OfPoint(raw);
                }

            }

            // ID из отчёта нет: сначала журнал замен, иначе (лоток соединили и разделили до установки плагина, правил коллега)
            // элемент того же файла и категории у точки коллизии
            foreach (ClashSide side in missing)
            {
                Found near = FindReplaced(doc, side, point, notes) ?? (point == null ? null : FindNear(doc, side, point, found, notes));
                if (near != null) found.Add(near);
                else notFound.Add(Describe(side));
            }

            double half = HalfBoxM / 0.3048;
            XYZ min, max;
            if (point != null)
            {
                min = point - new XYZ(half, half, half);
                max = point + new XYZ(half, half, half);
                if (item.IsViewpoint)
                {
                    // Точка обзора: куб вокруг точки перед камерой, по высоте — секущие плоскости Navisworks
                    double size = Math.Max(half, item.CameraFocal * item.UnitToFeet);
                    min = point - new XYZ(size, size, size);
                    max = point + new XYZ(size, size, size);
                    // Отметки секущих плоскостей переводятся тем же преобразованием (поворот общих координат — только в плане)
                    double dz = toProject.OfPoint(XYZ.Zero).Z;
                    if (item.ClipBottom != null) min = new XYZ(min.X, min.Y, item.ClipBottom.Value * item.UnitToFeet + dz);
                    if (item.ClipTop != null) max = new XYZ(max.X, max.Y, item.ClipTop.Value * item.UnitToFeet + dz);
                    if (max.Z - min.Z < 0.5) max = new XYZ(max.X, max.Y, min.Z + 2 * half);
                }
            }
            else if (elements != null)
            {
                XYZ pad = new XYZ(0.5 / 0.3048, 0.5 / 0.3048, 0.5 / 0.3048);
                min = elements.Min - pad;
                max = elements.Max + pad;
            }
            else
            {
                return "В отчёте нет точки коллизии, а элементы по ID не найдены" + (notFound.Count > 0 ? $": {string.Join(", ", notFound)}." : ".");
            }

            View3D view = PrepareView(doc);
            using (var t = new Transaction(doc, "AerBIM: показать коллизию"))
            {
                t.Start();
                view.SetSectionBox(new BoundingBoxXYZ { Min = min, Max = max });
                view.IsSectionBoxActive = true;
                if (item.IsViewpoint && point != null)
                {
                    XYZ forward = toProject.OfVector(new XYZ(item.CameraForward[0], item.CameraForward[1], item.CameraForward[2])).Normalize();
                    XYZ up = toProject.OfVector(new XYZ(item.CameraUp[0], item.CameraUp[1], item.CameraUp[2]));
                    up = (up - forward * up.DotProduct(forward)).Normalize(); // строго перпендикулярно направлению
                    XYZ eye = toProject.OfPoint(new XYZ(item.CameraEye[0], item.CameraEye[1], item.CameraEye[2]) * item.UnitToFeet);
                    view.SetOrientation(new ViewOrientation3D(eye, up, forward));
                }
                t.Commit();
            }

            if (ui.ActiveView.Id != view.Id) ui.ActiveView = view;
            focus = new ViewFocus(view, min, max, HostIds(found));

            var parts = new List<string>(notes);
            if (found.Count > 0) parts.Add($"найдено элементов: {found.Count}" + (found.Any(f => !f.InHost) ? " (в связях — не выделяются)" : ""));
            if (notFound.Count > 0) parts.Add($"не найдены: {string.Join(", ", notFound)}");
            if (point == null && item.Point == null && !item.IsViewpoint) parts.Add("точки коллизии в отчёте нет — показан габарит элементов");
            return parts.Count == 0 ? "Показано." : string.Join("; ", parts) + ".";
        }

        private static List<ElementId> HostIds(IEnumerable<Found> found) =>
            found.Where(element => element.InHost).Select(element => element.Element.Id).Distinct().ToList();

        internal sealed class ViewFocus
        {
            private readonly Document _document;
            private readonly ElementId _viewId;
            private readonly XYZ _min, _max;
            private readonly List<ElementId> _hostIds;

            public ViewFocus(View3D view, XYZ min, XYZ max, List<ElementId> hostIds)
            {
                _document = view.Document;
                _viewId = view.Id;
                _min = min;
                _max = max;
                _hostIds = hostIds;
            }

            public void Apply(UIApplication app)
            {
                UIDocument ui = app.ActiveUIDocument;
                if (ui?.ActiveView == null || !ui.Document.Equals(_document) || ui.ActiveView.Id != _viewId) return;

                UIView uiView = ui.GetOpenUIViews().FirstOrDefault(view => view.ViewId == _viewId);
                uiView?.ZoomAndCenterRectangle(_min, _max);
                List<ElementId> ids = _hostIds.Where(id => ui.Document.GetElement(id) != null).ToList();
                ui.Selection.SetElementIds(ids);
                ui.RefreshActiveView();

                if (ids.Count > 0)
                    PluginLog.Info("clash.selection", string.Join(", ", ui.Selection.GetElementIds().Select(RevitIds.IdOf)));
            }
        }

        private static string Describe(ClashSide side) =>
            $"ID {side.Id ?? side.ItemId}" + (string.IsNullOrEmpty(side.File) ? "" : $" ({side.File})");

        private static XYZ ReportPoint(ClashItem item) =>
            new XYZ(item.Point[0], item.Point[1], item.Point[2]) * item.UnitToFeet;

        /// <summary>Документы с геометрией, доступной в текущей модели.</summary>
        private static List<(Document doc, Transform transform, bool host)> Candidates(Document doc)
        {
            var candidates = new List<(Document doc, Transform transform, bool host)> { (doc, Transform.Identity, true) };
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Document linked = link.GetLinkDocument();
                if (linked != null) candidates.Add((linked, link.GetTotalTransform(), false));
            }
            return candidates;
        }

        private static Found Find(List<(Document doc, Transform transform, bool host)> sources, ClashSide side)
        {
            if (side.Id == null && side.ItemId == null) return null;
            string file = Normalize(side.File);
            var candidates = file.Length == 0 ? sources : sources.Where(source => ModelNames(source.doc).Contains(file)).ToList();
            if (file.Length > 0 && candidates.Count == 0)
                candidates = sources.Where(source => SameFile(source.doc, file)).ToList();

            foreach (long? id in new[] { side.Id, side.ItemId }.Where(value => value != null).Distinct())
            {
                var matches = new List<Found>();
                foreach (var source in candidates)
                {
                    Element element = source.doc.GetElement(RevitIds.ToElementId(id.Value));
                    if (element?.Category == null || element is ElementType || element is RevitLinkInstance) continue;
                    matches.Add(new Found { Element = element, Transform = source.transform, InHost = source.host });
                }

                var byCategory = matches.Where(found => string.Equals(found.Element.Category.Name, side.Category,
                    StringComparison.CurrentCultureIgnoreCase)).ToList();
                if (byCategory.Count == 1) return byCategory[0];
                if (matches.Count == 1 && (file.Length > 0 || string.IsNullOrEmpty(side.Category))) return matches[0];
            }

            return null;
        }

        /// <summary>Журнал замен: лоток соединили и снова разделили — сегмент, занявший его место
        /// (из нескольких — ближайший к точке коллизии).</summary>
        private static Found FindReplaced(Document doc, ClashSide side, XYZ point, List<string> notes)
        {
            if (side.Id == null) return null;
            List<long> ids = ElementIdJournal.Resolve(side.File, side.Id.Value);
            if (ids.Count == 0) return null;
            string file = Normalize(side.File);
            var hits = new List<Found>();
            foreach (var c in Candidates(doc).Where(x => ModelNames(x.doc).Contains(file)))
                foreach (long id in ids)
                {
                    Element e = c.doc.GetElement(RevitIds.ToElementId(id));
                    if (e != null && !(e is ElementType)) hits.Add(new Found { Element = e, Transform = c.transform, InHost = c.host });
                }
            Found best = point == null ? (hits.Count == 1 ? hits[0] : null)
                : hits.OrderBy(f => DistanceTo(f.Element, f.Transform.Inverse.OfPoint(point))).FirstOrDefault();
            if (best != null)
                notes.Add($"ID {side.Id} сменился после соединения/разделения — показан ID {RevitIds.IdOf(best.Element.Id)}"
                          + (ids.Count > 1 ? $" (замены: {string.Join(", ", ids)})" : ""));
            return best;
        }

        /// <summary>Элемент у точки коллизии (в пределах 50 мм) в файле из отчёта: той же категории, а если язык
        /// категорий отчёта и Revit разный — ближайшая сеть или фитинг.</summary>
        private static Found FindNear(Document doc, ClashSide side, XYZ point, List<Found> taken, List<string> notes)
        {
            string file = Normalize(side.File);
            if (file.Length == 0) return null; // без файла рядом слишком много чужих элементов — не угадываем
            double tol = 50 / 304.8;
            // Только точное имя файла: по геометрии «АР» подставила бы соседний элемент модели «АР_2»
            foreach (var c in Candidates(doc).Where(x => ModelNames(x.doc).Contains(file)))
            {
                XYZ local = c.transform.Inverse.OfPoint(point);
                var box = new Outline(local - new XYZ(tol, tol, tol), local + new XYZ(tol, tol, tol));
                List<Element> hits = new FilteredElementCollector(c.doc).WhereElementIsNotElementType()
                    .WherePasses(new BoundingBoxIntersectsFilter(box))
                    .Where(e => e.Category != null && !(e is RevitLinkInstance)
                                && !taken.Any(f => f.Element.Id == e.Id && f.Element.Document.Equals(c.doc)))
                    .ToList();
                bool known = !string.IsNullOrEmpty(side.Category)
                             && c.doc.Settings.Categories.Cast<Category>().Any(k => string.Equals(k.Name, side.Category, StringComparison.CurrentCultureIgnoreCase));
                List<Element> pool = known
                    ? hits.Where(e => string.Equals(e.Category.Name, side.Category, StringComparison.CurrentCultureIgnoreCase)).ToList()
                    : LooksMep(side.Category) ? hits.Where(IsMep).ToList() : new List<Element>();
                Element best = pool.OrderBy(e => DistanceTo(e, local)).FirstOrDefault();
                if (best == null) continue;
                notes.Add($"ID {side.Id ?? side.ItemId} не найден — у точки коллизии найден {best.Category.Name} ID {RevitIds.IdOf(best.Id)}");
                return new Found { Element = best, Transform = c.transform, InHost = c.host };
            }
            return null;
        }

        private static readonly BuiltInCategory[] MepFittings =
        {
            BuiltInCategory.OST_CableTrayFitting, BuiltInCategory.OST_ConduitFitting, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_DuctFitting,
        };

        /// <summary>Категория отчёта (на любом языке) — лоток, короб, труба, воздуховод или их фитинг.</summary>
        private static bool LooksMep(string category)
        {
            string c = (category ?? "").ToLowerInvariant();
            return new[] { "tray", "conduit", "pipe", "duct", "fitting", "лот", "короб", "труб", "воздуховод", "соединительн", "фитинг" }.Any(c.Contains);
        }

        private static bool IsMep(Element e) =>
            e is MEPCurve || MepFittings.Any(c => (long)c == RevitIds.IdOf(e.Category.Id));

        private static double DistanceTo(Element e, XYZ p)
        {
            if (e.Location is LocationCurve lc && lc.Curve != null) return lc.Curve.Distance(p);
            if (e.Location is LocationPoint lp) return lp.Point.DistanceTo(p);
            BoundingBoxXYZ b = e.get_BoundingBox(null);
            return b == null ? double.MaxValue : ((b.Min + b.Max) / 2).DistanceTo(p);
        }

        /// <summary>Имена активной модели для сравнения с «Файл источника» из отчёта: заголовок, путь и файл хранилища.</summary>
        public static List<string> ModelNames(Document doc)
        {
            var names = new List<string>();
            if (doc == null) return names;
            names.Add(Normalize(doc.Title));
            names.Add(Normalize(doc.PathName));
            // Локальная копия: «Модель_Иванов» → «модель»
            string user = "_" + (doc.Application.Username ?? "").Trim().ToLowerInvariant();
            if (user.Length > 1)
                foreach (string n in names.ToList())
                    if (n.EndsWith(user)) names.Add(n.Substring(0, n.Length - user.Length));
            try
            {
                if (doc.IsWorkshared)
                {
                    ModelPath central = doc.GetWorksharingCentralModelPath();
                    if (central != null) names.Add(Normalize(ModelPathUtils.ConvertModelPathToUserVisiblePath(central)));
                }
            }
            catch (Exception) { /* облачная модель или хранилище недоступно — хватит заголовка */ }
            return names.Where(n => n.Length > 0).Distinct().ToList();
        }

        /// <summary>Файл из отчёта — это активная модель. Только точное совпадение имени (без «_отсоединено» и имени пользователя):
        /// по началу имени «АР» совпала бы с «АР_2».</summary>
        public static bool IsModel(string reportFile, IList<string> modelNames)
        {
            string file = Normalize(reportFile);
            return file.Length > 0 && modelNames.Contains(file);
        }

        /// <summary>«Модель_отсоединено.rvt», «Модель_Иванов.rvt», «Модель.nwc» → «модель».</summary>
        internal static string Normalize(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file ?? "").Trim().ToLowerInvariant();
            foreach (string suffix in new[] { "_отсоединено", "_detached" })
                if (name.EndsWith(suffix)) name = name.Substring(0, name.Length - suffix.Length);
            return name;
        }

        private static bool SameFile(Document d, string normalized)
        {
            string title = Normalize(d.Title);
            string path = Normalize(d.PathName);
            return title == normalized || path == normalized || title.StartsWith(normalized + "_") || normalized.StartsWith(title + "_");
        }

        private static BoundingBoxXYZ BoxOf(Found f)
        {
            BoundingBoxXYZ b = f.Element.get_BoundingBox(null);
            if (b == null) return null;
            Transform t = f.Transform.Multiply(b.Transform);
            var corners = new List<XYZ>();
            foreach (double x in new[] { b.Min.X, b.Max.X })
                foreach (double y in new[] { b.Min.Y, b.Max.Y })
                    foreach (double z in new[] { b.Min.Z, b.Max.Z })
                        corners.Add(t.OfPoint(new XYZ(x, y, z)));
            return new BoundingBoxXYZ
            {
                Min = new XYZ(corners.Min(p => p.X), corners.Min(p => p.Y), corners.Min(p => p.Z)),
                Max = new XYZ(corners.Max(p => p.X), corners.Max(p => p.Y), corners.Max(p => p.Z)),
            };
        }

        private static BoundingBoxXYZ Union(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null) return b;
            if (b == null) return a;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(Math.Min(a.Min.X, b.Min.X), Math.Min(a.Min.Y, b.Min.Y), Math.Min(a.Min.Z, b.Min.Z)),
                Max = new XYZ(Math.Max(a.Max.X, b.Max.X), Math.Max(a.Max.Y, b.Max.Y), Math.Max(a.Max.Z, b.Max.Z)),
            };
        }

        /// <summary>Резервное позиционирование сохранённой камеры, у которой может быть только точка обзора.</summary>
        private static Transform ChooseViewpoint(Document doc, XYZ raw, BoundingBoxXYZ elements)
        {
            Transform shared = doc.ActiveProjectLocation.GetTotalTransform();
            var options = new[] { Transform.Identity, shared, shared.Inverse };
            BoundingBoxXYZ reference = elements ?? ModelBox(doc);
            if (reference == null) return shared;
            XYZ center = (reference.Min + reference.Max) / 2;
            return options.OrderBy(o => Distance(o.OfPoint(raw), reference)).ThenBy(o => o.OfPoint(raw).DistanceTo(center)).First();
        }

        internal sealed class CoordinateSession
        {
            private readonly Dictionary<(Document doc, string source), CoordinateOption> _confirmed =
                new Dictionary<(Document doc, string source), CoordinateOption>();

            internal CoordinateOption Cached(Document doc, string source) =>
                _confirmed.TryGetValue((doc, source), out CoordinateOption option) ? option : null;

            internal void Save(Document doc, string source, CoordinateOption option) => _confirmed[(doc, source)] = option;

            internal void Forget(Document doc, string source) => _confirmed.Remove((doc, source));
        }

        internal sealed class CoordinateOption
        {
            public string Label;
            public Transform Transform;
        }

        private static List<CoordinateOption> CoordinateOptions(Document doc)
        {
            var options = new List<CoordinateOption>();
            void Add(string label, Transform transform)
            {
                double tolerance = 1 / 304.8;
                if (options.Any(option => option.Transform.Origin.DistanceTo(transform.Origin) < tolerance &&
                    option.Transform.BasisX.IsAlmostEqualTo(transform.BasisX) &&
                    option.Transform.BasisY.IsAlmostEqualTo(transform.BasisY) &&
                    option.Transform.BasisZ.IsAlmostEqualTo(transform.BasisZ))) return;
                options.Add(new CoordinateOption { Label = label, Transform = transform });
            }

            Transform shared = doc.ActiveProjectLocation.GetTotalTransform();
            Add("внутренние координаты", Transform.Identity);
            Add("общие координаты", shared);
            Add("обратное преобразование общих координат", shared.Inverse);

            XYZ origin = BasePoint.GetProjectBasePoint(doc)?.Position;
            if (origin != null)
                Add("от базовой точки проекта", Transform.CreateTranslation(origin));

            var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>();
            foreach (Level level in levels.OrderBy(level => level.ProjectElevation))
            {
                double shift = level.ProjectElevation - level.Elevation;
                Add($"по отметкам уровней (ΔZ {RevitText.ToMm(shift):+0.#;-0.#;0} мм)",
                    Transform.CreateTranslation(new XYZ(0, 0, shift)));
            }
            return options;
        }

        private static List<(Found found, BoundingBoxXYZ box)> Evidence(IEnumerable<Found> found) =>
            found.Select(element => (found: element, box: BoxOf(element))).Where(anchor => anchor.box != null).ToList();

        private static bool Fits(CoordinateOption option, XYZ raw, List<(Found found, BoundingBoxXYZ box)> anchors)
        {
            // Допуск меньше половины секущего куба. Точка из отчёта может быть смещена относительно оси трубы.
            const double tolerance = 750 / 304.8;
            XYZ point = option.Transform.OfPoint(raw);
            return anchors.Count > 0 && anchors.All(anchor =>
                Distance(point, anchor.box) <= tolerance && NearPipe(point, anchor.found, tolerance));
        }

        private static bool NearPipe(XYZ point, Found found, double tolerance)
        {
            if (found.Element.Location is LocationCurve location &&
                found.Element.get_Parameter(BuiltInParameter.RBS_PIPE_OUTER_DIAMETER) is Parameter diameter &&
                diameter.StorageType == StorageType.Double)
                return location.Curve.Distance(found.Transform.Inverse.OfPoint(point)) <= diameter.AsDouble() / 2 + tolerance;
            return true;
        }

        private static CoordinateOption ConfirmCoordinates(IntPtr owner, IReadOnlyList<CoordinateOption> options, XYZ raw)
        {
            var dialog = new Window
            {
                Title = "Координаты Navisworks", Width = 680, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false,
            };
            PluginForRevit2022.UI.AerBimAppearance.Apply(dialog);
            new WindowInteropHelper(dialog).Owner = owner;
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock
            {
                Text = "Несколько преобразований подходят к найденным элементам. Выберите положение точки коллизии во внутренних координатах Revit.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            });
            var selector = new ComboBox { MaxDropDownHeight = 280, ItemsSource = options.Select(option =>
            {
                XYZ point = option.Transform.OfPoint(raw);
                return $"{option.Label} · X {RevitText.ToMm(point.X):0}, Y {RevitText.ToMm(point.Y):0}, Z {RevitText.ToMm(point.Z):0} мм";
            }).ToList() };
            panel.Children.Add(selector);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0) };
            actions.Children.Add(new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 });
            var confirm = new Button { Content = "Подтвердить", IsDefault = true, IsEnabled = false,
                MinWidth = 105, Margin = new Thickness(8, 0, 0, 0) };
            PluginForRevit2022.UI.AerBimAppearance.Primary(dialog, confirm);
            confirm.Click += (sender, args) => dialog.DialogResult = true;
            selector.SelectionChanged += (sender, args) => confirm.IsEnabled = selector.SelectedIndex >= 0;
            actions.Children.Add(confirm);
            panel.Children.Add(actions);
            dialog.Content = panel;
            return dialog.ShowDialog() == true ? options[selector.SelectedIndex] : null;
        }

        private static ViewFocus ShowFound(UIApplication app, UIDocument ui, Document doc, List<Found> found, BoundingBoxXYZ bounds)
        {
            View3D view = PrepareView(doc);
            if (view.IsSectionBoxActive)
            {
                using (var transaction = new Transaction(doc, "AerBIM: проверить вид коллизии"))
                {
                    transaction.Start();
                    view.IsSectionBoxActive = false;
                    transaction.Commit();
                }
            }
            if (ui.ActiveView.Id != view.Id) ui.ActiveView = view;
            List<ElementId> ids = HostIds(found);
            ui.Selection.SetElementIds(ids);
            if (ids.Count > 0)
            {
                try { ui.ShowElements(ids); }
                catch (Autodesk.Revit.Exceptions.ArgumentException) { /* штатный показ недоступен — используем габарит */ }
                catch (Autodesk.Revit.Exceptions.InvalidOperationException) { /* штатный показ недоступен — используем габарит */ }
            }
            if (ui.ActiveView.Id != view.Id) ui.ActiveView = view;
            XYZ margin = new XYZ(1, 1, 1);
            var focus = new ViewFocus(view, bounds.Min - margin, bounds.Max + margin, ids);
            focus.Apply(app);
            return focus;
        }

        private static double Distance(XYZ p, BoundingBoxXYZ b)
        {
            double dx = Math.Max(0, Math.Max(b.Min.X - p.X, p.X - b.Max.X));
            double dy = Math.Max(0, Math.Max(b.Min.Y - p.Y, p.Y - b.Max.Y));
            double dz = Math.Max(0, Math.Max(b.Min.Z - p.Z, p.Z - b.Max.Z));
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        [ThreadStatic] private static BoundingBoxXYZ _modelBox;
        [ThreadStatic] private static Document _modelBoxDoc;

        /// <summary>Габарит модели: связи и основные конструкции проекта (для точек обзора без ID элементов).</summary>
        private static BoundingBoxXYZ ModelBox(Document doc)
        {
            if (_modelBoxDoc == doc && _modelBox != null) return _modelBox;
            BoundingBoxXYZ box = null;
            foreach (RevitLinkInstance link in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
                box = Union(box, link.get_BoundingBox(null));
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_Walls, BuiltInCategory.OST_Floors, BuiltInCategory.OST_StructuralColumns,
                BuiltInCategory.OST_CableTray, BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_PipeCurves };
            foreach (Element e in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories)).WhereElementIsNotElementType().Take(5000))
                box = Union(box, e.get_BoundingBox(null));
            _modelBoxDoc = doc;
            _modelBox = box;
            return box;
        }

        /// <summary>Свой 3D-вид для коллизий: не меняет рабочие виды пользователя.</summary>
        private static View3D PrepareView(Document doc)
        {
            View3D view = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && v.Name == ViewName(doc));
            if (view != null) return view;
            ViewFamilyType type = new FilteredElementCollector(doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == ViewFamily.ThreeDimensional)
                ?? throw new InvalidOperationException("В проекте нет типа 3D-вида.");
            using (var t = new Transaction(doc, "AerBIM: вид для коллизий"))
            {
                t.Start();
                view = View3D.CreateIsometric(doc, type.Id);
                view.Name = ViewName(doc);
                t.Commit();
            }
            return view;
        }
    }
}
