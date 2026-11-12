using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Revit
{
    /// <summary>
    /// Парсер элементов кабельной системы для извлечения структурированной информации из параметров Revit.
    /// 
    /// Этот класс отвечает за преобразование строковых параметров семейств Revit (контроллеры, устройства, расширители)
    /// в типизированные объекты доменной модели (ControllerId, DeviceAddress и т.д.).
    /// 
    /// Поддерживаемые операции:
    /// • Парсинг идентификаторов контроллеров из параметра ARBM_ПС_Имя изделия (формат: PREFIX.FLOOR.CONTROLLER)
    /// • Парсинг адресов устройств из параметра ARBM_ПСБ_Адрес устройства (формат: CONTROLLER.LOOP)
    /// • Извлечение номера этажа из параметра ADSK_Этаж
    /// • Безопасное получение строковых параметров с обработкой null-значений
    /// 
    /// EN:
    /// Parser for cable system elements to extract structured information from Revit parameters.
    /// 
    /// This class is responsible for converting string parameters from Revit families (controllers, devices, expanders)
    /// into typed domain model objects (ControllerId, DeviceAddress, etc.).
    /// 
    /// Supported operations:
    /// • Parsing controller identifiers from ARBM_ПС_Имя изделия parameter (format: PREFIX.FLOOR.CONTROLLER)
    /// • Parsing device addresses from ARBM_ПСБ_Адрес устройства parameter (format: CONTROLLER.LOOP)
    /// • Extracting floor number from ADSK_Этаж parameter
    /// • Safe retrieval of string parameters with null-value handling
    /// 
    /// <remarks>
    /// Используется как часть конвейера обработки данных для построения кабельных цепей.
    /// EN: Used as part of the data processing pipeline for building cable chains.
    /// </remarks>
    /// </summary>
    public class SecurityElementParser
    {
        /// <summary>
        /// Имя параметра Revit, содержащего наименование изделия контроллера.
        /// 
        /// Значение в этом параметре используется для извлечения идентификатора контроллера
        /// и должно следовать формату: PREFIX.FLOOR.CONTROLLER (например, "ARK.1.1").
        /// 
        /// EN: Name of the Revit parameter containing the controller product name.
        /// 
        /// The value in this parameter is used to extract the controller identifier
        /// and should follow the format: PREFIX.FLOOR.CONTROLLER (e.g., "ARK.1.1").
        /// </summary>
        private const string Param_ControllerName = "ARBM_ПС_Имя изделия";

        /// <summary>
        /// Имя параметра Revit, содержащего номер этажа размещения элемента.
        /// 
        /// Этот параметр используется для определения уровня в Revit, на котором расположен элемент.
        /// Значение может быть как числовым (1, 2, 3...), так и строковым наименованием уровня.
        /// 
        /// EN: Name of the Revit parameter containing the floor number of the element location.
        /// 
        /// This parameter is used to determine the Revit level on which the element is located.
        /// The value can be either numeric (1, 2, 3...) or a textual level name.
        /// </summary>
        private const string Param_Floor = "ADSK_Этаж";

        /// <summary>
        /// Имя параметра Revit, содержащего адрес устройства в системе.
        /// 
        /// Адрес устройства определяет его положение в иерархии контроллер → петля → устройство.
        /// Значение должно следовать формату: CONTROLLER.LOOP (например, "1.1").
        /// 
        /// EN: Name of the Revit parameter containing the device address in the system.
        /// 
        /// The device address determines its position in the hierarchy: controller → loop → device.
        /// The value should follow the format: CONTROLLER.LOOP (e.g., "1.1").
        /// </summary>
        private const string Param_DeviceAddress = "ARBM_ПСБ_Адрес устройства";

        /// <summary>
        /// Парсит идентификатор контроллера из параметра ARBM_ПС_Имя изделия.
        /// 
        /// Ожидаемый формат значения параметра: PREFIX.FLOOR.CONTROLLER
        /// Например:
        /// • "ARK.1.1" → ControllerId { ControllerPrefix = "ARK", FloorNumber = 1, ControllerNumber = 1 }
        /// • "ARK.2.3" → ControllerId { ControllerPrefix = "ARK", FloorNumber = 2, ControllerNumber = 3 }
        /// 
        /// Процесс парсинга:
        /// 1) Извлекает значение параметра Param_ControllerName из семейства контроллера
        /// 2) Проверяет, что значение не пусто или null
        /// 3) Разбивает строку на три части по разделителю '.')
        /// 4) Проверяет, что получилось ровно 3 части
        /// 5) Преобразует вторую часть (floor) в целое число
        /// 6) Преобразует третью часть (controller number) в целое число
        /// 7) Если все проверки пройдены успешно, возвращает объект ControllerId
        /// 
        /// Обработка ошибок:
        /// • Если параметр не содержит значение → возвращает null
        /// • Если значение не соответствует формату (не 3 части) → возвращает null
        /// • Если вторая часть не является числом → возвращает null
        /// • Если третья часть не является числом → возвращает null
        /// 
        /// EN: Parses the controller identifier from the ARBM_ПС_Имя изделия parameter.
        /// 
        /// Expected parameter value format: PREFIX.FLOOR.CONTROLLER
        /// Examples:
        /// • "ARK.1.1" → ControllerId { ControllerPrefix = "ARK", FloorNumber = 1, ControllerNumber = 1 }
        /// • "ARK.2.3" → ControllerId { ControllerPrefix = "ARK", FloorNumber = 2, ControllerNumber = 3 }
        /// 
        /// Parsing process:
        /// 1) Extracts the value of Param_ControllerName parameter from the controller family
        /// 2) Checks that the value is not empty or null
        /// 3) Splits the string into three parts using the '.' separator
        /// 4) Verifies that exactly 3 parts were obtained
        /// 5) Converts the second part (floor) to an integer
        /// 6) Converts the third part (controller number) to an integer
        /// 7) If all validation checks pass, returns a ControllerId object
        /// 
        /// Error handling:
        /// • If parameter has no value → returns null
        /// • If value does not match format (not 3 parts) → returns null
        /// • If the second part is not a number → returns null
        /// • If the third part is not a number → returns null
        /// </summary>
        /// <param name="controller">
        /// Семейство контроллера (FamilyInstance), из которого извлекается идентификатор.
        /// EN: Controller family instance from which the identifier is extracted.
        /// </param>
        /// <returns>
        /// Объект ControllerId с распарсенными компонентами префикса, номера этажа и номера контроллера,
        /// либо null если парсинг не удался.
        /// EN: ControllerId object with parsed components of prefix, floor number, and controller number,
        /// or null if parsing failed.
        /// </returns>
        /// <example>
        /// Пример использования:
        /// EN: Usage example:
        /// <code>
        /// var parser = new SecurityElementParser();
        /// var controller = /* получить FamilyInstance контроллера */;
        /// var controllerId = parser.ParseControllerId(controller);
        /// 
        /// if (controllerId != null)
        /// {
        ///     Console.WriteLine($"Префикс: {controllerId.ControllerPrefix}");
        ///     Console.WriteLine($"Этаж: {controllerId.FloorNumber}");
        ///     Console.WriteLine($"Номер контроллера: {controllerId.ControllerNumber}");
        /// }
        /// </code>
        /// </example>
        public ControllerId ParseControllerId(FamilyInstance controller)
        {
            // Получаем значение параметра наименования контроллера
            // EN: Get the controller name parameter value
            string name = GetStringParam(controller, Param_ControllerName);
            
            // Если параметр пуст или null — парсинг невозможен
            // EN: If parameter is empty or null — parsing is not possible
            if (string.IsNullOrWhiteSpace(name))
                return null;

            // Разбиваем строку по разделителю '.',
            // Ожидаемый формат: PREFIX.FLOOR.CONTROLLER
            // EN: Split the string by '.' separator
            // Expected format: PREFIX.FLOOR.CONTROLLER
            var parts = name.Split('.');
            
            // Проверяем, что получилось ровно 3 части
            // EN: Verify that exactly 3 parts were obtained
            if (parts.Length != 3)
                return null;

            // Преобразуем вторую часть в число (номер этажа)
            // EN: Convert the second part to integer (floor number)
            if (!int.TryParse(parts[1], out int floor))
                return null;

            // Преобразуем третью часть в число (номер контроллера)
            // EN: Convert the third part to integer (controller number)
            if (!int.TryParse(parts[2], out int controllerNumber))
                return null;

            // Все проверки пройдены успешно — возвращаем распарсенный объект
            // EN: All validation checks passed — return the parsed object
            return new ControllerId
            {
                ControllerPrefix = parts[0],
                FloorNumber = floor,
                ControllerNumber = controllerNumber
            };
        }

        /// <summary>
        /// Парсит адрес устройства из параметра ARBM_ПСБ_Адрес устройства.
        /// 
        /// Ожидаемый формат значения параметра: CONTROLLER.LOOP
        /// Адрес определяет иерархическое положение устройства в системе:
        /// • Первая часть (CONTROLLER) — номер контроллера
        /// • Вторая часть (LOOP) — номер петли на контроллере
        /// 
        /// Примеры:
        /// • "1.1" → DeviceAddress { ControllerNumber = 1, LoopNumber = 1 }
        /// • "2.3" → DeviceAddress { ControllerNumber = 2, LoopNumber = 3 }
        /// 
        /// Процесс парсинга:
        /// 1) Извлекает значение параметра Param_DeviceAddress из семейства устройства
        /// 2) Проверяет, что значение не пусто или null
        /// 3) Разбивает строку на две части по разделителю '.',
        /// 4) Проверяет, что получилось ровно 2 части
        /// 5) Преобразует первую часть (controller number) в целое число
        /// 6) Преобразует вторую часть (loop number) в целое число
        /// 7) Если все проверки пройдены, возвращает объект DeviceAddress
        /// 
        /// Обработка ошибок:
        /// • Если параметр не содержит значение → возвращает null
        /// • Если значение не соответствует формату (не 2 части) → возвращает null
        /// • Если первая часть не является числом → возвращает null
        /// • Если вторая часть не является числом → возвращает null
        /// 
        /// EN: Parses the device address from the ARBM_ПСБ_Адрес устройства parameter.
        /// 
        /// Expected parameter value format: CONTROLLER.LOOP
        /// The address determines the hierarchical position of the device in the system:
        /// • First part (CONTROLLER) — controller number
        /// • Second part (LOOP) — loop number on the controller
        /// 
        /// Examples:
        /// • "1.1" → DeviceAddress { ControllerNumber = 1, LoopNumber = 1 }
        /// • "2.3" → DeviceAddress { ControllerNumber = 2, LoopNumber = 3 }
        /// 
        /// Parsing process:
        /// 1) Extracts the value of Param_DeviceAddress parameter from the device family
        /// 2) Checks that the value is not empty or null
        /// 3) Splits the string into two parts using the '.' separator
        /// 4) Verifies that exactly 2 parts were obtained
        /// 5) Converts the first part (controller number) to an integer
        /// 6) Converts the second part (loop number) to an integer
        /// 7) If all validation checks pass, returns a DeviceAddress object
        /// 
        /// Error handling:
        /// • If parameter has no value → returns null
        /// • If value does not match format (not 2 parts) → returns null
        /// • If the first part is not a number → returns null
        /// • If the second part is not a number → returns null
        /// </summary>
        /// <param name="device">
        /// Семейство устройства (FamilyInstance), из которого извлекается адрес.
        /// EN: Device family instance from which the address is extracted.
        /// </param>
        /// <returns>
        /// Объект DeviceAddress с распарсенными номерами контроллера и петли,
        /// либо null если парсинг не удался.
        /// EN: DeviceAddress object with parsed controller and loop numbers,
        /// or null if parsing failed.
        /// </returns>
        /// <example>
        /// Пример использования:
        /// EN: Usage example:
        /// <code>
        /// var parser = new SecurityElementParser();
        /// var device = /* получить FamilyInstance устройства */;
        /// var deviceAddr = parser.ParseDeviceAddress(device);
        /// 
        /// if (deviceAddr != null)
        /// {
        ///     Console.WriteLine($"Контроллер: {deviceAddr.ControllerNumber}");
        ///     Console.WriteLine($"Петля: {deviceAddr.LoopNumber}");
        /// }
        /// </code>
        /// </example>
        public DeviceAddress ParseDeviceAddress(FamilyInstance device)
        {
            // Получаем значение параметра адреса устройства
            // EN: Get the device address parameter value
            string address = GetStringParam(device, Param_DeviceAddress);
            
            // Если параметр пуст или null — парсинг невозможен
            // EN: If parameter is empty or null — parsing is not possible
            if (string.IsNullOrWhiteSpace(address))
                return null;

            // Разбиваем строку по разделителю '.',
            // Ожидаемый формат: CONTROLLER.LOOP
            // EN: Split the string by '.' separator
            // Expected format: CONTROLLER.LOOP
            var parts = address.Split('.');
            
            // Проверяем, что получилось ровно 2 части
            // EN: Verify that exactly 2 parts were obtained
            if (parts.Length != 2)
                return null;

            // Преобразуем первую часть в число (номер контроллера)
            // EN: Convert the first part to integer (controller number)
            if (!int.TryParse(parts[0], out int controllerNumber))
                return null;

            // Преобразуем вторую часть в число (номер петли)
            // EN: Convert the second part to integer (loop number)
            if (!int.TryParse(parts[1], out int loopNumber))
                return null;

            // Все проверки пройдены успешно — возвращаем распарсенный объект
            // EN: All validation checks passed — return the parsed object
            return new DeviceAddress
            {
                ControllerNumber = controllerNumber,
                LoopNumber = loopNumber
            };
        }

        /// <summary>
        /// Извлекает номер этажа из параметра ADSK_Этаж элемента.
        /// 
        /// Этот метод используется для определения этажа (уровня в Revit), на котором размещен элемент.
        /// Номер этажа необходим для правильного построения кабельных цепей и маршрутизации.
        /// 
        /// Процесс извлечения:
        /// 1) Получает значение параметра ADSK_Этаж из элемента
        /// 2) Пытается преобразовать строку в целое число
        /// 3) Если преобразование успешно, возвращает номер этажа
        /// 4) Если преобразование не удалось, возвращает null
        /// 
        /// EN: Extracts the floor number from the ADSK_Этаж parameter of the element.
        /// 
        /// This method is used to determine the floor (level in Revit) on which the element is located.
        /// The floor number is necessary for correct construction of cable chains and routing.
        /// 
        /// Extraction process:
        /// 1) Gets the value of ADSK_Этаж parameter from the element
        /// 2) Attempts to convert the string to an integer
        /// 3) If conversion is successful, returns the floor number
        /// 4) If conversion fails, returns null
        /// </summary>
        /// <param name="element">
        /// Элемент Revit (Element), из которого извлекается номер этажа.
        /// EN: Revit element (Element) from which the floor number is extracted.
        /// </param>
        /// <returns>
        /// Номер этажа (целое число), либо null если параметр не содержит числовое значение.
        /// EN: Floor number (integer), or null if the parameter does not contain a numeric value.
        /// </returns>
        /// <example>
        /// Пример использования:
        /// EN: Usage example:
        /// <code>
        /// var parser = new SecurityElementParser();
        /// var element = /* получить Element из Revit */;
        /// var floorNum = parser.GetFloorNumber(element);
        /// 
        /// if (floorNum.HasValue)
        /// {
        ///     Console.WriteLine($"Элемент на этаже: {floorNum.Value}");
        /// }
        /// else
        /// {
        ///     Console.WriteLine("Номер этажа не определен");
        /// }
        /// </code>
        /// </example>
        public int? GetFloorNumber(Element element)
        {
            // Получаем значение параметра этажа как строку
            // EN: Get the floor parameter value as a string
            string floorStr = GetStringParam(element, Param_Floor);
            
            // Пытаемся преобразовать строку в число
            // EN: Attempt to convert the string to an integer
            if (int.TryParse(floorStr, out int floor))
                return floor;

            // Преобразование не удалось — возвращаем null (nullable int)
            // EN: Conversion failed — return null (nullable int)
            return null;
        }

        /// <summary>
        /// Безопасно извлекает значение строкового параметра из элемента Revit.
        /// 
        /// Этот метод является вспомогательной функцией для получения значений параметров
        /// из семейств и элементов Revit с обработкой исключений и null-значений.
        /// 
        /// Процесс извлечения:
        /// 1) Ищет параметр по имени в элементе с помощью LookupParameter
        /// 2) Если параметр найден, получает его строковое значение через AsString()
        /// 3) Если параметр не найден, возвращает null
        /// 4) Если при получении значения возникает исключение, оно распространяется дальше
        /// 
        /// Особенности:
        /// • Метод использует null-conditional оператор (?.), что обеспечивает безопасный доступ
        /// • Если параметр не существует, метод возвращает null вместо выброса исключения
        /// • Пустые значения параметров возвращаются как пустые строки (не null)
        /// 
        /// EN: Safely extracts the value of a string parameter from a Revit element.
        /// 
        /// This method is a helper function for retrieving parameter values
        /// from Revit families and elements with exception and null-value handling.
        /// 
        /// Extraction process:
        /// 1) Searches for a parameter by name in the element using LookupParameter
        /// 2) If parameter is found, gets its string value through AsString()
        /// 3) If parameter is not found, returns null
        /// 4) If an exception occurs while getting the value, it is propagated further
        /// 
        /// Features:
        /// • The method uses the null-conditional operator (?.) which provides safe access
        /// • If a parameter does not exist, the method returns null instead of throwing an exception
        /// • Empty parameter values are returned as empty strings (not null)
        /// </summary>
        /// <param name="e">
        /// Элемент Revit (Element или его подкласс), из которого извлекается параметр.
        /// EN: Revit element (Element or its subclass) from which the parameter is extracted.
        /// </param>
        /// <param name="paramName">
        /// Имя параметра для поиска. Должно быть точным именем параметра в Revit.
        /// EN: Name of the parameter to search for. Must be the exact parameter name in Revit.
        /// </param>
        /// <returns>
        /// Строковое значение параметра, либо null если параметр не найден.
        /// EN: String value of the parameter, or null if the parameter is not found.
        /// </returns>
        /// <example>
        /// Пример использования:
        /// EN: Usage example:
        /// <code>
        /// var parser = new SecurityElementParser();
        /// var element = /* получить Element из Revit */;
        /// 
        /// string controllerName = parser.GetStringParam(element, "ARBM_ПС_Имя изделия");
        /// if (!string.IsNullOrEmpty(controllerName))
        /// {
        ///     Console.WriteLine($"Имя контроллера: {controllerName}");
        /// }
        /// 
        /// string unknownParam = parser.GetStringParam(element, "НесуществующийПараметр");
        /// // unknownParam будет null
        /// </code>
        /// </example>
        public string GetStringParam(Element e, string paramName)
        {
            // Ищем параметр по имени в элементе
            // EN: Search for the parameter by name in the element
            var p = e.LookupParameter(paramName);
            
            // Если параметр найден, возвращаем его строковое значение
            // Если параметр не найден, возвращаем null (благодаря null-conditional оператору ?.)
            // EN: If parameter is found, return its string value
            // If parameter is not found, return null (thanks to the null-conditional operator ?.)
            return p?.AsString();
        }
    }

}
