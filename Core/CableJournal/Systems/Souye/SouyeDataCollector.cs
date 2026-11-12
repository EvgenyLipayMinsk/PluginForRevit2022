using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
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
        public SouyeControllerWithLoops Data { get; private set; }

        /// <summary>
        /// Сообщение об ошибке, если сбор данных не удался.
        /// Равно <c>null</c>, если сбор выполнен успешно.
        /// </summary>
        public string ErrorMessage { get; private set; }

        /// <summary>Признак успешного завершения сбора.</summary>
        public bool IsSuccess => ErrorMessage == null;

        private SouyeCollectResult() { }

        /// <summary>Создаёт успешный результат с собранными данными.</summary>
        public static SouyeCollectResult Success(SouyeControllerWithLoops data)
            => new SouyeCollectResult { Data = data };

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
    ///   <item>Поиск единственного контроллера (<c>OST_ElectricalEquipment</c>) по значению Comments.</item>
    ///   <item>Сбор всех оповещателей (<c>OST_NurseCallDevices</c>) с тем же значением Comments.</item>
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
        public SouyeCollectResult Collect(Document doc, string commentsValue)
        {
            // ── Шаг 1: поиск контроллера ─────────────────────────────────────────
            var controllerResult = 
                CollectController(doc, commentsValue);
            if (!controllerResult.IsSuccess)
                return SouyeCollectResult.Failure(controllerResult.ErrorMessage);

            var controller = controllerResult.Controller;

            // ── Шаг 2: сбор оповещателей ─────────────────────────────────────────
            var devices = CollectDevices(doc, commentsValue);

            // ── Шаг 3: группировка и сортировка ──────────────────────────────────
            // У прибора номер шлейфа один: шлейф с устройствами разных корпусов слился бы в одну цепь
            // (и сечение считалось бы по мощности обоих) — это ошибка маркировки.
            var mixed = devices.Where(d => d.LoopAddressParsed > 0).GroupBy(d => d.LoopAddressParsed)
                .Select(g => (loop: g.Key, buildings: g.Select(d => d.HousingBlock?.Trim() ?? "").Distinct().ToList()))
                .Where(x => x.buildings.Count > 1).ToList();
            if (mixed.Count > 0)
                return SouyeCollectResult.Failure(
                    "У шлейфов устройства разных корпусов:\n" +
                    string.Join("\n", mixed.Select(x => $"  шлейф {x.loop}: корпуса {string.Join(", ", x.buildings.Select(b => b.Length == 0 ? "(пусто)" : b))}")) +
                    "\nНомер шлейфа прибора должен относиться к одному корпусу. Проверьте «ARBM_Корпус» и адрес устройств.");
            var loopGroups = BuildLoopGroups(devices);

            // ── Шаг 4: сборка итоговой модели ────────────────────────────────────
            var result = new SouyeControllerWithLoops
            {
                Controller = controller,
                LoopGroups = loopGroups,
            };

            return SouyeCollectResult.Success(result);
        }

        // ─── Приватные методы ─────────────────────────────────────────────────────

        /// <summary>
        /// Ищет ровно один элемент <c>OST_ElectricalEquipment</c> с заданным значением Comments.
        /// При отклонении от этого условия возвращает описательную ошибку.
        /// </summary>
        private (SouyeControllerInfo Controller, bool IsSuccess, string ErrorMessage)
            CollectController(Document doc, string commentsValue)
        {
            var controllers = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType()
                .Where(e => SameComments(GetCommentsValue(e), commentsValue))
                .ToList();

            if (controllers.Count == 0)
            {
                return (null, false,
                    $"Не найден контроллер СОУЭ с параметром Comments = \"{commentsValue}\".\n" +
                    "Убедитесь, что в модели присутствует прибор управления оповещением " +
                    "категории «Электрооборудование» с указанным значением комментария.");
            }

            if (controllers.Count > 1)
            {
                return (null, false,
                    $"Найдено {controllers.Count} элементов категории «Электрооборудование» " +
                    $"с параметром Comments = \"{commentsValue}\".\n" +
                    "Должен быть ровно один контроллер СОУЭ. " +
                    "Проверьте корректность заполнения параметра Comments в модели.");
            }

            var controller = _parser.ParseController(controllers[0], commentsValue);
            return (controller, true, null);
        }

        /// <summary>
        /// Собирает все элементы <c>OST_NurseCallDevices</c> с заданным значением Comments
        /// и преобразует их в доменные модели через <see cref="SouyeElementParser"/>.
        /// </summary>
        private List<SouyeDeviceInfo> CollectDevices(Document doc, string commentsValue)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_NurseCallDevices)
                .WhereElementIsNotElementType()
                .Where(e => SameComments(GetCommentsValue(e), commentsValue))
                .Select(e => _parser.ParseDevice(e))
                .ToList();
        }

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
        private List<SouyeLoopGroup> BuildLoopGroups(List<SouyeDeviceInfo> devices)
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
                    // Внутри группы — сортировка по позиционному номеру устройства
                    Devices = g
                        .OrderBy(d => d.ElementMarkParsed)
                        .ToList(),
                })
                // Сортировка групп: по корпусу, затем по номеру шлейфа
                .OrderBy(g => g.Devices.FirstOrDefault()?.HousingBlock ?? string.Empty)
                .ThenBy(g => g.LoopAddress)
                .ToList();
        }

        /// <summary>
        /// Читает значение параметра <c>ALL_MODEL_INSTANCE_COMMENTS</c> у элемента.
        /// </summary>
        private static bool SameComments(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string GetCommentsValue(Element element)
        {
            return element
                .get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)
                ?.AsString();
        }
    }
}
