using Autodesk.Revit.DB;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Systems.Souye.Models
{
    /// <summary>
    /// Модель единственного контроллера СОУЭ (прибора управления оповещением).
    /// Соответствует элементу категории <c>OST_ElectricalEquipment</c> в Revit.
    /// </summary>
    public class SouyeControllerInfo
    {
        /// <summary>
        /// Ссылка на элемент Revit (прибор управления оповещением).
        /// Используется для расчёта длины кабеля до первого оповещателя шлейфа.
        /// </summary>
        public Element Element { get; set; }

        /// <summary>
        /// Этаж установки контроллера.
        /// Источник: параметр <c>ADSK_Этаж</c>.
        /// Используется как <c>CableRecord.StartLevel</c> для первого сегмента каждого шлейфа.
        /// </summary>
        public string Floor { get; set; }

        /// <summary>
        /// Корпус здания, в котором установлен контроллер.
        /// Источник: параметр <c>ARBM_Корпус</c>.
        /// Может быть <c>null</c> или пустой строкой — в этом случае не включается в форматирование точки.
        /// </summary>
        public string HousingBlock { get; set; }

        /// <summary>
        /// Наименование изделия (прибора управления).
        /// Источник: параметр <c>ARBM_ПС_Имя изделия</c>.
        /// Используется как составная часть <c>CableRecord.StartPoint</c>.
        /// </summary>
        public string ProductName { get; set; }

        /// <summary>
        /// Значение параметра Comments, по которому был выполнен отбор контроллера.
        /// Источник: <c>BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS</c>.
        /// Используется для перекрёстной проверки при сборе устройств.
        /// </summary>
        public string CommentsValue { get; set; }
    }

    /// <summary>
    /// Модель оповещателя СОУЭ (речевого или тонального).
    /// Соответствует элементу категории <c>OST_NurseCallDevices</c> в Revit.
    /// </summary>
    public class SouyeDeviceInfo
    {
        /// <summary>
        /// Ссылка на элемент Revit (оповещатель).
        /// Используется для расчёта длины кабеля между соседними устройствами шлейфа.
        /// </summary>
        public Element Element { get; set; }

        /// <summary>
        /// Этаж установки оповещателя.
        /// Источник: параметр <c>ADSK_Этаж</c>.
        /// Используется как <c>CableRecord.StartLevel</c> / <c>CableRecord.EndLevel</c>.
        /// </summary>
        public string Floor { get; set; }

        /// <summary>
        /// Корпус здания, в котором установлен оповещатель.
        /// Источник: параметр <c>ARBM_Корпус</c>.
        /// Может быть <c>null</c> — в этом случае не включается в форматирование точки.
        /// Используется как поле сортировки (первый уровень): сначала по корпусу, затем по шлейфу.
        /// </summary>
        public string HousingBlock { get; set; }

        /// <summary>
        /// Буквенно-цифровое обозначение (БЦО) оповещателя по ГОСТ 2.710.
        /// Источник: параметр <c>ARBM_ПСБ_БЦО</c>.
        /// Используется как составная часть <c>CableRecord.StartPoint</c> / <c>CableRecord.EndPoint</c>.
        /// </summary>
        public string Bco { get; set; }

        /// <summary>
        /// Адрес шлейфа (номер шлейфа) в виде строки — как хранится в параметре Revit.
        /// Источник: параметр <c>ARBM_ПСБ_Адрес устройства</c>.
        /// Должен быть числовым — валидируется в <see cref="SouyeDataValidator"/>.
        /// </summary>
        public string LoopAddressRaw { get; set; }

        /// <summary>
        /// Адрес шлейфа, преобразованный в целое число для сортировки и группировки.
        /// Заполняется в <see cref="SouyeElementParser"/> при успешном парсинге <see cref="LoopAddressRaw"/>.
        /// Значение <c>-1</c> означает, что парсинг не удался — будет выявлено в валидаторе.
        /// </summary>
        public int LoopAddressParsed { get; set; } = -1;

        /// <summary>
        /// Позиционный номер оповещателя в шлейфе в виде строки — как хранится в параметре Revit.
        /// Источник: параметр <c>ARBM_SP_ElementMark</c>.
        /// Должен быть числовым — валидируется в <see cref="SouyeDataValidator"/>.
        /// Должен быть уникальным в пределах одного шлейфа.
        /// </summary>
        public string ElementMarkRaw { get; set; }

        /// <summary>
        /// Позиционный номер оповещателя в шлейфе, преобразованный в целое число для сортировки.
        /// Заполняется в <see cref="SouyeElementParser"/> при успешном парсинге <see cref="ElementMarkRaw"/>.
        /// Значение <c>-1</c> означает, что парсинг не удался — будет выявлено в валидаторе.
        /// </summary>
        public int ElementMarkParsed { get; set; } = -1;

        /// <summary>
        /// Потребляемая мощность оповещателя в Ваттах.
        /// Источник: параметр <c>ARBM_Мощность</c>.
        /// Значение <c>null</c> означает, что параметр не задан — будет выявлено в валидаторе.
        /// Используется для расчёта суммарной мощности шлейфа и выбора сечения кабеля.
        /// </summary>
        public double? Power { get; set; }
    }

    /// <summary>
    /// Группа оповещателей, объединённых одним шлейфом СОУЭ.
    /// Шлейф определяется единым значением <c>ARBM_ПСБ_Адрес устройства</c>.
    /// </summary>
    public class SouyeLoopGroup
    {
        /// <summary>
        /// Номер шлейфа (ключ группировки).
        /// Соответствует <see cref="SouyeDeviceInfo.LoopAddressParsed"/> всех устройств группы.
        /// </summary>
        public int LoopAddress { get; set; }

        /// <summary>
        /// Список оповещателей шлейфа, отсортированных по <see cref="SouyeDeviceInfo.ElementMarkParsed"/>
        /// по возрастанию. Порядок определяет последовательность сегментов кабельного журнала.
        /// </summary>
        public List<SouyeDeviceInfo> Devices { get; set; } = new List<SouyeDeviceInfo>();

        /// <summary>
        /// Суммарная потребляемая мощность всех оповещателей шлейфа в Ваттах.
        /// Вычисляется в <see cref="SouyeDataCollector"/> как сумма <see cref="SouyeDeviceInfo.Power"/>
        /// по всем устройствам группы. Используется в формуле расчёта сечения кабеля.
        /// </summary>
        public double TotalPower => Devices.Sum(d => d.Power ?? 0.0);
    }

    /// <summary>
    /// Итоговая доменная модель СОУЭ: единственный контроллер со всеми шлейфами оповещателей.
    /// Является входным параметром для <see cref="SouyeDataValidator"/> и <see cref="SouyeCableRecordBuilder"/>.
    /// </summary>
    public class SouyeControllerWithLoops
    {
        /// <summary>
        /// Прибор управления оповещением (контроллер СОУЭ).
        /// Источник первого сегмента каждого шлейфа в кабельном журнале.
        /// </summary>
        public SouyeControllerInfo Controller { get; set; }

        /// <summary>
        /// Список шлейфов с оповещателями, отсортированных по полю сортировки:
        /// первый уровень — <see cref="SouyeDeviceInfo.HousingBlock"/> (корпус),
        /// второй уровень — <see cref="SouyeLoopGroup.LoopAddress"/> (номер шлейфа).
        /// </summary>
        public List<SouyeLoopGroup> LoopGroups { get; set; } = new List<SouyeLoopGroup>();
    }
}
