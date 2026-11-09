using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using System;
using System.Collections.Generic;
using System.Linq;
using static ARBM.CableJournalExporter.Services.RouteLengthCalculator;

namespace ARBM.CableJournalExporter.Services
{
    public enum LengthMode
    {
        Direct,
        Route
    }

    public class SecurityCableRecordBuilder
    {
        private readonly SecurityElementParser _parser;
        private readonly LengthMode _mode;
        private readonly RouteLengthCalculator _routeCalc;
        private readonly Document _doc;
        private readonly double _correctionFactor;
        // private Dictionary<long, Graph> _graphsByLevel;
        // Кэш уровней (ElementId → Level)
        //private Dictionary<long, Level> _levelsById;
        public SecurityCableRecordBuilder(SecurityElementParser parser, Document doc, LengthMode mode, double correctionFactor = 1)
        {
            _parser = parser;
            _doc = doc;
            _mode = mode;
            _routeCalc = new RouteLengthCalculator();
            _correctionFactor = correctionFactor;

        }

        public List<CableRecord> Build(List<ControllerWithLoops> controllers, string param = "")
        {
            var result = new List<CableRecord>();

            // 🔥 Строим графы по уровням один раз
            _routeCalc.BuildGraphsByLevel(_doc, controllers, param);
            // _graphsByLevel = _routeCalc._graphsByLevel;
            //  _levelsById = _routeCalc.LevelsById;


            foreach (var ctrl in controllers)
            {
                // ---------------------------------------------------------
                // 1. Контроллер ↔ Расширители
                // ---------------------------------------------------------
                if (ctrl.HasExpanders())
                {
                    result.Add(CreateCableRecordBetweenControllerAndExpander(ctrl, ctrl.Expanders[0]));

                    for (int i = 0; i < ctrl.Expanders.Count - 1; i++)
                        result.Add(CreateCableRecordBetweenExpanders(ctrl.Expanders[i], ctrl.Expanders[i + 1], ctrl));

                    result.Add(CreateCableRecordBetweenExpanderAndController(ctrl.Expanders.Last(), ctrl));
                }

                // ---------------------------------------------------------
                // 2. LoopGroup контроллера
                // ---------------------------------------------------------
                foreach (var loop in ctrl.Loops)
                {
                    if (loop.Devices.Count == 0)
                        continue;

                    result.Add(CreateCableRecordBetweenControllerAndDevice(ctrl, loop, loop.Devices[0], _correctionFactor));

                    for (int i = 0; i < loop.Devices.Count - 1; i++)
                        result.Add(CreateCableRecordBetweenDevices(ctrl, loop, loop.Devices[i], loop.Devices[i + 1],
                            _correctionFactor));
                }

                // ---------------------------------------------------------
                // 3. LoopGroup расширителей
                // ---------------------------------------------------------
                if (ctrl.HasExpanders())
                {
                    foreach (var exp in ctrl.Expanders)
                    {
                        foreach (var loop in exp.Loops)
                        {
                            if (loop.Devices.Count == 0)
                                continue;

                            result.Add(CreateCableRecordBetweenExpanderAndDevice(ctrl, exp, loop, loop.Devices[0],
                                _correctionFactor));

                            for (int i = 0; i < loop.Devices.Count - 1; i++)
                                result.Add(CreateCableRecordBetweenDevices(ctrl, loop, loop.Devices[i], loop.Devices[i + 1], 
                                    _correctionFactor));
                        }
                    }
                }
            }

            return result;
        }

        // =====================================================================
        // ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ
        // =====================================================================
        /// <summary>
        /// Определяет категорию кабеля на основе типов устройств в шлейфе.
        /// Если все устройства имеют ARBM_ПСБ_БЦО = BGB, то "2х0,50",
        /// иначе "4х0,50".
        /// </summary>
        private string DetermineCableCategory(LoopGroup loop)
        {
            if (loop == null || loop.Devices.Count == 0)
                return "4х0,50"; // Значение по умолчанию

            // Проверяем, что ВСЕ устройства имеют ARBM_ПСБ_БЦО = BGB
            bool allBGB = loop.Devices.All(d =>
            {
                string bco = _parser.GetStringParam(d, "ARBM_ПСБ_БЦО");
                return !string.IsNullOrWhiteSpace(bco) &&
                       bco.Equals("BGB", StringComparison.OrdinalIgnoreCase);
            });

            return allBGB ? "2х0,50" : "4х0,50";
        }

        private CableRecord CreateCableRecordBetweenControllerAndExpander(
            ControllerWithLoops ctrl, Expander exp)
        {
            return new CableRecord
            {
                //StartLevel = _parser.GetFloorNumber(ctrl.Controller)?.ToString(),
                StartLevel = ctrl.ControllerId.FloorNumber.ToString(),
                StartPoint = $"{ctrl.ControllerId.ControllerPrefix}.{ctrl.ControllerId.FloorNumber}.{ctrl.ControllerId.ControllerNumber}",
                EndLevel = exp.ExpanderId.FloorNumber.ToString(),
                EndPoint = $"{exp.ExpanderId.Prefix}.{exp.ExpanderId.FloorNumber}.{exp.ExpanderId.ControllerNumber}.{exp.ExpanderId.ExpanderNumber}",
                CableType = "КСРЭПнг(А)-FRHF",
                CableCategory = "1х2х0,80",
                Purpose = "СОТС (АДЛС)",
                Length = "1",
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenExpanders(
            Expander exp1, Expander exp2, ControllerWithLoops ctrl)
        {
            return new CableRecord
            {
                StartLevel = exp1.ExpanderId.FloorNumber.ToString(),
                StartPoint = $"{exp1.ExpanderId.Prefix}.{exp1.ExpanderId.FloorNumber}.{exp1.ExpanderId.ControllerNumber}.{exp1.ExpanderId.ExpanderNumber}",
                EndLevel = exp2.ExpanderId.FloorNumber.ToString(),
                EndPoint = $"{exp2.ExpanderId.Prefix}.{exp2.ExpanderId.FloorNumber}.{exp2.ExpanderId.ControllerNumber}.{exp2.ExpanderId.ExpanderNumber}",
                CableType = "КСРЭПнг(А)-FRHF",
                CableCategory = "1х2х0,80",
                Purpose = "СОТС (АДЛС)",
                Length = "1",
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenExpanderAndController(
            Expander exp, ControllerWithLoops ctrl)
        {
            return new CableRecord
            {
                StartLevel = exp.ExpanderId.FloorNumber.ToString(),
                StartPoint = $"{exp.ExpanderId.Prefix}.{exp.ExpanderId.FloorNumber}.{exp.ExpanderId.ControllerNumber}.{exp.ExpanderId.ExpanderNumber}",
                //EndLevel = _parser.GetFloorNumber(ctrl.Controller)?.ToString(),
                EndLevel = ctrl.ControllerId.FloorNumber.ToString(),
                EndPoint = $"{ctrl.ControllerId.ControllerPrefix}.{ctrl.ControllerId.FloorNumber}.{ctrl.ControllerId.ControllerNumber}",
                CableType = "КСРЭПнг(А)-FRHF",
                CableCategory = "1х2х0,80",
                Purpose = "СОТС (АДЛС)",
                Length = "1",
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenControllerAndDevice(
            ControllerWithLoops ctrl, LoopGroup loop, FamilyInstance device, double correctionFactor = 1)
        {
            bool isFallbackForLength;
            string cableCategory = DetermineCableCategory(loop);
            return new CableRecord
            {
                // from ADSK_Этаж
                StartLevel = _parser.GetFloorNumber(ctrl.Controller)?.ToString(),
                StartPoint = $"{ctrl.ControllerId.ControllerPrefix}.{ctrl.ControllerId.FloorNumber}.{ctrl.ControllerId.ControllerNumber}",
                EndLevel = _parser.GetFloorNumber(device)?.ToString(),
                EndPoint = BuildDevicePoint(device),
                CableType = "КСПВПнг(А)-HF",
                CableCategory = cableCategory,
                Purpose = "Шлейф СОТС",
                Length = Math.Ceiling(ComputeLengthSmart(ctrl.Controller, device, null, out isFallbackForLength) * correctionFactor).ToString("F2"),
                IsFallbackForLength = isFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenDevices(
            ControllerWithLoops ctrl, LoopGroup loop, FamilyInstance d1, FamilyInstance d2, double correctionFactor = 1)
        {
            bool isFallbackForLength;
            string cableCategory = DetermineCableCategory(loop);
            return new CableRecord
            {
                StartLevel = _parser.GetFloorNumber(d1)?.ToString(),
                StartPoint = BuildDevicePoint(d1),
                EndLevel = _parser.GetFloorNumber(d2)?.ToString(),
                EndPoint = BuildDevicePoint(d2),
                CableType = "КСПВПнг(А)-HF",
                CableCategory = cableCategory,
                Purpose = "Шлейф СОТС",
                Length = Math.Ceiling(ComputeLengthSmart(d1, d2, null, out isFallbackForLength) * correctionFactor).ToString("F2"),
                ColumnMap = new ExcelColumnMap()
            };
        }

        private CableRecord CreateCableRecordBetweenExpanderAndDevice(
            ControllerWithLoops ctrl, Expander exp, LoopGroup loop, FamilyInstance device, double correctionFactor = 1)
        {
            Element startElement = ctrl.Controller;
            bool isFallbackForLength;
            string cableCategory = DetermineCableCategory(loop);
            return new CableRecord
            {
                StartLevel = exp.ExpanderId.FloorNumber.ToString(),
                StartPoint = $"{exp.ExpanderId.Prefix}.{exp.ExpanderId.FloorNumber}.{exp.ExpanderId.ControllerNumber}.{exp.ExpanderId.ExpanderNumber}",
                EndLevel = _parser.GetFloorNumber(device)?.ToString(),
                EndPoint = BuildDevicePoint(device),
                CableType = "КСПВПнг(А)-HF",
                CableCategory = cableCategory,
                Purpose = "Шлейф СОТС",
                Length = Math.Ceiling(ComputeLengthSmart(startElement, device, exp.ExpanderId.ToString(), out isFallbackForLength)
                * correctionFactor).ToString("F2"),
                IsFallbackForLength = isFallbackForLength,
                ColumnMap = new ExcelColumnMap()
            };
        }

        private string BuildDevicePoint(FamilyInstance device)
        {
            string floor = _parser.GetFloorNumber(device)?.ToString();
            string bco = _parser.GetStringParam(device, "ARBM_ПСБ_БЦО");
            string addr = _parser.GetStringParam(device, "ARBM_ПСБ_Адрес устройства");
            string mark = _parser.GetStringParam(device, "ARBM_SP_ElementMark");

            return $"{floor}.{bco}.{addr}.{mark}";
        }

        private double ComputeLength(Element e1, Element e2)
        {
            var p1 = (e1.Location as LocationPoint)?.Point;
            var p2 = (e2.Location as LocationPoint)?.Point;

            if (p1 == null || p2 == null)
                return 0;

            return p1.DistanceTo(p2);
        }

        // =====================================================================
        // 🔥 НОВЫЙ ComputeLengthSmart
        // =====================================================================
        /// <summary>
        /// Computes the cable length either directly or using the routing graph.
        /// Falls back to direct geometric distance if the route cannot be resolved.
        /// </summary>
        /// <param name="start">Starting Revit element.</param>
        /// <param name="end">Ending Revit element.</param>
        /// <param name="expanderId">
        /// Optional identifier of an expander device used to refine routing logic.
        /// </param>
        /// <param name="isFallbackForLength">
        /// Output flag indicating whether the length was calculated using the fallback
        /// direct distance method instead of the routing graph.
        /// </param>
        /// <returns>Length in meters.</returns>
        private double ComputeLengthSmart(
            Element start,
            Element end,
            string expanderId,
            out bool isFallbackForLength)
        {
            isFallbackForLength = false;
            double feet;

            // DIRECT — по прямой
            if (_mode == LengthMode.Direct)
            {
                feet = ComputeLength(start, end);
                return feet * 0.3048;
            }

            // ROUTE — по графу
            if (_mode == LengthMode.Route)
            {
                // Явно передаем null для expanderId, чтобы не было неоднозначности
                feet = !string.IsNullOrEmpty(expanderId)
                    ? _routeCalc.ComputeShortestRouteBetweenDevices(start, end, expanderId)
                    : _routeCalc.ComputeShortestRouteBetweenDevices(start, end, (string)null);

                // Если путь не найден — fallback
                if (double.IsInfinity(feet))
                {
                    isFallbackForLength = true;

                    double directFeet = ComputeLength(start, end);
                    return directFeet * 0.3048;
                }

                // Возвращаем длину в метрах
                return feet * 0.3048;
            }

            return 0;
        }


    }
}

