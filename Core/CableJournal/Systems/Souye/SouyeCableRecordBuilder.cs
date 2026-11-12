using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Services;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace ARBM.CableJournalExporter.Systems.Souye
{
    /// <summary>
    /// Сводная информация по одному шлейфу СОУЭ для отображения в итоговом окне команды.
    /// Формируется в <see cref="SouyeCableRecordBuilder"/> и передаётся в команду
    /// через <see cref="SouyeBuildResult.LoopSummaries"/>.
    /// </summary>
    public class SouyeLoopSummary
    {
        /// <summary>Номер шлейфа.</summary>
        public int LoopAddress { get; set; }

        /// <summary>Количество оповещателей в шлейфе.</summary>
        public int DeviceCount { get; set; }

        /// <summary>Суммарная мощность оповещателей шлейфа, Вт.</summary>
        public double TotalPower { get; set; }

        /// <summary>Суммарная протяжённость шлейфа, м.</summary>
        public double TotalLength { get; set; }

        /// <summary>Расчётное сечение кабеля, мм².</summary>
        public double CalculatedSection { get; set; }

        /// <summary>
        /// Подобранный кабель из справочника <see cref="SouyeCableCatalog"/>.
        /// Используется для заполнения колонок «Марка кабеля» и «Категория» в итоговой таблице.
        /// </summary>
        public SouyeCableSpec CableSpec { get; set; }
    }

    /// <summary>
    /// Результат построения записей кабельного журнала СОУЭ.
    /// Содержит либо список готовых записей со сводкой по шлейфам,
    /// либо сообщение об ошибке (превышение допустимого сечения кабеля).
    /// </summary>
    public class SouyeBuildResult
    {
        /// <summary>
        /// Список записей кабельного журнала.
        /// Равен <c>null</c>, если построение завершилось с ошибкой.
        /// </summary>
        public List<CableRecord> Records { get; private set; }

        /// <summary>
        /// Сводная информация по каждому шлейфу: мощность, длина, расчётное сечение, кабель.
        /// Используется в итоговом диалоговом окне команды.
        /// Равен <c>null</c>, если построение завершилось с ошибкой.
        /// </summary>
        public List<SouyeLoopSummary> LoopSummaries { get; private set; }

        /// <summary>
        /// Сообщение об ошибке.
        /// Равно <c>null</c>, если построение выполнено успешно.
        /// </summary>
        public string ErrorMessage { get; private set; }

        /// <summary>Признак успешного завершения построения.</summary>
        public bool IsSuccess => ErrorMessage == null;

        private SouyeBuildResult() { }

        /// <summary>Создаёт успешный результат со списком записей и сводкой по шлейфам.</summary>
        public static SouyeBuildResult Success(List<CableRecord> records, List<SouyeLoopSummary> loopSummaries)
            => new SouyeBuildResult { Records = records, LoopSummaries = loopSummaries };

        /// <summary>Создаёт результат с ошибкой превышения сечения.</summary>
        public static SouyeBuildResult Failure(string errorMessage)
            => new SouyeBuildResult { ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Строит список записей кабельного журнала (<see cref="CableRecord"/>) из доменной
    /// модели СОУЭ (<see cref="SouyeControllerWithLoops"/>).
    /// <para>
    /// Алгоритм для каждого шлейфа:
    /// <list type="number">
    ///   <item>Рассчитываются длины всех сегментов шлейфа через <see cref="LoggedRouteLengthCalculator"/>.</item>
    ///   <item>По суммарной длине и мощности рассчитывается сечение кабеля по формуле:
    ///     S = (ρ × 2L × P) / (U × U_пад).</item>
    ///   <item>Если сечение превышает 2,5 мм² — команда завершается с сообщением.</item>
    ///   <item>По сечению подбирается кабель из <see cref="SouyeCableCatalog"/>.</item>
    ///   <item>Формируются записи КЖ для каждого сегмента шлейфа.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SouyeCableRecordBuilder
    {
        private const string PurposeValue = "Шлейф СОУЭ";

        private readonly LoggedRouteLengthCalculator _routeCalculator; // logged wrapper
        private readonly Document _doc;
        private readonly LengthMode _mode;
        private readonly double _correctionFactor;
        private CancellationMonitor _currentCancel;

        /// <summary>
        /// Вспомогательный класс для хранения результата расчёта длины сегмента.
        /// Содержит саму длину и флаг, указывающий, была ли использована fallback-логика.
        /// </summary>
        private class SegmentLengthResult
        {
            /// <summary>Длина сегмента в метрах.</summary>
            public double LengthMeters { get; set; }

            /// <summary>
            /// Флаг, указывающий, что длина была рассчитана с использованием fallback-метода
            /// (прямое расстояние вместо маршрута по трассам).
            /// </summary>
            public bool IsFallbackForLength { get; set; }
        }

        /// <summary>
        /// Инициализирует новый экземпляр <see cref="SouyeCableRecordBuilder"/>.
        /// </summary>
        /// <param name="doc">Активный документ Revit (требуется калькулятору длин).</param>
        /// <param name="mode">Режим расчёта длины (Direct или Route).</param>
        /// <param name="correctionFactor">Коэффициент корректировки, по умолчанию 1.</param>
        public SouyeCableRecordBuilder(Document doc, LengthMode mode, double correctionFactor = 1)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _mode = mode;
            _correctionFactor = correctionFactor;

            // Use the logged wrapper so that BuildGraphsByLevel and route computations are profiled.
            _routeCalculator = new LoggedRouteLengthCalculator();
        }

        /// <summary>
        /// Строит список записей кабельного журнала для всех шлейфов СОУЭ.
        /// Поддерживает cooperative cancellation через опциональный <paramref name="cancel"/>.
        /// </summary>
        /// <param name="data">Доменная модель, прошедшая валидацию в <see cref="SouyeDataValidator"/>.</param>
        /// <param name="doc">Активный документ Revit (требуется калькулятору длин).</param>
        /// <param name="cancel">Опциональный монитор отмены для поддержки cooperative cancellation.</param>
        /// <returns>
        /// <see cref="SouyeBuildResult.Success"/> со списком записей, или
        /// <see cref="SouyeBuildResult.Failure"/> с сообщением о превышении сечения.
        /// </returns>
        public SouyeBuildResult Build(SouyeControllerWithLoops data, string param = "", CancellationMonitor cancel = null)
        {
            var records = new List<CableRecord>();
            var loopSummaries = new List<SouyeLoopSummary>();

            // Save current monitor for inner calls
            _currentCancel = cancel;

            // Строим графы по уровням один раз
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel");
            if (cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException("Cancelled before building graphs.");

            // Delegate graph construction (uses LoggedRouteLengthCalculator which supports a cancellation-aware API)
            _routeCalculator.BuildGraphsByLevel(_doc, data, param, cancel);

            // Граф не построен (ошибка уровней, переходов или трасс — окно показано): журнал не строится.
            if (_routeCalculator.BuildError != null)
                return SouyeBuildResult.Failure($"Граф трасс не построен: {_routeCalculator.BuildError}. Журнал не сформирован.");

            cancel?.ClearCurrentOperation();

            foreach (var loopGroup in data.LoopGroups)
            {
                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled during building records.");

                var loopResult = BuildLoopRecords(data.Controller, loopGroup, _doc, cancel);
                if (!loopResult.IsSuccess)
                    return SouyeBuildResult.Failure(loopResult.ErrorMessage);

                records.AddRange(loopResult.Records);

                // Собираем сводку по шлейфу
                var loopSummary = loopResult.LoopSummaries != null && loopResult.LoopSummaries.Count > 0
                    ? loopResult.LoopSummaries[0]
                    : null;

                loopSummaries.Add(new SouyeLoopSummary
                {
                    LoopAddress = loopGroup.LoopAddress,
                    DeviceCount = loopGroup.Devices.Count,
                    TotalPower = loopGroup.TotalPower,
                    TotalLength = loopSummary?.TotalLength ?? 0,
                    CalculatedSection = loopSummary?.CalculatedSection ?? 0,
                    CableSpec = loopSummary?.CableSpec
                });
            }

            return SouyeBuildResult.Success(records, loopSummaries);
        }

        // ─── Построение записей одного шлейфа ────────────────────────────────────

        /// <summary>
        /// Строит записи кабельного журнала для одного шлейфа.
        /// <para>
        /// Длины всех сегментов рассчитываются до формирования записей,
        /// поскольку суммарная длина шлейфа нужна для расчёта сечения кабеля.
        /// </para>
        /// </summary>
        private SouyeBuildResult BuildLoopRecords(
            SouyeControllerInfo controller,
            SouyeLoopGroup loopGroup,
            Document doc,
            CancellationMonitor cancel = null)
        {
            var devices = loopGroup.Devices;

            // ── Шаг 1: рассчитать длины всех сегментов шлейфа ────────────────────
            // Сегмент 0: контроллер → первое устройство
            // Сегменты 1..N-1: устройство[i] → устройство[i+1]
            var segmentLengths = new List<SegmentLengthResult>();

            if (cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException("Cancelled during length calculation.");

            var controllerToFirst = CalculateLengthSmart(controller.Element, devices[0].Element, doc, cancel);
            segmentLengths.Add(controllerToFirst);

            for (int i = 0; i < devices.Count - 1; i++)
            {
                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled during length calculation.");

                var segmentLength = CalculateLengthSmart(devices[i].Element, devices[i + 1].Element, doc, cancel);
                segmentLengths.Add(segmentLength);
            }

            // ── Шаг 2: рассчитать суммарную длину шлейфа ─────────────────────────
            double totalLength = 0.0;
            foreach (var segment in segmentLengths)
                totalLength += segment.LengthMeters;

            // Запас на линию добавляется к каждой строке журнала уже после подбора (ApplyLineReserve),
            // поэтому сечение подбирается по длине вместе с этим запасом — той, что попадёт в журнал.
            totalLength += PluginForRevit2022.Core.MarkingSettings.LineReserveM() * segmentLengths.Count;

            // ── Шаг 3: рассчитать требуемое сечение кабеля ───────────────────────
            double calculatedSection = SouyeCableCatalog.CalculateCrossSection(
                loopGroup.TotalPower,
                totalLength);

            // ── Шаг 4: проверить не превышение допустимого сечения ───────────────
            if (calculatedSection > SouyeCableCatalog.MaxAllowedCrossSection)
            {
                return SouyeBuildResult.Failure(
                    $"Шлейф {loopGroup.LoopAddress}: суммарная мощность {loopGroup.TotalPower:F1} Вт, " +
                    $"протяжённость {totalLength:F1} м, " +
                    $"расчётное сечение {calculatedSection:F3} мм² " +
                    $"превышает максимально допустимое " +
                    $"{SouyeCableCatalog.MaxAllowedCrossSection} мм².\n" +
                    "Необходимо разделить шлейф или пересмотреть схему подключения оповещателей.");
            }

            // ── Шаг 5: подобрать кабель из справочника ───────────────────────────
            var cableSpec = SouyeCableCatalog.GetByCrossSection(calculatedSection);

            // ── Шаг 6: сформировать записи КЖ ────────────────────────────────────
            var records = new List<CableRecord>();

            // Сегмент 0: контроллер → первое устройство шлейфа
            records.Add(new CableRecord
            {
                StartLevel    = controller.Floor,
                StartPoint    = FormatControllerPoint(controller),
                EndLevel      = devices[0].Floor,
                EndPoint      = FormatDevicePoint(devices[0]),
                CableType     = cableSpec.CableType,
                CableCategory = cableSpec.CableCategory,
                Purpose       = PurposeValue,
                Length        = FormatLength(segmentLengths[0].LengthMeters),
                IsFallbackForLength = segmentLengths[0].IsFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            });

            // Сегменты 1..N-1: между соседними устройствами шлейфа
            for (int i = 0; i < devices.Count - 1; i++)
            {
                records.Add(new CableRecord
                {
                    StartLevel    = devices[i].Floor,
                    StartPoint    = FormatDevicePoint(devices[i]),
                    EndLevel      = devices[i + 1].Floor,
                    EndPoint      = FormatDevicePoint(devices[i + 1]),
                    CableType     = cableSpec.CableType,
                    CableCategory = cableSpec.CableCategory,
                    Purpose       = PurposeValue,
                    Length        = FormatLength(segmentLengths[i + 1].LengthMeters),
                    IsFallbackForLength = segmentLengths[i + 1].IsFallbackForLength,
                    ColumnMap = new ExcelColumnMap()
                });
            }

            // ── Создаём сводку по шлейфу ──────────────────────────────────────────
            var loopSummary = new SouyeLoopSummary
            {
                LoopAddress = loopGroup.LoopAddress,
                DeviceCount = devices.Count,
                TotalPower = loopGroup.TotalPower,
                TotalLength = totalLength,
                CalculatedSection = calculatedSection,
                CableSpec = cableSpec
            };

            // Возвращаем результат с записями И сводкой
            return SouyeBuildResult.Success(records, new List<SouyeLoopSummary> { loopSummary });
        }

        // ─── Форматирование точек подключения ────────────────────────────────────

        /// <summary>
        /// Формирует текстовое обозначение точки подключения для контроллера.
        /// </summary>
        private static string FormatControllerPoint(SouyeControllerInfo controller)
        {
            return !string.IsNullOrWhiteSpace(controller.HousingBlock)
                ? $"{controller.HousingBlock}.{controller.ProductName}"
                : controller.ProductName;
        }

        /// <summary>
        /// Формирует текстовое обозначение точки подключения для оповещателя.
        /// </summary>
        private static string FormatDevicePoint(SouyeDeviceInfo device)
        {
            return !string.IsNullOrWhiteSpace(device.HousingBlock)
                ? $"{device.HousingBlock}.{device.Bco}.{device.LoopAddressRaw}.{device.ElementMarkRaw}"
                : $"{device.Bco}.{device.LoopAddressRaw}.{device.ElementMarkRaw}";
        }

        // ─── Расчёт длины ─────────────────────────────────────────────────────────

        /// <summary>
        /// Рассчитывает прямое расстояние между двумя элементами Revit по их центроидам.
        /// </summary>
        /// <returns>Расстояние в метрах.</returns>
        private double ComputeLength(Element from, Element to)
        {
            var p1 = (from.Location as LocationPoint)?.Point;
            var p2 = (to.Location as LocationPoint)?.Point;

            if (p1 == null || p2 == null)
                return 0;

            return p1.DistanceTo(p2) * 0.3048; // Преобразуем из футов в метры
        }

        /// <summary>
        /// Рассчитывает длину кабеля между двумя элементами с поддержкой различных режимов расчёта.
        /// <para>
        /// Реализует логику на основе режима <see cref="LengthMode"/>:
        /// - <c>Direct</c>: использует прямое расстояние между центроидами элементов.
        /// - <c>Route</c>: использует маршрут по трассам; при невозможности расчёта маршрута
        ///   переходит на прямое расстояние с установкой флага <c>IsFallbackForLength</c>.
        /// </para>
        /// Поддерживает cooperative cancellation через <paramref name="cancel"/>.
        /// Длина округляется в большую сторону до целого значения и умножается на коэффициент корректировки.
        /// </summary>
        /// <returns><see cref="SegmentLengthResult"/> с длиной в метрах и флагом использования fallback.</returns>
        private SegmentLengthResult CalculateLengthSmart(Element from, Element to, Document doc, CancellationMonitor cancel = null)
        {
            if (cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException("Cancelled before CalculateLengthSmart.");

            var result = new SegmentLengthResult { IsFallbackForLength = false };

            if (_mode == LengthMode.Direct)
            {
                result.LengthMeters = Math.Ceiling(ComputeLength(from, to) * _correctionFactor);
                return result;
            }

            if (_mode == LengthMode.Route)
            {
                // Вызываем расчёт маршрута через logged wrapper с поддержкой отмены
                double lengthFeet = _routeCalculator.ComputeShortestRouteBetweenDevices(from, to, "", _currentCancel);

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after route computation.");

                // Если маршрут не найден (бесконечность), используем fallback-логику
                if (double.IsInfinity(lengthFeet))
                {
                    result.IsFallbackForLength = true;
                    result.LengthMeters = Math.Ceiling(ComputeLength(from, to) * _correctionFactor);
                }
                else
                {
                    // Преобразуем из футов в метры, умножаем на коэффициент корректировки и округляем в большую сторону
                    result.LengthMeters = Math.Ceiling(lengthFeet * 0.3048 * _correctionFactor);
                    // Путь найден только через «мост» между разрывами трасс — длина не по трассе.
                    if (_routeCalculator.LastRouteBridged) result.IsFallbackForLength = true;
                }
            }

            return result;
        }

        /// <summary>
        /// Преобразует длину кабеля в метрах в строку с одним знаком после запятой.
        /// Пример: <c>12.5</c> → <c>"12,5"</c>.
        /// </summary>
        private static string FormatLength(double lengthMeters)
        {
            return lengthMeters.ToString("F1",
                System.Globalization.CultureInfo.CurrentCulture);
        }
    }
}
