using TaskDialog = PluginForRevit2022.Core.CableJournal.JournalDialog;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Revit;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Services
{
    public class SecurityDataCollector
    {
        private readonly SecurityElementParser _parser;

        public SecurityDataCollector(SecurityElementParser parser)
        {
            _parser = parser;
        }

        private static readonly string[] DeviceParams = { "ADSK_Этаж", "ARBM_ПСБ_БЦО", "ARBM_ПСБ_Адрес устройства", "ARBM_SP_ElementMark" };
        private readonly List<string> _skipped = new List<string>();

        /// <summary>Все параметры прибора заполнены; иначе — в отчёт о пропущенных (в режиме «все системы» — только частично заполненные).</summary>
        private bool Complete(FamilyInstance d, bool reportIfPartial)
        {
            var empty = DeviceParams.Where(p => string.IsNullOrWhiteSpace(d.LookupParameter(p)?.AsString())).ToList();
            if (empty.Count == 0) return true;
            // «все системы»: в отчёт — только приборы с адресом, БЦО или номером (этаж заполнен у всего подряд)
            if (!reportIfPartial || empty.Count < DeviceParams.Length - (empty.Contains("ADSK_Этаж") ? 0 : 1))
                _skipped.Add($"прибор id {d.Id}: не заполнено {string.Join(", ", empty)}");
            return false;
        }

        private void ReportSkipped()
        {
            if (_skipped.Count == 0) return;
            TaskDialog.Show("Журнал СОТС: элементы не попали в журнал",
                $"Не попали в журнал: {_skipped.Count}.\n\n" + string.Join("\n", _skipped.Take(40)) +
                (_skipped.Count > 40 ? $"\n… и ещё {_skipped.Count - 40}" : ""));
        }

        public List<ControllerWithLoops> CollectControllersWithLoops(Document doc)
        {
            var result = new List<ControllerWithLoops>();
            _skipped.Clear();

            // 1. Get all controllers (OST_ElectricalEquipment)
            // Без фильтра по «Комментариям» контроллер СОТС — электрооборудование с «ARK» в имени изделия.
            // Щиты ЭОМ и прочее оборудование без этих параметров журнал не проверяет и не обрывают.
            var allControllers = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(c => (c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString() ?? "").Trim().StartsWith("ARK.", StringComparison.OrdinalIgnoreCase)) // АПС — «M2.ARK.3», не СОТС
                .ToList();

            if (!allControllers.Any())
                return result;

            // ✅ ПРОВЕРКА: контроллеры с пустыми параметрами
            var controllersWithEmptyParams = allControllers
                .Where(c =>
                {
                    string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = c.LookupParameter("ADSK_Этаж")?.AsString();

                    // Если есть хотя бы один пустой параметр
                    return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(floor);
                })
                .ToList();

            if (controllersWithEmptyParams.Any())
            {
                var errorMessage = "⛔ ОШИБКА: Обнаружены контроллеры с пустыми параметрами:\n\n";
                
                foreach (var ctrl in controllersWithEmptyParams)
                {
                    string name = ctrl.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = ctrl.LookupParameter("ADSK_Этаж")?.AsString();

                    errorMessage += $"ID: {ctrl.Id}\n";
                    
                    if (string.IsNullOrWhiteSpace(name))
                        errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не заполнен\n";
                    
                    if (string.IsNullOrWhiteSpace(floor))
                        errorMessage += $"  ⚠️  'ADSK_Этаж' — не заполнен\n";
                    
                    errorMessage += "\n";
                }

                errorMessage += "Заполните пустые параметры и повторите операцию.";
                
                TaskDialog.Show("Ошибка валидации контроллеров", errorMessage);
                return result;
            }

            // 2. Filter controllers with valid parameters
            var controllers = allControllers
                .Where(c =>
                {
                    string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = c.LookupParameter("ADSK_Этаж")?.AsString();

                    return !string.IsNullOrWhiteSpace(name)
                           && name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0
                           && !string.IsNullOrWhiteSpace(floor);
                })
                .ToList();

            if (!controllers.Any())
                return result;

            // ✅ ВАЛИДАЦИЯ: контроллеры (формат и уникальность)
            if (!SecurityDataValidator.ValidateControllers(controllers))
                return result;

            // 3. Get all data devices (OST_DataDevices)
            var devices = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DataDevices)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(d => Complete(d, reportIfPartial: true))
                .ToList();

            if (!devices.Any())
            {
                ReportSkipped();
                return result;
            }

            // ✅ ВАЛИДАЦИЯ: устройства
            if (!SecurityDataValidator.ValidateDevices(devices))
                return result;

            // 4. Parse all devices with their floor and address
            var parsedDevices = devices
                .Select(d => new
                {
                    Device = d,
                    Floor = _parser.GetFloorNumber(d),
                    Address = _parser.ParseDeviceAddress(d)
                })
                .ToList();
            foreach (var x in parsedDevices.Where(x => !x.Floor.HasValue || x.Address == null))
                _skipped.Add($"прибор id {x.Device.Id}: " + (!x.Floor.HasValue ? "«ADSK_Этаж» не числом" : "адрес не вида «прибор.шлейф»"));
            parsedDevices = parsedDevices.Where(x => x.Floor.HasValue && x.Address != null).ToList();

            // 5. For each controller, find matching devices on ALL floors
            foreach (var controller in controllers)
            {
                var controllerId = _parser.ParseControllerId(controller);
                if (controllerId == null)
                    continue;

                var matchedDevices = parsedDevices
                    .Where(x => x.Address.ControllerNumber == controllerId.ControllerNumber)
                    .ToList();

                if (!matchedDevices.Any())
                    continue;

                var loopGroups = matchedDevices
                    .GroupBy(x => x.Address.LoopNumber)
                    .OrderBy(g => g.Key)
                    .Select(g => new LoopGroup
                    {
                        LoopNumber = g.Key,
                        Devices = g.Select(x => x.Device)
                            // Номер в шлейфе — число: 1, 2, …, 10 (по строке было бы 1, 10, 11, 2).
                            .OrderBy(d => int.TryParse(d.LookupParameter("ARBM_SP_ElementMark")?.AsString()?.Trim(), out int n) ? n : int.MaxValue)
                            .ThenBy(d => d.LookupParameter("ARBM_SP_ElementMark")?.AsString())
                            .ToList()
                    })
                    .ToList();

                if (!loopGroups.Any())
                    continue;

                var controllerWithLoops = new ControllerWithLoops
                {
                    Controller = controller,
                    ControllerId = controllerId,
                    Loops = new List<LoopGroup>(),
                    Expanders = new List<Expander>()
                };

                var mainLoops = loopGroups.Take(8).ToList();
                controllerWithLoops.Loops.AddRange(mainLoops);

                var remaining = loopGroups.Skip(8).ToList();
                int expanderIndex = 1;

                while (remaining.Any())
                {
                    var chunk = remaining.Take(8).ToList();
                    remaining = remaining.Skip(8).ToList();

                    var expanderId = new ExpanderId
                    {
                        FloorNumber = controllerId.FloorNumber,
                        ControllerNumber = controllerId.ControllerNumber,
                        ExpanderNumber = expanderIndex
                    };

                    var expander = new Expander
                    {
                        ExpanderId = expanderId,
                        Loops = chunk
                    };

                    controllerWithLoops.Expanders.Add(expander);
                    expanderIndex++;
                }

                result.Add(controllerWithLoops);
            }

            // ✅ ВАЛИДАЦИЯ: проверка наличия петель у контроллеров
            var matchedNumbers = new HashSet<int>(result.Select(c => c.ControllerId.ControllerNumber));
            foreach (var x in parsedDevices.Where(x => !matchedNumbers.Contains(x.Address.ControllerNumber)))
                _skipped.Add($"прибор id {x.Device.Id}: нет контроллера с номером {x.Address.ControllerNumber} (адрес «{x.Device.LookupParameter("ARBM_ПСБ_Адрес устройства")?.AsString()}»)");
            ReportSkipped();

            if (!SecurityDataValidator.ValidateControllers(controllers, result))
                return new List<ControllerWithLoops>();

            return result
                .OrderBy(c => c.ControllerId.FloorNumber)
                .ThenBy(c => c.ControllerId.ControllerNumber)
                .ToList();
        }

        public List<ControllerWithLoops> CollectControllersWithLoopsByParam(Document doc, string param)
        {
            var result = new List<ControllerWithLoops>();
            _skipped.Clear();

            // 1. Get all controllers (OST_ElectricalEquipment)
            var allControllers = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(c =>
                {
                    string comments = c.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    return string.Equals(comments?.Trim(), param?.Trim(), StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (!allControllers.Any())
                return result;

            // ✅ ПРОВЕРКА: контроллеры с пустыми параметрами
            var controllersWithEmptyParams = allControllers
                .Where(c =>
                {
                    string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = c.LookupParameter("ADSK_Этаж")?.AsString();

                    // Если есть хотя бы один пустой параметр
                    return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(floor);
                })
                .ToList();

            if (controllersWithEmptyParams.Any())
            {
                var errorMessage = $"⛔ ОШИБКА: Обнаружены контроллеры с пустыми параметрами (Comments = \"{param}\"):\n\n";
                
                foreach (var ctrl in controllersWithEmptyParams)
                {
                    string name = ctrl.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = ctrl.LookupParameter("ADSK_Этаж")?.AsString();

                    errorMessage += $"ID: {ctrl.Id}\n";
                    
                    if (string.IsNullOrWhiteSpace(name))
                        errorMessage += $"  ⚠️  'ARBM_ПС_Имя изделия' — не заполнен\n";
                    
                    if (string.IsNullOrWhiteSpace(floor))
                        errorMessage += $"  ⚠️  'ADSK_Этаж' — не заполнен\n";
                    
                    errorMessage += "\n";
                }

                errorMessage += "Заполните пустые параметры и повторите операцию.";
                
                TaskDialog.Show("Ошибка валидации контроллеров", errorMessage);
                return result;
            }

            // 2. Filter controllers with valid parameters
            var controllers = allControllers
                .Where(c =>
                {
                    string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    string floor = c.LookupParameter("ADSK_Этаж")?.AsString();

                    return !string.IsNullOrWhiteSpace(name)
                           && name.IndexOf("ARK", StringComparison.OrdinalIgnoreCase) >= 0
                           && !string.IsNullOrWhiteSpace(floor);
                })
                .ToList();

            if (!controllers.Any())
                return result;

            // ✅ ВАЛИДАЦИЯ: контроллеры (формат и уникальность)
            if (!SecurityDataValidator.ValidateControllers(controllers))
                return result;

            // 3. Collect devices (OST_DataDevices)
            var devices = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_DataDevices)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(d =>
                {
                    string comments = d.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    if (!string.Equals(comments?.Trim(), param?.Trim(), StringComparison.OrdinalIgnoreCase))
                        return false;

                    return Complete(d, reportIfPartial: false);
                })
                .ToList();

            if (!devices.Any())
            {
                ReportSkipped();
                return result;
            }

            // ✅ ВАЛИДАЦИЯ: устройства
            if (!SecurityDataValidator.ValidateDevices(devices))
                return result;

            // 4. Parse all devices with their floor and address
            var parsedDevices = devices
                .Select(d => new
                {
                    Device = d,
                    Floor = _parser.GetFloorNumber(d),
                    Address = _parser.ParseDeviceAddress(d)
                })
                .ToList();
            foreach (var x in parsedDevices.Where(x => !x.Floor.HasValue || x.Address == null))
                _skipped.Add($"прибор id {x.Device.Id}: " + (!x.Floor.HasValue ? "«ADSK_Этаж» не числом" : "адрес не вида «прибор.шлейф»"));
            parsedDevices = parsedDevices.Where(x => x.Floor.HasValue && x.Address != null).ToList();

            // 5. Match controllers with devices (on ALL floors)
            foreach (var controller in controllers)
            {
                var controllerId = _parser.ParseControllerId(controller);
                if (controllerId == null)
                    continue;

                var matchedDevices = parsedDevices
                    .Where(x => x.Address.ControllerNumber == controllerId.ControllerNumber)
                    .ToList();

                if (!matchedDevices.Any())
                    continue;

                var loopGroups = matchedDevices
                    .GroupBy(x => x.Address.LoopNumber)
                    .OrderBy(g => g.Key)
                    .Select(g => new LoopGroup
                    {
                        LoopNumber = g.Key,
                        Devices = g.Select(x => x.Device)
                            // Номер в шлейфе — число: 1, 2, …, 10 (по строке было бы 1, 10, 11, 2).
                            .OrderBy(d => int.TryParse(d.LookupParameter("ARBM_SP_ElementMark")?.AsString()?.Trim(), out int n) ? n : int.MaxValue)
                            .ThenBy(d => d.LookupParameter("ARBM_SP_ElementMark")?.AsString())
                            .ToList()
                    })
                    .ToList();

                if (!loopGroups.Any())
                    continue;

                var controllerWithLoops = new ControllerWithLoops
                {
                    Controller = controller,
                    ControllerId = controllerId,
                    Loops = new List<LoopGroup>(),
                    Expanders = new List<Expander>()
                };

                var mainLoops = loopGroups.Take(8).ToList();
                controllerWithLoops.Loops.AddRange(mainLoops);

                var remaining = loopGroups.Skip(8).ToList();
                int expanderIndex = 1;

                while (remaining.Any())
                {
                    var chunk = remaining.Take(8).ToList();
                    remaining = remaining.Skip(8).ToList();

                    var expanderId = new ExpanderId
                    {
                        FloorNumber = controllerId.FloorNumber,
                        ControllerNumber = controllerId.ControllerNumber,
                        ExpanderNumber = expanderIndex
                    };

                    var expander = new Expander
                    {
                        ExpanderId = expanderId,
                        Loops = chunk
                    };

                    controllerWithLoops.Expanders.Add(expander);
                    expanderIndex++;
                }

                result.Add(controllerWithLoops);
            }

            // ✅ ВАЛИДАЦИЯ: проверка наличия петель у контроллеров
            var matchedNumbers = new HashSet<int>(result.Select(c => c.ControllerId.ControllerNumber));
            foreach (var x in parsedDevices.Where(x => !matchedNumbers.Contains(x.Address.ControllerNumber)))
                _skipped.Add($"прибор id {x.Device.Id}: нет контроллера с номером {x.Address.ControllerNumber} (адрес «{x.Device.LookupParameter("ARBM_ПСБ_Адрес устройства")?.AsString()}»)");
            ReportSkipped();

            if (!SecurityDataValidator.ValidateControllers(controllers, result))
                return new List<ControllerWithLoops>();

            return result
                .OrderBy(c => c.ControllerId.FloorNumber)
                .ThenBy(c => c.ControllerId.ControllerNumber)
                .ToList();
        }
    }
}
