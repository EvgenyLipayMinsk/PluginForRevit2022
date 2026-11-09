using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using System.Linq;

namespace ARBM.CableJournalExporter.Systems.Souye
{
    /// <summary>
    /// Извлекает параметры элементов Revit и преобразует их в доменные модели СОУЭ.
    /// <para>
    /// Отвечает только за чтение и преобразование данных — не выполняет валидацию.
    /// Некорректные значения (например, нечисловые строки в числовых полях) фиксируются
    /// через сигнальное значение <c>-1</c> в <c>*Parsed</c>-свойствах и выявляются
    /// позднее в <see cref="SouyeDataValidator"/>.
    /// </para>
    /// </summary>
    public class SouyeElementParser
    {
        // ─── Имена параметров ─────────────────────────────────────────────────────

        private readonly string _paramFloor;
        private readonly string _paramPower;
        
        private const string ParamHousingBlock = "ARBM_Корпус";
        private const string ParamProductName  = "ARBM_ПС_Имя изделия";
        private const string ParamBco          = "ARBM_ПСБ_БЦО";
        private const string ParamLoopAddress  = "ARBM_ПСБ_Адрес устройства";
        private const string ParamElementMark  = "ARBM_SP_ElementMark";

        // ─── Конструктор ──────────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the <see cref="SouyeElementParser"/> class.
        /// <para>RU: Инициализирует новый экземпляр класса <see cref="SouyeElementParser"/>.</para>
        /// </summary>
        /// <param name="parameterMapper">The parameter mapper defining parameter names based on the selected standard.
        /// <para>RU: Маппер параметров, определяющий имена параметров на основе выбранного стандарта.</para></param>
        public SouyeElementParser(SouyeParameterMapper parameterMapper)
        {
            _paramFloor = parameterMapper.FloorParameterName;
            _paramPower = parameterMapper.PowerParameterName;
        }

        // ─── Публичные методы ─────────────────────────────────────────────────────

        /// <summary>
        /// Создаёт модель контроллера СОУЭ из элемента Revit категории
        /// <c>OST_ElectricalEquipment</c>.
        /// </summary>
        /// <param name="element">Элемент прибора управления оповещением.</param>
        /// <param name="commentsValue">
        /// Значение параметра <c>Comments</c>, по которому элемент был отобран.
        /// Записывается в <see cref="SouyeControllerInfo.CommentsValue"/> для последующей
        /// перекрёстной проверки при сборе устройств.
        /// </param>
        /// <returns>Заполненный <see cref="SouyeControllerInfo"/>.</returns>
        public SouyeControllerInfo ParseController(Element element, string commentsValue)
        {
            return new SouyeControllerInfo
            {
                Element       = element,
                Floor         = TryGetStringParam(element, _paramFloor),
                HousingBlock  = TryGetStringParam(element, ParamHousingBlock),
                ProductName   = TryGetStringParam(element, ParamProductName),
                CommentsValue = commentsValue,
            };
        }

        /// <summary>
        /// Создаёт модель оповещателя СОУЭ из элемента Revit категории
        /// <c>OST_NurseCallDevices</c>.
        /// <para>
        /// Числовые поля (<see cref="SouyeDeviceInfo.LoopAddressParsed"/>,
        /// <see cref="SouyeDeviceInfo.ElementMarkParsed"/>) заполняются при успешном
        /// парсинге строковых значений. При неудаче остаётся сигнальное значение <c>-1</c>.
        /// </para>
        /// </summary>
        /// <param name="element">Элемент оповещателя.</param>
        /// <returns>Заполненный <see cref="SouyeDeviceInfo"/>.</returns>
        public SouyeDeviceInfo ParseDevice(Element element)
        {
            var loopAddressRaw = TryGetStringParam(element, ParamLoopAddress);
            var elementMarkRaw = TryGetStringParam(element, ParamElementMark);

            var device = new SouyeDeviceInfo
            {
                Element        = element,
                Floor          = TryGetStringParam(element, _paramFloor),
                HousingBlock   = TryGetStringParam(element, ParamHousingBlock),
                Bco            = TryGetStringParam(element, ParamBco),
                LoopAddressRaw = loopAddressRaw,
                ElementMarkRaw = elementMarkRaw,
                Power          = TryGetDoubleParam(element, _paramPower),
            };

            // Попытка преобразовать строковые адреса в числа.
            // Неудача не генерирует исключение — фиксируется через значение -1
            // и будет выявлена в SouyeDataValidator.
            if (TryParseIntFromString(loopAddressRaw, out int loopAddressParsed))
                device.LoopAddressParsed = loopAddressParsed;

            if (TryParseIntFromString(elementMarkRaw, out int elementMarkParsed))
                device.ElementMarkParsed = elementMarkParsed;

            return device;
        }

        // ─── Вспомогательные методы ───────────────────────────────────────────────

        /// <summary>
        /// Читает строковый параметр элемента по имени.
        /// Сначала ищет среди параметров экземпляра, затем среди параметров типа.
        /// </summary>
        /// <param name="element">Элемент Revit.</param>
        /// <param name="paramName">Имя параметра (shared или project parameter).</param>
        /// <returns>
        /// Строковое значение параметра, или <c>null</c>, если параметр не найден
        /// или его значение не задано.
        /// </returns>
        public string TryGetStringParam(Element element, string paramName)
        {
            // 1. Сначала ищем в параметрах экземпляра
            var param = element.LookupParameter(paramName);
            
            // 2. Если не нашли, ищем в параметрах типа
            if (param == null && element.Document != null)
            {
                var elementType = element.Document.GetElement(element.GetTypeId()) as ElementType;
                if (elementType != null)
                {
                    param = elementType.LookupParameter(paramName);
                }
            }

            if (param == null || !param.HasValue)
                return null;

            return param.AsString()
                ?? param.AsValueString();
        }

        /// <summary>
        /// Читает числовой параметр элемента (тип <c>Double</c>) по имени.
        /// Сначала ищет среди параметров экземпляра, затем среди параметров типа.
        /// </summary>
        /// <param name="element">Элемент Revit.</param>
        /// <param name="paramName">Имя параметра.</param>
        /// <returns>
        /// Числовое значение параметра, или <c>null</c>, если параметр не найден,
        /// не задан или имеет несовместимый тип хранилища.
        /// </returns>
        public double? TryGetDoubleParam(Element element, string paramName)
        {
            // 1. Сначала ищем в параметрах экземпляра
            var param = element.LookupParameter(paramName);
            
            // 2. Если не нашли, ищем в параметрах типа
            if (param == null && element.Document != null)
            {
                var elementType = element.Document.GetElement(element.GetTypeId()) as ElementType;
                if (elementType != null)
                {
                    param = elementType.LookupParameter(paramName);
                }
            }

            if (param == null || !param.HasValue)
                return null;

            // 3) Если параметр строковый — пробуем извлечь число из AsString()
            if (param.StorageType == StorageType.String)
            {
                var raw = param.AsString();
                var parsed = ExtractNumber(raw);
                return parsed;
            }

            // 4) Если параметр Double — но имеет форматированное значение
            //    (например "3 Вт"), то AsString() может быть null,
            //    но AsValueString содержит нужное число.
            if (param.StorageType == StorageType.Double)
            {
                var raw = param.AsValueString(); // "3 Вт"
                var parsed = ExtractNumber(raw);

                if (parsed != null)
                    return parsed;

                // fallback — если AsValueString не содержит числа
                return null;
            }

            return null;
        }
        
        private double? ExtractNumber(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            // Оставляем только цифры, точку и запятую
            var cleaned = new string(raw
                .Where(c => char.IsDigit(c) || c == '.' || c == ',')
                .ToArray());

            if (string.IsNullOrWhiteSpace(cleaned))
                return null;

            // Пробуем парсить
            if (double.TryParse(cleaned.Replace(',', '.'),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out double result))
            {
                return result;
            }

            return null;
        }

        /// <summary>
        /// Пытается преобразовать строку в целое число.
        /// Используется для парсинга <c>ARBM_ПСБ_Адрес устройства</c> и
        /// <c>ARBM_SP_ElementMark</c>, которые в Revit хранятся как строки,
        /// но семантически являются числами.
        /// </summary>
        /// <param name="value">Строка для преобразования.</param>
        /// <param name="result">Результат преобразования при успехе; 0 при неудаче.</param>
        /// <returns>
        /// <c>true</c>, если строка корректно преобразована в <c>int</c>; иначе <c>false</c>.
        /// </returns>
        public bool TryParseIntFromString(string value, out int result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return int.TryParse(value.Trim(), out result);
        }
    }
}
