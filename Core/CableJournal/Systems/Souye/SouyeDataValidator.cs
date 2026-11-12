using ARBM.CableJournalExporter.Systems.Souye.Models;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Systems.Souye
{
    /// <summary>
    /// Проверяет корректность и полноту доменной модели <see cref="SouyeControllerWithLoops"/>
    /// перед построением кабельного журнала.
    /// <para>
    /// Валидатор не останавливается на первой ошибке — собирает полный список нарушений,
    /// чтобы пользователь мог исправить все проблемы за один проход.
    /// Список ошибок отображается в диалоговом окне в <c>ExportSouyeCableJournalCommand</c>.
    /// </para>
    /// </summary>
    public class SouyeDataValidator
    {
        private readonly string _powerParameterName;
        private readonly string _floorParameterName;

        /// <summary>
        /// Initializes a new instance of the <see cref="SouyeDataValidator"/> class.
        /// <para>RU: Инициализирует новый экземпляр класса <see cref="SouyeDataValidator"/>.</para>
        /// </summary>
        /// <param name="parameterMapper">The parameter mapper defining parameter names based on the selected standard.
        /// <para>RU: Маппер параметров, определяющий имена параметров на основе выбранного стандарта.</para></param>
        public SouyeDataValidator(SouyeParameterMapper parameterMapper)
        {
            _powerParameterName = parameterMapper.PowerParameterName;
            _floorParameterName = parameterMapper.FloorParameterName;
        }

        /// <summary>
        /// Выполняет полную проверку доменной модели СОУЭ.
        /// </summary>
        /// <param name="data">Доменная модель, собранная <see cref="SouyeDataCollector"/>.</param>
        /// <returns>
        /// Список строк с описаниями ошибок. Пустой список означает успешную валидацию.
        /// </returns>
        public List<string> Validate(SouyeControllerWithLoops data)
        {
            var errors = new List<string>();

            ValidateController(data.Controller, errors);
            ValidateLoopGroups(data.LoopGroups, errors);

            return errors;
        }

        // ─── Проверки контроллера ─────────────────────────────────────────────────

        /// <summary>
        /// Проверяет обязательные поля контроллера СОУЭ.
        /// </summary>
        private void ValidateController(SouyeControllerInfo controller, List<string> errors)
        {
            if (controller == null)
            {
                errors.Add("Контроллер СОУЭ не определён.");
                return;
            }

            if (string.IsNullOrWhiteSpace(controller.ProductName))
                errors.Add(
                    "Контроллер СОУЭ: не заполнен параметр «ARBM_ПС_Имя изделия».");

            if (string.IsNullOrWhiteSpace(controller.Floor))
                errors.Add(
                    $"Контроллер СОУЭ: не заполнен параметр «{_floorParameterName}».");
        }

        // ─── Проверки шлейфов ─────────────────────────────────────────────────────

        /// <summary>
        /// Проверяет наличие шлейфов и корректность каждого из них.
        /// </summary>
        private void ValidateLoopGroups(List<SouyeLoopGroup> loopGroups, List<string> errors)
        {
            if (loopGroups == null || loopGroups.Count == 0)
            {
                errors.Add(
                    "Не найдено ни одного оповещателя СОУЭ (OST_NurseCallDevices) " +
                    "с заданным значением параметра Comments.");
                return;
            }

            foreach (var group in loopGroups)
                ValidateLoopGroup(group, errors);
        }

        /// <summary>
        /// Проверяет один шлейф: наличие устройств, уникальность ElementMark
        /// и корректность каждого устройства.
        /// </summary>
        private void ValidateLoopGroup(SouyeLoopGroup group, List<string> errors)
        {
            // Идентификатор шлейфа для вывода в сообщениях об ошибках.
            // Если LoopAddressParsed == -1 — адрес шлейфа не удалось разобрать,
            // выводим сырое значение из первого устройства.
            var loopId = group.LoopAddress >= 0
                ? group.LoopAddress.ToString()
                : $"(некорректный адрес: \"{group.Devices.FirstOrDefault()?.LoopAddressRaw}\")";

            if (group.Devices == null || group.Devices.Count == 0)
            {
                errors.Add($"Шлейф {loopId}: не содержит ни одного оповещателя.");
                return;
            }

            // Проверка уникальности ElementMark внутри шлейфа
            var duplicateMarks = group.Devices
                .GroupBy(d => d.ElementMarkRaw)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            foreach (var mark in duplicateMarks)
                errors.Add(
                    $"Шлейф {loopId}: обнаружены устройства с одинаковым " +
                    $"значением «ARBM_SP_ElementMark» = \"{mark}\". " +
                    "Позиционный номер устройства должен быть уникальным в пределах шлейфа.");

            // Проверка каждого устройства
            foreach (var device in group.Devices)
                ValidateDevice(device, loopId, errors);
        }

        // ─── Проверки устройства ──────────────────────────────────────────────────

        /// <summary>
        /// Проверяет корректность и полноту параметров одного оповещателя.
        /// </summary>
        /// <param name="device">Модель оповещателя.</param>
        /// <param name="loopId">Идентификатор шлейфа для вывода в сообщениях.</param>
        /// <param name="errors">Список ошибок для добавления новых нарушений.</param>
        private void ValidateDevice(SouyeDeviceInfo device, string loopId, List<string> errors)
        {
            // Идентификатор устройства для вывода в сообщениях.
            // Включает БЦО (если заполнен), ElementMark и ElementId для быстрого поиска.
            var deviceParts = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(device.Bco))
                deviceParts.Add($"БЦО={device.Bco}");
            
            if (!string.IsNullOrWhiteSpace(device.ElementMarkRaw))
                deviceParts.Add($"ElementMark={device.ElementMarkRaw}");
            
            if (device.Element?.Id != null)
                deviceParts.Add($"ElementId={device.Element.Id}");
            
            var deviceId = deviceParts.Count > 0 
                ? string.Join(", ", deviceParts)
                : $"ElementId={device.Element?.Id}";

            // ── ARBM_ПСБ_Адрес устройства должен быть числом ──────────────────
            if (device.LoopAddressParsed == -1)
                errors.Add(
                    $"Шлейф {loopId}, устройство ({deviceId}): " +
                    $"значение параметра «ARBM_ПСБ_Адрес устройства» = \"{device.LoopAddressRaw}\" " +
                    "не является целым числом.");

            // ── ARBM_SP_ElementMark должен быть числом ────────────────────────
            if (device.ElementMarkParsed == -1)
                errors.Add(
                    $"Шлейф {loopId}, устройство ({deviceId}): " +
                    $"значение параметра «ARBM_SP_ElementMark» = \"{device.ElementMarkRaw}\" " +
                    "не является целым числом.");

            // ── Мощность должна быть задана и положительна ────────────────────
            if (device.Power == null || device.Power <= 0.0)
                errors.Add(
                    $"Шлейф {loopId}, устройство ({deviceId}): " +
                    $"не задана или равна нулю мощность (параметр «{_powerParameterName}»). " +
                    "Мощность необходима для расчёта сечения кабеля шлейфа.");

            // ── БЦО должно быть заполнено ─────────────────────────────────────
            if (string.IsNullOrWhiteSpace(device.Bco))
                errors.Add(
                    $"Шлейф {loopId}, устройство ({deviceId}): " +
                    "не заполнен параметр «ARBM_ПСБ_БЦО» (буквенно-цифровое обозначение).");

            // ── Этаж должен быть заполнен ─────────────────────────────────────
            if (string.IsNullOrWhiteSpace(device.Floor))
                errors.Add(
                    $"Шлейф {loopId}, устройство ({deviceId}): " +
                    $"не заполнен параметр «{_floorParameterName}».");
        }
    }
}
