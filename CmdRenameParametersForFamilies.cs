using System;
using System.Collections.Generic;
// using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Alphaleonis.Win32.Filesystem;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;

using ScanFolderCopyAndProcess;

namespace Aerbim._3DSD.Test
{

    [Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    class CmdRenameParametersForFamilies : IExternalCommand
    {

        List<BuiltInCategory> m_categories = new List<BuiltInCategory>()
        {
            BuiltInCategory.OST_FireAlarmDevices,
            BuiltInCategory.OST_DataDevices,
            BuiltInCategory.OST_ElectricalEquipment,
            BuiltInCategory.OST_CommunicationDevices,
            BuiltInCategory.OST_ElectricalFixtures,
            BuiltInCategory.OST_LightingFixtures,
            BuiltInCategory.OST_NurseCallDevices,
            BuiltInCategory.OST_TelephoneDevices,
            BuiltInCategory.OST_SecurityDevices,
        };
        List<string> m_prefixesForDeleteParameters = new List<string>()
        {
            "AER_SP"
        };
        //List<string> m_prefixesForChangeParameters = new List<string>()
        //{
        //    "AER_ПП",
        //    "AER_ПС",
        //    "AER_ПСБ",
        //    "AER_ТП"
        //};
        List<string> m_prefixesForChangeParameters = new List<string>()
        {
            "AER",
        };
        // Key -it is what changing (from) , Value it is for replace (to),
        KeyValuePair<string, string> m_prefixesForReplace = new KeyValuePair<string, string>("AER", "ARBM");


        Result IExternalCommand.Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var log = new StringBuilderLogger();

            string pathToPreviewSharedParametersFile = string.Empty;
            Autodesk.Revit.ApplicationServices.Application revitApp = commandData.Application.Application;

            try
            {
                revitApp = commandData.Application.Application;

                string pathForAssembly = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

                // string folder_settings_path = pathForAssembly + @"\folder_settings.json";
                // string folder_settings_path = pathForAssembly + @"\folder_settings_remote_path.json";
                //  string folder_settings_path = pathForAssembly + @"\folder_settings_remote_path_new.json";
                 // string folder_settings_path = pathForAssembly + @"\folder_settings_local_path.json";
                string folder_settings_path = pathForAssembly + @"\folder_settings_remote_path_test.json";
                string log_path = pathForAssembly + @"\folder_result.log";

                var log_f = new FileLogger(log_path, true);

                log.ChainLogger(log_f);

                var folder_settings = FolderSettings.FromFile(folder_settings_path);

                if (folder_settings == null)
                {
                    MessageBox.Show($"Cannot read '{folder_settings_path}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return Result.Failed;
                }
                //Scan setup
                var folder_from = new DirectoryInfo(folder_settings.folder_from);
                var folder_to = new DirectoryInfo(folder_settings.folder_to);

                // string root_path_from = @"\\192.168.88.205\aerbim\База данных\3DSDбд\3DSD\Revit 2019";
                // string root_path_to = @"\\192.168.88.205\aerbim\База данных\3DSDбд\3DSD\Revit 2019_Release";

                //var folder_from = new DirectoryInfo(root_path_from);
                //var folder_to = new DirectoryInfo(root_path_to);


                log.LogInfo($"folder_from='{folder_from.FullName}'");
                log.LogInfo($"folder_to='{folder_to.FullName}'");

                if (ScanFolder.Prepare(folder_from.FullName, folder_to.FullName, out string msg) == false)
                {
                    MessageBox.Show($"'{msg}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return Result.Failed;
                }
                // Scan folder recursively
                var rfa_file_results = new List<RfaFileResult>();
                ScanFolder.ScanFolderMain(folder_from, folder_to, rfa_file_results, log);
                // Report
                int count_all = rfa_file_results.Count;
                //If rfa files not found 
                if (count_all == 0)
                {
                    string notFoundRfaFileslWarning = $"Rfa files not found.";
                    log.LogWarning(notFoundRfaFileslWarning);
                    MessageBox.Show($"{notFoundRfaFileslWarning}", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return Result.Failed;
                }

                int count_ok = rfa_file_results.Where(m => m.Success == true).Count();
                int count_err = rfa_file_results.Where(m => m.Success == false).Count();
                log.LogInfo($"Rfa processing statistics: All={count_all}, Success={count_ok}, Errors={count_err}");

                // Get current file of shared parameters
                pathToPreviewSharedParametersFile = revitApp.SharedParametersFilename;
                log.LogInfo($"Current was shared parameters filename '{pathToPreviewSharedParametersFile}'.");
               
                string pathToSharedParametersFile = pathForAssembly + @"\ARBM_PARAMETERS_FOR_RENAME.txt";
                log.LogInfo($"New will be shared parameters filename '{pathToSharedParametersFile}'.");

                // Load and open our file with shared parameters
                if (ArbmAPIElementHelper.SetSharedParametersFile(revitApp, pathToSharedParametersFile, log) == false)
                {
                    revitApp.SharedParametersFilename = pathToPreviewSharedParametersFile;
                    revitApp.OpenSharedParameterFile();
                    return Result.Failed;
                }

                // Set info for change of parameters
                ChangeParametersInfo changeParametersInfo = new ChangeParametersInfo();
                changeParametersInfo.PrefixesForDeleteParameters = m_prefixesForDeleteParameters;
                changeParametersInfo.PrefixesForChangeParameters = m_prefixesForChangeParameters;
                changeParametersInfo.PrefixesForReplace = m_prefixesForReplace;
                // changeParametersInfo.PathForSave = root_path_to;
                changeParametersInfo.PathForSave = folder_settings.folder_to;

                // Names of owner families and lists nested shared family for they
                Dictionary<string, List<string>> ownerAndNestedFamilies = new Dictionary<string, List<string>>();

                foreach (var rfaFile in rfa_file_results)
                {
                    ArbmFamiliesManager arbmFamilyManager = new ArbmFamiliesManager(commandData, rfaFile.PathFrom, pathForAssembly, log);

                    if (arbmFamilyManager.IsReadOnlyFile() == true) // the file is read only
                    {
                        string onlyReadWarning = $"File:{rfaFile.PathFrom} has access only for read.Parameter processing failed.";
                        log.LogWarning(onlyReadWarning);
                        MessageBox.Show($"'{onlyReadWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }

                    //we try open and check family document in the Revit application
                    Document familyDoc = arbmFamilyManager.CheckAndOpenFamilyDocument(m_categories);
                    if (familyDoc == null)
                    {
                        continue;
                    }

                    // Change nested shared families
                    //arbmFamilyManager.ChangeNestedSharedFamilies(familyDoc, changeParametersInfo, m_categories);
                  // List<string> pathsToNestedFamily = arbmFamilyManager.ChangeAndSaveNestedSharedFamilies(familyDoc, changeParametersInfo, m_categories);

                    string nameOwherFamily = arbmFamilyManager.ChangeOwnerFamily(familyDoc, changeParametersInfo);

                    List<string> namesNestedFamilies = arbmFamilyManager.ChangeNestedSharedFamilies(familyDoc, changeParametersInfo, m_categories);

                    if (nameOwherFamily.Equals(string.Empty) == false && namesNestedFamilies != null)
                    {
                        if (ownerAndNestedFamilies.ContainsKey(nameOwherFamily) == false)
                        {
                            ownerAndNestedFamilies.Add(nameOwherFamily, namesNestedFamilies);
                        }
                        else
                        {
                            log.LogError($"The family '{nameOwherFamily}' has name such as was added earlier.");
                        }
                        
                    }
                    arbmFamilyManager.SaveAndCloseAfterChanges(familyDoc, rfaFile.PathTo);


                } // END foreach (var rfaFile in rfa_file_results)

                // Load and open preview file with shared parameters
                if (ArbmAPIElementHelper.SetSharedParametersFile(revitApp, pathToPreviewSharedParametersFile, log) == false)
                {
                    return Result.Failed;
                }
                // Print results
                PrintTotalResults(ownerAndNestedFamilies, commandData.Application.Application.VersionNumber, log);

                string totalWarning = $"Total were changed '{ownerAndNestedFamilies.Count}' owner families from '{count_all}' families.";
                log.LogInfo(totalWarning);
                MessageBox.Show($"'{totalWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
               
                return Result.Succeeded;
            }
             catch (Exception exp)
            {
                string cmdExceptionWarning = $"EXCEPTION '{exp.Message}' in the command CmdRenameParametersForFamilies. Return Result.Failed.";
                log.LogWarning(cmdExceptionWarning);
                MessageBox.Show($"'{ cmdExceptionWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);

                // Restore path to the preview shared parameters file
                ArbmAPIElementHelper.SetSharedParametersFile(revitApp, pathToPreviewSharedParametersFile, log);
                return Result.Failed;
            }

            void PrintTotalResults(Dictionary<string, List<string>> ownerAndNestedFamilies, string revitVersion, StringBuilderLogger logger)
            {
                logger.LogInfo("******************************************************************************");
                logger.LogInfo("******************************************************************************");
                logger.LogInfo($"TOTAL RESULTS PROCCES FAMILY FILES FROM AERBIM DB (REVIT VERSION - {revitVersion}).");
                foreach (var ownerAndNestedFamily in ownerAndNestedFamilies) // names of owner families
                {
                    logger.LogInfo("******************************************************************************");
                    logger.LogInfo($"OWNER FAMILY: {ownerAndNestedFamily.Key}");
                    foreach (string nameNestetFamily in ownerAndNestedFamily.Value) // names of nested families
                    {
                        logger.LogInfo($"          NESTED SHARED FAMILY: {nameNestetFamily}");
                    }
                    logger.LogInfo("******************************************************************************");
                }

            }
        
    }

 }

    ///// <summary>
    ///// An interface class which provide the callback for family load options.
    ///// If the family is not loaded, or if the family is loaded but unchanged, the situation will never trigger and OnFamilyFound(Boolean, Boolean%) 
    ///// and OnSharedFamilyFound(Family, Boolean, FamilySource%, Boolean%) will not be called. 
    ///// Only if the family is loaded and changed should the interface methods be called. 
    ///// </summary>
    //class LoadOptions : IFamilyLoadOptions
    //{
    //    public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
    //    {
    //        overwriteParameterValues = true;
    //        return true;
    //    }

    //    public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
    //    {
    //        source = FamilySource.Family;
    //        overwriteParameterValues = true;
    //        return true;
    //    }
    //}
}
