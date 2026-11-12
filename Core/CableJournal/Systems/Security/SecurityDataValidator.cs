using TaskDialog = PluginForRevit2022.Core.CableJournal.JournalDialog;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Systems.Security.Models;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Валидирует параметры кабельных элементов перед обработкой.
    /// </summary>
    public class SecurityDataValidator
    {
        // Регулярные выражения для проверки формата
        private static readonly Regex ControllerNameRegex = new Regex(@"^ARK\.-?\d+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex DeviceAddressRegex = new Regex(@"^\d+\.\d+$", RegexOptions.Compiled);
        private static readonly Regex ElementMarkRegex = new Regex(@"^\d+$", RegexOptions.Compiled);

        /// <summary>
        /// Валидирует контроллеры (OST_ElectricalEquipment) и их петли.
        /// Проверяет:
        /// 1. Заполнение параметра ARBM_ПС_Имя изделия (не пусто)
        /// 2. Формат ARBM_ПС_Имя изделия (ARK.-N.N или ARK.N.N)
        /// 3. Уникальность имён контроллеров в выборке
        /// 4. Совпадение ADSK_Этаж с первым числом в ARBM_ПС_Имя изделия (FloorNumber)
        /// 5. Наличие хотя бы одной петли у каждого контроллера
        /// </summary>
        public static bool ValidateControllers(List<FamilyInstance> controllers, List<ControllerWithLoops> controllersWithLoops = null)
        {
            var errors = new List<string>();
            var controllerNames = new Dictionary<string, List<ElementId>>();

            foreach (var ctrl in controllers)
            {
                string name = ctrl.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                string floorParam = ctrl.LookupParameter("ADSK_Этаж")?.AsString();

                // Проверка 1: Параметр не заполнен (пусто, null или только пробелы)
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors.Add($"❌ Контроллер (ID: {ctrl.Id}):\n" +
                        $"   Параметр 'ARBM_ПС_Имя изделия' не заполнен.\n" +
                        $"   Ожидаемый формат: ARK.N.N или ARK.-N.N (например, ARK.7.4 или ARK.-1.2)");
                    continue;
                }

                // Проверка 2: Пустая строка (после Trim())
                if (name.Trim().Length == 0)
                {
                    errors.Add($"❌ Контроллер (ID: {ctrl.Id}):\n" +
                        $"   Параметр 'ARBM_ПС_Имя изделия' содержит только пробелы.\n" +
                        $"   Ожидаемый формат: ARK.N.N или ARK.-N.N (например, ARK.7.4 или ARK.-1.2)");
                    continue;
                }

                // Проверка 3: Формат имени
                if (!ControllerNameRegex.IsMatch(name))
                {
                    errors.Add($"❌ Контроллер (ID: {ctrl.Id}):\n" +
                        $"   Параметр 'ARBM_ПС_Имя изделия' имеет некорректный формат.\n" +
                        $"   Текущее значение: '{name}'\n" +
                        $"   Ожидаемый формат: ARK.N.N или ARK.-N.N (например, ARK.7.4 или ARK.-1.2)");
                }
                else
                {
                    // Проверка 4: Совпадение ADSK_Этаж с FloorNumber из ARBM_ПС_Имя изделия
                    // Парсим имя для извлечения FloorNumber
                    string[] parts = name.Split('.');
                    if (parts.Length == 3 && int.TryParse(parts[1], out int floorFromName))
                    {
                        if (string.IsNullOrWhiteSpace(floorParam) || !int.TryParse(floorParam, out int floorFromParam))
                        {
                            errors.Add($"❌ Контроллер (ID: {ctrl.Id}):\n" +
                                $"   Параметр 'ADSK_Этаж' не заполнен или содержит некорректное значение.\n" +
                                $"   Текущее значение: '{floorParam}'\n" +
                                $"   Ожидаемое значение: {floorFromName} (из ARBM_ПС_Имя изделия '{name}')");
                        }
                        else if (floorFromName != floorFromParam)
                        {
                            errors.Add($"❌ Контроллер (ID: {ctrl.Id}):\n" +
                                $"   Несовпадение этажей:\n" +
                                $"   ARBM_ПС_Имя изделия: '{name}' → этаж {floorFromName}\n" +
                                $"   ADSK_Этаж: '{floorParam}' → этаж {floorFromParam}\n" +
                                $"   Значения должны совпадать!");
                        }
                        else
                        {
                            // Проверка 5: Уникальность
                            if (!controllerNames.ContainsKey(name))
                                controllerNames[name] = new List<ElementId>();
                            controllerNames[name].Add(ctrl.Id);
                        }
                    }
                }
            }

            // Проверка дублей по имени
            foreach (var kvp in controllerNames.Where(x => x.Value.Count > 1))
            {
                string ids = string.Join(", ", kvp.Value.Select(id => id.ToString()));
                errors.Add($"❌ Дублирование имени контроллера:\n" +
                    $"   Имя: '{kvp.Key}'\n" +
                    $"   Найдено {kvp.Value.Count} контроллеров (IDs: {ids})");
            }

            // Адрес прибора СОТС — «прибор.шлейф», без этажа: два контроллера с одним номером (ARK.1.2 и ARK.3.2)
            // получили бы одни и те же приборы, и шлейфы в журнале задвоились бы.
            foreach (var group in controllerNames
                         .Select(kvp => (name: kvp.Key, ids: kvp.Value, number: kvp.Key.Split('.')))
                         .Where(x => x.number.Length == 3 && int.TryParse(x.number[2], out _))
                         .GroupBy(x => int.Parse(x.number[2]))
                         .Where(g => g.Count() > 1))
            {
                errors.Add($"❌ Одинаковый номер контроллера {group.Key} у разных контроллеров:\n" +
                    $"   {string.Join(", ", group.Select(x => $"'{x.name}' (ID: {string.Join(", ", x.ids.Select(id => id.ToString()))})"))}\n" +
                    $"   Адрес прибора «прибор.шлейф» не указывает этаж, поэтому номер контроллера должен быть уникальным.");
            }

            // Проверка 6: Наличие петель (если передана коллекция с Петлями)
            if (controllersWithLoops != null)
            {
                foreach (var ctrlWithLoops in controllersWithLoops)
                {
                    int totalLoops = ctrlWithLoops.Loops.Count;
                    foreach (var exp in ctrlWithLoops.Expanders)
                        totalLoops += exp.Loops.Count;

                    if (totalLoops == 0)
                    {
                        errors.Add($"❌ Контроллер '{ctrlWithLoops.ControllerId.ControllerPrefix}." +
                            $"{ctrlWithLoops.ControllerId.FloorNumber}." +
                            $"{ctrlWithLoops.ControllerId.ControllerNumber}' (ID: {ctrlWithLoops.Controller.Id}):\n" +
                            $"   У контроллера нет подключенных устройств (петель).\n" +
                            $"   Убедитесь, что к контроллеру подключены устройства с совпадающим адресом.");
                    }
                }
            }

            // Вывод ошибок
            if (errors.Any())
            {
                string message = "⛔ ОШИБКИ ВАЛИДАЦИИ КОНТРОЛЛЕРОВ:\n\n" +
                    string.Join("\n\n", errors) + "\n\n" +
                    "Исправьте ошибки и повторите операцию.";
                TaskDialog.Show("Ошибка валидации контроллеров", message);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Валидирует устройства (OST_DataDevices).
        /// Проверяет:
        /// 1. Заполнение всех требуемых параметров
        /// 2. Формат ARBM_ПСБ_Адрес устройства (N.N)
        /// 3. Формат ARBM_SP_ElementMark (только цифры)
        /// 4. Уникальность комбинации БЦО + Адрес + Марка
        /// </summary>
        public static bool ValidateDevices(List<FamilyInstance> devices)
        {
            var errors = new List<string>();
            var deviceKeys = new Dictionary<string, List<ElementId>>();

            foreach (var dev in devices)
            {
                string floor = dev.LookupParameter("ADSK_Этаж")?.AsString();
                string bco = dev.LookupParameter("ARBM_ПСБ_БЦО")?.AsString();
                string address = dev.LookupParameter("ARBM_ПСБ_Адрес устройства")?.AsString();
                string mark = dev.LookupParameter("ARBM_SP_ElementMark")?.AsString();

                // Проверка 1: Заполнение параметров
                if (string.IsNullOrWhiteSpace(floor))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ADSK_Этаж' не заполнен");
                }

                if (string.IsNullOrWhiteSpace(bco))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ARBM_ПСБ_БЦО' не заполнен");
                }

                if (string.IsNullOrWhiteSpace(address))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ARBM_ПСБ_Адрес устройства' не заполнен");
                }
                else if (!DeviceAddressRegex.IsMatch(address))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ARBM_ПСБ_Адрес устройства' имеет некорректный формат.\n" +
                        $"   Текущее значение: '{address}'\n" +
                        $"   Ожидаемый формат: N.N (например, 1.1)");
                }

                if (string.IsNullOrWhiteSpace(mark))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ARBM_SP_ElementMark' не заполнен");
                }
                else if (!ElementMarkRegex.IsMatch(mark))
                {
                    errors.Add($"❌ Устройство (ID: {dev.Id}):\n" +
                        $"   Параметр 'ARBM_SP_ElementMark' должен содержать только цифры.\n" +
                        $"   Текущее значение: '{mark}'");
                }

                // Проверка 2: Уникальность ключа (только если все параметры заполнены корректно)
                if (!string.IsNullOrWhiteSpace(bco) &&
                    !string.IsNullOrWhiteSpace(address) && DeviceAddressRegex.IsMatch(address) &&
                    !string.IsNullOrWhiteSpace(mark) && ElementMarkRegex.IsMatch(mark))
                {
                    string key = $"{bco}.{address}.{mark}";

                    if (!deviceKeys.ContainsKey(key))
                        deviceKeys[key] = new List<ElementId>();
                    deviceKeys[key].Add(dev.Id);
                }
            }

            // Проверка 3: Дублирование ключей
            foreach (var kvp in deviceKeys.Where(x => x.Value.Count > 1))
            {
                string ids = string.Join(", ", kvp.Value.Select(id => id.ToString()));
                errors.Add($"❌ Дублирование адреса устройства:\n" +
                    $"   Адрес: '{kvp.Key}'\n" +
                    $"   Найдено {kvp.Value.Count} устройств (IDs: {ids})");
            }

            // Вывод ошибок
            if (errors.Any())
            {
                string message = "⛔ ОШИБКИ ВАЛИДАЦИИ УСТРОЙСТВ:\n\n" +
                    string.Join("\n\n", errors) + "\n\n" +
                    "Исправьте ошибки и повторите операцию.";
                TaskDialog.Show("Ошибка валидации устройств", message);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Валидирует трассы (OST_MechanicalEquipment).
        /// Проверяет наличие трасс (кроме переходов).
        /// </summary>
        public static bool ValidateRoutes(List<Element> routes)
        {
            if (!routes.Any())
            {
                string message = "⛔ ОШИБКА ВАЛИДАЦИИ ТРАСС:\n\n" +
                    "В документе не найдено ни одного элемента трассы\n" +
                    "(OST_MechanicalEquipment).\n\n" +
                    "Добавьте трассы и повторите операцию.";
                TaskDialog.Show("Ошибка валидации трасс", message);
                return false;
            }

            return true;
        }
    }
}