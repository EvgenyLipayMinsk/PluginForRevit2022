using System;
using System.Linq;
//using System.IO;
using System.Collections.Generic;

using Alphaleonis.Win32.Filesystem;


namespace ScanFolderCopyAndProcess
{
    partial class Program
    {
        // Path to start the scan from
        //public static string root_path = @"d:\AERBIM\dbModels3DSD\3DSD";  
        //public static string root_path = @"\\192.168.88.205\aerbim\База данных\3DSDбд\3DSD";  
        //public static string root_path = @"\\Bpp-nas-d2\aerbim\База данных\3DSDбд\3DSD\Revit 2019\";

        public static string folder_settings_path = @"folder_settings_remote_path.json";
        public static string log_path = @"folder_result.log";

        static void Main(string[] args)
        {
            // Log to console, duplicate console logs to file
            var log = new ConsoleLogger();  
            var log_f = new FileLogger(log_path, true);
            log.ChainLogger(log_f); 
            
            var folder_settings = FolderSettings.FromFile(folder_settings_path);
            if (folder_settings == null) 
            {
                log.LogError($"Cannot read '{folder_settings_path}'");
                return;
            }

            try
            {
                // Scan setup
                var folder_from = new DirectoryInfo(folder_settings.folder_from);
                var folder_to = new DirectoryInfo(folder_settings.folder_to);
                log.LogInfo($"folder_from='{folder_from.FullName}'");
                log.LogInfo($"folder_to='{folder_to.FullName}'");
                if (ScanFolder.Prepare(folder_from.FullName, folder_to.FullName, out string message) == false)
                {
                    throw new Exception(message);
                }

                // Scan folder recursively
                var rfa_file_results = new List<RfaFileResult>();
                ScanFolder.ScanFolderMain(folder_from, folder_to, rfa_file_results, log);

                // Report
                int count_all = rfa_file_results.Count;
                int count_ok = rfa_file_results.Where(m => m.Success == true).Count();
                int count_err = rfa_file_results.Where(m => m.Success == false).Count();
                log.LogInfo($"Rfa processing statistics: All={count_all}, Success={count_ok}, Errors={count_err}");
            }
            catch (Exception ex)
            {
                log.LogError(ex.Message);
            }
            log.LogInfo($"Log is saved to '{log_path}'");

            Console.WriteLine("Press any key");
            Console.ReadKey();
        }
    }
}
