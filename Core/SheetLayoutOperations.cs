using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class PlaceOnSheetRequest
    {
        /// <summary>Легенда, чертёжный вид, спецификация или другой вид: id или имя.</summary>
        public string Item { get; set; }

        public long? SheetId { get; set; }
        public string SheetNumber { get; set; }

        /// <summary>auto — свободное место (справа сверху вниз); top-right, top-left, bottom-right (над штампом), bottom-left, center; xy — по координатам.</summary>
        public string Position { get; set; } = "auto";

        /// <summary>position=xy: левый верхний угол элемента от левого нижнего угла листа, мм.</summary>
        public double? XMm { get; set; }
        public double? YMm { get; set; }

        /// <summary>Зазор между элементами, мм листа.</summary>
        public double GapMm { get; set; } = 5;

        public double MarginLeftMm { get; set; } = 20;
        public double MarginTopMm { get; set; } = 5;
        public double MarginRightMm { get; set; } = 5;
        public double MarginBottomMm { get; set; } = 5;

        /// <summary>Основная надпись в правом нижнем углу (ГОСТ 2.104, форма 3: 185×55 мм) — считается занятой.</summary>
        public double StampWidthMm { get; set; } = 185;
        public double StampHeightMm { get; set; } = 55;

        /// <summary>Чертёжный вид уже на другом листе — разместить его копию.</summary>
        public bool DuplicateIfPlaced { get; set; }

        public bool DryRun { get; set; } = true;
    }

    /// <summary>
    /// Размещение легенд, чертёжных видов и спецификаций на существующем листе:
    /// поиск свободного места в рабочем поле рамки, привязка к углам или координаты.
    /// </summary>
    internal static class SheetLayoutOperations
    {
        private const double ScanStepMm = 5;

        private struct Rect
        {
            public double MinX, MinY, MaxX, MaxY;

            public Rect(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
            }

            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;

            public bool Intersects(Rect other, double gap) =>
                MinX < other.MaxX + gap && other.MinX < MaxX + gap &&
                MinY < other.MaxY + gap && other.MinY < MaxY + gap;

            public bool Inside(Rect area) =>
                MinX >= area.MinX - 1e-6 && MaxX <= area.MaxX + 1e-6 && MinY >= area.MinY - 1e-6 && MaxY <= area.MaxY + 1e-6;

            public static Rect From(Outline o) => new Rect(o.MinimumPoint.X, o.MinimumPoint.Y, o.MaximumPoint.X, o.MaximumPoint.Y);
            public static Rect From(BoundingBoxXYZ b) => new Rect(b.Min.X, b.Min.Y, b.Max.X, b.Max.Y);
        }

        public static Dictionary<string, object> PlaceOnSheet(Document doc, PlaceOnSheetRequest request)
        {
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Размещение на листах доступно только в проекте.");
            }
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }

            View item = ViewOperations.ResolveView(doc, request.Item);
            if (item.ViewType == ViewType.DrawingSheet)
            {
                throw new ArgumentException("Лист нельзя разместить на листе.");
            }
            ViewSheet sheet = SheetOperations.ResolveSheet(doc, request.SheetId, request.SheetNumber)
                ?? throw new ArgumentException("Укажите лист: sheetId или sheetNumber. Новый лист — create_sheets.");

            var warnings = new List<string>();
            var result = new Dictionary<string, object> { ["dryRun"] = request.DryRun };
            string position = (request.Position ?? "auto").Trim().ToLowerInvariant();

            using (var transaction = new Transaction(doc, $"AerBIM: разместить «{item.Name}» на листе {sheet.SheetNumber}"))
            {
                transaction.Start();

                // 1. Уже размещён?
                List<ViewSheet> placedOn = ViewOperations.SheetsOf(doc, item);
                if (placedOn.Any(s => s.Id == sheet.Id))
                {
                    throw new InvalidOperationException($"«{item.Name}» уже есть на листе {sheet.SheetNumber}.");
                }
                if (placedOn.Count > 0 && !ViewOperations.AllowsManySheets(item))
                {
                    if (!request.DuplicateIfPlaced)
                    {
                        throw new InvalidOperationException(
                            $"«{item.Name}» уже размещён на листе {placedOn[0].SheetNumber}. Разрешите duplicateIfPlaced, чтобы разместить копию.");
                    }
                    var copy = (View)doc.GetElement(item.Duplicate(ViewDuplicateOption.WithDetailing));
                    warnings.Add($"вид уже на листе {placedOn[0].SheetNumber}: размещается копия «{copy.Name}»");
                    result["duplicatedFrom"] = item.Name;
                    item = copy;
                }

                // 2. Рамка, рабочее поле, занятые места (до размещения)
                Rect frame = Frame(doc, sheet, warnings);
                Rect work = new Rect(
                    frame.MinX + RevitText.ToFeet(request.MarginLeftMm),
                    frame.MinY + RevitText.ToFeet(request.MarginBottomMm),
                    frame.MaxX - RevitText.ToFeet(request.MarginRightMm),
                    frame.MaxY - RevitText.ToFeet(request.MarginTopMm));
                List<Rect> occupied = Occupied(doc, sheet);
                Rect stamp = new Rect(work.MaxX - RevitText.ToFeet(request.StampWidthMm), work.MinY,
                                      work.MaxX, work.MinY + RevitText.ToFeet(request.StampHeightMm));
                if (request.StampHeightMm > 0 && request.StampWidthMm > 0)
                {
                    occupied.Add(stamp);
                }

                // 3. Размещение в начале координат и измерение
                Element placed;
                if (item is ViewSchedule schedule)
                {
                    placed = ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, XYZ.Zero);
                }
                else
                {
                    if (!Viewport.CanAddViewToSheet(doc, sheet.Id, item.Id))
                    {
                        throw new InvalidOperationException($"Revit не разрешает разместить «{item.Name}» на листе {sheet.SheetNumber}.");
                    }
                    placed = Viewport.Create(doc, sheet.Id, item.Id, XYZ.Zero);
                }
                doc.Regenerate();
                Rect current = Bounds(placed, sheet);

                // 4. Целевое место
                double gap = RevitText.ToFeet(request.GapMm);
                Rect target = Target(position, current, frame, work, stamp, occupied, gap, request, warnings);
                XYZ delta = new XYZ(target.MinX - current.MinX, target.MinY - current.MinY, 0);
                if (!delta.IsZeroLength())
                {
                    ElementTransformUtils.MoveElement(doc, placed.Id, delta);
                }
                doc.Regenerate();
                Rect final = Bounds(placed, sheet);

                if (!final.Inside(work))
                {
                    warnings.Add("элемент выходит за рабочее поле рамки");
                }
                if (occupied.Any(o => final.Intersects(o, -1e-6)))
                {
                    warnings.Add("элемент перекрывает другие элементы листа или штамп");
                }

                result["item"] = item.Name;
                result["itemType"] = item is ViewSchedule ? "Schedule" : item.ViewType.ToString();
                result["sheet"] = $"{sheet.SheetNumber} - {sheet.Name}";
                result["position"] = position;
                result["sizeMm"] = RevitText.Size(RevitText.ToMm(final.Width), RevitText.ToMm(final.Height));
                result["topLeftMm"] = new Dictionary<string, object>
                {
                    ["x"] = RevitText.ToMm(final.MinX - frame.MinX),
                    ["y"] = RevitText.ToMm(final.MaxY - frame.MinY),
                };
                result["warnings"] = warnings;

                if (request.DryRun)
                {
                    transaction.RollBack();
                    result["summary"] = "Предпросмотр: ничего не изменено. Для размещения повторите с dryRun=false.";
                }
                else
                {
                    if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("Revit не применил размещение.");
                    }
                    result["summary"] = $"«{item.Name}» размещён на листе {sheet.SheetNumber}. Отменить можно в Revit через Ctrl+Z.";
                }
            }
            return result;
        }

        private static Rect Target(string position, Rect item, Rect frame, Rect work, Rect stamp, List<Rect> occupied,
                                   double gap, PlaceOnSheetRequest request, List<string> warnings)
        {
            double w = item.Width;
            double h = item.Height;
            Rect At(double minX, double minY) => new Rect(minX, minY, minX + w, minY + h);

            switch (position)
            {
                case "xy":
                    if (request.XMm == null || request.YMm == null)
                    {
                        throw new ArgumentException("position=xy: укажите xMm и yMm (левый верхний угол от левого нижнего угла листа).");
                    }
                    double left = frame.MinX + RevitText.ToFeet(request.XMm.Value);
                    double top = frame.MinY + RevitText.ToFeet(request.YMm.Value);
                    return At(left, top - h);
                case "top-right":
                    return At(work.MaxX - w, work.MaxY - h);
                case "top-left":
                    return At(work.MinX, work.MaxY - h);
                case "bottom-right":
                    return At(work.MaxX - w, stamp.MaxY + gap);
                case "bottom-left":
                    return At(work.MinX, work.MinY);
                case "center":
                    return At((work.MinX + work.MaxX - w) / 2, (work.MinY + work.MaxY - h) / 2);
                case "auto":
                    double step = RevitText.ToFeet(ScanStepMm);
                    for (double x = work.MaxX - w; x >= work.MinX - 1e-6; x -= step)
                    {
                        for (double y = work.MaxY - h; y >= work.MinY - 1e-6; y -= step)
                        {
                            Rect candidate = At(x, y);
                            if (!occupied.Any(o => candidate.Intersects(o, gap)))
                            {
                                return candidate;
                            }
                        }
                    }
                    warnings.Add("свободного места нужного размера нет: элемент поставлен в центр рабочего поля");
                    return At((work.MinX + work.MaxX - w) / 2, (work.MinY + work.MaxY - h) / 2);
                default:
                    throw new ArgumentException("position: auto, top-right, top-left, bottom-right, bottom-left, center или xy.");
            }
        }

        private static Rect Frame(Document doc, ViewSheet sheet, List<string> warnings)
        {
            FamilyInstance titleBlock = SheetOperations.TitleBlockOf(doc, sheet);
            BoundingBoxXYZ box = titleBlock?.get_BoundingBox(sheet);
            if (box != null)
            {
                return Rect.From(box);
            }
            warnings.Add("на листе нет основной надписи: рабочее поле — границы листа");
            BoundingBoxUV outline = sheet.Outline;
            return new Rect(outline.Min.U, outline.Min.V, outline.Max.U, outline.Max.V);
        }

        private static List<Rect> Occupied(Document doc, ViewSheet sheet)
        {
            var result = new List<Rect>();
            foreach (ElementId id in sheet.GetAllViewports())
            {
                if (doc.GetElement(id) is Viewport viewport)
                {
                    result.Add(Rect.From(viewport.GetBoxOutline()));
                }
            }
            foreach (ScheduleSheetInstance instance in new FilteredElementCollector(doc, sheet.Id)
                         .OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>()
                         .Where(s => !s.IsTitleblockRevisionSchedule))
            {
                BoundingBoxXYZ box = instance.get_BoundingBox(sheet);
                if (box != null)
                {
                    result.Add(Rect.From(box));
                }
            }
            return result;
        }

        private static Rect Bounds(Element placed, ViewSheet sheet)
        {
            if (placed is Viewport viewport)
            {
                return Rect.From(viewport.GetBoxOutline());
            }
            BoundingBoxXYZ box = placed.get_BoundingBox(sheet)
                ?? throw new InvalidOperationException("Не удалось определить размер элемента на листе.");
            return Rect.From(box);
        }
    }
}
