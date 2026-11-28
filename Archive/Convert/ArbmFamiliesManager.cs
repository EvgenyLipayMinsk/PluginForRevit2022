
using System;
using System.Collections.Generic;
// using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;

using Alphaleonis.Win32.Filesystem;

using ScanFolderCopyAndProcess;


namespace Aerbim._3DSD.Test
{
    // The class provides methods and properties for work with Revit family
    public class ArbmFamiliesManager
    {
        #region Class Members
        Autodesk.Revit.ApplicationServices.Application m_revitApp = null; // Revit app

        StringBuilderLogger m_log = null; // logger for get test info

        string m_fullFamilyFileName = string.Empty; // full path to family file

        string m_pathForAssembly = string.Empty; // full path to assembly

        #endregion Class Members
        // Ctor 
        public ArbmFamiliesManager(ExternalCommandData commandData, string fullFamilyFileName, string pathForAssembly, StringBuilderLogger log)
        {
            m_revitApp = commandData.Application.Application;

            m_fullFamilyFileName = fullFamilyFileName;

            m_pathForAssembly = pathForAssembly;

            m_log = log;
        }

        // Check each file .rfa and return true, if it has read only and otherwise false
        public bool IsReadOnlyFile(/*string fullPath*/)
        {
            FileInfo fileInfo = new FileInfo(m_fullFamilyFileName);
            if (fileInfo.IsReadOnly == true)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        // Try open the document and return it. Otherwise return null
       public Document CheckAndOpenFamilyDocument(List<BuiltInCategory> categories)
        {
            Document familyDoc = null;
            try
            {
                familyDoc = m_revitApp.OpenDocumentFile(m_fullFamilyFileName);
            }
            catch (CorruptModelException exp)
            {
                //MessageBox.Show(msg, _3DSD_Shared_Resources.PluginName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                if (familyDoc != null)
                {
                    familyDoc.Close();
                }
                string corruptModeWarning = $"EXCEPTION '{exp.Message}'. File '{m_fullFamilyFileName}' has created in the later Revit version than {m_revitApp.VersionNumber}. Parameter processing failed.";
                m_log.LogWarning(corruptModeWarning);
                MessageBox.Show($"'{corruptModeWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;

            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException exp)
            {
                if (familyDoc != null)
                {
                    familyDoc.Close();
                }
                string invalidOperationWarning = $"EXCEPTION '{exp.Message}'. File '{m_fullFamilyFileName}' not possible to download. Parameter processing failed.";
                m_log.LogWarning(invalidOperationWarning);
                MessageBox.Show($"'{invalidOperationWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

            if (!familyDoc.IsFamilyDocument)
            {

                familyDoc.Close();

                string notFamilyFilenWarning = $"File '{m_fullFamilyFileName}' is not family document.Parameter processing failed.";
                m_log.LogWarning(notFamilyFilenWarning);
                MessageBox.Show($"'{notFamilyFilenWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            // Проверяем категорию семейства на соответствие допустимой
            // We check the category of the family for compliance with the admissible
            Category category = familyDoc.OwnerFamily.FamilyCategory;
            BuiltInCategory familyCategory = (BuiltInCategory)category.Id.IntegerValue;
            if (categories.Contains(familyCategory) != true)
            {
                familyDoc.Close();

                string notSupportedCategoryWarning = $"The category '{category.ToString()}' of family file:{m_fullFamilyFileName} is not supported.Parameter processing failed.";
                m_log.LogWarning(notSupportedCategoryWarning);
                MessageBox.Show($"'{notSupportedCategoryWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            //m_log.LogInfo($"The family file '{m_fullFamilyFileName}' was successful opened and checked.");
            return familyDoc;
        }

        // We change (delete AER_SP_ parameters and rename other parameters with prefix AER_ to prefix AERBM_) nested shared families, which correspond our criteria
        // Return list of families instances, which were changed 
        public List<string> ChangeNestedSharedFamilies(Document familyOwnerDocument, ChangeParametersInfo parametersInfo, List<BuiltInCategory> categories)
        {
            // m_log.LogInfo($"It starts change nested shared families.");
            List<string> nestedSharedFamily = new List<string>();
            // get nested shared family for owner family
            List<FamilyInstance> nestedFamilyInstances = ArbmAPIElementHelper.GetSharedFamilies(familyOwnerDocument, BuiltInParameter.FAMILY_SHARED, categories);
            if (nestedFamilyInstances.Count == 0) // we don't have nested shared family
            {
                return new List<string>();
            }

            string nestedFamilyName = string.Empty;
            foreach (FamilyInstance instance in nestedFamilyInstances)
            {
                nestedFamilyName = instance.Symbol.FamilyName;
                m_log.LogInfo($"START FOR NESTED SHARED FAMILY: {nestedFamilyName}.********************************************************************");
                Family family = ChangeNestedFamily(familyOwnerDocument, instance.Symbol.Family, parametersInfo, m_log);
               
                m_log.LogInfo($"END FOR NESTED SHARED FAMILY: {nestedFamilyName}.**********************************************************************");
                if (family != null)
                {
                    nestedSharedFamily.Add(family.Name + ".rfa");
                }
            }
            return nestedSharedFamily;

        }

        // We change (delete AER_SP_ parameters and rename other parameters with prefix AER_ to prefix AERBM_) nested shared families, which correspond our criteria
        // Return list of families instances, which were changed 
        public List<string> ChangeAndSaveNestedSharedFamilies(Document familyOwnerDocument, ChangeParametersInfo parametersInfo, List<BuiltInCategory> categories)
        {
            m_log.LogInfo($"It starts change nested shared families.");
            List<string> nestedSharedFamily = new List<string>();
            // get nested shared family for owner family
            List<FamilyInstance> nestedFamilyInstances = ArbmAPIElementHelper.GetSharedFamilies(familyOwnerDocument, BuiltInParameter.FAMILY_SHARED, categories);
            if (nestedFamilyInstances.Count == 0) // we don't have nested shared family
            {
                return new List<string>();
            }
            // TO DO CODE!!!!!
            foreach (FamilyInstance instance in nestedFamilyInstances)
            {
                string pathToNestedFamily = ChangeAndSaveNestedFamily(familyOwnerDocument, instance.Symbol.Family, parametersInfo, m_log);
                if (pathToNestedFamily != null)
                {
                    nestedSharedFamily.Add(pathToNestedFamily);
                }
            }
            return nestedSharedFamily;

        }
        // We change (delete AER_SP_ parameters and rename other parameters with prefix AER_ to prefix AERBM_) nested shared family, which correspond our criteria
        // Return family instances, which were changed 
        Family ChangeNestedFamily(Document familyOwnerDocument, Family family, ChangeParametersInfo parametersInfo, StringBuilderLogger log)
        {
            //TransactionGroup transGroup = null;
            try
            {

                //log.LogInfo($"It starts change nested shared family '{family.Name}'.");
                Document nestedFamilyDoc = familyOwnerDocument.EditFamily(family);

                if (nestedFamilyDoc == null)
                {
                    string notOpenedNestedFamilyDocWarning = $"The nested shared family'{family.Name}' cant opened for edit.Parameter processing failed.";
                    log.LogWarning(notOpenedNestedFamilyDocWarning);
                    MessageBox.Show($"'{notOpenedNestedFamilyDocWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }

                LoadOptions loadOptions = new LoadOptions();

                FamilyManager familyManager = nestedFamilyDoc.FamilyManager;
                ChangeAndRemoveParametersForNestedFamily(nestedFamilyDoc, familyManager, parametersInfo, true);

                // Add service parameters
                CheckAndAddAllParameters(nestedFamilyDoc, familyManager);

                family = nestedFamilyDoc.LoadFamily(familyOwnerDocument, loadOptions);

                nestedFamilyDoc.Close(false);

                if (family == null)
                {
                    log.LogWarning($"The parameters of nested shared family'{family.Name}' cant be to get for edit.Parameter processing failed.");
                    return null;
                }

                //log.LogInfo($"It ends change nested shared family '{family.Name}'. Operation is successful.");
                return family;
            }  
            catch (Exception exp)
            {

                string changeNestedFamilyExpWarning = $"EXCEPTION '{exp.Message}'. The parameters of nested shared family'{family.Name}' cant be to get for edit.";
                log.LogInfo(changeNestedFamilyExpWarning);
                MessageBox.Show($"'{changeNestedFamilyExpWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

        }

        // We change (delete AER_SP_ parameters and rename other parameters with prefix AER_ to prefix AERBM_) nested shared family, which correspond our criteria
        // Return family instances, which were changed 
        string ChangeAndSaveNestedFamily(Document familyOwnerDocument, Family family, ChangeParametersInfo parametersInfo, StringBuilderLogger log)
        {
            try
            {

                log.LogInfo($"It starts change nested shared family '{family.Name}'.");
                Document nestedFamilyDoc = familyOwnerDocument.EditFamily(family);
                if (nestedFamilyDoc == null)
                {
                    string notOpenedNestedFamilyDocWarning = $"The nested shared family'{family.Name}' cant opened for edit.Parameter processing failed.";
                    log.LogInfo(notOpenedNestedFamilyDocWarning);
                    MessageBox.Show($"'{notOpenedNestedFamilyDocWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return null;
                }
                LoadOptions loadOptions = new LoadOptions();

                FamilyManager familyManager = nestedFamilyDoc.FamilyManager;
                ChangeAndRemoveParametersForNestedFamily(nestedFamilyDoc, familyManager, parametersInfo, true);

                string tempPath = GetTemplatePathForNestedFamily(parametersInfo.PathForSave, family.Name + ".rfa");
                nestedFamilyDoc.SaveAs(tempPath);

                nestedFamilyDoc.Close(false);
                //} // END using (TransafalsectionGroup transGroup = new TransactionGroup((famEditDoc, "..."))
                if (family == null)
                {
                    log.LogInfo($"The parameters of nested shared family'{family.Name}' cant be to get for edit.Parameter processing failed.");
                    return null;
                }

                log.LogInfo($"It ends change nested shared family '{family.Name}'. Operation is successful.");
                return tempPath;
            }
            catch (Exception exp)
            {

                string changeNestedFamilyExpWarning = $"The parameters of nested shared family'{family.Name}' cant be to get for edit, exception '{exp.Message}'.Parameter processing failed.";
                log.LogInfo(changeNestedFamilyExpWarning);
                MessageBox.Show($"'{changeNestedFamilyExpWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }

        }


        // Change and remove parameters from nested family
        void ChangeAndRemoveParametersForNestedFamily(Document familyDocument, FamilyManager familyManager, ChangeParametersInfo parametersInfo, bool forNested = false)
        {
              string typeOfFamily = string.Empty;

                if (forNested == true)
                {
                    typeOfFamily = "nested shared";
                }
                else
                {
                    typeOfFamily = "owner";
                }

                // m_log.LogInfo($"It starts change and remove parameters for {typeOfFamily} family.");
               
                string nameParameter = string.Empty;
            try
            {
                List<FamilyParameter> familyParameters = familyManager.GetParameters().ToList();
                
                foreach (var parameter in familyParameters)
                {
                    nameParameter = parameter.Definition.Name;
                    // Remove target parameter in the family
                    if (IsDeleteParameter(parameter, parametersInfo.PrefixesForDeleteParameters) == true)
                    {

                        // Save name of parameter
                        string nameParam = parameter.Definition.Name;
                        // Delete target parameter
                        DeleteParameter(familyDocument, familyManager, parameter);
                        m_log.LogInfo($"PARAMETER: {nameParam} was deleted.");

                        continue;

                    } // if (IsDeleteParameter(parameter, parametersInfo.PrefixesForDeleteParameters) == true)

                    // Change target parameter in the family
                    if (IsChangeParameter(parameter, parametersInfo.PrefixesForChangeParameters) == true)
                    {
                         //m_log.LogInfo($"It starts rename parameter '{parameter.Definition.Name}' for {typeOfFamily} family.");
                        string oldName = parameter.Definition.Name;
                        // Replace prefix in the parameter
                        string newName = oldName.Replace(parametersInfo.PrefixesForReplace.Key, parametersInfo.PrefixesForReplace.Value);

                        
                        // Rename parameter
                        if (parameter.IsShared == false)
                        {
                            using (Transaction t = new Transaction(familyDocument, Guid.NewGuid().GetHashCode().ToString()))
                            {
                                t.Start();
                                familyManager.RenameParameter(parameter, newName);

                                t.Commit();
                                t.Dispose();
                            }
                            m_log.LogInfo($"PARAMETER: {oldName} was renamed to {newName}.");
                            continue;
                        }
                        //TO DO code!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!! DONT FOGET ABOUT FORMULA
                        else // IsShared == true
                        {
                            bool isOpenDefinitionFile = true; // true - DefinitionFile was opened or otherwise false - was not opened

                            if (ReplaceArbmSharedParameter(familyDocument, familyManager, parameter, out isOpenDefinitionFile) == false)
                            {
                                if (isOpenDefinitionFile == true) // All is well with opening the DefinitionFile
                                {
                                    // Save name of parameter
                                    string nameParam = parameter.Definition.Name;
                                    // Delete target parameter
                                    DeleteParameter(familyDocument, familyManager, parameter);
                                    m_log.LogInfo($"PARAMETER: {nameParam} was deleted.");

                                } // if (isOpenDefinitionFile == true)
                                continue;
                            }
                            else // if if (ReplaceArbmSharedParameter(familyDocument, familyManager, parameter, out isOpenDefinitionFile) == true)
                            {
                                continue;
                            }
                           // familyManager.ReplaceParameter(parameter, newName, parameter.Definition.ParameterGroup, parameter.IsInstance);
                           //familyManager.ReplaceParameter(parameter, newName, parameter.Definition.ParameterGroup, parameter.IsInstance);
                        }

                    } //  if (IsChangeParameter(parameter, parametersInfo.PrefixesForChangeParameters) == true)

                    // Only for ADSK_Версия Revit!!!!!
                    if (parameter.Definition.Name.Equals("ADSK_Версия Revit") == true)
                    {
                        // Get Revit Version
                        string currentRevitVersion = m_revitApp.VersionNumber;
                        // Set Revit version
                        SetRevitVersion(familyDocument, familyManager, parameter, currentRevitVersion);

                        m_log.LogInfo($"FOR PARAMETER: {parameter.Definition.Name} set Revit version '{currentRevitVersion}'.");
                    }

                } // foreach (var parameter in familyParameters)

                //m_log.LogInfo($"It ends change and remove parameters for {typeOfFamily} family. Operation is successful.");
            }
            catch (Exception exp)
            {
                m_log.LogWarning($"EXCEPTION '{exp.Message} in during processing of parameter {nameParameter}. Operation is failed.");
                throw new Exception(exp.Message);
            }

        }

        // Set Revit Version for shared ADSK_Версия Revit parameter
        void SetRevitVersion(Document familyDocument, FamilyManager familyManager, FamilyParameter parameter, string currentRevitVersion)
        {
            using (Transaction t = new Transaction(familyDocument, Guid.NewGuid().GetHashCode().ToString()))
            {
                t.Start();

                // m_log.LogInfo($"The shared parameter '{parameter.Definition.Name}' will change value for Revit Version.");
               
                if (parameter.IsDeterminedByFormula)
                {
                    //Formula = "\"Revit 2016\""
                    string formulaValue = '\"'+ currentRevitVersion + '\"';
                    familyManager.SetFormula(parameter, formulaValue);
                    
                }
                else
                {
                    FamilyTypeSet familyTypeSet = familyManager.Types;
                    foreach (var type in familyTypeSet)
                    {
                        familyManager.Set(parameter, currentRevitVersion);
                    }

                }
                t.Commit();
                t.Dispose();
            }
        }

        // Replace our (to prefix arbm) shared parameter in the file of family
        bool ReplaceArbmSharedParameter(Document familyDocument, FamilyManager familyManager, FamilyParameter targetParameter, out bool isOpenDefinitionFile)
        {
            // ONLY FOR SPECIAL CASE
            Dictionary<string, Guid> aerToARBD = new Dictionary<string, Guid>()
            {
                ["AER_Разработчик модели (URL)"] = new Guid("c8deeb8e-7aeb-4231-8849-2854632dea84"), // Guid for ARBM_ПИ_Разработчик модели (URL)
                ["AER_Разработчик модели (email)"] = new Guid("8109ec9b-871e-4a04-9f57-e9ce698e1ecc"), // Guid for ARBM_ПИ_Разработчик модели (email)
                ["AER_Разработчик модели (телефон)"] = new Guid("5c707694-357f-471e-8c6e-820f718fba4c"), // Guid for ARBM_ПИ_Разработчик модели (телефон)
                ["AER_Разработчик модели"] = new Guid("a21b7d62-239c-4e89-8e6d-2ffaf08d53a2"), // Guid for ARBM_ПИ_Разработчик модели
                ["AER_Дата изменения семейства"] = new Guid("bee9a7e0-4db0-477c-8a6d-b1e84a90491d"), // Guid for ARBM_ПИ_Дата изменения семейств
                ["AER_Раздел проекта"] = new Guid("0164198c-838c-4c9e-9283-8dde8d406b16"), // Guid for ARBM_ПП_Раздел проекта
                ["AER_Раздел спецификации"] = new Guid("11342e4d-debb-42c1-b223-6cc8e1170872"), // Guid for ARBM_ПП_Раздел спецификации ОИМ
            };
            Guid guidParam = new Guid();
            // ONLY FOR SPECIAL CASE
            if (aerToARBD.ContainsKey(targetParameter.Definition.Name) == true)
            {
                // Check - if parameter with GUID = aerToARBD[targetParameter.Definition.Name] already exists in the family document
                // then the targetParameter delete
                FamilyParameter sharedParameter = familyManager.get_Parameter(aerToARBD[targetParameter.Definition.Name]);
                if (sharedParameter != null)
                {
                    string nameDeletedParameter = targetParameter.Definition.Name;
                    DeleteParameter(familyDocument, familyManager, targetParameter);
                    m_log.LogInfo($"PARAMETER '{sharedParameter.Definition.Name}' ALREADY EXISTS. SO PARAMETER '{nameDeletedParameter}' WILL BE DELETED.");
                    isOpenDefinitionFile = true;
                    return true;
                }
                // otherwise change it
                else
                {
                    // Save values from preview parameter
                    guidParam = aerToARBD[targetParameter.Definition.Name];
                }
            }
            else
            {
                // Save values from preview parameter
                guidParam = targetParameter.GUID;
            }
            
            bool isInstance = targetParameter.IsInstance;
            BuiltInParameterGroup paramGroup = targetParameter.Definition.ParameterGroup;
            StorageType storageTypeParam = targetParameter.StorageType;
            string formulaValue = string.Empty;
            if (targetParameter.IsDeterminedByFormula == true) // if value of this parameter is formula
            {
                formulaValue = targetParameter.Formula;
            }

            ExternalDefinition externalDefinition = ArbmAPIElementHelper.GetExternalDefinition(m_revitApp, guidParam, m_log, out isOpenDefinitionFile);

            if (externalDefinition == null)
            {
                return false;
            }

            Dictionary<FamilyType, string> valueOfParameterFoTypes = null;
            // Check if value of parameter was formula
            if (formulaValue.Equals(string.Empty) == true) // The parameter is not formula
            {
                // Save preview values of shared parameter for each type before delete it 
                valueOfParameterFoTypes = GetValuesOfParameterForTypes(familyManager.Types, targetParameter);
            }

            // Save name of parameter
            string nameParam = targetParameter.Definition.Name;
            // Delete target parameter
            DeleteParameter(familyDocument, familyManager, targetParameter);
            m_log.LogInfo($"PARAMETER: {nameParam} was deleted.");

            using (Transaction t = new Transaction(familyDocument, Guid.NewGuid().GetHashCode().ToString()))
            {
                t.Start();
                // Before inserting we must check exists parameter with same name in the document, may be as Family parameter 
                // Add new shared parameter with the same GUID, but with changed name (from prefix AER to prefix ARBM)
                FamilyParameter changedParameter = familyManager.get_Parameter(externalDefinition.Name);
                if (changedParameter != null)
                {
                    string typeParam = changedParameter.IsShared  ? "Shared" : "Family";
                    m_log.LogWarning($"PARAMETER: {changedParameter.Definition.Name} already exists in the family document as {typeParam} parameter.");

                    isOpenDefinitionFile = true;
                    return true;

                }
                else
                {
                    changedParameter = familyManager.AddParameter(externalDefinition, paramGroup, isInstance);
                }

                // Check if value of parameter was formula
                if (formulaValue.Equals(string.Empty) == false) // The parameter is formula
                {
                    familyManager.SetFormula(changedParameter, formulaValue);
                }
                else // 
                {
                    SetValueForTypes(familyManager, valueOfParameterFoTypes, changedParameter);
                }
              
                t.Commit();
                t.Dispose();
                m_log.LogInfo($"PARAMETER: '{changedParameter.Definition.Name}' will be added.");
            }

            return true;
        }
        // Delete parameter from family document
        void DeleteParameter(Document familyDocument, FamilyManager familyManager, FamilyParameter targetParameter)
        {
            using (Transaction t = new Transaction(familyDocument, Guid.NewGuid().GetHashCode().ToString()))
            {
                t.Start();
                // Delete preview shared parameter from this family file
                // IT VERY IMPOTANT - THE METHOD familyManager.RemoveParameter DONT SHARED PARAMETER FROM FAMILY DOCUMENT, WHICH WAS CREATED ORIGINALLY  (СОЗДАН ПЕРВОНАЧАЛЬНО)  IN THE REVIT 2016 
                // THE SHARED PARAMETER MAY DELETE ONLY IF ONLY TO USE METHOD familyDocument.Delete(parameterElement.Id) FOR THIS FAMILY DOCUMENT
                // HOWEVER FOR FAMILY DOCUMET, WHICH WAS CREATED ORIGINALLY (СОЗДАН ПЕРВОНАЧАЛЬНО) IN THE REVIT 2019 
                // THE SHARED PARAMETER MAY DELETE ONLY IF ONLY TO USE METHOD familyManager.RemoveParameter(targetParameter) FOR THIS FAMILY DOCUMENT

                //m_log.LogInfo($"The preview shared parameter '{targetParameter.Definition.Name}' will be deleted and then changed.");
                //ParameterElement parameterElement = familyDocument.GetElement(targetParameter.Id) as ParameterElement;
                //bool isCanParameterDeleted = DocumentValidation.CanDeleteElement(familyDocument, parameterElement.Id);
                //if (isCanParameterDeleted == false)
                //{
                //    FamilyTypeSet familyTypeSet = familyManager.Types;
                //    // Check count of types, because family document always has minimum one type with name or without name
                //    // 
                //    int countOfTypes = familyTypeSet.Size;

                //    if (countOfTypes > 1)
                //    {
                //        foreach (FamilyType familyType in familyTypeSet)
                //        {

                //            familyManager.CurrentType = familyType;
                //            string nameType = familyType.Name;
                //            if (nameType.Trim(' ').Equals("") == true)
                //            {
                //                familyManager.DeleteCurrentType();
                //            }
                //        }
                //    }
                //}
                familyManager.RemoveParameter(targetParameter);
                // familyManager.Parameters.Erase(targetParameter);
                t.Commit();
                t.Dispose();
            }
            if (targetParameter != null)
            {
                ParameterElement parameterElement = familyDocument.GetElement(targetParameter.Id) as ParameterElement;
                bool isCanParameterDeleted = parameterElement != null ? DocumentValidation.CanDeleteElement(familyDocument, parameterElement.Id) : true;
                if (isCanParameterDeleted == false)
                {
                    m_log.LogWarning($"PARAMETER '{ parameterElement.Name}' IS CANT DELETED "); 
                }
                if (parameterElement != null && parameterElement.IsValidObject == true && isCanParameterDeleted == true)
                {
                    using (Transaction t = new Transaction(familyDocument, Guid.NewGuid().GetHashCode().ToString()))
                    {
                        t.Start();

                        familyDocument.Delete(parameterElement.Id);
                        // familyDocument.ParameterBindings.Remove(parameterElement.GetDefinition());
                        t.Commit();
                        t.Dispose();

                    }
                }

            }
        }
        // Get value of parameter for each type in the family
        Dictionary<FamilyType, string> GetValuesOfParameterForTypes(FamilyTypeSet familyTypeSet, FamilyParameter targetParameter)
        {
            Dictionary<FamilyType, string> typesAndValue = new Dictionary<FamilyType, string>();

            foreach (FamilyType familyType in familyTypeSet)
            {
                string value = FamilyParamValueString(familyType, targetParameter);
                if (typesAndValue.ContainsKey(familyType) == false)
                {
                    typesAndValue.Add(familyType, value);
                }
               
              
            }

            return typesAndValue;
        }

        // Set value of parameter for each type 
        void SetValueForTypes(FamilyManager familyManager, Dictionary<FamilyType, string> valueOfParameterFoTypes, FamilyParameter changedParameter)
        {
            foreach (FamilyType familyType in valueOfParameterFoTypes.Keys)
            {
                familyManager.CurrentType = familyType;
               
                if (valueOfParameterFoTypes[familyType] != null)
                {
                    switch (changedParameter.StorageType)
                    {
                        case StorageType.Double:
                            double valueDouble = double.Parse(valueOfParameterFoTypes[familyType]);
                            familyManager.Set(changedParameter, valueDouble);
                            break;

                        case StorageType.ElementId:
                            int valueElementId = int.Parse(valueOfParameterFoTypes[familyType]);
                            ElementId elementId = new ElementId(valueElementId);
                            familyManager.Set(changedParameter, elementId);
                            break;

                        case StorageType.Integer:
                            int valueInt = int.Parse(valueOfParameterFoTypes[familyType]);
                            familyManager.Set(changedParameter, valueInt);
                            break;

                        case StorageType.String:
                            string valueString = valueOfParameterFoTypes[familyType];
                            familyManager.Set(changedParameter, valueString);
                            break;
                    }
                }
            }

        }

        // Get value of parameter for type
        string FamilyParamValueString (FamilyType type, FamilyParameter targetParameter/*, Document doc*/)
        {
            
            string value = string.Empty;
            switch (targetParameter.StorageType)
            {
                case StorageType.Double:
                    value = type.AsDouble(targetParameter).ToString();
                    break;

                case StorageType.ElementId:
                    value = type.AsElementId(targetParameter).ToString();
                    break;

                case StorageType.Integer:
                    value = type.AsInteger(targetParameter).ToString();
                    break;

                case StorageType.String:
                    value = type.AsString(targetParameter);
                    break;
            }
            return value;
        }

        // We change (delete AER_SP_ parameters and rename other parameters with prefix AER_ to prefix AERBM_) owner family, which correspond our criteria
        public string ChangeOwnerFamily(Document familyOwnerDocument, ChangeParametersInfo parametersInfo)
        {
            // For get only get name of family file
            FileInfo fileInfo = new FileInfo(familyOwnerDocument.PathName);

            try
            {

               FamilyManager familyManager = familyOwnerDocument.FamilyManager;
                m_log.LogInfo($"START FOR OWNER FAMILY: {fileInfo.Name}.********************************************************************");
                ChangeAndRemoveParametersForOwnerFamily(familyOwnerDocument, familyManager, parametersInfo);
                // Add service parameters
                CheckAndAddAllParameters(familyOwnerDocument, familyManager);
                m_log.LogInfo($"END FOR OWNER FAMILY: {fileInfo.Name}***********************************************************************");
                return fileInfo.Name;
                  
                }
            catch (Exception exp)
            {

                string changeOwnerFamilyExpWarning = $"EXCEPTION '{exp.Message}'. The parameters of owner family'{fileInfo.Name}' cant be to get for edit. Parameter processing failed.";
                m_log.LogWarning(changeOwnerFamilyExpWarning);
                MessageBox.Show($"'{changeOwnerFamilyExpWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                //throw new Exception(exp.Message);
                return string.Empty;
            }

        }
        // Change and remove parameters from owner family
        void ChangeAndRemoveParametersForOwnerFamily(Document familyDocument, FamilyManager familyManager, ChangeParametersInfo parametersInfo)
        {
            try
            {
                ChangeAndRemoveParametersForNestedFamily(familyDocument, familyManager, parametersInfo);
            }
            catch(Exception exp)
            {
                throw new Exception(exp.Message);
            }

            
        }

        // Save changes in this file and close it
        public void SaveAndCloseAfterChanges(Document familyOwnerDocument, string path, bool isOverwriteExistingFile = true)
        {
            // We need get only one file ".rfa" without it copy (for example: file.0001.rfa, file.0002.rfa and etc.)
            // so we create template directory with name path + Guid.NewGuid().GetHashCode().ToString()
            // And then we move file ".rfa" without it copy in the directory with name path. For it:

            // 1. Get path to file ".rfa" without name of file
            FileInfo fileInfo = new FileInfo(path);
            //string pathToRfaFile = fileInfo.DirectoryName + "\\";

            // 2. Create path for template directory
            // string templPathToDir = pathToRfaFile + Guid.NewGuid().ToString().Substring(0, 2);
            //string templPathToDir = pathToRfaFile + Guid.NewGuid().ToString();

            // Create template directory on the local host on the disk D for decreasing (уменьшения) length of path to rfa file
            // otherwise throw EXCEPTION = Path length exceeds MAX_PATH = 260 characters
            string templPathToDir = "D:\\3DSD_DB_TEMPL\\" + Guid.NewGuid().ToString();

            // 3. Create template directory in the specified path
            Directory.CreateDirectory(templPathToDir);

            // 4. Get only name of ".rfa" file
            string rfaFileName = fileInfo.Name;

            // 5. Create path to template ".rfa" file
            string templPathToRfaFile = templPathToDir + "\\" + rfaFileName;
      
            if (isOverwriteExistingFile == true)
            {
                SaveAsOptions saveAsOptions = new SaveAsOptions();
                saveAsOptions.OverwriteExistingFile = true;
                // familyOwnerDocument.SaveAs(path, saveAsOptions);
                familyOwnerDocument.SaveAs(templPathToRfaFile, saveAsOptions);
            }
            else
            {
                familyOwnerDocument.SaveAs(templPathToRfaFile);
            }
            //familyOwnerDocument.Save();
            familyOwnerDocument.Close();

            // 6. Move target ".rfa" without ".0001, .0002, ..."
            FileInfo fileInfoForTemplateRfaFile = new FileInfo(templPathToRfaFile);
            fileInfoForTemplateRfaFile.MoveTo(path);

            // 7. Delete template directory and files in the it
            Directory.Delete(templPathToDir, true);
        }

        // Get template path fo saving families

        string GetTemplatePathForNestedFamily(string targetPath, string nestedFamilyName)
        {
            // We need get only one file ".rfa" without it copy (for example: file.0001.rfa, file.0002.rfa and etc.)
            // so we create template directory with name path + Guid.NewGuid().GetHashCode().ToString()
            // And then we move file ".rfa" without it copy in the directory with name path. For it:

            // 1. Get path to file ".rfa" without name of file
            FileInfo fileInfo = new FileInfo(targetPath);
            string pathToRfaFile = fileInfo.DirectoryName + "\\";

            // 2. Create path for template directory
            string templPathToDir = pathToRfaFile + Guid.NewGuid().ToString();

            // 3. Create template directory in the specified path
            Directory.CreateDirectory(templPathToDir);

            // 4. Get only name of ".rfa" file
            string rfaFileName = nestedFamilyName;

            // 5. Create path to template ".rfa" file
            string templPathToRfaFile = templPathToDir + "\\" + rfaFileName;
            return templPathToRfaFile;

        }

        // Check parameter for deleting
        bool IsDeleteParameter(FamilyParameter familyParameter, List<string> prefixesForDeleteParameters)
        {
            string nameParam = familyParameter.Definition.Name;
            foreach (string prefix in prefixesForDeleteParameters)
            {
                if (nameParam.StartsWith(prefix) == true)
                {
                    return true;
                }
            }

            return false;
        }
        // Check parameter for changing
        bool IsChangeParameter(FamilyParameter familyParameter, List<string> prefixesForChangeParameters)
        {
            string nameParam = familyParameter.Definition.Name;
            foreach (string prefix in prefixesForChangeParameters)
            {
                if (nameParam.StartsWith(prefix) == true)
                {
                    return true;
                }
            }

            return false;
        }

        // Add service parameters to the family document
        // Return list of names of added service parameters
        List<string> CheckAndAddAllParameters(Document familyDoc, FamilyManager familyManager)
        {
            List<string> addedParameters = new List<string>();

            // Get current file of shared parameters

            string pathToPreviewSharedParametersFile = m_revitApp.SharedParametersFilename;

            m_log.LogInfo($"Current was shared parameters filename '{pathToPreviewSharedParametersFile}'.");

            string pathToSharedParametersFile = m_pathForAssembly + @"\ARBM_TOTAL_PARAMETERS_FOR_ARBM_FAMILIES.v1.0.txt";

            m_log.LogInfo($"New will be shared parameters filename '{pathToSharedParametersFile}'.");

            // Load and open our file with shared parameters
            if (ArbmAPIElementHelper.SetSharedParametersFile(m_revitApp, pathToSharedParametersFile, m_log) == false)
            {
                m_revitApp.SharedParametersFilename = pathToPreviewSharedParametersFile;
                m_revitApp.OpenSharedParameterFile();
                return null;
            }
            DefinitionFile defFileWithServiceParameters = m_revitApp.OpenSharedParameterFile();
            string[] separatingStrings = { "##" };

            foreach (DefinitionGroup group in defFileWithServiceParameters.Groups)
            {
                foreach (ExternalDefinition def in group.Definitions)
                {
                    // check whether the parameter already exists in the document
                    FamilyParameter param = familyManager.get_Parameter(def.GUID);

                    if (null != param)
                    {
                        // Check name of parameter - ARBM_ПП_Раздел спецификации ОИМ
                        if (param.Definition.Name.Equals("ARBM_ПП_Раздел спецификации ОИМ") == true)
                        {
                            DeleteParameter(familyDoc, familyManager, param);
                        }
                        else
                        {
                            // Parameter already exists in the family document
                            continue;
                        }
                        
                    }
                    try
                    {
                        string[] paramData = def.Description.Split(separatingStrings, StringSplitOptions.RemoveEmptyEntries);
                        BuiltInParameterGroup paramGroup = (BuiltInParameterGroup)int.Parse(paramData[1]);

                        bool isInstance = Convert.ToBoolean(int.Parse(paramData[2]));

                        using (Transaction t = new Transaction(familyDoc, Guid.NewGuid().GetHashCode().ToString()))
                        {
                            t.Start();

                            familyManager.AddParameter(def, paramGroup, isInstance);
                            t.Commit();
                            t.Dispose();

                        }

                        string sharedParamName = def.Name;

                        m_log.LogInfo($"Was added shared parameter '{sharedParamName}'.'");

                        addedParameters.Add(sharedParamName);

                    }
                    catch (Exception exp)
                    {
                        // Restore preview shared parameters file
                        ArbmAPIElementHelper.SetSharedParametersFile(m_revitApp, pathToPreviewSharedParametersFile, m_log);

                        throw new Exception(exp.Message);
                    }
                } // foreach (ExternalDefinition def in group.Definitions)

            } // foreach (DefinitionGroup group in defFileWithServiceParameters.Groups)

              // Restore preview shared parameters file
            ArbmAPIElementHelper.SetSharedParametersFile(m_revitApp, pathToPreviewSharedParametersFile, m_log);

            return addedParameters;
        }


    }
    /// <summary>
    /// An interface class which provide the callback for family load options.
    /// If the family is not loaded, or if the family is loaded but unchanged, the situation will never trigger and OnFamilyFound(Boolean, Boolean%) 
    /// and OnSharedFamilyFound(Family, Boolean, FamilySource%, Boolean%) will not be called. 
    /// Only if the family is loaded and changed should the interface methods be called. 
    /// </summary>
    class LoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
    public class ChangeParametersInfo
    {
        public string PathForSave { get; set; } = null;
        public List<string> PrefixesForDeleteParameters { get; set; } = null;
        public List<string> PrefixesForChangeParameters { get; set; } = null;

        // Key -it is what changing (from) , Value it is for replace (to),
        public KeyValuePair<string, string> PrefixesForReplace { get; set; } = new KeyValuePair<string, string>(string.Empty, string.Empty);
    }
}
