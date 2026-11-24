using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Размеченная линия проекта. Публичный класс: на него привязана таблица окна выбора.</summary>
    public sealed class LineInfo
    {
        public string Key { get; set; }
        public string System { get; set; }
        public string Building { get; set; }
        public string Controller { get; set; }
        public string Line { get; set; }
        public int Devices { get; set; }
        /// <summary>«Комментарии» линии, заданные при маркировке.</summary>
        public string Comment { get; set; }
        public bool ByConnection { get; set; }
        public string MarkingType => ByConnection ? "По подключению" : "По порядку";
        public string Occupied => $"{Devices} из {DeviceMarkingOperations.LineCapacity}";
        /// <summary>Номера прибора и линии числом — для сортировки (1, 3, 10, а не 1, 10, 3).</summary>
        public int ControllerOrder => NumberOrder(Controller);
        public int LineOrder => NumberOrder(Line);
        internal static int NumberOrder(string value) => int.TryParse((value ?? "").Trim(), out int n) ? n : int.MaxValue;
        /// <summary>Диапазон номеров «1–24».</summary>
        public string Numbers { get; set; }
        /// <summary>Пропуски и повторы номеров, «нет», если номера идут подряд.</summary>
        public string Problems { get; set; }
        /// <summary>Этажи устройств (параметр этажа системы).</summary>
        public string Floors { get; set; }
        /// <summary>Линия переполнена или в номерах есть пропуски/повторы.</summary>
        public bool HasIssues { get; set; }
        public string Title => $"{System}: " + (string.IsNullOrEmpty(Building) ? "" : $"корпус {Building}, ") + $"прибор {Controller}, линия {Line} — {Devices} устр.";
    }

    /// <summary>Правка уже размеченных линий: список линий, вставка устройства и удаление со сдвигом номеров.</summary>
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

        // ===================== Линии проекта =====================

        /// <param name="withUncommented">Показать и устройства АПС без «Комментарии» (только для просмотра и удаления в «Диспетчере линий»).</param>
        public static List<LineInfo> ListLines(Document doc, bool withUncommented = false)
        {
            var groups = new Dictionary<string, List<FamilyInstance>>();
            foreach (FamilyInstance device in AllDevices(doc))
            {
                string key = KeyOf(device, withUncommented);
                if (key == null || !int.TryParse(Read(device, Mark), out _)) continue;
                if (!groups.TryGetValue(key, out List<FamilyInstance> list)) groups[key] = list = new List<FamilyInstance>();
                list.Add(device);
            }

            var connectionOrders = LineConnectionOrder.Read(doc);
            var titles = new Dictionary<string, string> { ["aps"] = "АПС", ["security"] = "СОТС", ["souye"] = "СОУЭ" };
            return groups.Select(g =>
            {
                FamilyInstance first = g.Value[0];
                List<int> numbers = g.Value.Select(d => int.Parse(Read(d, Mark))).OrderBy(n => n).ToList();
                int min = numbers.First(), max = numbers.Last();
                long missing = Math.Max(0, (long)max - numbers.Where(n => n > 0).Distinct().Count());
                bool byConnection = connectionOrders.ContainsKey(g.Key);
                int repeated = numbers.Count - numbers.Distinct().Count();
                var problems = new List<string>();
                if (missing > 0 && !byConnection) problems.Add($"пропусков: {missing}");
                if (repeated > 0) problems.Add($"повторов: {repeated}");
                if (numbers.Count > DeviceMarkingOperations.LineCapacity) problems.Add("больше ёмкости");
                string floorParam = MarkingSettings.CurrentFloorParameter();
                List<string> floors = g.Value.Select(d => Read(d, floorParam)).Where(f => !string.IsNullOrEmpty(f)).Distinct()
                    .OrderBy(f => int.TryParse(f, out int n) ? n : int.MaxValue).ThenBy(f => f).ToList();
                string[] address = (Read(first, Address) ?? "").Split('.');
                bool aps = g.Key.StartsWith("aps|");
                return new LineInfo
                {
                    Key = g.Key,
                    ByConnection = byConnection,
                    System = titles[SystemOf(first)],
                    Building = Read(first, Building),
                    // СОТС: «прибор.шлейф»; СОУЭ: только номер шлейфа.
                    Controller = aps ? Read(first, Controller) : (address.Length > 1 ? address[0] : ""),
                    Line = aps ? Read(first, LineNumber) : (address.Length > 1 ? address[1] : address[0]),
                    Devices = g.Value.Count,
                    Comment = DeviceMarkingOperations.CommentOf(first),
                    Numbers = min == max ? min.ToString() : $"{min}–{max}",
                    Problems = problems.Count == 0 ? "нет" : string.Join(", ", problems),
                    Floors = floors.Count == 0 ? "—" : string.Join(", ", floors),
                    HasIssues = problems.Count > 0,
                };
            }).OrderBy(l => l.System).ThenBy(l => l.Building)
              .ThenBy(l => l.ControllerOrder).ThenBy(l => l.Controller).ThenBy(l => l.LineOrder).ThenBy(l => l.Line).ToList();
        }

        public static Dictionary<string, object> ListLinesResult(Document doc) => new Dictionary<string, object>
        {
            ["lines"] = ListLines(doc).Select(l => new Dictionary<string, object>
            {
                ["line"] = l.Key, ["system"] = l.System, ["building"] = l.Building, ["controller"] = l.Controller,
                ["markingType"] = l.ByConnection ? "connection" : "sequential", ["number"] = l.Line, ["comment"] = l.Comment, ["devices"] = l.Devices, ["capacity"] = DeviceMarkingOperations.LineCapacity,
            }).ToList(),
        };

        /// <summary>Линия устройства: АПС — корпус, прибор и линия; СОТС и СОУЭ — адрес. БЦО не входит: оно бывает разным у семейств.</summary>
        public static string KeyOf(Element device) => KeyOf(device, false);

        /// <param name="withUncommented">АПС без «Комментарии» тоже даёт ключ (с пустым комментарием в конце) —
        /// так «Диспетчер линий» показывает и снимает ручную разметку. Добавление и маркировка такие устройства не видят.</param>
        public static string KeyOf(Element device, bool withUncommented)
        {
            string controller = Read(device, Controller), line = Read(device, LineNumber), address = Read(device, Address);
            if (!string.IsNullOrEmpty(controller) && !string.IsNullOrEmpty(line))
            {
                // АПС — линия вместе с комментарием, заданным при маркировке: без «Комментарии» (модули, разметка вручную)
                // номера прибора и линии линию не образуют; с другим комментарием — это другая линия
                string comment = DeviceMarkingOperations.CommentOf(device);
                return comment.Length == 0 && !withUncommented ? null : ApsKey(Read(device, Building), controller, line, comment);
            }
            // СОУЭ: адрес — только номер шлейфа, поэтому в ключе корпус (шлейф 1 корпусов M1 и M2 — разные линии).
            // СОТС: адрес «прибор.шлейф», корпус обычно пуст.
            return string.IsNullOrEmpty(address) ? null : $"addr|{Read(device, Building)}|{address}";
        }

        internal static string ApsKey(string building, string controller, string line, string comment)
        {
            string Number(string value) => int.TryParse(value, out int n) ? n.ToString() : value;
            return $"aps|{building?.Trim().ToUpperInvariant()}|{Number(controller)}|{Number(line)}|{comment.Trim().ToUpperInvariant()}";
        }

        public static HashSet<long> MemberIds(Document doc, string key) =>
            new HashSet<long>(Members(doc, key).Select(m => RevitIds.IdOf(m.Device.Id)));

        /// <summary>Устройство линии с наибольшим номером (для вставки в конец линии).</summary>
        public static long? LastMemberId(Document doc, string key)
        {
            LineMember last = Members(doc, key).OrderByDescending(m => m.Number).FirstOrDefault();
            return last == null ? (long?)null : RevitIds.IdOf(last.Device.Id);
        }

        /// <summary>Все устройства линий, в том числе без номера (маркировка снимается целиком).</summary>
        public static List<long> DeviceIdsOfLines(Document doc, IEnumerable<string> keys)
        {
            var set = new HashSet<string>(keys);
            return AllDevices(doc).Where(d => { string k = KeyOf(d, true); return k != null && set.Contains(k); })
                .Select(d => RevitIds.IdOf(d.Id)).ToList();
        }

        /// <summary>Удаление линий: со всех их устройств снимается маркировка (как «Удалить устройство» для каждого).</summary>
        public static Dictionary<string, object> RemoveLines(Document doc, List<string> keys, bool dryRun)
        {
            List<long> ids = DeviceIdsOfLines(doc, keys);
            if (ids.Count == 0) throw new InvalidOperationException("На выбранных линиях нет устройств.");
            Dictionary<string, object> result = Remove(doc, null, ids, dryRun, deletingLines: true);
            result["lines"] = keys.Count;
            result["summary"] = dryRun
                ? $"Предпросмотр: линий: {keys.Count}, устройств: {ids.Count} — маркировка будет снята."
                : $"Удалено линий: {keys.Count}, снята маркировка с устройств: {ids.Count}. Отменить можно через Ctrl+Z.";
            return result;
        }

        // ===================== Добавление =====================

        /// <summary>
        /// Вставляет устройство в линию: после afterId или, если он не задан, в начало линии. Следующие номера сдвигаются на 1.
        /// </summary>
        public static Dictionary<string, object> Add(Document doc, string lineKey, long? afterId, long deviceId, bool dryRun) =>
            Add(doc, lineKey, afterId, new List<long> { deviceId }, dryRun);

        /// <summary>
        /// Вставляет устройства в линию подряд, в порядке списка: после afterId или, если он не задан, в начало линии.
        /// Следующие номера линии сдвигаются на число вставленных устройств.
        /// </summary>
        public static Dictionary<string, object> Add(Document doc, string lineKey, long? afterId, List<long> deviceIds, bool dryRun, int? fixedAddress = null)
        {
            List<long> ids = (deviceIds ?? new List<long>()).Distinct().ToList();
            if (ids.Count == 0) throw new ArgumentException("Укажите устройства.");
            if (afterId != null && ids.Contains(afterId.Value)) throw new ArgumentException("Устройство, после которого вставляем, не может быть среди новых.");
            List<FamilyInstance> devices = ids.Select(id => Instance(doc, id)).ToList();
            FamilyInstance after = afterId == null ? null : Instance(doc, afterId.Value);
            string key = !string.IsNullOrWhiteSpace(lineKey) ? lineKey.Trim() : after != null ? KeyOf(after) : null;
            if (key == null) throw new ArgumentException("Укажите линию или устройство, после которого вставить.");
            if (after != null && KeyOf(after) != key) throw new InvalidOperationException($"Устройство id {afterId} не на выбранной линии.");

            var orders = LineConnectionOrder.Read(doc);
            orders.TryGetValue(key, out var connection);
            if (devices.Any(d => orders.ContainsKey(KeyOf(d, true) ?? "")))
                throw new InvalidOperationException("Устройство уже входит в линию по подключению. Сначала исключите его из неё в «Диспетчере линий».");
            if (connection != null && fixedAddress == null)
                throw new InvalidOperationException("Линия размечена по подключению. Добавьте устройство без перенумерации в «Диспетчере линий».");
            if (fixedAddress != null && (connection == null || ids.Count != 1 || fixedAddress <= 0 || fixedAddress > DeviceMarkingOperations.LineCapacity))
                throw new ArgumentException("Выберите линию по подключению, одно устройство и адрес от 1 до " + DeviceMarkingOperations.LineCapacity + ".");
            List<LineMember> members = Members(doc, key);
            List<string> newOrder = null;
            if (connection != null)
            {
                if (devices[0].SuperComponent != null || !AllDevices(doc).Any(d => d.Id == devices[0].Id) ||
                    RevitIds.IdOf(devices[0].Category.Id) == (long)BuiltInCategory.OST_NurseCallDevices)
                    throw new ArgumentException("Выберите отдельное устройство из категории «Пожарная сигнализация» или «Устройства передачи данных».");
                if (KeyOf(devices[0], true) != null) throw new ArgumentException("Новое устройство уже принадлежит линии. Сначала исключите его из прежней линии.");
                if (members.Any(m => m.Number == fixedAddress.Value)) throw new ArgumentException("Этот адрес уже занят.");
                newOrder = LineConnectionOrder.Ordered(DevicesOf(doc, key), connection.Order).Select(d => d.UniqueId).ToList();
                int position = after == null ? 0 : newOrder.IndexOf(after.UniqueId) + 1;
                newOrder.Insert(position, devices[0].UniqueId);
            }
            if (members.Count == 0) throw new InvalidOperationException("На выбранной линии нет размеченных устройств.");
            FamilyInstance already = devices.FirstOrDefault(d => members.Any(m => m.Device.Id == d.Id));
            if (already != null) throw new InvalidOperationException($"Устройство id {RevitIds.IdOf(already.Id)} уже на этой линии.");

            LineMember source = after == null
                ? members.OrderBy(m => m.Number).First()
                : members.FirstOrDefault(m => m.Device.Id == after.Id) ?? throw new InvalidOperationException($"У устройства id {afterId} нет номера в линии.");
            FamilyInstance anchor = source.Device;
            int number = fixedAddress ?? (after == null ? source.Number : source.Number + 1);

            string system = SystemOf(anchor);
            // Правило маркировки: в линию входят только устройства с её «Комментарии»
            string lineComment = DeviceMarkingOperations.CommentOf(anchor);
            MarkingSettings settings = MarkingSettings.Load();
            foreach (FamilyInstance device in devices)
            {
                if (!string.IsNullOrEmpty(lineComment) && !DeviceMarkingOperations.HasComment(device, lineComment))
                    throw new InvalidOperationException($"У устройства id {RevitIds.IdOf(device.Id)} нет «Комментарии» = «{lineComment}»: в линию оно не добавляется. Заполните комментарий и повторите.");
                if (!settings.IsInLine(device.Symbol.FamilyName, system))
                    throw new InvalidOperationException($"У семейства «{device.Symbol.FamilyName}» нет галочки этой системы в настройках маркировки.");
                if (string.IsNullOrWhiteSpace(settings.BcoOf(device.Symbol.FamilyName)))
                    throw new InvalidOperationException($"Не задано БЦО семейства «{device.Symbol.FamilyName}» в «Настройках маркировки».");
            }

            var warnings = new List<string>();
            int moved = devices.Count(d => KeyOf(d) != null);
            if (moved > 0) warnings.Add($"устройств с другой линии: {moved} — будут перенесены; номера той линии не изменятся — в ней останутся пропуски");
            List<LineMember> shifted = fixedAddress.HasValue ? new List<LineMember>() : members.Where(m => m.Number >= number).ToList();
            int occupied = members.Count + devices.Count;
            if (fixedAddress.HasValue && occupied > DeviceMarkingOperations.LineCapacity) throw new InvalidOperationException("Превышена ёмкость линии.");
            if (occupied > DeviceMarkingOperations.LineCapacity) warnings.Add($"на линии {occupied} адресов при ёмкости {DeviceMarkingOperations.LineCapacity}");

            var failures = new List<string>();
            using (var transaction = new Transaction(doc, devices.Count == 1 ? "AerBIM: добавить устройство в линию" : "AerBIM: добавить устройства в линию"))
            {
                transaction.Start();
                foreach (LineMember member in shifted) Set(member.Device, Mark, (member.Number + devices.Count).ToString(), failures);

                string floorParameter = MarkingSettings.CurrentFloorParameter();
                string comments = anchor.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                for (int i = 0; i < devices.Count; i++)
                {
                    FamilyInstance device = devices[i];
                    foreach (string name in new[] { Building, Controller, LineNumber, Address })
                    {
                        // Как у линии, в том числе пустое: иначе оставшийся корпус переносимого устройства дал бы другой ключ линии
                        string value = Read(anchor, name) ?? "";
                        Parameter own = device.LookupParameter(name);
                        if ((own?.AsString() ?? "").Trim() == value) continue;
                        if (!fixedAddress.HasValue && (own == null || own.IsReadOnly)) continue;
                        Set(device, name, value, failures);
                    }
                    // Перенос из линии другой системы: её параметры очищаются, иначе устройство осталось бы на старой линии.
                    DeviceMarkingOperations.ClearForeign(device, system, failures);
                    Set(device, Bco, settings.BcoOf(device.Symbol.FamilyName), failures); // БЦО — только из «Настроек маркировки», как при маркировке
                    Set(device, Mark, (number + i).ToString(), failures);
                    foreach (string name in new[] { MarkingSettings.DefaultFloorParameter, floorParameter }.Distinct())
                    {
                        if (!string.IsNullOrEmpty(Read(anchor, name)))
                        {
                            Set(device, name, DeviceMarkingOperations.FloorOf(doc, device, null) ?? Read(anchor, name), failures);
                        }
                    }
                    if (!string.IsNullOrEmpty(comments))
                    {
                        string error = RevitText.TrySet(device.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), "Комментарии", comments);
                        if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, Комментарии: {error}");
                    }
                }
                if (newOrder != null && !dryRun && failures.Count == 0) LineConnectionOrder.Save(doc, key, newOrder);
                Finish(transaction, dryRun, failures);
            }

            int last = number + devices.Count - 1;
            string numbers = devices.Count == 1 ? $"номер {number}" : $"номера {number}–{last}";
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["line"] = key,
                ["number"] = number,
                ["numbers"] = devices.Count == 1 ? number.ToString() : $"{number}–{last}",
                ["added"] = devices.Count,
                ["shifted"] = shifted.Count,
                ["lineAddresses"] = occupied,
                ["lineCapacity"] = DeviceMarkingOperations.LineCapacity,
                ["warnings"] = warnings,
                ["summary"] = dryRun
                    ? $"Предпросмотр: устройств {devices.Count}, {numbers}, сдвинется номеров: {shifted.Count}."
                    : $"Добавлено устройств: {devices.Count}, {numbers}, сдвинуто номеров: {shifted.Count}. Отменить можно через Ctrl+Z.",
            };
        }

        // ===================== Удаление =====================

        /// <summary>Снимает устройства с линии и закрывает пропуски. Очищаются маркировка и «Комментарии»; этаж и ЗКПС остаются.</summary>
        public static Dictionary<string, object> Remove(Document doc, string lineKey, List<long> ids, bool dryRun, bool preserveAddresses = false, bool deletingLines = false)
        {
            if (ids == null || ids.Count == 0) throw new ArgumentException("Укажите устройства.");
            List<FamilyInstance> devices = ids.Distinct().Select(id => Instance(doc, id)).ToList();
            // Ключ с линиями без комментария: снять можно и ручную разметку (из «Диспетчера линий»)
            FamilyInstance unmarked = devices.FirstOrDefault(d => KeyOf(d, true) == null);
            if (unmarked != null) throw new InvalidOperationException($"Устройство id {RevitIds.IdOf(unmarked.Id)} не размечено: нет линии.");
            if (!string.IsNullOrWhiteSpace(lineKey))
            {
                FamilyInstance foreign = devices.FirstOrDefault(d => KeyOf(d, true) != lineKey.Trim());
                if (foreign != null) throw new InvalidOperationException($"Устройство id {RevitIds.IdOf(foreign.Id)} не на выбранной линии.");
            }

            var orders = LineConnectionOrder.Read(doc);
            var protectedKeys = devices.Select(d => KeyOf(d, true)).Distinct().Where(orders.ContainsKey).ToList();
            if (protectedKeys.Count > 0 && !preserveAddresses && !deletingLines)
                throw new InvalidOperationException("Линия размечена по подключению. Исключите устройство без перенумерации в «Диспетчере линий».");
            if (preserveAddresses && (protectedKeys.Count != 1 || devices.Any(d => KeyOf(d, true) != protectedKeys[0])))
                throw new ArgumentException("Выберите устройства одной линии по подключению.");
            var connectionOrder = preserveAddresses ? LineConnectionOrder.Ordered(DevicesOf(doc, protectedKeys[0]), orders[protectedKeys[0]].Order) : null;
            var removedIds = new HashSet<long>(devices.Select(d => RevitIds.IdOf(d.Id)));
            var failures = new List<string>();
            int shiftedCount = 0;
            int left = 0;
            using (var transaction = new Transaction(doc, "AerBIM: удалить устройство из линии"))
            {
                transaction.Start();
                foreach (IGrouping<string, FamilyInstance> line in devices.GroupBy(d => KeyOf(d, true)))
                {
                    List<LineMember> members = Members(doc, line.Key);
                    List<int> removedNumbers = members.Where(m => removedIds.Contains(RevitIds.IdOf(m.Device.Id))).Select(m => m.Number).ToList();
                    List<LineMember> remaining = members.Where(m => !removedIds.Contains(RevitIds.IdOf(m.Device.Id))).ToList();
                    left += remaining.Count;
                    if (orders.ContainsKey(line.Key)) continue;
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
                    // ЗКПС — зона на плане, а не данные линии: остаётся (её переписывает assign_zkps).
                    // «Комментарии» снимаются: с ними журнал счёл бы устройство неразмеченным устройством системы.
                    foreach (string name in new[] { Building, Controller, Bco, LineNumber, Address, Mark })
                    {
                        if (!string.IsNullOrEmpty(Read(device, name))) Set(device, name, "", failures);
                    }
                    Parameter comments = device.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (!preserveAddresses && !string.IsNullOrEmpty(comments?.AsString()))
                    {
                        string error = RevitText.TrySet(comments, "Комментарии", "");
                        if (error != null) failures.Add($"id {RevitIds.IdOf(device.Id)}, Комментарии: {error}");
                    }
                }
                if (!dryRun && failures.Count == 0)
                {
                    if (deletingLines)
                        foreach (string key in protectedKeys) LineConnectionOrder.Save(doc, key, null);
                    if (connectionOrder != null)
                    {
                        var remaining = connectionOrder.Where(d => !removedIds.Contains(RevitIds.IdOf(d.Id))).Select(d => d.UniqueId).ToList();
                        LineConnectionOrder.Save(doc, protectedKeys[0], remaining.Count == 0 ? null : remaining);
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

        // ===================== Общее =====================

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

        internal static IEnumerable<FamilyInstance> AllDevices(Document doc)
        {
            var categories = new List<BuiltInCategory> { BuiltInCategory.OST_DataDevices, BuiltInCategory.OST_FireAlarmDevices, BuiltInCategory.OST_NurseCallDevices };
            return new FilteredElementCollector(doc).WherePasses(new ElementMulticategoryFilter(categories))
                .WhereElementIsNotElementType().OfType<FamilyInstance>();
        }

        internal static List<FamilyInstance> DevicesOf(Document doc, string key) =>
            AllDevices(doc).Where(d => d.SuperComponent == null && KeyOf(d, true) == key).ToList();

        internal static void SaveConnectionOrder(Document doc, string key, List<string> order)
        {
            if (order != null) LineConnectionOrder.Ordered(DevicesOf(doc, key), order);
            using (var transaction = new Transaction(doc, "AerBIM: тип маркировки и порядок подключения"))
            {
                transaction.Start();
                LineConnectionOrder.Save(doc, key, order);
                Finish(transaction, false, new List<string>());
            }
        }

        internal static void Renumber(Document doc, string key, List<string> order)
        {
            if (LineConnectionOrder.Read(doc).ContainsKey(key))
                throw new InvalidOperationException("Для линии по подключению перенумерация недоступна.");
            var devices = LineConnectionOrder.Ordered(DevicesOf(doc, key), order, checkAddresses: false);
            if (devices.Count > DeviceMarkingOperations.LineCapacity) throw new InvalidOperationException("Превышена ёмкость линии.");
            var failures = new List<string>();
            using (var transaction = new Transaction(doc, "AerBIM: обновить маркировку по порядку"))
            {
                transaction.Start();
                for (int i = 0; i < devices.Count; i++)
                    if (LineConnectionOrder.Address(devices[i]) != i + 1) Set(devices[i], Mark, (i + 1).ToString(), failures);
                Finish(transaction, false, failures);
            }
        }

        private static List<LineMember> Members(Document doc, string key)
        {
            var result = new List<LineMember>();
            foreach (FamilyInstance device in AllDevices(doc))
            {
                if (KeyOf(device, key.EndsWith("|")) == key && int.TryParse(Read(device, Mark), out int number))
                {
                    result.Add(new LineMember { Device = device, Number = number });
                }
            }
            return result;
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
