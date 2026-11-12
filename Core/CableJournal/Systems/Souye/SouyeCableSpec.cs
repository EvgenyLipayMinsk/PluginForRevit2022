using System;
using System.Collections.Generic;
using System.Linq;

namespace ARBM.CableJournalExporter.Systems.Souye.Models
{
    /// <summary>
    /// Описание одной марки кабеля из справочника допустимых кабелей для шлейфов СОУЭ.
    /// </summary>
    public class SouyeCableSpec
    {
        /// <summary>
        /// Марка кабеля (тип).
        /// Пример: <c>"КСРЭПнг(А)-FRHF"</c>.
        /// Записывается в <c>CableRecord.CableType</c>.
        /// </summary>
        public string CableType { get; set; }

        /// <summary>
        /// Конфигурация кабеля: число жил и диаметр.
        /// Пример: <c>"2х0,5"</c>, <c>"1х2х0,8мм"</c>.
        /// Записывается в <c>CableRecord.CableCategory</c>.
        /// </summary>
        public string CableCategory { get; set; }

        /// <summary>
        /// Сечение жилы кабеля в мм².
        /// Используется как ключ для выбора кабеля по расчётному сечению.
        /// Стандартные значения: 0.2, 0.5, 0.75, 1.0, 1.5, 2.5.
        /// </summary>
        public double CrossSection { get; set; }

        /// <summary>
        /// Инициализирует новый экземпляр <see cref="SouyeCableSpec"/>.
        /// </summary>
        /// <param name="cableType">Марка кабеля.</param>
        /// <param name="cableCategory">Конфигурация кабеля.</param>
        /// <param name="crossSection">Сечение жилы в мм².</param>
        public SouyeCableSpec(string cableType, string cableCategory, double crossSection)
        {
            CableType = cableType;
            CableCategory = cableCategory;
            CrossSection = crossSection;
        }
    }

    /// <summary>
    /// Статический справочник допустимых кабелей для шлейфов СОУЭ и утилиты расчёта сечения.
    /// <para>
    /// Содержит список стандартных кабелей, физические константы линии 100В
    /// и метод подбора кабеля по расчётному сечению.
    /// </para>
    /// </summary>
    public static class SouyeCableCatalog
    {
        // ─── Физические константы линии СОУЭ 100В ────────────────────────────────

        /// <summary>
        /// Удельное электрическое сопротивление меди (ρ), Ом·мм²/м.
        /// Стандартное значение для расчётов по ГОСТ.
        /// </summary>
        public const double CopperResistivity = 0.0175;

        /// <summary>
        /// Номинальное напряжение линии оповещения, В.
        /// Для систем СОУЭ с трансляцией 100В.
        /// </summary>
        public const double LineVoltage = 100.0;

        /// <summary>
        /// Максимально допустимое падение напряжения в линии, В.
        /// Принято 15% от номинального напряжения 100В = 10В.
        /// </summary>
        public const double AllowedVoltageDrop = 10.0;

        /// <summary>
        /// Максимально допустимое расчётное сечение кабеля в мм².
        /// При превышении команда завершается с сообщением об ошибке.
        /// </summary>
        public const double MaxAllowedCrossSection = 2.5;

        /// <summary>
        /// Резерв мощности, добавляемый к суммарной мощности шлейфа при расчёте сечения кабеля, Вт.
        /// Значение по умолчанию: 20 Вт. Может быть изменено через интерфейс диалогового окна.
        /// </summary>
        public static double PowerReserve { get; set; } = 20.0;

        // ─── Справочник кабелей ───────────────────────────────────────────────────

        /// <summary>
        /// Список допустимых кабелей для шлейфов СОУЭ, отсортированных по сечению по возрастанию.
        /// Используется для подбора кабеля методом «округления вверх» до ближайшего стандартного сечения.
        /// </summary>
        public static readonly List<SouyeCableSpec> StandardCables = new List<SouyeCableSpec>
        {
            //new SouyeCableSpec("КСРЭПнг(А)-FRHF", "2х0,5",       0.20),
            new SouyeCableSpec("КСРЭПнг(А)-FRHF", "1х2х0,8",   0.50),
            new SouyeCableSpec("КСРЭПнг(А)-FRHF", "1х2х0,97",  0.75),
            new SouyeCableSpec("КСРЭПнг(А)-FRHF", "1х2х1,13",  1.00),
            new SouyeCableSpec("КСРЭПнг(А)-FRHF", "1х2х1,38",  1.50),
            new SouyeCableSpec("КСРЭПнг(А)-FRHF", "1х2х1,78",  2.50),
        };

        // ─── Расчёт ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Рассчитывает требуемое сечение кабеля шлейфа по формуле:
        /// <code>S = (ρ × 2L × (P + P_рез)) / (U × U_пад)</code>
        /// где коэффициент 2 учитывает двухпроводную линию (ток идёт туда и обратно),
        /// P_рез — резерв мощности <see cref="PowerReserve"/>.
        /// </summary>
        /// <param name="totalPowerWatt">Суммарная мощность всех оповещателей шлейфа, Вт.</param>
        /// <param name="totalLengthMeters">Суммарная протяжённость шлейфа, м.</param>
        /// <returns>Расчётное сечение кабеля в мм².</returns>
        public static double CalculateCrossSection(double totalPowerWatt, double totalLengthMeters)
        {
            double totalPowerWithReserve = totalPowerWatt + PowerReserve;
            return (CopperResistivity * 2.0 * totalLengthMeters * totalPowerWithReserve)
                   / (LineVoltage * AllowedVoltageDrop);
        }

        /// <summary>
        /// Подбирает кабель из справочника <see cref="StandardCables"/> методом округления вверх:
        /// возвращает первый кабель, сечение которого не меньше расчётного.
        /// </summary>
        /// <param name="calculatedSection">Расчётное сечение кабеля в мм², полученное из <see cref="CalculateCrossSection"/>.</param>
        /// <returns>
        /// Подходящий <see cref="SouyeCableSpec"/>, или <c>null</c>, если расчётное сечение
        /// превышает <see cref="MaxAllowedCrossSection"/> (2,5 мм²).
        /// </returns>
        public static SouyeCableSpec GetByCrossSection(double calculatedSection)
        {
            // Список уже отсортирован по возрастанию CrossSection,
            // поэтому FirstOrDefault вернёт минимально достаточное сечение.
            return StandardCables.FirstOrDefault(c => c.CrossSection >= calculatedSection);
        }

        /// <summary>
        /// Форматирует значение сечения кабеля с округлением в большую сторону до двух знаков после запятой.
        /// Пример: 0.076 → "0,08", 0.051 → "0,05", 0.005 → "0,01".
        /// </summary>
        /// <param name="crossSection">Сечение в мм².</param>
        /// <returns>Отформатированная строка сечения с локализацией.</returns>
        public static string FormatCrossSection(double crossSection)
        {
            // Округляем в большую сторону до 2 знаков после запятой
            double rounded = Math.Ceiling(crossSection * 100) / 100.0;
            return rounded.ToString("F2", System.Globalization.CultureInfo.CurrentCulture);
        }
    }
}
