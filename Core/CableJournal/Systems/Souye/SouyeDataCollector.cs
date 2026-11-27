using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using PluginForRevit2022.Core;
using PluginForRevit2022.Core.CableJournal;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Systems.Souye
{
    /// <summary>
    /// Результат сбора данных СОУЭ из документа Revit.
    /// Содержит либо успешно собранную доменную модель, либо сообщение об ошибке.
    /// </summary>
    public class SouyeCollectResult
    {
        /// <summary>
        /// Собранная доменная модель СОУЭ.
        /// Равна <c>null</c>, если сбор завершился с ошибкой.
        /// </summary>
        public IReadOnlyList<SouyeControllerWithLoops> Groups { get; private set; }

        /// <summary>
        /// Сообщение об ошибке, если сбор данных не удался.
        /// Равно <c>null</c>, если сбор выполнен успешно.
        /// </summary>
        public string ErrorMessage { get; private set; }

        /// <summary>Признак успешного завершения сбора.</summary>
        public bool IsSuccess => ErrorMessage == null;

        private SouyeCollectResult() { }

        /// <summary>Создаёт успешный результат с собранными данными.</summary>
        public static SouyeCollectResult Success(IReadOnlyList<SouyeControllerWithLoops> groups)
            => new SouyeCollectResult { Groups = groups };

        /// <summary>Создаёт результат с ошибкой.</summary>
        public static SouyeCollectResult Failure(string errorMessage)
            => new SouyeCollectResult { ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Собирает данные СОУЭ из документа Revit и строит доменную модель
    /// <see cref="SouyeControllerWithLoops"/>.
    /// <para>
    /// Алгоритм:
    /// <list type="number">
    ///   <item>Выбранная стойка (<c>OST_ElectricalEquipment</c>) или однозначный выбор по комментарию.</item>
    ///   <item>Сбор оповещателей (<c>OST_NurseCallDevices</c>) по корпусу и номеру выбранной стойки.</item>
    ///   <item>Группировка устройств по шлейфу (<c>LoopAddressParsed</c>).</item>
    ///   <item>Сортировка: корпус → номер шлейфа → позиция устройства.</item>
    /// </list>
    /// </para>
    /// </summary>
    public class SouyeDataCollector
    {
        private readonly SouyeElementParser _parser;

        /// <summary>
        /// Инициализирует новый экземпляр <see cref="SouyeDataCollector"/>.
        /// </summary>
        /// <param name="parser">Парсер элементов Revit в доменные модели СОУЭ.
        /// <para>RU: Парсер элементов Revit в доменные модели СОУЭ.</para></param>
        public SouyeDataCollector(SouyeElementParser parser)
        {
            _parser = parser;
        }

        /// <summary>
        /// Выполняет сбор данных СОУЭ из документа Revit.
        /// </summary>
        /// <param name="doc">Активный документ Revit.</param>
        /// <param name="commentsValue">
        /// Значение параметра <c>ALL_MODEL_INSTANCE_COMMENTS</c>, по которому отбираются
        /// контроллер и оповещатели данной системы СОУЭ.
        /// </param>
        /// <returns>
        /// <see cref="SouyeCollectResult.Success"/> с заполненной моделью, или
        /// <see cref="SouyeCollectResult.Failure"/> с описанием ошибки.
        /// </returns>
        public SouyeCollectResult Collect(Document doc, string commentsValue, Element selected = null)
        {
            bool functionalZones = MarkingSettings.CurrentJournal().SouyeFunctionalZones;
            var candidates = SouyeLineIdentity.Racks(doc, commentsValue)
                .Where(rack => SameComments(GetCommentsValue(rack), commentsValue)).ToList();
            var current = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_NurseCallDevices)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(element => element.SuperComponent == null).ToList();
            var linkedByDevice = current.ToDictionary(element => element.Id, SouyeLineIdentity.LinkedRack);
            var linkedLines = new HashSet<(string rack, int number, int line)>();
            foreach (FamilyInstance device in current)
            {
                string linkedRack = linkedByDevice[device.Id];
                if (!string.IsNullOrEmpty(linkedRack) && SouyeLineIdentity.TryLineNumbers(device, out int number, out int line))
                    linkedLines.Add((linkedRack, number, line));
            }
            Element rackElement = null;
            int? selectedDeviceNumber = null;
            if (selected != null && SouyeLineIdentity.IsRack(selected))
            {
                if (!SameComments(GetCommentsValue(selected), commentsValue))
                    return SouyeCollectResult.Failure("У выбранной стойки другое значение «Комментарии».");
                rackElement = selected;
            }
            else if (selected != null && SouyeLineIdentity.TryLineNumbers(selected, out int selectedNumber, out int selectedLine))
            {
                string building = SouyeLineIdentity.Read(selected, DeviceMarkingOperations.ParamBuilding);
                string link = SouyeLineIdentity.LinkedRack(selected);
                if (!string.IsNullOrEmpty(link))
                {
                    rackElement = doc.GetElement(link);
                    if (!SouyeLineIdentity.IsRack(rackElement) || !SouyeLineIdentity.RackContains(rackElement, selectedNumber))
                        return SouyeCollectResult.Failure($"Сохранённая связь оповещателя ID {RevitIds.IdOf(selected.Id)} не ведёт к действующей стойке с прибором №{selectedNumber}. Повторите маркировку с выбором стойки.");
                    if (!SameComments(GetCommentsValue(rackElement), commentsValue))
                        return SouyeCollectResult.Failure($"У связанной стойки ID {RevitIds.IdOf(rackElement.Id)} другие «Комментарии». Проверьте группу СОУЭ.");
                }
                else
                {
                    var matches = MatchUnlinkedRacks(candidates, building, selectedNumber, selectedLine, functionalZones, linkedLines);
                    if (matches.Count != 1)
                        return SouyeCollectResult.Failure(
                            $"Для устройства ID {RevitIds.IdOf(selected.Id)} не определена стойка СОУЭ с прибором №{selectedNumber}. Найдено: {matches.Count}. " +
                            "Проверьте корпус, номер и «Комментарии» стойки. При неоднозначности повторите маркировку с выбором стойки.");
                    rackElement = matches[0];
                }
                selectedDeviceNumber = selectedNumber;
            }
            else if (candidates.Count == 1)
            {
                rackElement = candidates[0];
            }
            if (rackElement == null)
                return SouyeCollectResult.Failure($"По комментарию «{commentsValue}» найдено стоек: {candidates.Count}. Укажите конкретную стойку СОУЭ.");

            string numbersText = SouyeLineIdentity.Read(rackElement, DeviceMarkingOperations.ParamController);
            if (!SouyeLineIdentity.TryDeviceNumbers(numbersText, out var rackNumbers))
                return SouyeCollectResult.Failure($"«ARBM_Номер прибора» стойки: «{numbersText}». Укажите различные положительные номера через запятую.");
            string buildingCode = SouyeLineIdentity.Read(rackElement, DeviceMarkingOperations.ParamBuilding);
            var requested = new HashSet<int>(rackNumbers.Where(number => !selectedDeviceNumber.HasValue || number == selectedDeviceNumber.Value));
            bool hasLinkedLines = linkedLines.Any(line => line.rack == rackElement.UniqueId && requested.Contains(line.number));
            if (!functionalZones && !hasLinkedLines && rackNumbers.Count > 0 && string.IsNullOrWhiteSpace(buildingCode))
                return SouyeCollectResult.Failure("У выбранной стойки не заполнен «ARBM_Корпус» и нет сохранённых связей с оповещателями.");

            string rackComments = GetCommentsValue(rackElement);
            var devices = new List<SouyeDeviceInfo>();
            var ambiguousLegacy = new List<long>();
            var ambiguousUnlinked = new List<long>();
            var options = new Dictionary<(string building, int number, int line), List<FamilyInstance>>();
            foreach (FamilyInstance element in current)
            {
                string link = linkedByDevice[element.Id];
                bool sameComments = SameComments(GetCommentsValue(element), rackComments);
                if (link != rackElement.UniqueId && (!sameComments || !string.IsNullOrEmpty(link))) continue;

                var device = _parser.ParseDevice(element);
                if (!device.UsesLegacyNumber)
                {
                    if (link == rackElement.UniqueId && !sameComments)
                    {
                        if (selectedDeviceNumber.HasValue && selectedDeviceNumber != device.RackNumber) continue;
                        return SouyeCollectResult.Failure($"У оповещателя ID {RevitIds.IdOf(element.Id)} «Комментарии» не совпадают со связанной стойкой.");
                    }
                    if (!device.RackNumber.HasValue || !requested.Contains(device.RackNumber.Value)) continue;
                    if (link == rackElement.UniqueId)
                    {
                        devices.Add(device);
                        continue;
                    }

                    var key = (SouyeLineIdentity.NormalizeBuilding(device.HousingBlock)?.ToUpperInvariant(),
                        device.RackNumber.Value, device.LoopAddressParsed);
                    if (!options.TryGetValue(key, out var matches))
                    {
                        matches = MatchUnlinkedRacks(candidates, key.Item1, key.Item2, key.Item3, functionalZones, linkedLines);
                        options.Add(key, matches);
                    }
                    if (matches.Count == 1 && matches[0].Id == rackElement.Id)
                        devices.Add(device);
                    else if (matches.Count > 1 && matches.Any(rack => rack.Id == rackElement.Id))
                        ambiguousUnlinked.Add(RevitIds.IdOf(element.Id));
                    continue;
                }

                if (!sameComments ||
                    string.IsNullOrWhiteSpace(device.LoopAddressRaw) ||
                    (!functionalZones && !CompatibleBuilding(buildingCode, device.HousingBlock))) continue;
                int possible = candidates.Where(rack => SameComments(GetCommentsValue(rack), rackComments) &&
                        (functionalZones || CompatibleBuilding(SouyeLineIdentity.Read(rack, DeviceMarkingOperations.ParamBuilding), device.HousingBlock)))
                    .Sum(rack =>
                    {
                        string value = SouyeLineIdentity.Read(rack, DeviceMarkingOperations.ParamController);
                        return SouyeLineIdentity.TryDeviceNumbers(value, out var numbers) ? Math.Max(1, numbers.Count) : 2;
                    });
                if (possible == 1 && rackNumbers.Count <= 1) devices.Add(device);
                else ambiguousLegacy.Add(RevitIds.IdOf(element.Id));
            }

            if (ambiguousUnlinked.Count > 0)
                return SouyeCollectResult.Failure($"У {ambiguousUnlinked.Count} оповещателей один номер прибора соответствует нескольким стойкам. Повторите маркировку этих устройств с выбором стойки. ID: " +
                    string.Join(", ", ambiguousUnlinked.Take(10)) + ".");

            string legacyWarning = ambiguousLegacy.Count == 0 ? null :
                $"У {ambiguousLegacy.Count} устройств со старой маркировкой не определён прибор. Укажите номер прибора и линии. ID: " +
                string.Join(", ", ambiguousLegacy.Take(10)) + ".";
            if (devices.Count == 0)
                return SouyeCollectResult.Failure(legacyWarning ?? "У выбранной стойки или прибора нет размеченных устройств СОУЭ.");
            if (legacyWarning != null)
                JournalDialog.Show("Кабельный журнал СОУЭ", legacyWarning + " Эти устройства пропущены.");

            int? legacyNumber = rackNumbers.Count == 1 ? (int?)rackNumbers[0] : null;
            var groups = devices.GroupBy(device => device.UsesLegacyNumber ? legacyNumber : device.RackNumber)
                .OrderBy(group => group.Key)
                .Select(group => new SouyeControllerWithLoops
                {
                    Controller = _parser.ParseController(rackElement, rackComments, group.Key, rackNumbers.Count > 1),
                    LoopGroups = BuildLoopGroups(group.ToList(), linkedByDevice, functionalZones),
                }).ToList();
            return SouyeCollectResult.Success(groups);
        }

        private static List<FamilyInstance> MatchUnlinkedRacks(List<FamilyInstance> candidates, string building, int number, int line,
            bool functionalZones, HashSet<(string rack, int number, int line)> linkedLines)
        {
            var matches = candidates.Where(rack => SouyeLineIdentity.RackContains(rack, number)).ToList();
            var sameBuilding = string.IsNullOrWhiteSpace(building) ? new List<FamilyInstance>() :
                matches.Where(rack => SouyeLineIdentity.MatchesRack(rack, building, number)).ToList();
            if (!functionalZones && sameBuilding.Count > 0) return sameBuilding;

            // Без связи режим профиля сохраняет прежнее толкование. Сохранённый связанный шлейф также позволяет
            // восстановить старую маркировку, если у номера ровно одна стойка в этой группе комментариев.
            if (functionalZones || matches.Any(rack => linkedLines.Contains((rack.UniqueId, number, line))))
                return matches;
            return sameBuilding;
        }

        private static bool CompatibleBuilding(string first, string second) =>
            string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second) || SouyeLineIdentity.SameBuilding(first, second);

        /// <summary>
        /// Группирует оповещатели по номеру шлейфа (<see cref="SouyeDeviceInfo.LoopAddressParsed"/>).
        /// <para>
        /// Сортировка групп: первый уровень — <see cref="SouyeDeviceInfo.HousingBlock"/> (корпус),
        /// второй уровень — <see cref="SouyeLoopGroup.LoopAddress"/> (номер шлейфа).
        /// Устройства внутри группы сортируются по <see cref="SouyeDeviceInfo.ElementMarkParsed"/>.
        /// </para>
        /// <para>
        /// Устройства с незаполненным (<c>null</c>) корпусом попадают в начало списка
        /// (сортировка по <c>null</c> как пустой строке).
        /// </para>
        /// </summary>
        private List<SouyeLoopGroup> BuildLoopGroups(List<SouyeDeviceInfo> devices, Dictionary<ElementId, string> linkedByDevice,
            bool functionalZones)
        {
            return devices
                // Группируем по номеру шлейфа
                // возвращает коллекцию объектов типа:IEnumerable<IGrouping<int, SouyeDeviceInfo>>
                // Key — значение, по которому сгруппировали
                // (в твоём случае — номер шлейфа)
                // Коллекцию элементов, которые попали в эту группу
                // (все устройства с этим номером шлейфа)
                .GroupBy(d => d.LoopAddressParsed)              
                .Select(g => new SouyeLoopGroup
                {
                    // где g — это группа устройств, созданная методом .GroupBy(d => d.LoopAddressParsed).
                    // g.Key → номер шлейфа
                    // g → коллекция устройств, которые относятся к этому шлейфу
                    LoopAddress = g.Key,
                    FunctionalZones = g.Any(device => !string.IsNullOrEmpty(linkedByDevice[device.Element.Id])) ||
                        (functionalZones && g.Any(device => !device.UsesLegacyNumber)),
                    // Внутри группы — сортировка по позиционному номеру устройства
                    Devices = g
                        .OrderBy(d => d.ElementMarkParsed)
                        .ToList(),
                })
                // Корпус и стойка уже выбраны; группы сортируются по номеру линии.
                .OrderBy(g => g.LoopAddress)
                .ToList();
        }

        /// <summary>
        /// Читает значение параметра <c>ALL_MODEL_INSTANCE_COMMENTS</c> у элемента.
        /// </summary>
        private static bool SameComments(string a, string b) =>
            string.Equals(a?.Trim() ?? "", b?.Trim() ?? "", StringComparison.OrdinalIgnoreCase);

        private static string GetCommentsValue(Element element)
        {
            return element
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString();
        }
    }
}
