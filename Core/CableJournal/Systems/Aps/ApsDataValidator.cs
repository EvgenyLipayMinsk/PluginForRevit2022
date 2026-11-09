using TaskDialog = PluginForRevit2022.Core.CableJournal.JournalDialog;
using ARBM.CableJournalExporter.Models;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Валидатор данных для системы АПС (Автоматическая пожарная сигнализация).
    /// 
    /// Отвечает за проверку корректности:
    /// • ПКП (приемно-контрольных приборов)
    /// • Устройств АПС
    /// • Структуры контроллеров с их петлями
    /// 
    /// EN: Data validator for APS (Automatic Fire Alarm System).
    /// 
    /// Responsible for checking correctness of:
    /// • Control panels (ПКП)
    /// • APS devices
    /// • Structure of controllers with their loops
    /// </summary>
    public class ApsDataValidator
    {
        /// <summary>
        /// Валидирует контроллеры АПС (OST_ElectricalEquipment) и их параметры.
        ///
        /// Проверяет:
        /// 1. Заполнение параметра ARBM_ПС_Имя изделия (не пусто)
        /// 2. Формат ARBM_ПС_Имя изделия (N.CODE.M, где:
        ///       N — буквенно-цифровой блок (любой длины),
        ///       CODE — буквенно-цифровой код,
        ///       M — номер прибора (число))
        /// 3. Уникальность имён контроллеров
        /// 4. Наличие параметра ADSK_Этаж (не пусто)
        ///
        /// EN: Validates APS controllers and their parameters.
        /// </summary>
        public bool ValidateApsControllers(List<FamilyInstance> controllers)
        {
            if (!controllers.Any())
            {
                TaskDialog.Show("Error", "No APS controllers found.");
                return false;
            }

            var errors = new List<string>();

            // -----------------------------
            // 1. Проверка уникальности имён
            // -----------------------------
            var names = controllers
                .Select(c => c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToList();

            var duplicates = names
                .GroupBy(n => n)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            if (duplicates.Any())
            {
                errors.Add($"⛔ Найдены дублирующиеся имена контроллеров:\n  {string.Join(", ", duplicates)}");
            }

            // ---------------------------------------------------
            // 2. Проверка формата ARBM_ПС_Имя изделия: N.CODE.M
            //    где N — буквы/цифры (любая комбинация)
            // ---------------------------------------------------
            var invalidFormats = controllers
                .Where(c =>
                {
                    string name = c.LookupParameter("ARBM_ПС_Имя изделия")?.AsString();
                    if (string.IsNullOrWhiteSpace(name))
                        return true;

                    var parts = name.Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3)
                        return true;

                    string nPart = parts[0]; // теперь НЕ число
                    string codePart = parts[1];
                    string mPart = parts[2];

                    // N: буквы/цифры, минимум 1 символ
                    if (!Regex.IsMatch(nPart, @"^[A-Za-z0-9]+$"))
                        return true;

                    // CODE: буквы/цифры, минимум 1 символ
                    if (!Regex.IsMatch(codePart, @"^[A-Za-z0-9]+$"))
                        return true;

                    // M: обязательно число
                    if (!int.TryParse(mPart, out _))
                        return true;

                    return false;
                })
                .ToList();

            if (invalidFormats.Any())
            {
                var msg = "⛔ Обнаружены контроллеры с неправильным форматом ARBM_ПС_Имя изделия:\n";
                foreach (var ctrl in invalidFormats)
                {
                    string name = ctrl.LookupParameter("ARBM_ПС_Имя изделия")?.AsString() ?? "(пусто)";
                    msg += $"  ID {ctrl.Id}: {name}\n";
                }
                errors.Add(msg);
            }

            // -----------------------------
            // 3. Проверка ADSK_Этаж
            // -----------------------------
            var missingLevel = controllers
                .Where(c => string.IsNullOrWhiteSpace(c.LookupParameter("ADSK_Этаж")?.AsString()))
                .ToList();

            if (missingLevel.Any())
            {
                var msg = "⛔ У следующих контроллеров не заполнен параметр ADSK_Этаж:\n";
                foreach (var ctrl in missingLevel)
                    msg += $"  ID {ctrl.Id}: {ctrl.Name}\n";

                errors.Add(msg);
            }

            // -----------------------------
            // Итог
            // -----------------------------
            if (errors.Any())
            {
                TaskDialog.Show("Validation Error - APS Controllers", string.Join("\n\n", errors));
                return false;
            }

            return true;
        }


        /// <summary>
        /// Валидирует устройства АПС (OST_DataDevices, OST_FireAlarmDevices).
        /// 
        /// Проверяет:
        /// 1. Заполнение всех требуемых параметров
        /// 2. Формат числовых параметров (ARBM_Номер прибора, ARBM_SP_ElementMark, ARBM_Номер линии — только цифры)
        /// 3. Уникальность комбинации ARBM_Корпус.ARBM_Номер прибора.ARBM_ПСБ_БЦО.ARBM_Номер линии.ARBM_SP_ElementMark
        /// 
        /// EN: Validates APS devices (OST_DataDevices, OST_FireAlarmDevices).
        /// </summary>
        public bool ValidateApsDevices(List<FamilyInstance> devices)
        {
            if (!devices.Any())
            {
                TaskDialog.Show("Error", "No APS devices found.");
                return false;
            }

            var errors = new List<string>();

            // Проверка формата числовых параметров
            var invalidFormats = devices
                .Where(d =>
                {
                    // Проверяем ARBM_Номер прибора
                    string controllerNum = d.LookupParameter("ARBM_Номер прибора")?.AsString();
                    if (!IsNumeric(controllerNum))
                        return true;

                    // Проверяем ARBM_SP_ElementMark
                    string deviceMark = d.LookupParameter("ARBM_SP_ElementMark")?.AsString();
                    if (!IsNumeric(deviceMark))
                        return true;

                    // Проверяем ARBM_Номер линии
                    string lineNum = d.LookupParameter("ARBM_Номер линии")?.AsString();
                    if (!IsNumeric(lineNum))
                        return true;

                    return false;
                })
                .ToList();

            if (invalidFormats.Any())
            {
                var msg = "⛔ Обнаружены устройства с неправильным форматом числовых параметров:\n";
                foreach (var dev in invalidFormats)
                {
                    msg += $"  ID {dev.Id}: {dev.Name}\n";
                    if (!IsNumeric(dev.LookupParameter("ARBM_Номер прибора")?.AsString()))
                        msg += $"    ARBM_Номер прибора: {dev.LookupParameter("ARBM_Номер прибора")?.AsString()}\n";
                    if (!IsNumeric(dev.LookupParameter("ARBM_SP_ElementMark")?.AsString()))
                        msg += $"    ARBM_SP_ElementMark: {dev.LookupParameter("ARBM_SP_ElementMark")?.AsString()}\n";
                    if (!IsNumeric(dev.LookupParameter("ARBM_Номер линии")?.AsString()))
                        msg += $"    ARBM_Номер линии: {dev.LookupParameter("ARBM_Номер линии")?.AsString()}\n";
                }
                errors.Add(msg);
            }

            // Проверка уникальности комбинации адресов
            var addresses = devices
                .Select(d => new
                {
                    Device = d,
                    Address = $"{d.LookupParameter("ARBM_Корпус")?.AsString()}." +
                             $"{d.LookupParameter("ARBM_Номер прибора")?.AsString()}." +
                             $"{d.LookupParameter("ARBM_ПСБ_БЦО")?.AsString()}." +
                             $"{d.LookupParameter("ARBM_Номер линии")?.AsString()}." +
                             $"{d.LookupParameter("ARBM_SP_ElementMark")?.AsString()}"
                })
                .ToList();

            var duplicateAddresses = addresses
                .GroupBy(a => a.Address)
                .Where(g => g.Count() > 1)
                .ToList();

            if (duplicateAddresses.Any())
            {
                var msg = "⛔ Обнаружены устройства с дублирующимися адресами:\n";
                foreach (var dupGroup in duplicateAddresses)
                {
                    msg += $"  Адрес: {dupGroup.Key}\n";
                    foreach (var item in dupGroup)
                    {
                        msg += $"    ID {item.Device.Id}: {item.Device.Name}\n";
                    }
                }
                errors.Add(msg);
            }

            if (errors.Any())
            {
                TaskDialog.Show("Validation Error - Devices", string.Join("\n\n", errors));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Валидирует структуру контроллеров с петлями и количество устройств в петле.
        /// 
        /// Проверяет:
        /// 1. Наличие хотя бы одной петли у каждого контроллера
        /// 2. Количество устройств в петле не превышает ApsController.Capabilities.MaxDevicesPerLine
        /// 
        /// EN: Validates structure of controllers with loops and device count per loop.
        /// </summary>
        public bool ValidateApsControllersLoops(List<ApsControllerWithLoops> controllers)
        {
            if (!controllers.Any())
            {
                TaskDialog.Show("Error", "No APS controllers with loops found.");
                return false;
            }

            var errors = new List<string>();

            // Проверка наличия петель
            var controllersWithoutLoops = controllers
                .Where(c => c.Loops == null || c.Loops.Count == 0)
                .ToList();

            if (controllersWithoutLoops.Any())
            {
                var msg = "⛔ Обнаружены контроллеры без петель:\n";
                foreach (var ctrl in controllersWithoutLoops)
                {
                    msg += $"  {ctrl.ControllerId}\n";
                }
                errors.Add(msg);
            }

            // Проверка количества устройств в петле
            var loopsWithTooManyDevices = new List<(ApsControllerWithLoops Controller, ApsLoopGroup Loop)>();

            foreach (var controller in controllers)
            {
                foreach (var loop in controller.Loops ?? Enumerable.Empty<ApsLoopGroup>())
                {
                    int maxDevices = controller.Controller.Capabilities.MaxDevicesPerLine;
                    if (loop.Devices.Count > maxDevices)
                    {
                        loopsWithTooManyDevices.Add((controller, loop));
                    }
                }
            }

            if (loopsWithTooManyDevices.Any())
            {
                var msg = "⛔ Обнаружены петли с количеством устройств, превышающим максимум:\n";
                foreach (var (ctrl, loop) in loopsWithTooManyDevices)
                {
                    msg += $"  Контроллер: {ctrl.ControllerId}, Петля: {loop.LoopNumber}\n";
                    msg += $"    Устройств: {loop.Devices.Count}, Максимум: {ctrl.Controller.Capabilities.MaxDevicesPerLine}\n";
                }
                errors.Add(msg);
            }

            if (errors.Any())
            {
                TaskDialog.Show("Validation Error - Loops", string.Join("\n\n", errors));
                return false;
            }

            return true;
        }

        /// <summary>
        /// Проверяет, является ли строка числовой.
        /// EN: Checks if string is numeric.
        /// </summary>
        private bool IsNumeric(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Regex.IsMatch(value.Trim(), @"^\d+$");
        }
    }
}
