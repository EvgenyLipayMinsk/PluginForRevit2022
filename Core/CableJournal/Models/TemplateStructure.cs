using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Models
{
    /// <summary>
    /// Represents the expected structure of the Excel template used
    /// for generating the cable journal.
    /// </summary>
    public class TemplateStructure
    {
        /// <summary>
        /// Expected values of the first header row.
        /// </summary>
        public List<string> HeaderRow1 { get; set; }

        /// <summary>
        /// Expected values of the second header row.
        /// </summary>
        public List<string> HeaderRow2 { get; set; }

        public TemplateStructure()
        {
            HeaderRow1 = new List<string>()
            {
                "Откуда", "", "", "Куда", "Кабель", "", "", ""
            };
            HeaderRow2 = new List<string>()
            {
                "№ этажа", "Начало", "№ этажа", "Конец",
                "Тип кабеля", "Категория", "Назначение кабельной линии", "Длина"
            };
        }
    }
}
