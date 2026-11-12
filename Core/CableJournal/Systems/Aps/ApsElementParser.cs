using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ARBM.CableJournalExporter.Models;
using Autodesk.Revit.DB;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Parser for extracting data from APS (Automatic Fire Alarm System) elements.
    /// Converts Revit element parameters into typed domain model objects.
    /// <para>RU: Парсер для извлечения данных из элементов системы АПС
    /// (Автоматическая Пожарная Сигнализация). Отвечает за преобразование
    /// параметров элементов Revit в типизированные объекты доменной модели.</para>
    /// </summary>
    public class ApsElementParser 
    {
        // =====================================================================
        // APS Parameter Constants
        // RU: Константы параметров АПС
        // =====================================================================

        /// <summary>
        /// APS controller name parameter (ПКП), format: N.CODE.M.
        /// N — alphanumeric building code, CODE — system code, M — device number.
        /// <para>RU: Параметр с наименованием ПКП (приемно-контрольный прибор),
        /// формат: N.CODE.M, где N — буквенно-цифровой код корпуса,
        /// CODE — код системы, M — номер прибора.</para>
        /// </summary>
        private const string Param_ControllerName = "ARBM_ПС_Имя изделия";

        /// <summary>
        /// Building code for device (alphanumeric).
        /// <para>RU: Буквенно-цифровой код корпуса (здания) для устройства.</para>
        /// </summary>
        private const string Param_BuildingNumber = "ARBM_Корпус";

        /// <summary>
        /// Number of APS controller to which device is connected.
        /// <para>RU: Номер ПКП, к которому подключено устройство.</para>
        /// </summary>
        private const string Param_ControllerNumber = "ARBM_Номер прибора";

        /// <summary>
        /// Floor number of element location.
        /// <para>RU: Номер этажа размещения элемента.</para>
        /// </summary>
        private const string Param_Floor = "ADSK_Этаж";

        /// <summary>
        /// Alphanumeric system type code.
        /// <para>RU: Буквенно-цифровое обозначение типа системы.</para>
        /// </summary>
        private const string Param_SystemCode = "ARBM_ПСБ_БЦО";

        /// <summary>
        /// Object line (circuit) number.
        /// <para>RU: Номер объектовой линии (шлейфа), к которой подключено устройство.</para>
        /// </summary>
        private const string Param_LineNumber = "ARBM_Номер линии";

        /// <summary>
        /// Device number within object line.
        /// <para>RU: Номер устройства в рамках объектовой линии (порядковый номер).</para>
        /// </summary>
        private const string Param_DeviceNumberInLine = "ARBM_SP_ElementMark";

        /// <summary>
        /// Fire Alarm Zone (ЗКПС) number.
        /// <para>RU: Номер Зоны контроля пожарной сигнализации (ЗКПС).</para>
        /// </summary>
        private const string Param_ZkpsNumber = "ARBM_ЗКПС";

        /// <summary>
        /// System type handled by this parser (APS).
        /// <para>RU: Тип системы, обрабатываемый данным парсером (АПС).</para>
        /// </summary>
        public SystemType SystemType => SystemType.Aps;

        /// <summary>
        /// Extracts APS controller information from Revit element.
        /// <para>RU: Извлекает информацию о ПКП из элемента АПС.</para>
        /// </summary>
        public dynamic ParseController(Element element)
        {
            if (element == null)
                return null;

            try
            {
                var controllerId = ParseApsControllerId(element);
                if (controllerId == null)
                    return null;

                int? floorNumber = GetIntParameter(element, Param_Floor);
                if (!floorNumber.HasValue)
                    return null;

                return new ApsController
                {
                    Element = element as FamilyInstance,
                    ControllerId = controllerId,
                    FloorNumber = floorNumber.Value,
                    Capabilities = new ApsControllerCapabilities
                    {
                        MaxObjectLines = 2,
                        MaxDevicesPerLine = 255
                    },
                    IsAddress = true
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extracts APS device information from Revit element.
        /// <para>RU: Извлекает информацию об устройстве из элемента АПС.</para>
        /// </summary>
        public dynamic ParseDevice(Element element)
        {
            if (element == null)
                return null;

            try
            {
                string buildingCode = GetStringParameter(element, Param_BuildingNumber); // строка
                int? controllerNumber = GetIntParameter(element, Param_ControllerNumber);
                int? floorNumber = GetIntParameter(element, Param_Floor);
                int? lineNumber = GetIntParameter(element, Param_LineNumber);
                int? deviceNumberInLine = GetIntParameter(element, Param_DeviceNumberInLine);
                int? zkpsNumber = GetIntParameter(element, Param_ZkpsNumber);
                string zkpsText = GetStringParameter(element, Param_ZkpsNumber)?.Trim();
                string systemCode = GetStringParameter(element, Param_SystemCode);

                if (string.IsNullOrWhiteSpace(buildingCode) ||
                    !controllerNumber.HasValue ||
                    !floorNumber.HasValue ||
                    !lineNumber.HasValue ||
                    !deviceNumberInLine.HasValue ||
                    string.IsNullOrWhiteSpace(systemCode))
                    return null;

                var deviceInfo = new ApsDeviceInfo
                {
                    BaseInfo = new DeviceInfo
                    {
                        Element = element,
                        Name = element.Name,
                        SystemType = SystemType.Aps
                    },
                    BuildingCode = buildingCode,
                    ControllerNumber = controllerNumber.Value,
                    FloorNumber = floorNumber.Value,
                    SystemCode = systemCode,
                    LineNumber = lineNumber.Value,
                    DeviceNumberInLine = deviceNumberInLine.Value,
                    ZkpsNumber = zkpsNumber > 0 ? zkpsNumber : null,
                    ZkpsText = string.IsNullOrEmpty(zkpsText) || zkpsText == "0" ? null : zkpsText
                };

                return deviceInfo;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Parses APS controller ID from ARBM_ПС_Имя изделия parameter.
        ///
        /// Format: N.CODE.M
        /// N — alphanumeric building code (letters and digits),
        /// CODE — system code (alphanumeric, may contain dash),
        /// M — device number (integer).
        ///
        /// <para>RU: Парсит идентификатор ПКП из параметра ARBM_ПС_Имя изделия.
        /// Формат: N.CODE.M, где N — буквенно-цифровой код корпуса,
        /// CODE — код системы, M — номер прибора.</para>
        /// </summary>
        public ApsControllerId ParseApsControllerId(Element element)
        {
            string nameValue = GetStringParameter(element, Param_ControllerName);
            if (string.IsNullOrWhiteSpace(nameValue))
                return null;

            string[] parts = nameValue.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return null;

            string buildingCode = parts[0];
            string deviceTypeCode = parts[1];
            string deviceNumberStr = parts[2];

            // N: буквенно-цифровой код (только буквы и цифры)
            if (!Regex.IsMatch(buildingCode, @"^[A-Za-z0-9]+$"))
                return null;

            if (string.IsNullOrWhiteSpace(deviceTypeCode))
                return null;

            if (!int.TryParse(deviceNumberStr, out int deviceNumber))
                return null;

            return new ApsControllerId
            {
                BuildingNumber = buildingCode,   // строка, не число
                DeviceTypeCode = deviceTypeCode,
                DeviceNumber = deviceNumber
            };
        }

        /// <summary>
        /// Gets integer parameter value from Revit element.
        /// <para>RU: Получает целочисленное значение параметра из элемента Revit.</para>
        /// </summary>
        private int? GetIntParameter(Element element, string paramName)
        {
            try
            {
                string value = GetStringParameter(element, paramName);
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                if (int.TryParse(value, out int result))
                    return result;

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets string parameter value from Revit element.
        /// <para>RU: Получает строковое значение параметра из элемента Revit.</para>
        /// </summary>
        private string GetStringParameter(Element element, string paramName)
        {
            try
            {
                if (element == null)
                    return null;

                var param = element.LookupParameter(paramName);
                return param?.AsString();
            }
            catch
            {
                return null;
            }
        }
    }
}
