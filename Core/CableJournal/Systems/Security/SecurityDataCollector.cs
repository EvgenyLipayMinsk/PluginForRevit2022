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

        public List<ControllerWithLoops> CollectControllersWithLoops(Document doc)
        {
            var result = new List<ControllerWithLoops>();

            // 1. Get all controllers (OST_ElectricalEquipment)
            var allControllers = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
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
                .Where(d =>
                    !string.IsNullOrWhiteSpace(d.LookupParameter("ADSK_Этаж")?.AsString()) &&
                    !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_БЦО")?.AsString()) &&
                    !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_Адрес устройства")?.AsString()) &&
                    !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_SP_ElementMark")?.AsString()))
                .ToList();

            if (!devices.Any())
                return result;

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
                .Where(x => x.Floor.HasValue && x.Address != null)
                .ToList();

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
                            .OrderBy(d => d.LookupParameter("ARBM_SP_ElementMark")?.AsString())
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

            // 1. Get all controllers (OST_ElectricalEquipment)
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
                    if (!string.Equals(comments, param, StringComparison.OrdinalIgnoreCase))
                        return false;

                    return !string.IsNullOrWhiteSpace(d.LookupParameter("ADSK_Этаж")?.AsString()) &&
                           !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_БЦО")?.AsString()) &&
                           !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_ПСБ_Адрес устройства")?.AsString()) &&
                           !string.IsNullOrWhiteSpace(d.LookupParameter("ARBM_SP_ElementMark")?.AsString());
                })
                .ToList();

            if (!devices.Any())
                return result;

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
                .Where(x => x.Floor.HasValue && x.Address != null)
                .ToList();

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
                            .OrderBy(d => d.LookupParameter("ARBM_SP_ElementMark")?.AsString())
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
            if (!SecurityDataValidator.ValidateControllers(controllers, result))
                return new List<ControllerWithLoops>();

            return result
                .OrderBy(c => c.ControllerId.FloorNumber)
                .ThenBy(c => c.ControllerId.ControllerNumber)
                .ToList();
        }
    }
}
