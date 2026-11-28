using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ScanFolderCopyAndProcess
{

    /// <summary>
    /// Result of rfa file processing.
    /// </summary>
    public class RfaFileResult
    {
        public string PathFrom { get; set; }
        public string PathTo { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

}
