using TaskDialog = PluginForRevit2022.Core.CableJournal.JournalDialog;
using ARBM.CableJournalExporter.Models;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Коллектор данных для системы АПС (Автоматическая пожарная сигнализация).
    /// 
    /// Отвечает за сбор, валидацию и структурирование контроллеров и устройств АПС из документа Revit.
    /// 
    /// Основной рабочий процесс:
    /// 1. Собирает ПКП из категории OST_ElectricalEquipment
    /// 2. Собирает устройства из категорий OST_DataDevices и OST_FireAlarmDevices
    /// 3. Валидирует корректность данных
    /// 4. Группирует устройства по ПКП и объектовым линиям
    /// 5. Возвращает структурированный список ApsControllerWithLoops
    /// 
    /// EN: Data collector for APS (Automatic Fire Alarm System).
    /// 
    /// Responsible for collecting, validating and structuring APS controllers and devices from Revit document.
    /// </summary>
    public class ApsDataCollector
    {
        private readonly ApsElementParser _parser;
        private readonly ApsDataValidator _validator;

        public ApsDataCollector(ApsElementParser parser = null, ApsDataValidator validator = null)
        {
            _parser = parser ?? new ApsElementParser();
            _validator = validator ?? new ApsDataValidator();
        }

        /// <summary>
        /// Собирает контроллеры с их объектными линиями и устройствами, отфильтрованные по параметру Comments.
        ///
        /// Добавлена опция cooperative cancellation: если передан <paramref name="cancel"/>,
        /// долгие циклы периодически проверяют флаг и бросают <see cref="OperationCanceledException"/>.
        /// </summary>
        /// <param name="doc">Документ Revit.</param>
        /// <param name="param">Значение параметра Comments для фильтрации.</param>
        /// <param name="cancel">Опциональный монитор отмены.</param>
        /// <returns>Список контроллеров с группировкой петель и устройств.</returns>
        public List<ApsControllerWithLoops> CollectApsControllersWithLoopsByParam(Document doc, string param, CancellationMonitor cancel = null)
        {
            var result = new List<ApsControllerWithLoops>();

            if (string.IsNullOrWhiteSpace(param))
                return result;

            try
            {
                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled before collecting controllers.");

                // =================================================================
                // 1.2 - 1.3: Получить все ПКП категории OST_ElectricalEquipment с фильтром по Comments
                // Get all APS controllers (OST_ElectricalEquipment) filtered by Comments
                // =================================================================
                var allControllers = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(c =>
                    {
                        string comments = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        return string.Equals(comments, param, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (!allControllers.Any())
                    return result;

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after collecting controllers list.");

                // =================================================================
                // 1.4: Проверить наличие и заполнение параметров ПКП
                // Validate controller parameters
                // =================================================================
                var controllersWithValidParams = allControllers
                    .Where(c =>
                    {
                        string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                        string floor = c.LookupParameter("ADSK_Этаж")?.AsString();

                        return !string.IsNullOrWhiteSpace(name) &&
                               name != null && name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               !string.IsNullOrWhiteSpace(floor);
                    })
                    .ToList();

                if (!controllersWithValidParams.Any())
                {
                    var invalidControllers = allControllers.Except(controllersWithValidParams).ToList();
                    ShowInvalidControllerMessage(invalidControllers, param);
                    return result;
                }

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after validating controllers.");

                // =================================================================
                // 1.5: Валидировать контроллеры АПС
                // Validate APS controllers
                // =================================================================
                if (!_validator.ValidateApsControllers(controllersWithValidParams))
                    return result;

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled before collecting devices.");

                // =================================================================
                // 1.6: Получить устройства АПС с проверкой требуемых параметров
                // Get APS devices with parameter validation
                // =================================================================
                var devices = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_DataDevices)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(d =>
                    {
                        string comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        if (!string.Equals(comments, param, StringComparison.OrdinalIgnoreCase))
                            return false;

                        return !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Корпус")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Номер прибора")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ADSK_Этаж")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_БЦО")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Номер линии")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_SP_ElementMark")?.AsString());
                    })
                    .ToList();

                // Добавляем устройства из категории OST_FireAlarmDevices
                var fireAlarmDevices = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(d =>
                    {
                        string comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        if (!string.Equals(comments, param, StringComparison.OrdinalIgnoreCase))
                            return false;

                        return !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Корпус")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Номер прибора")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ADSK_Этаж")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_БЦО")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_Номер линии")?.AsString()) &&
                               !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_SP_ElementMark")?.AsString());
                    })
                    .ToList();

                devices.AddRange(fireAlarmDevices);

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after collecting devices.");

                if (!devices.Any())
                    return result;

                // =================================================================
                // 1.7: Валидировать устройства АПС
                // Validate APS devices
                // =================================================================
                if (!_validator.ValidateApsDevices(devices))
                    return result;

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled before grouping controllers and devices.");

                // =================================================================
                // 1.8: Группировать контроллеры и устройства в ApsControllerWithLoops
                // Group controllers and devices into ApsControllerWithLoops
                // =================================================================
                result = GroupControllersWithDevices(controllersWithValidParams, devices, cancel);

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after grouping controllers and devices.");

                // =================================================================
                // 1.9: Валидировать наличие петель и количество устройств в петле
                // Validate loops and device count per loop
                // =================================================================
                if (!_validator.ValidateApsControllersLoops(result))
                    return new List<ApsControllerWithLoops>();

                return result
                    .OrderBy(c => c.ControllerId.BuildingNumber)
                    .ThenBy(c => c.ControllerId.DeviceNumber)
                    .ToList();
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException)
                {
                    // cooperative cancel — log and rethrow so caller can handle
                    try { ExecutionLogger.Log("CollectApsControllers", "cancelled", details: "operation cancelled by monitor"); } catch { }
                    throw;
                }

                TaskDialog.Show("Error",
                    $"Error collecting APS controllers and devices: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Группирует контроллеры и устройства в структуру ApsControllerWithLoops.
        /// 
        /// Алгоритм группирования:
        /// 1. Группирует контроллеры по номеру корпуса (BuildingCode)
        /// 2. Внутри каждой группы сортирует по номеру прибора (DeviceNumber)
        /// 3. Для каждого контроллера находит его устройства
        /// 4. Проверяет, не превышает ли ARBM_Номер линии MaxObjectLines
        /// 5. Группирует устройства по объектовым линиям
        /// 6. Создаёт ApsControllerWithLoops с ApsLoopGroup
        /// 
        /// EN: Groups controllers and devices into ApsControllerWithLoops structure.
        /// 
        /// Добавлен optional параметр cancel для cooperative cancellation: метод периодически проверяет флаг.
        /// </summary>
        private List<ApsControllerWithLoops> GroupControllersWithDevices(
            List<FamilyInstance> controllers,
            List<FamilyInstance> devices,
            CancellationMonitor cancel = null)
        {
            var result = new List<ApsControllerWithLoops>();

            // Группируем контроллеры по корпусу, затем по номеру прибора
            var groupedControllers = controllers
                .GroupBy(c => c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString())
                .OrderBy(g => g.Key)
                .SelectMany(g => g)
                .ToList();

            foreach (var controllerElement in groupedControllers)
            {
                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled during grouping controllers.");

                // Парсим контроллер
                var apsController = _parser.ParseController(controllerElement) as ApsController;
                if (apsController == null)
                    continue;

                var controllerId = apsController.ControllerId;

                // Находим устройства для этого контроллера
                var controllerDevices = devices
                    .Where(d =>
                    {
                        string devBuilding = d.LookupParameter("ARBM_Корпус")?.AsString();
                        int? devController = GetIntParam(d, "ARBM_Номер прибора");

                        return !string.IsNullOrWhiteSpace(devBuilding) &&
                               devBuilding.Equals(controllerId.BuildingNumber.ToString(), StringComparison.OrdinalIgnoreCase) &&
                               devController == controllerId.DeviceNumber;
                    })
                    .ToList();

                if (!controllerDevices.Any())
                    continue;

                // Парсим устройства
                var parsedDevices = controllerDevices
                    .Select(d => _parser.ParseDevice(d) as ApsDeviceInfo)
                    .Where(d => d != null)
                    .ToList();

                if (!parsedDevices.Any())
                    continue;

                // ⛔ КРИТИЧЕСКАЯ ПРОВЕРКА: номер линии превышает MaxObjectLines
                // Это критическая ошибка — нельзя продолжать выполнение
                var deviceLinesExceeding = parsedDevices
                    .Where(d => d.LineNumber > apsController.Capabilities.MaxObjectLines)
                    .ToList();

                if (deviceLinesExceeding.Any())
                {
                    // Показываем ошибку и завершаем вызывающий метод
                    ShowLineNumberExceedingMessage(deviceLinesExceeding, apsController);
                    return new List<ApsControllerWithLoops>(); // ⛔ Завершаем метод
                }

                // Группируем устройства по номерам линий
                var loopGroups = parsedDevices
                    .GroupBy(d => d.LineNumber)
                    .OrderBy(g => g.Key)
                    .Select(g => new ApsLoopGroup
                    {
                        LoopNumber = g.Key,
                        TypeLoop = TypeLoop.Annular,
                        Devices = g.OrderBy(d => d.DeviceNumberInLine)
                            .ToList()
                    })
                    .ToList();

                var controllerWithLoops = new ApsControllerWithLoops
                {
                    Controller = apsController,
                    ControllerId = controllerId,
                    Loops = loopGroups,
                    Expanders = new List<ApsExpander>()
                };

                result.Add(controllerWithLoops);
            }

            return result;
        }

        /// <summary>
        /// Получает целочисленный параметр из элемента.
        /// EN: Gets integer parameter from element.
        /// </summary>
        private int? GetIntParam(Element element, string paramName)
        {
            try
            {
                string value = element.LookupParameter(paramName)?.AsString();
                if (string.IsNullOrWhiteSpace(value))
                    return null;

                return int.TryParse(value, out int result) ? result : null as int?;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Показывает сообщение об ошибке для контроллеров с пустыми параметрами.
        /// EN: Shows error message for controllers with empty parameters.
        /// </summary>
        private void ShowInvalidControllerMessage(List<FamilyInstance> invalidControllers, string param)
        {
            var errorMessage = $"⛔ ОШИБКА: Обнаружены контроллеры с пустыми/неправильными параметрами (Comments = \"{param}\"):\n\n";

            foreach (var ctrl in invalidControllers)
            {
                string name = ctrl.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                string floor = ctrl.LookupParameter("ADSK_Этаж")?.AsString();

                errorMessage += $"ID: {ctrl.Id}\n";

                if (string.IsNullOrWhiteSpace(name))
                    errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не заполнен\n";
                else if (name == null || name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) < 0)
                    errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не содержит 'ARK': {name}\n";

                if (string.IsNullOrWhiteSpace(floor))
                    errorMessage += $"  ⚠️  'ADSK_Этаж' — не заполнен\n";

                errorMessage += "\n";
            }

            errorMessage += "Заполните пустые параметры и повторите операцию.";

            TaskDialog.Show("Ошибка валидации контроллеров", errorMessage);
        }

        /// <summary>
        /// Показывает сообщение об ошибке для устройств с номером линии, превышающим максимум.
        /// Это критическая ошибка, требующая исправления данных.
        /// EN: Shows error message for devices with line number exceeding maximum.
        /// This is a critical error requiring data correction.
        /// </summary>
        private void ShowLineNumberExceedingMessage(List<ApsDeviceInfo> devices, ApsController controller)
        {
            var errorMessage = $"⛔ КРИТИЧЕСКАЯ ОШИБКА: Обнаружены устройства с номером линии, превышающим MaxObjectLines\n\n" +
                             $"Контроллер: {controller.ControllerId}\n" +
                             $"Максимальное количество линий: {controller.Capabilities.MaxObjectLines}\n\n" +
                             $"Проблемные устройства:\n";

            foreach (var device in devices)
            {
                errorMessage += $"\n  ID элемента: {device.BaseInfo.Element.Id}\n";
                errorMessage += $"  Имя: {device.BaseInfo.Name}\n";
                errorMessage += $"  ARBM_Номер линии: {device.LineNumber}\n";
            }

            errorMessage += $"\n\n⚠️ ВЫПОЛНЕНИЕ КОМАНДЫ ОСТАНОВЛЕНО\n" +
                           $"Исправьте номера линий в параметре ARBM_Номер линии и повторите операцию.";

            TaskDialog.Show("Критическая ошибка валидации линий", errorMessage);
        }
    }
}
