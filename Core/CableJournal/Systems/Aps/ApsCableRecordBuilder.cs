using System;
using System.Collections.Generic;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using Autodesk.Revit.DB;
using static ARBM.CableJournalExporter.Services.RouteLengthCalculator;
using System.Diagnostics;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Builds cable journal records for APS (Automatic Fire Alarm System) based on
    /// structured controller/loop/device data.
    /// <para>RU: Формирует записи кабельного журнала для системы АПС
    /// (Автоматическая пожарная сигнализация) на основе структурированных данных
    /// контроллеров, петель и устройств.</para>
    /// </summary>
    public class ApsCableRecordBuilder
    {
        private readonly ApsElementParser _parser;
        private readonly LengthMode _mode;
        private readonly LoggedRouteLengthCalculator _routeCalc; // logged wrapper
        private readonly Document _doc;
        private readonly double _correctionFactor;

        // Current cancellation monitor for this Build execution (set in Build and used by ComputeLengthSmart)
        private CancellationMonitor _currentCancel;

        /// <summary>
        /// Initializes a new instance of the APS cable record builder.
        /// <para>RU: Инициализирует новый экземпляр построителя записей кабельного журнала АПС.</para>
        /// </summary>
        public ApsCableRecordBuilder(
            ApsElementParser parser,
            Document doc,
            LengthMode mode,
            double correctionFactor = 1)
        {
            _parser = parser ?? new ApsElementParser();
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _mode = mode;

            // Use the logged wrapper so that BuildGraphsByLevel and route computations are profiled.
            _routeCalc = new LoggedRouteLengthCalculator();

            _correctionFactor = correctionFactor;
        }

        /// <summary>
        /// Builds cable records with optional cooperative cancellation.
        /// Pass a CancellationMonitor to support watchdog timeouts.
        /// </summary>
        public List<CableRecord> Build(List<ApsControllerWithLoops> controllers, string param = "", CancellationMonitor cancel = null)
        {
            var result = new List<CableRecord>();

            if (controllers == null || controllers.Count == 0)
                return result;

            // Save current monitor for inner calls (ComputeLengthSmart)
            _currentCancel = cancel;

            // Строим графы по уровням один раз
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel");
            if (cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException("Cancelled before building graphs.");

            // Delegate graph construction (uses LoggedRouteLengthCalculator which supports a cancellation-aware API)
            _routeCalc.BuildGraphsByLevel(_doc, controllers, param, cancel);

            // Граф не построен (ошибка уровней, переходов или трасс — окно показано): журнал не строится.
            if (_routeCalc.BuildError != null) return result;

            cancel?.ClearCurrentOperation();

            foreach (var ctrl in controllers)
            {
                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled during building records.");

                var controllerElement = ctrl.Controller.Element;
                var controllerId = ctrl.ControllerId;

                foreach (var loop in ctrl.Loops)
                {
                    if (cancel?.IsCancellationRequested == true)
                        throw new OperationCanceledException("Cancelled during building records (loops).");

                    if (loop.Devices == null || loop.Devices.Count == 0)
                        continue;

                    // 1. Контроллер → первое устройство
                    var firstDevice = loop.Devices[0];
                    result.Add(CreateCableRecordBetweenControllerAndDevice(
                        ctrl,
                        loop,
                        firstDevice,
                        _correctionFactor));

                    // 2. Между устройствами в петле
                    for (int i = 0; i < loop.Devices.Count - 1; i++)
                    {
                        var d1 = loop.Devices[i];
                        var d2 = loop.Devices[i + 1];

                        result.Add(CreateCableRecordBetweenDevices(
                            loop,
                            d1,
                            d2,
                            _correctionFactor));
                    }

                    // 3. Если петля кольцевая — последнее устройство → контроллер
                    if (loop.TypeLoop == TypeLoop.Annular)
                    {
                        var lastDevice = loop.Devices[loop.Devices.Count - 1];

                        result.Add(CreateCableRecordBetweenLastDeviceAndController(
                            ctrl,
                            loop,
                            lastDevice,
                            _correctionFactor));
                    }
                }
            }

            // Clear monitor reference after work completed
            _currentCancel = null;

            return result;
        }

        // =====================================================================
        // HELPERS — forward the current cancellation monitor into ComputeLengthSmart
        // =====================================================================

        private CableRecord CreateCableRecordBetweenControllerAndDevice(
            ApsControllerWithLoops ctrl,
            ApsLoopGroup loop,
            ApsDeviceInfo device,
            double correctionFactor = 1)
        {
            bool isFallbackForLength;

            var controllerElement = ctrl.Controller.Element;
            var deviceElement = device.BaseInfo.Element;

            string startLevel = ctrl.Controller.FloorNumber.ToString();
            string endLevel = device.FloorNumber.ToString();

            string startPoint = ctrl.ControllerId.ToString();
            string endPoint = device.ToString();

            double lengthMeters = ComputeLengthSmart(
                controllerElement,
                deviceElement,
                null,
                out isFallbackForLength,
                _currentCancel) * correctionFactor;

            return new CableRecord
            {
                StartLevel = startLevel,
                StartPoint = startPoint,
                EndLevel = endLevel,
                EndPoint = endPoint,
                CableType = "КСРЭВнг(А)-FRLS",
                CableCategory = "1х2х1,13",
                Purpose = "Шлейф АПС",
                Length = Math.Ceiling(lengthMeters).ToString("F2"),
                IsFallbackForLength = isFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenDevices(
            ApsLoopGroup loop,
            ApsDeviceInfo d1,
            ApsDeviceInfo d2,
            double correctionFactor = 1)
        {
            bool isFallbackForLength;

            var e1 = d1.BaseInfo.Element;
            var e2 = d2.BaseInfo.Element;

            string startLevel = d1.FloorNumber.ToString();
            string endLevel = d2.FloorNumber.ToString();

            string startPoint = d1.ToString();
            string endPoint = d2.ToString();

            double lengthMeters = ComputeLengthSmart(
                e1,
                e2,
                null,
                out isFallbackForLength,
                _currentCancel) * correctionFactor;

            return new CableRecord
            {
                StartLevel = startLevel,
                StartPoint = startPoint,
                EndLevel = endLevel,
                EndPoint = endPoint,
                CableType = "КСРЭВнг(А)-FRLS",
                CableCategory = "1х2х1,13",
                Purpose = "Шлейф АПС",
                Length = Math.Ceiling(lengthMeters).ToString("F2"),
                IsFallbackForLength = isFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenLastDeviceAndController(
            ApsControllerWithLoops ctrl,
            ApsLoopGroup loop,
            ApsDeviceInfo lastDevice,
            double correctionFactor = 1)
        {
            bool isFallbackForLength;

            var controllerElement = ctrl.Controller.Element;
            var deviceElement = lastDevice.BaseInfo.Element;

            string startLevel = lastDevice.FloorNumber.ToString();
            string endLevel = ctrl.Controller.FloorNumber.ToString();

            string startPoint = lastDevice.ToString();
            string endPoint = ctrl.ControllerId.ToString();

            double lengthMeters = ComputeLengthSmart(
                deviceElement,
                controllerElement,
                null,
                out isFallbackForLength,
                _currentCancel) * correctionFactor;

            return new CableRecord
            {
                StartLevel = startLevel,
                StartPoint = startPoint,
                EndLevel = endLevel,
                EndPoint = endPoint,
                CableType = "КСРЭВнг(А)-FRLS",
                CableCategory = "1х2х1,13",
                Purpose = "Шлейф АПС",
                Length = Math.Ceiling(lengthMeters).ToString("F2"),
                IsFallbackForLength = isFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            };
        }

        // =====================================================================
        // ЛОГИКА ВЫЧИСЛЕНИЯ ДЛИНЫ (обновлено: принимает optional CancellationMonitor)
        // =====================================================================

        private double ComputeLength(Element e1, Element e2)
        {
            var p1 = (e1.Location as LocationPoint)?.Point;
            var p2 = (e2.Location as LocationPoint)?.Point;

            if (p1 == null || p2 == null)
                return 0;

            return p1.DistanceTo(p2);
        }

        private double ComputeLengthSmart(
            Element start,
            Element end,
            string expanderId,
            out bool isFallbackForLength,
            CancellationMonitor cancel = null)
        {
            isFallbackForLength = false;
            double feet;

            if (cancel?.IsCancellationRequested == true)
                throw new OperationCanceledException("Cancelled before ComputeLengthSmart.");

            if (_mode == LengthMode.Direct)
            {
                feet = ComputeLength(start, end);
                return feet * 0.3048;
            }

            if (_mode == LengthMode.Route)
            {
                // Delegate to logged route calculator — pass cancellation monitor so the wrapper can report and pre-check.
                feet = !string.IsNullOrEmpty(expanderId)
                    ? _routeCalc.ComputeShortestRouteBetweenDevices(start, end, expanderId, cancel)
                    : _routeCalc.ComputeShortestRouteBetweenDevices(start, end, "", cancel);

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after route computation.");

                if (double.IsInfinity(feet))
                {
                    isFallbackForLength = true;
                    double directFeet = ComputeLength(start, end);
                    return directFeet * 0.3048;
                }

                // Путь найден только через «мост» между разрывами трасс — длина не по трассе.
                if (_routeCalc.LastRouteBridged) isFallbackForLength = true;
                return feet * 0.3048;
            }

            return 0;
        }
    }
}

