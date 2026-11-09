using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Models
{
    // =====================================================================
    // APS (Автоматическая пожарная сигнализация) — специфичные типы
    // APS (Automatic Fire Alarm System) — specific types
    // =====================================================================

    /// <summary>
    /// Тип топологии объектовой линии (шлейфа) в системе АПС.
    /// EN: Type of object line (circuit) topology in APS system.
    /// </summary>
    public enum TypeLoop
    {
        /// <summary>
        /// Кольцевая топология — устройства подключены в кольцо.
        /// EN: Annular topology — devices connected in a ring.
        /// </summary>
        Annular = 0,

        /// <summary>
        /// Радиальная топология — устройства подключены звездой (от центра).
        /// EN: Radial topology — devices connected in a star (from center).
        /// </summary>
        Radial = 1
    }

    /// <summary>
    /// Идентификатор приемно-контрольного прибора (ПКП) системы АПС.
    /// Парсится из параметра ARBM_ПС_Имя изделия в формате [номер корпуса].[буквенно-цифровое обозначение].[номер прибора]
    /// Например: "1.АПС.1" или "2.АПС-РИ.3"
    /// 
    /// EN: Identifier of APS control panel (ПКП).
    /// Parsed from ARBM_ПС_Имя изделия parameter in format [building number].[alphanumeric code].[device number]
    /// Example: "1.АПС.1" or "2.АПС-РИ.3"
    /// </summary>
    public class ApsControllerId
    {
        /// <summary>
        /// Номер корпуса (здания).
        /// EN: Building number.
        /// </summary>
        public string BuildingNumber { get; set; }

        /// <summary>
        /// Буквенно-цифровое обозначение типа прибора (например, "АПС", "АПС-РИ").
        /// EN: Alphanumeric code of device type (e.g., "АПС", "АПС-РИ").
        /// </summary>
        public string DeviceTypeCode { get; set; }

        /// <summary>
        /// Номер приемно-контрольного прибора в здании.
        /// EN: Number of control panel in building.
        /// </summary>
        public int DeviceNumber { get; set; }

        /// <summary>
        /// Создаёт полное строковое представление идентификатора в формате BUILDING.CODE.NUMBER
        /// EN: Creates full string representation in format BUILDING.CODE.NUMBER
        /// </summary>
        /// <returns>Строка формата "1.АПС.1"</returns>
        public override string ToString()
        {
            return $"{BuildingNumber}.{DeviceTypeCode}.{DeviceNumber}";
        }
    }

    /// <summary>
    /// Параметры приемно-контрольного прибора (ПКП) системы АПС.
    /// Содержит информацию о количестве объектовых линий и устройств в линии.
    /// 
    /// EN: Parameters of APS control panel.
    /// Contains information about number of object lines and max devices per line.
    /// </summary>
    public class ApsControllerCapabilities
    {
        /// <summary>
        /// Количество объектовых линий, которые может обслуживать данный ПКП.
        /// EN: Number of object lines this control panel can handle.
        /// </summary>
        public int MaxObjectLines { get; set; }

        /// <summary>
        /// Максимальное количество устройств, которое может быть подключено к одной объектовой линии.
        /// EN: Maximum number of devices that can be connected to one object line.
        /// </summary>
        public int MaxDevicesPerLine { get; set; }

        /// <summary>
        /// Параметры ПКП, используемые при расчете кабельных цепей.
        /// EN: Control panel parameters used in cable chain calculations.
        /// </summary>
        public string Comments { get; set; }
    }

    /// <summary>
    /// Полная информация об устройстве системы АПС с разобранными параметрами.
    /// Содержит как базовую информацию (из DeviceInfo), так и АПС-специфичные данные.
    /// 
    /// EN: Complete information about APS device with parsed parameters.
    /// Contains both basic information (from DeviceInfo) and APS-specific data.
    /// </summary>
    public class ApsDeviceInfo
    {
        /// <summary>
        /// Базовая информация об устройстве (элемент Revit, имя, тип системы).
        /// EN: Basic device information (Revit element, name, system type).
        /// </summary>
        public DeviceInfo BaseInfo { get; set; }

        /// <summary>
        /// Номер/код корпуса, к которому подключено устройство.
        /// Может содержать буквы и цифры (например, "1", "A", "1A", "Корпус1").
        /// Парсится из параметра ARBM_Корпус.
        /// EN: Building number/code where device is connected.
        /// Can contain letters and digits (e.g., "1", "A", "1A", "Building1").
        /// Parsed from ARBM_Корпус parameter.
        /// </summary>
        public string BuildingCode { get; set; }

        /// <summary>
        /// Буквенно-цифровое обозначение типа системы (например, "АПС", "АПС-РИ").
        /// Парсится из параметра ARBM_ПСБ_БЦО.
        /// EN: Alphanumeric code of system type (e.g., "АПС", "АПС-РИ").
        /// Parsed from ARBM_ПСБ_БЦО parameter.
        /// </summary>
        public string SystemCode { get; set; }

        /// <summary>
        /// Номер приемно-контрольного прибора (ПКП), к которому подключено устройство.
        /// Парсится из параметра ARBM_Номер прибора.
        /// EN: Number of control panel device is connected to.
        /// Parsed from ARBM_Номер прибора parameter.
        /// </summary>
        public int ControllerNumber { get; set; }

        /// <summary>
        /// Номер объектовой линии, к которой подключено устройство.
        /// Парсится из параметра ARBM_Номер линии.
        /// EN: Number of object line device is connected to.
        /// Parsed from ARBM_Номер линии parameter.
        /// </summary>
        public int LineNumber { get; set; }

        /// <summary>
        /// Номер устройства в пределах объектовой линии (порядковый номер в линии).
        /// Парсится из параметра ARBM_SP_ElementMark.
        /// EN: Device number within object line (sequential number in line).
        /// Parsed from ARBM_SP_ElementMark parameter.
        /// </summary>
        public int DeviceNumberInLine { get; set; }

        /// <summary>
        /// Номер Зоны контроля пожарной сигнализации (ЗКПС), к которой принадлежит устройство.
        /// Парсится из параметра ARBM_ЗКПС.
        /// 
        /// Может быть NULL или 0 — это означает, что устройство не принадлежит ни одной ЗКПС
        /// (например, контрольный модуль, повторитель сигнала и т.д.).
        /// 
        /// EN: Number of Fire Alarm Zone (ЗКПС) device belongs to.
        /// Parsed from ARBM_ЗКПС parameter.
        /// 
        /// Can be NULL or 0 — this means device does not belong to any ЗКПС
        /// (e.g., control module, signal repeater, etc.).
        /// </summary>
        public int? ZkpsNumber { get; set; }

        /// <summary>
        /// Номер этажа, на котором расположено устройство.
        /// Парсится из параметра ADSK_Этаж.
        /// EN: Floor number where device is located.
        /// Parsed from ADSK_Этаж parameter.
        /// </summary>
        public int FloorNumber { get; set; }

        /// <summary>
        /// Флаг, указывающий, принадлежит ли устройство зоне контроля (имеет непустой ARBM_ЗКПС).
        /// EN: Flag indicating whether device belongs to control zone (has non-empty ARBM_ЗКПС).
        /// </summary>
        public bool IsHasZkps => ZkpsNumber.HasValue && ZkpsNumber.Value > 0;

        /// <summary>
        /// Создаёт строковое представление для логирования и отладки.
        /// Формат: BUILDING.CONTROLLER.SYSTEMCODE.LINE.DEVICE или BUILDING.CONTROLLER.SYSTEMCODE.LINE.DEVICE(ZKPS)
        /// EN: Creates string representation for logging and debugging.
        /// Format: BUILDING.CONTROLLER.SYSTEMCODE.LINE.DEVICE or BUILDING.CONTROLLER.SYSTEMCODE.LINE.DEVICE(ZKPS)
        /// </summary>
        public override string ToString()
        {
            if (IsHasZkps)
            {
                return $"{BuildingCode}.{ControllerNumber}.{SystemCode}.{LineNumber}.{DeviceNumberInLine}({ZkpsNumber})";
            }
            else
            {
                return $"{BuildingCode}.{ControllerNumber}.{SystemCode}.{LineNumber}.{DeviceNumberInLine}";
            }
        }
    }

    /// <summary>
    /// Объектовая линия (шлейф) системы АПС.
    /// Содержит все устройства, подключённые к одной объектовой линии конкретного ПКП,
    /// и информацию о топологии линии (кольцевая или радиальная).
    /// 
    /// EN: Object line (circuit) of APS system.
    /// Contains all devices connected to one object line of specific control panel,
    /// and information about line topology (annular or radial).
    /// </summary>
    public class ApsLoopGroup
    {
        /// <summary>
        /// Номер объектовой линии (1, 2, 3, ...).
        /// EN: Number of object line (1, 2, 3, ...).
        /// </summary>
        public int LoopNumber { get; set; }

        /// <summary>
        /// Список устройств, подключённых к этой линии, упорядоченных по номерам устройств.
        /// EN: List of devices connected to this line, ordered by device numbers.
        /// </summary>
        public List<ApsDeviceInfo> Devices { get; set; } = new List<ApsDeviceInfo>();

        /// <summary>
        /// Тип топологии объектовой линии (кольцевая или радиальная).
        /// EN: Type of object line topology (annular or radial).
        /// </summary>
        public TypeLoop TypeLoop { get; set; }

        /// <summary>
        /// Получает устройства, которые относятся к зонам контроля (имеют непустой ARBM_ЗКПС).
        /// EN: Gets devices that belong to monitoring zones (have non-empty ARBM_ЗКПС).
        /// </summary>
        public IEnumerable<ApsDeviceInfo> GetMonitoringDevices()
        {
            return Devices.Where(d => d.IsHasZkps);
        }

        /// <summary>
        /// Получает устройства, которые НЕ относятся к зонам контроля (без ARBM_ЗКПС).
        /// EN: Gets devices that do NOT belong to monitoring zones (without ARBM_ЗКПС).
        /// </summary>
        public IEnumerable<ApsDeviceInfo> GetNonMonitoringDevices()
        {
            return Devices.Where(d => !d.IsHasZkps);
        }

        public override string ToString()
        {
            return $"Line {LoopNumber} ({TypeLoop}, Devices={Devices.Count})";
        }
    }

    /// <summary>
    /// Структура приемно-контрольного прибора (ПКП) системы АПС.
    /// Содержит элемент Revit и основные параметры.
    /// 
    /// EN: Structure of APS control panel.
    /// Contains Revit element and basic parameters.
    /// </summary>
    public class ApsController
    {
        /// <summary>
        /// Элемент Revit (FamilyInstance), представляющий ПКП.
        /// EN: Revit element (FamilyInstance) representing the control panel.
        /// </summary>
        public FamilyInstance Element { get; set; }

        /// <summary>
        /// Идентификатор ПКП (разобранный из ARBM_ПС_Имя изделия).
        /// EN: Control panel identifier (parsed from ARBM_ПС_Имя изделия).
        /// </summary>
        public ApsControllerId ControllerId { get; set; }

        /// <summary>
        /// Возможности ПКП (количество линий, устройств в линии).
        /// EN: Control panel capabilities (number of lines, devices per line).
        /// </summary>
        public ApsControllerCapabilities Capabilities { get; set; }

        /// <summary>
        /// Номер этажа, на котором расположен ПКП.
        /// EN: Floor number where control panel is located.
        /// </summary>
        public int FloorNumber { get; set; }

        /// <summary>
        /// Флаг, указывающий, имеет ли ПКП адресацию (адресные ПКП позволяют идентифицировать каждое устройство).
        /// EN: Flag indicating whether the control panel has addressing capability 
        /// (addressed panels allow identifying each device).
        /// </summary>
        public bool IsAddress { get; set; }

        /// <summary>
        /// Создаёт строковое представление, используя ControllerId.
        /// EN: Creates string representation using ControllerId.
        /// </summary>
        public override string ToString()
        {
            return ControllerId?.ToString() ?? "Unknown Controller";
        }
    }

    /// <summary>
    /// Расширитель объектовых линий для системы АПС.
    /// Используется для подключения дополнительных объектовых линий сверх базовых.
    /// 
    /// В текущей версии — заглушка для будущей реализации.
    /// 
    /// EN: Expander for additional object lines in APS system.
    /// Used to connect additional object lines beyond the basic ones.
    /// 
    /// In current version — stub for future implementation.
    /// </summary>
    public class ApsExpander
    {
        // Заглушка для будущей реализации
        // Stub for future implementation
    }

    /// <summary>
    /// Полная информация об приемно-контрольном приборе (ПКП) системы АПС.
    /// Содержит идентификатор, возможности, параметры и структурирует устройства по объектовым линиям.
    /// 
    /// Аналогично ControllerWithLoops, но специализирован для системы АПС с поддержкой расширителей
    /// для будущих версий.
    /// 
    /// EN: Complete information about APS control panel.
    /// Contains identifier, capabilities, parameters and structures devices by object lines.
    /// 
    /// Similar to ControllerWithLoops but specialized for APS system with support for expanders
    /// in future versions.
    /// </summary>
    public class ApsControllerWithLoops
    {
        /// <summary>
        /// Элемент Revit, представляющий ПКП.
        /// EN: Revit element representing the control panel.
        /// </summary>
        public ApsController Controller { get; set; }

        /// <summary>
        /// Идентификатор ПКП (разобранный из ARBM_ПС_Имя изделия).
        /// EN: Control panel identifier (parsed from ARBM_ПС_Имя изделия).
        /// </summary>
        public ApsControllerId ControllerId { get; set; }

        /// <summary>
        /// Коллекция объектовых линий, организованная по номерам линий.
        /// Ключ — номер линии, значение — список устройств в этой линии.
        /// EN: Collection of object lines organized by line numbers.
        /// Key — line number, value — list of devices in this line.
        /// </summary>
        public List<ApsLoopGroup> Loops { get; set; } = new List<ApsLoopGroup>();

        /// <summary>
        /// Расширители, которые содержат дополнительные объектовые линии (сверх первых базовых).
        /// В текущей версии не используется, но добавлено для будущей совместимости.
        /// EN: Expanders that hold additional object lines (beyond the basic ones).
        /// Not used in current version but added for future compatibility.
        /// </summary>
        public List<ApsExpander> Expanders { get; set; } = new List<ApsExpander>();

        /// <summary>
        /// Проверяет, содержит ли ПКП хотя бы один расширитель.
        /// EN: Indicates whether this control panel has any expanders.
        /// </summary>
        public bool HasExpanders()
        {
            return Expanders != null && Expanders.Count > 0;
        }

        /// <summary>
        /// Создаёт строковое представление, используя ControllerId.
        /// EN: Creates string representation using ControllerId.
        /// </summary>
        public override string ToString()
        {
            return ControllerId?.ToString() ?? "Unknown Controller";
        }
    }
}
