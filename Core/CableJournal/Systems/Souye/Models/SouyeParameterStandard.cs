using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Systems.Souye.Models
{
    /// <summary>
    /// Defines the parameter naming standards for SOUYE system elements.
    /// <para>RU: Определяет стандарты именования параметров для элементов системы СОУЭ.</para>
    /// </summary>
    public enum SouyeParameterStandard
    {
        /// <summary>
        /// EOM (Electrical Equipment Manufacturer) standard.
        /// Uses: ЭОМ_Мощность_Номинальная, О_Этаж
        /// <para>RU: Стандарт ЭОМ (Электрооборудование и материалы).</para>
        /// </summary>
        Eom,

        /// <summary>
        /// Autodesk standard.
        /// Uses: ADSK_Номинальная мощность, ADSK_Этаж
        /// <para>RU: Стандарт Autodesk.</para>
        /// </summary>
        Adsk
    }
}
