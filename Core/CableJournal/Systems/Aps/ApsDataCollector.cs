using PluginForRevit2022.Core;
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
            _validator = validator ?? new ApsDataValidator(_parser);
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
        public List<ApsControllerWithLoops> CollectApsControllersWithLoopsByParam(Document doc, string param, CancellationMonitor cancel = null, string connectionLine = null)
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
                    .Where(x => x.SuperComponent == null) // вложенные семейства — часть родителя
                    .Where(c =>
                    {
                        string comments = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        return string.Equals(comments?.Trim(), param?.Trim(), StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (connectionLine != null)
                {
                    string[] parts = connectionLine.Split('|');
                    var matchingControllers = allControllers.Where(c =>
                    {
                        var id = _parser.ParseApsControllerId(c);
                        return id != null && id.DeviceNumber.ToString() == parts[2] &&
                            string.Equals(id.BuildingNumber, parts[1], StringComparison.OrdinalIgnoreCase);
                    }).ToList();
                    if (matchingControllers.Count == 0)
                    {
                        var names = allControllers.Select(c => RevitText.FindParameter(c, "ARBM_ПС_Имя изделия")?.AsString())
                            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().Take(10).ToList();
                        string found = names.Count == 0 ? "нет приборов с заполненным именем" : string.Join(", ", names);
                        TaskDialog.Show("Прибор АПС не найден",
                            $"Ожидается прибор «{parts[1]}.ARK.{parts[2]}» с комментарием «{param}». Найденные имена в этой системе: {found}.");
                    }
                    allControllers = matchingControllers;
                }
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
                        string name = RevitText.FindParameter(c, "ARBM_ПС_Имя изделия")?.AsString();
                        string floor = RevitText.FindParameter(c, _parser.FloorParameter)?.AsString();

                        return !string.IsNullOrWhiteSpace(name) &&
                               name != null && name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0 &&
                               int.TryParse(floor, out _);
                    })
                    .ToList();

                if (!controllersWithValidParams.Any())
                {
                    var invalidControllers = allControllers.Except(controllersWithValidParams).ToList();
                    ShowInvalidControllerMessage(invalidControllers, param);
                    return result;
                }
                _skipped.Clear();
                _skippedIds.Clear();
                foreach (var c in allControllers.Except(controllersWithValidParams))
                    _skipped.Add($"ПКП id {c.Id}: пустое «ARBM_ПС_Имя изделия» (или без ARK) либо «{_parser.FloorParameter}» не заполнен числом");

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
                    .Where(x => x.SuperComponent == null) // вложенные семейства — часть родителя
                    .Where(d =>
                    {
                        string comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        if (!string.Equals(comments?.Trim(), param?.Trim(), StringComparison.OrdinalIgnoreCase))
                            return false;

                        if (connectionLine != null && PluginForRevit2022.Core.DeviceLineOperations.KeyOf(d) != connectionLine)
                            return false;
                        string empty = EmptyDeviceParams(d);
                        if (empty != null) _skipped.Add($"устройство id {d.Id}: не заполнено {empty}");
                        return empty == null;
                    })
                    .ToList();

                // Добавляем устройства из категории OST_FireAlarmDevices
                var fireAlarmDevices = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_FireAlarmDevices)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>()
                    .Where(x => x.SuperComponent == null) // вложенные семейства — часть родителя
                    .Where(d =>
                    {
                        string comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                        if (!string.Equals(comments?.Trim(), param?.Trim(), StringComparison.OrdinalIgnoreCase))
                            return false;

                        if (connectionLine != null && PluginForRevit2022.Core.DeviceLineOperations.KeyOf(d) != connectionLine)
                            return false;
                        string empty = EmptyDeviceParams(d);
                        if (empty != null) _skipped.Add($"устройство id {d.Id}: не заполнено {empty}");
                        return empty == null;
                    })
                    .ToList();

                devices.AddRange(fireAlarmDevices);

                if (cancel?.IsCancellationRequested == true)
                    throw new OperationCanceledException("Cancelled after collecting devices.");

                if (!devices.Any())
                {
                    ReportSkipped();
                    if (connectionLine != null && _skipped.Count == 0)
                        TaskDialog.Show("Шлейф АПС", $"Не найдены устройства шлейфа с комментарием «{param}» в категориях «Устройства данных» и «Пожарная сигнализация».");
                    return result;
                }

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
                if (connectionLine == null)
                {
                    var orders = PluginForRevit2022.Core.LineConnectionOrder.Read(doc);
                    foreach (var controller in result)
                    foreach (var loop in controller.Loops)
                    {
                        string key = PluginForRevit2022.Core.DeviceLineOperations.KeyOf(loop.Devices[0].BaseInfo.Element);
                        if (key == null || !orders.TryGetValue(key, out var saved)) continue;
                        try
                        {
                            var byId = loop.Devices.ToDictionary(d => d.BaseInfo.Element.UniqueId);
                            var order = PluginForRevit2022.Core.LineConnectionOrder.Ordered(loop.Devices.Select(d => (FamilyInstance)d.BaseInfo.Element), saved.Order);
                            loop.Devices = order.Select(d => byId[d.UniqueId]).ToList();
                        }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                        {
                            TaskDialog.Show("Порядок подключения", $"Прибор {controller.ControllerId}, шлейф {loop.LoopNumber}. {ex.Message}");
                            return new List<ApsControllerWithLoops>();
                        }
                    }
                }
                if (!_validator.ValidateApsControllersLoops(result))
                    return new List<ApsControllerWithLoops>();

                // Устройства, не попавшие ни к одному ПКП (корпус или номер прибора не совпали — например «М» кириллицей)
                var used = new HashSet<ElementId>(result.SelectMany(c => c.Loops).SelectMany(l => l.Devices).Select(x => x.BaseInfo?.Element?.Id).Where(id => id != null));
                foreach (var d in devices.Where(d => !used.Contains(d.Id) && !_skippedIds.Contains(d.Id)))
                    _skipped.Add($"устройство id {d.Id}: нет ПКП с корпусом «{RevitText.FindParameter(d, "ARBM_Корпус")?.AsString()}» и номером прибора «{RevitText.FindParameter(d, "ARBM_Номер прибора")?.AsString()}» (или имя этого ПКП не разобрано — см. выше)");
                ReportSkipped();

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
                return new List<ApsControllerWithLoops>();
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
        // Что не попало в журнал и почему — одно сообщение в конце сбора (в MCP — в messages)
        private readonly List<string> _skipped = new List<string>();
        private readonly HashSet<ElementId> _skippedIds = new HashSet<ElementId>();

        private string EmptyDeviceParams(FamilyInstance d)
        {
            var empty = new[] { "ARBM_Корпус", "ARBM_Номер прибора", _parser.FloorParameter, "ARBM_ПСБ_БЦО", "ARBM_Номер линии", "ARBM_SP_ElementMark" }
                .Where(p => string.IsNullOrWhiteSpace(RevitText.FindParameter(d, p)?.AsString())).ToList();
            return empty.Count == 0 ? null : string.Join(", ", empty);
        }

        private void ReportSkipped()
        {
            if (_skipped.Count == 0) return;
            TaskDialog.Show("Журнал АПС: элементы не попали в журнал",
                $"Не попали в журнал: {_skipped.Count}.\n\n" + string.Join("\n", _skipped.Take(40)) +
                (_skipped.Count > 40 ? $"\n… и ещё {_skipped.Count - 40}" : ""));
        }

        private List<ApsControllerWithLoops> GroupControllersWithDevices(
            List<FamilyInstance> controllers,
            List<FamilyInstance> devices,
            CancellationMonitor cancel = null)
        {
            var result = new List<ApsControllerWithLoops>();

            // Группируем контроллеры по корпусу, затем по номеру прибора
            var groupedControllers = controllers
                .GroupBy(c => RevitText.FindParameter(c, "ARBM_ПС_Имя изделия")?.AsString())
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
                {
                    _skipped.Add($"ПКП id {controllerElement.Id} «{RevitText.FindParameter(controllerElement, "ARBM_ПС_Имя изделия")?.AsString()}»: не разобрано имя или «{_parser.FloorParameter}» не числом");
                    continue;
                }

                var controllerId = apsController.ControllerId;

                // Находим устройства для этого контроллера
                var controllerDevices = devices
                    .Where(d =>
                    {
                        string devBuilding = RevitText.FindParameter(d, "ARBM_Корпус")?.AsString();
                        int? devController = GetIntParam(d, "ARBM_Номер прибора");

                        return !string.IsNullOrWhiteSpace(devBuilding) &&
                               devBuilding.Trim().Equals(controllerId.BuildingNumber, StringComparison.OrdinalIgnoreCase) &&
                               devController == controllerId.DeviceNumber;
                    })
                    .ToList();

                if (!controllerDevices.Any())
                    continue;

                // Парсим устройства
                var parsedDevices = new List<ApsDeviceInfo>();
                foreach (var d in controllerDevices)
                {
                    if (_parser.ParseDevice(d) is ApsDeviceInfo info) parsedDevices.Add(info);
                    else
                    {
                        _skipped.Add($"устройство id {d.Id}: «{_parser.FloorParameter}», номер линии или номер в линии не числом");
                        _skippedIds.Add(d.Id);
                    }
                }

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
                    Loops = loopGroups
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
                string value = RevitText.FindParameter(element, paramName)?.AsString();
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
                string name = RevitText.FindParameter(ctrl, "ARBM_ПС_Имя изделия")?.AsString();
                string floor = RevitText.FindParameter(ctrl, _parser.FloorParameter)?.AsString();

                errorMessage += $"Прибор «{name ?? ctrl.Name}» (ID {ctrl.Id})\n";

                if (string.IsNullOrWhiteSpace(name))
                    errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не заполнен\n";
                else if (name == null || name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) < 0)
                    errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не содержит 'ARK': {name}\n";

                if (!int.TryParse(floor, out _))
                    errorMessage += $"  ⚠️  '{_parser.FloorParameter}' — укажите номер этажа целым числом (текущее значение: «{floor}»)\n";

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
