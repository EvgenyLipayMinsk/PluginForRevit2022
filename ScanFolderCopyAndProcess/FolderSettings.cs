using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading.Tasks;

using Newtonsoft.Json;

namespace ScanFolderCopyAndProcess
{
    public class FolderSettings
    {
        public string folder_from { get; set; }
        public string folder_to { get; set; }


        public static FolderSettings FromFile(string path)
        {
            try
            {
                string json = File.ReadAllText(path);
                FolderSettings result = JsonConvert.DeserializeObject<FolderSettings>(json);
                return result;
            }
            catch
            {
                return null;
            }
        }
    }
}
