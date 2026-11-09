using ARBM.CableJournalExporter.Systems.Souye.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Systems.Souye
{
    /// <summary>
    /// Maps logical parameter names to actual parameter names based on the selected standard.
    /// <para>RU: Сопоставляет логические имена параметров с фактическими именами на основе выбранного стандарта.</para>
    /// </summary>
    public class SouyeParameterMapper
    {
        /// <summary>
        /// Gets the parameter name for nominal power.
        /// <para>RU: Получает имя параметра для номинальной мощности.</para>
        /// </summary>
        public string PowerParameterName { get; }

        /// <summary>
        /// Gets the parameter name for floor number.
        /// <para>RU: Получает имя параметра для номера этажа.</para>
        /// </summary>
        public string FloorParameterName { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SouyeParameterMapper"/> class.
        /// <para>RU: Инициализирует новый экземпляр класса <see cref="SouyeParameterMapper"/>.</para>
        /// </summary>
        /// <param name="standard">The parameter naming standard to use.
        /// <para>RU: Стандарт именования параметров для использования.</para></param>
        public SouyeParameterMapper(SouyeParameterStandard standard)
        {
            switch (standard)
            {
                case SouyeParameterStandard.Eom:
                    PowerParameterName = "ЭОМ_Мощность_Номинальная";
                    FloorParameterName = "О_Этаж";
                    break;

                case SouyeParameterStandard.Adsk:
                    PowerParameterName = "ADSK_Номинальная мощность";
                    FloorParameterName = "ADSK_Этаж";
                    break;

                default:
                    PowerParameterName = "ЭОМ_Мощность_Номинальная";
                    FloorParameterName = "О_Этаж";
                    break;
            }
        }
    }
}
