using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ARBM.CableJournalExporter.Models
{
    /// <summary>
    /// Defines the mapping between cable record properties and Excel columns
    /// in the template file.
    /// </summary>
    public class ExcelColumnMap
    {
        public string StartLevelColumn = "C";
        public string StartPointColumn = "D";
        public string EndLevelColumn = "E";
        public string EndPointColumn = "F";
        public string CableTypeColumn = "G";
        public string CableCategoryColumn = "H";
        public string PurposeColumn = "I";
        public string LengthColumn = "J";
        public string FallbackForLengthColumn = "K";
    }

}
