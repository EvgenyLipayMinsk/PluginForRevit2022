using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class PlaceViewRequest
    {
        public string View { get; set; }

        /// <summary>Существующий лист (id или номер). Если не задан — создаётся новый.</summary>
        public long? SheetId { get; set; }
        public string SheetNumber { get; set; }

        /// <summary>Параметры нового листа (номер, имя, параметры). Имя по умолчанию — имя вида.</summary>
        public SheetSpec NewSheet { get; set; }

        /// <summary>Конкретная основная надпись (id или «Семейство : Тип»).</summary>
        public string TitleBlock { get; set; }

        /// <summary>Семейство основных надписей, среди типов которого подбирается формат.</summary>
        public string TitleBlockFamily { get; set; }

        /// <summary>Подбирать формат листа под размер вида (меняет тип основной надписи).</summary>
        public bool FitFormat { get; set; } = true;

        /// <summary>Новый масштаб вида (1:N) перед размещением; null — не менять.</summary>
        public int? Scale { get; set; }

        /// <summary>Поля рамки по ГОСТ 2.301: слева 20, остальные 5 мм.</summary>
        public double MarginLeftMm { get; set; } = 20;
        public double MarginTopMm { get; set; } = 5;
        public double MarginRightMm { get; set; } = 5;
        public double MarginBottomMm { get; set; } = 5;

        /// <summary>Высота полосы под основной надписью внизу (ГОСТ 2.104, форма 3 — 55 мм); 0 — не резервировать.</summary>
        public double StampHeightMm { get; set; } = 55;

        /// <summary>Если вид уже на другом листе — разместить его копию (с детализацией).</summary>
        public bool DuplicateIfPlaced { get; set; }

        public bool DryRun { get; set; } = true;
    }

    /// <summary>Размещение вида на листе с подбором формата листа. Без UI.</summary>
    internal static class PlacementOperations
    {
        private static readonly int[] StandardScales = { 1, 2, 5, 10, 20, 25, 50, 75, 100, 150, 200, 250, 300, 400, 500, 1000, 2000, 5000 };

        public static Dictionary<string, object> PlaceView(Document doc, PlaceViewRequest request)
        {
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Размещение на листах доступно только в проекте.");
            }
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }

            View view = ViewOperations.ResolveView(doc, request.View, ViewType.FloorPlan);
            if (view is ViewSchedule || view.ViewType == ViewType.DrawingSheet)
            {
                throw new ArgumentException("Спецификации и листы так не размещаются: укажите графический вид (план, разрез и т. п.).");
            }

            ViewSheet existingSheet = SheetOperations.ResolveSheet(doc, request.SheetId, request.SheetNumber);
            if (existingSheet != null && request.NewSheet != null)
            {
                throw new ArgumentException("Укажите либо существующий лист (sheetId/sheetNumber), либо newSheet.");
            }

            var warnings = new List<string>();
            var result = new Dictionary<string, object> { ["dryRun"] = request.DryRun };

            using (var transaction = new Transaction(doc, $"AerBIM: разместить «{view.Name}» на листе"))
            {
                transaction.Start();

                // 1. Вид уже на листе?
                Viewport placedAt = new FilteredElementCollector(doc).OfClass(typeof(Viewport)).Cast<Viewport>()
                    .FirstOrDefault(vp => vp.ViewId == view.Id);
                if (placedAt != null && view.ViewType != ViewType.Legend)
                {
                    var placedSheet = (ViewSheet)doc.GetElement(placedAt.SheetId);
                    if (!request.DuplicateIfPlaced)
                    {
                        throw new InvalidOperationException(
                            $"Вид «{view.Name}» уже размещён на листе {placedSheet.SheetNumber} «{placedSheet.Name}». " +
                            "Разрешите duplicateIfPlaced, чтобы разместить копию.");
                    }
                    var copy = (View)doc.GetElement(view.Duplicate(ViewDuplicateOption.WithDetailing));
                    warnings.Add($"вид уже на листе {placedSheet.SheetNumber}: размещается копия «{copy.Name}»");
                    result["duplicatedFrom"] = view.Name;
                    view = copy;
                }

                // 2. Масштаб
                if (request.Scale != null && request.Scale.Value != view.Scale)
                {
                    Parameter scaleParameter = view.get_Parameter(BuiltInParameter.VIEW_SCALE_PULLDOWN_METRIC) ?? view.get_Parameter(BuiltInParameter.VIEW_SCALE);
                    if (scaleParameter != null && scaleParameter.IsReadOnly)
                    {
                        warnings.Add("масштаб задан шаблоном вида и не изменён");
                    }
                    else
                    {
                        view.Scale = request.Scale.Value;
                    }
                }
                if (!view.CropBoxActive && ViewOperations.SupportsCrop(view))
                {
                    warnings.Add("у вида не включена подрезка: размер на листе определяется всеми элементами — лучше сначала crop_view");
                }

                // 3. Размер вида на листе (пробное размещение)
                (double width, double height) = MeasureViewport(doc, view);
                result["viewportSizeMm"] = RevitText.Size(width, height);

                // 4. Выбор основной надписи
                List<FamilySymbol> candidates = Candidates(doc, request, existingSheet, warnings);
                TitleBlockInfo chosen = null;
                bool fits = false;
                if (candidates.Count > 0)
                {
                    List<TitleBlockInfo> measured = SheetOperations.MeasureInOpenTransaction(doc, candidates)
                        .Where(c => c.WidthMm != null && c.HeightMm != null).ToList();
                    List<TitleBlockInfo> fitting = measured.Where(c => Fits(c, width, height, request)).ToList();
                    if (fitting.Count > 0)
                    {
                        chosen = fitting.OrderBy(c => c.WidthMm * c.HeightMm).First();
                        fits = true;
                    }
                    else if (measured.Count > 0)
                    {
                        chosen = measured.OrderByDescending(c => c.WidthMm * c.HeightMm).First();
                        double factor = Math.Max(width / UsableWidth(chosen, request), height / UsableHeight(chosen, request));
                        int suggested = StandardScales.FirstOrDefault(s => s >= view.Scale * factor);
                        warnings.Add($"вид не помещается даже на самый большой формат ({chosen.Type}). " +
                                     $"Уменьшите подрезку или задайте масштаб 1:{(suggested > 0 ? suggested : (int)Math.Ceiling(view.Scale * factor))} (scale).");
                        result["suggestedScale"] = suggested > 0 ? suggested : (int?)null;
                    }

                    if (measured.Count > 1 && measured.Select(m => $"{m.WidthMm}x{m.HeightMm}").Distinct().Count() == 1)
                    {
                        warnings.Add("все типы основной надписи одного размера: вероятно, формат задаётся параметрами экземпляра — подгонка по типам невозможна");
                    }
                }

                // 5. Лист
                ViewSheet sheet = existingSheet;
                bool titleBlockChanged = false;
                if (sheet == null)
                {
                    SheetSpec spec = request.NewSheet ?? new SheetSpec();
                    if (string.IsNullOrWhiteSpace(spec.Name))
                    {
                        spec.Name = view.Name;
                    }
                    FamilySymbol symbol = chosen == null ? null : (FamilySymbol)doc.GetElement(RevitIds.ToElementId(chosen.Id));
                    if (symbol != null && !symbol.IsActive)
                    {
                        symbol.Activate();
                    }
                    sheet = SheetOperations.CreateOne(doc, symbol, spec, out List<string> sheetWarnings);
                    warnings.AddRange(sheetWarnings);
                    result["sheetCreated"] = true;
                }
                else
                {
                    FamilyInstance titleBlock = SheetOperations.TitleBlockOf(doc, sheet);
                    if (titleBlock == null)
                    {
                        warnings.Add("на листе нет основной надписи: вид размещён в центре листа без подбора формата");
                    }
                    else if (chosen != null && request.FitFormat && RevitIds.IdOf(titleBlock.Symbol.Id) != chosen.Id)
                    {
                        titleBlock.ChangeTypeId(RevitIds.ToElementId(chosen.Id));
                        titleBlockChanged = true;
                    }
                    if (sheet.GetAllPlacedViews().Count > 0)
                    {
                        warnings.Add("на листе уже есть виды: новый вид ставится в центр рабочего поля и может их перекрыть");
                    }
                    result["sheetCreated"] = false;
                }

                // 6. Размещение по центру рабочего поля
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    throw new InvalidOperationException($"Revit не разрешает разместить «{view.Name}» на листе {sheet.SheetNumber}.");
                }
                Viewport viewport = Viewport.Create(doc, sheet.Id, view.Id, XYZ.Zero);
                doc.Regenerate();

                FamilyInstance sheetTitleBlock = SheetOperations.TitleBlockOf(doc, sheet);
                BoundingBoxXYZ frame = sheetTitleBlock?.get_BoundingBox(sheet);
                if (frame != null)
                {
                    double minX = frame.Min.X + RevitText.ToFeet(request.MarginLeftMm);
                    double maxX = frame.Max.X - RevitText.ToFeet(request.MarginRightMm);
                    double minY = frame.Min.Y + RevitText.ToFeet(request.MarginBottomMm + request.StampHeightMm);
                    double maxY = frame.Max.Y - RevitText.ToFeet(request.MarginTopMm);
                    viewport.SetBoxCenter(new XYZ((minX + maxX) / 2, (minY + maxY) / 2, 0));
                    result["sheetSizeMm"] = RevitText.Size(RevitText.ToMm(frame.Max.X - frame.Min.X), RevitText.ToMm(frame.Max.Y - frame.Min.Y));
                    result["usableAreaMm"] = RevitText.Size(RevitText.ToMm(maxX - minX), RevitText.ToMm(maxY - minY));
                }

                FamilyInstance finalTitleBlock = SheetOperations.TitleBlockOf(doc, sheet);
                result["view"] = view.Name;
                result["viewId"] = RevitIds.IdOf(view.Id);
                result["scale"] = view.Scale;
                result["sheet"] = new Dictionary<string, object>
                {
                    ["id"] = request.DryRun ? (object)null : RevitIds.IdOf(sheet.Id),
                    ["number"] = sheet.SheetNumber,
                    ["name"] = sheet.Name,
                };
                result["titleBlock"] = finalTitleBlock == null ? null : $"{finalTitleBlock.Symbol.FamilyName} : {finalTitleBlock.Symbol.Name}";
                result["titleBlockChanged"] = titleBlockChanged;
                result["fits"] = fits;
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
                    result["summary"] = $"Вид «{view.Name}» размещён на листе {sheet.SheetNumber}. Отменить можно в Revit через Ctrl+Z.";
                }
            }
            return result;
        }

        private static (double width, double height) MeasureViewport(Document doc, View view)
        {
            using (var sub = new SubTransaction(doc))
            {
                sub.Start();
                ViewSheet probe = ViewSheet.Create(doc, ElementId.InvalidElementId);
                Viewport viewport = Viewport.Create(doc, probe.Id, view.Id, XYZ.Zero);
                doc.Regenerate();
                Outline outline = viewport.GetBoxOutline();
                double width = RevitText.ToMm(outline.MaximumPoint.X - outline.MinimumPoint.X);
                double height = RevitText.ToMm(outline.MaximumPoint.Y - outline.MinimumPoint.Y);
                sub.RollBack();
                return (width, height);
            }
        }

        private static List<FamilySymbol> Candidates(Document doc, PlaceViewRequest request, ViewSheet existingSheet, List<string> warnings)
        {
            List<FamilySymbol> all = SheetOperations.TitleBlockSymbols(doc);
            if (all.Count == 0)
            {
                warnings.Add("в проекте нет основных надписей: лист без рамки");
                return all;
            }

            FamilySymbol explicitSymbol = string.IsNullOrWhiteSpace(request.TitleBlock) ? null : SheetOperations.ResolveTitleBlock(doc, request.TitleBlock);
            if (explicitSymbol != null && !request.FitFormat)
            {
                return new List<FamilySymbol> { explicitSymbol };
            }

            string family = request.TitleBlockFamily?.Trim();
            if (string.IsNullOrEmpty(family))
            {
                family = explicitSymbol?.FamilyName ?? (existingSheet == null ? null : SheetOperations.TitleBlockOf(doc, existingSheet)?.Symbol.FamilyName);
            }
            if (string.IsNullOrEmpty(family))
            {
                List<string> families = all.Select(s => s.FamilyName).Distinct().ToList();
                if (families.Count > 1)
                {
                    throw new ArgumentException(
                        $"В проекте несколько семейств основных надписей: {string.Join("; ", families)}. Укажите titleBlockFamily или titleBlock.");
                }
                family = families[0];
            }

            List<FamilySymbol> inFamily = all.Where(s => string.Equals(s.FamilyName, family, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (inFamily.Count == 0)
            {
                throw new ArgumentException($"Семейство основных надписей «{family}» не найдено. Список: list_title_blocks.");
            }
            return inFamily;
        }

        private static double UsableWidth(TitleBlockInfo c, PlaceViewRequest r) => c.WidthMm.Value - r.MarginLeftMm - r.MarginRightMm;

        private static double UsableHeight(TitleBlockInfo c, PlaceViewRequest r) => c.HeightMm.Value - r.MarginTopMm - r.MarginBottomMm - r.StampHeightMm;

        private static bool Fits(TitleBlockInfo c, double width, double height, PlaceViewRequest r) =>
            UsableWidth(c, r) >= width && UsableHeight(c, r) >= height;
    }
}
