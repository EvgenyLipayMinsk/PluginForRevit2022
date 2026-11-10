using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Правка уже размеченной линии: вставка устройства и удаление со сдвигом номеров.</summary>
    internal static class DeviceLineOperations
    {
        private const string Building = DeviceMarkingOperations.ParamBuilding;
        private const string Controller = DeviceMarkingOperations.ParamController;
        private const string Bco = DeviceMarkingOperations.ParamBco;
        private const string LineNumber = DeviceMarkingOperations.ParamLine;
        private const string Address = DeviceMarkingOperations.ParamAddress;
        private const string Mark = DeviceMarkingOperations.ParamMark;

        private sealed class LineMember
        {
            public FamilyInstance Device;
            public int Number;
        }

        /// <summary>Вставляет устройство после указанного; следующие номера сдвигаются на 1.</summary>
        public static Dictionary<string, object> Add(Document doc, long afterId, long deviceId, bool dryRun)
        {
            if (afterId == deviceId) throw new ArgumentException("Устройства должны быть разными.");
            FamilyInstance anchor = Instance(doc, afterId);
            FamilyInstance device = Instance(doc, deviceId);
            string key = LineKey(anchor) ?? throw new InvalidOperationException($"Устройство id {afterId} не размечено: нет линии.");
            if (!int.TryParse(Read(anchor, Mark), out int anchorNumber)) throw new InvalidOperationException($"У устройства id {afterId} нет номера в линии.");

            List<LineMember> members = Members(doc, key);
            if (members.Any(m => m.Device.Id == device.Id)) throw new InvalidOperationException("Это устройство уже на этой линии.");

            string system = SystemOf(anchor);
            MarkingSettings settings = MarkingSettings.Load();
            if (!settings.IsInLine(device.Symbol.FamilyName, system))
            {
                throw new InvalidOperationException($"У семейства «{device.Symbol.FamilyName}» нет галочки этой системы в настройках маркировки.");
            }

            var warnings = new List<string>();
            if (LineKey(device) != null) warnings.Add("устройство было на другой линии и будет перенесено; номера той линии не изменятся");
            int number = anchorNumber + 1;
            List<LineMember> shifted = members.Where(m => m.Number >= number).ToList();
            int occupied = members.Count + 1;
            if (occupied > DeviceMarkingOperations.LineCapacity) warnings.Add($"на линии {occupied} адресов при ёмкости {DeviceMarkingOperations.LineCapacity}");

            var failures = new List<string>();
            using (var transaction = new Transaction(doc, "AerBIM: добавить устройство в линию"))
            {
                transaction.Start();
                foreach (LineMember member in shifted) Set(member.Device, Mark, (member.Number + 1).ToString(), failures);

                foreach (string name in new[] { Building, Controller, LineNumber, Address })
                {
                    string value = Read(anchor, name);
                    if (!string.IsNullOrEmpty(value)) Set(device, name, value, failures);
                }
                Set(device, Bco, settings.BcoOf(device.Symbol.FamilyName) ?? Read(anchor, Bco) ?? "", failures);
                Set(device, Mark, number.ToString(), failures);
                foreach (string name in new[] { "ADSK_Этаж", "О_Этаж" })
                {
                    if (!string.IsNullOrEmpty(Read(anchor, name)))
                    {
                        Set(device, name, DeviceMarkingOperations.FloorOf(doc, device, null) ?? Read(anchor, name), failures);
                    }
                }
                string comments = anchor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                if (!string.IsNullOrEmpty(comments))
                {
                    string error = RevitText.TrySet(device.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), "Комментарии", comments);
                    if (error != null) failures.Add($"id {deviceId}, Комментарии: {error}");
                }
                Finish(transaction, dryRun, failures);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["number"] = number,
                ["shifted"] = shifted.Count,
                ["lineAddresses"] = occupied,
                ["lineCapacity"] = DeviceMarkingOperations.LineCapacity,
                ["warnings"] = warnings,
                ["summary"] = dryRun
                    ? $"Предпросмотр: устройство получит номер {number}, сдвинется номеров: {shifted.Count}."
                    : $"Устройство добавлено под номером {number}, сдвинуто номеров: {shifted.Count}. Отменить можно через Ctrl+Z.",
            };
        }

        /// <summary>Снимает устройства с линии и закрывает пропуски. Очищается всё, кроме этажа.</summary>
        public static Dictionary<string, object> Remove(Document doc, List<long> ids, bool dryRun)
        {
            if (ids == null || ids.Count == 0) throw new ArgumentException("Укажите устройства.");
            List<FamilyInstance> devices = ids.Distinct().Select(id => Instance(doc, id)).ToList();
            FamilyInstance unmarked = devices.FirstOrDefault(d => LineKey(d) == null);
            if (unmarked != null) throw new InvalidOperationException($"Устройство id {RevitIds.IdOf(unmarked.Id)} не размечено: нет линии.");

            var removedIds = new HashSet<long>(devices.Select(d => RevitIds.IdOf(d.Id)));
            var failures = new List<string>();
            int shiftedCount = 0;
            int left = 0;
            using (var transaction = new Transaction(doc, "AerBIM: удалить устройство из линии"))
            {
                transaction.Start();
                foreach (IGrouping<string, FamilyInstance> line in devices.GroupBy(LineKey))
                {
                    List<LineMember> members = Members(doc, line.Key);
                    List<int> removedNumbers = members.Where(m => removedIds.Contains(RevitIds.IdOf(m.Device.Id))).Select(m => m.Number).ToList();
                    List<LineMember> remaining = members.Where(m => !removedIds.Contains(RevitIds.IdOf(m.Device.Id))).ToList();
                    left += remaining.Count;
                    foreach (LineMember member in remaining)
                    {
                        int shift = removedNumbers.Count(n => n < member.Number);
                        if (shift == 0) continue;
                        Set(member.Device, Mark, (member.Number - shift).ToString(), failures);
                        shiftedCount++;
                    }
                }
                foreach (FamilyInstance device in devices)
                {
                    foreach (string name in new[] { Building, Controller, Bco, LineNumber, Address, Mark, "ARBM_ЗКПС" })
                    {
                        if (!string.IsNullOrEmpty(Read(device, name))) Set(device, name, "", failures);
                    }
                    Parameter comments = device.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (!string.IsNullOrEmpty(comments?.AsString()))
                    {
                        string error = RevitText.TrySet(comments, "Комментарии", "");
                        if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, Комментарии: {error}");
                    }
                }
                Finish(transaction, dryRun, failures);
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["removed"] = devices.Count,
                ["shifted"] = shiftedCount,
                ["lineAddresses"] = left,
                ["lineCapacity"] = DeviceMarkingOperations.LineCapacity,
                ["summary"] = dryRun
                    ? $"Предпросмотр: будет снято устройств: {devices.Count}, сдвинется номеров: {shiftedCount}."
                    : $"Снято устройств: {devices.Count}, сдвинуто номеров: {shiftedCount}. Отменить можно через Ctrl+Z.",
            };
        }

        private static void Finish(Transaction transaction, bool dryRun, List<string> failures)
        {
            if (failures.Count > 0)
            {
                transaction.RollBack();
                throw new InvalidOperationException("Изменения не применены: " + string.Join("; ", failures.Distinct().Take(10)));
            }
            if (dryRun)
            {
                transaction.RollBack();
            }
            else if (transaction.Commit() != TransactionStatus.Committed)
            {
                throw new InvalidOperationException("Revit не применил изменения.");
            }
        }

        private static List<LineMember> Members(Document doc, string key)
        {
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_NurseCallDevices };
            var result = new List<LineMember>();
            foreach (FamilyInstance device in new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories))
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (LineKey(device) == key && int.TryParse(Read(device, Mark), out int number))
                {
                    result.Add(new LineMember { Device = device, Number = number });
                }
            }
            return result;
        }

        /// <summary>Линия устройства: АПС — корпус, прибор и линия; СОТС и СОУЭ — адрес. БЦО не входит: оно бывает разным у семейств.</summary>
        private static string LineKey(Element device)
        {
            string controller = Read(device, Controller), line = Read(device, LineNumber), address = Read(device, Address);
            if (!string.IsNullOrEmpty(controller) && !string.IsNullOrEmpty(line)) return $"aps|{Read(device, Building)}|{controller}|{line}";
            return string.IsNullOrEmpty(address) ? null : $"addr|{address}";
        }

        private static string SystemOf(Element device)
        {
            if (!string.IsNullOrEmpty(Read(device, LineNumber))) return "aps";
            return string.IsNullOrEmpty(Read(device, Building)) ? "security" : "souye";
        }

        private static string Read(Element element, string name) => element.LookupParameter(name)?.AsString()?.Trim();

        private static void Set(Element element, string name, string value, List<string> failures)
        {
            string error = RevitText.TrySet(element, name, value);
            if (error != null) failures.Add($"id {RevitIds.IdOf(element.Id)}, {name}: {error}");
        }

        private static FamilyInstance Instance(Document doc, long id) =>
            doc.GetElement(RevitIds.ToElementId(id)) as FamilyInstance ?? throw new ArgumentException($"Экземпляр семейства id {id} не найден в проекте.");
    }
}
