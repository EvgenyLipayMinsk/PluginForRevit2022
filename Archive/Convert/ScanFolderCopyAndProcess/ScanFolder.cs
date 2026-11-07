using System;
using System.Collections.Generic;
//using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Alphaleonis.Win32.Filesystem;

namespace ScanFolderCopyAndProcess
{

    public class ScanFolder
    {

        // <summary>
        // Ensure that folders are different, folder_from exists and folder_to does not exist. 
        // </summary>
        /// <returns>True if folder_from exists and folder_to does not exist.</returns>            
        public static bool Prepare(DirectoryInfo folder_from, DirectoryInfo folder_to, out string message)
        {
            if (folder_from.FullName == folder_to.FullName)
            {
                message = "folder_from == folder_to";
                return false;
            }
            if (folder_from.Exists == false)
            {
                message = "folder_from does not exist";
                return false;
            }
            if (folder_to.Exists == true) folder_to.Delete(true); // with files subfolders. It does not work !!!
            if (folder_to.Exists == true) 
            {
                message = "Cannot delete folder_to";
                return false; 
            }
            message = "OK";
            return true;
        }

        public static bool Prepare(string folder_from, string folder_to, out string message)
        {
            if (folder_from == folder_to) 
            { 
                message = "folder_from == folder_to"; 
                return false; 
            }
            if (Directory.Exists(folder_from) == false)
            {
                message = "folder_from does not exist";
                return false;
            }
            if (Directory.Exists(folder_to) == true)
            {
                Directory.Delete(folder_to, true); // Works fine !!!
            } 
            if (Directory.Exists(folder_to) == true)
            {
                message = "Cannot delete folder_to";
                return false; // Error, if cannot delete.
            }
            message = "OK";
            return true;
        }



        /// <summary>
        /// Scan folder recursively, process rfa files and copy other content. Make text log and get list of rfa-file results.
        /// </summary>
        /// <param name="folder_from">Source folder</param>
        /// <param name="folder_to">Target folder</param>
        /// <param name="rfa_file_results">list of results for ffa files</param>
        /// <param name="log">Text logger</param>
        public static void ScanFolderMain(DirectoryInfo folder_from, DirectoryInfo folder_to, List<RfaFileResult> rfa_file_results, Logger log)
        {
            try
            {
                // Read source folder.
                log.LogInfo($"Read folder {folder_from.FullName}");
                if (folder_from.Exists == false)
                {
                    log.LogInfo($"Cannot find folder {folder_from.FullName}");
                    return;
                }

                // create target folder.
                log.LogInfo($"Create folder '{folder_to.FullName}'");
                folder_to.Create();
                if (folder_to.Exists == false)
                {
                    log.LogError($"Cannot create folder '{folder_to.FullName}'");
                    return;
                }

                // Scan source folder. 
                FileInfo[] subfiles = folder_from.GetFiles();
                DirectoryInfo[] subfolders = folder_from.GetDirectories();

                // Process files. 
                foreach (var file_from in subfiles)
                {
                    string path_to = Path.Combine(folder_to.FullName, file_from.Name);
                    FileInfo file_to = new FileInfo(path_to);

                    if (file_from.Extension.ToLower() == ".rfa")
                    {
                        log.LogInfo($"PROCESS {file_from.FullName}");
                        ProcessRfaFile(file_from.FullName, file_to.FullName, out RfaFileResult rfa_file_result, log);
                        rfa_file_results.Add(rfa_file_result);
                    }
                    else
                    {
                        log.LogInfo($"COPY {file_from.FullName}");
                        try
                        {
                            File.Copy(file_from.FullName, file_to.FullName);
                            log.LogInfo($"COPIED FROM {file_from.FullName} TO {file_to.FullName}");
                        }
                        catch (Exception ex1)
                        {
                            log.LogWarning(ex1.Message);
                            try
                            {
                                File.Copy(file_from.FullName, file_to.FullName);
                                log.LogWarning($"COPIED FROM {file_from.FullName} TO {file_to.FullName}");
                            }
                            catch (Exception ex2)
                            {
                                log.LogError(ex2.Message);
                            }
                        }
                    }
                }

                // Process subfolders.
                foreach (var subfolder_from in subfolders)
                {
                    string path_to = Path.Combine(folder_to.FullName, subfolder_from.Name);
                    DirectoryInfo subfolder_to = new DirectoryInfo(path_to);

                    ScanFolderMain(subfolder_from, subfolder_to, rfa_file_results, log);
                }
            }
            catch (Exception ex3)
            {
                log.LogWarning($"EXCEPTION {ex3.Message} IN THE ScanFolderMain METHOD.");
            }

        }

        /// <summary>
        /// Template for processing Rfa file.
        /// </summary>
        /// <param name="path_from"></param>
        /// <param name="path_to"></param>
        /// <param name="rfa_file_result"></param>
        /// <param name="log"></param>
        public static void ProcessRfaFile(string path_from, string path_to, out RfaFileResult rfa_file_result, Logger log)
        {
            log.LogInfo($"... Insert real Rfa file processing here. Now Just copy ...");

           // File.Copy(path_from, path_to);

            rfa_file_result = new RfaFileResult
            {
                PathFrom = path_from,
                PathTo = path_to,
                Success = true,
                Message = "OK"
            };
        }

    }
}
