

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Collections.Specialized;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.ApplicationServices;
using System.Windows.Forms;
using ScanFolderCopyAndProcess;

namespace Aerbim._3DSD.Test
{
    public class ArbmAPIElementHelper
    {

        /// <summary>
        /// Find an element by its type and name
        /// </summary>
        /// <param name="_doc">Reference to the current document.</param>
        /// <param name="_targetType">The type of the required element. </param>
        /// There is a limit of 1023 characters for this message; strings longer than this will be truncated</param>
        /// <param name="elements">Element set indicating problem elements to display in the failure dialog. This will be used only if the command status was "Failed".</param>
        /// <returns>The result indicates if the execution fails, succeeds, or was canceled by user. If it does not succeed, Revit will undo any changes made by the external command.</returns>
        public static Element FindElementByName(Document _doc, Type _targetType, string _targetName)
        {
            return new FilteredElementCollector(_doc).OfClass(_targetType).FirstOrDefault<Element>(e => e.Name.Equals(_targetName));
        }

        /// <summary>
        /// Поиск типоразмера
        /// по его семейству и наименованию
        /// </summary>
        public static FamilySymbol FindFamilySymbolByFamilyAndName(Document doc, string familyName, string FamilySymbolName)
        {

            FamilySymbol familySymbol = null;
            // Получаем семейство если оно уже загружено в проект
            Family family = FindElementByName(doc, typeof(Family), familyName) as Family;
            if (family == null) return familySymbol;
            //проверяем загружен ли нужный типоразмрер
            FamilySymbolFilter symbolFilter = new FamilySymbolFilter(family.Id);
            FilteredElementCollector fec = new FilteredElementCollector(doc);
            familySymbol = fec.WherePasses(symbolFilter).FirstOrDefault<Element>(e => e.Name.Equals(FamilySymbolName)) as FamilySymbol;
            return familySymbol;
            //symbolFilter.
            //return new FilteredElementCollector(doc).OfClass(targetType).FirstOrDefault<Element>(e => e.Name.Equals(targetName));
        }
        /// <summary>
        /// Возвращает список FamilyInstance для ВСЕГО текущего документа, НЕ ТОЛЬКО в активном представлении 
        /// у которых категория (одна из) OST_FireAlarmDevices (Пожарная сигнализация)/OST_ElectricalEqupment(Электрооборудование)/OST_DataDevices(Датчики)/
        /// OST_CommunicationDevices(Устройства связи)/OST_ElectricalFixtures(Электроприборы)/OST_LightingFixtures(Осветительные приборы)/
        /// OST_NurseCallDevices(Устройства для вызова и оповещения)/OST_TelephoneDevices(Телефонные устройства)/OST_SecurityDevices(Охранная сигнализация)
        /// и у которых имеются параметры: AER_SP_AdaptationForMarking(type)/AER_SP_CircuitName(instance)/AER_SP_ElementIndex(instance)/AER_SP_ElementNumber(instance)/
        /// AER_SP_ElementMark(instance)/AER_SP_ElementAltMark(instance).
        /// Returns a FamilyInstance list for the current document NO ONLY in the active view 
        /// which have a category(one of) OST_FireAlarmDevices / OST_ElectricalEqupment / OST_DataDevices(Sensors) /
        ///OST_CommunicationDevices / OST_ElectricalFixtures / OST_LightingFixtures /
        /// OST_NurseCallDevices(Call and Paging Devices) / OST_TelephoneDevices(Phone Devices) / OST_SecurityDevices(Burglar Alarm)
        /// and which have parameters: AER_SP_AdaptationForMarking(type) / AER_SP_CircuitName(instance) / AER_SP_ElementIndex(instance) / AER_SP_ElementNumber(instance) /
        ///AER_SP_ElementMark(instance) / AER_SP_ElementAltMark(instance).
        /// </summary>
        /// <param name="_doc">Current document.</param>
        /// <param name="_forActiveView">For active view only.</param>
        /// <param name="_targetСategories">Searched categories Revit.</param>
        /// <param name="_targetTypeParams">Searched type parameters.</param>
        /// <param name="_targetInstParams">Searched instance parameters.</param>
        /// <param name="hasSuperComponent">The presence of a parameter in the family.</param>
        /// <returns>Returns all instances of families that match the conditions (excluding nested families).</returns>
        public static List<FamilyInstance> GetFamilyInsance(Document _doc, bool _forActiveView, List<BuiltInCategory> _targetСategories, List<string> _targetTypeParams, List<string> _targetInstParams, bool hasSuperComponent = false)
        {
            ElementClassFilter familyInstanceFilter = new ElementClassFilter(typeof(FamilyInstance));
            FilteredElementCollector collector = null;
            // For active view only
            if (_forActiveView == true)
            {
                collector = new FilteredElementCollector(_doc, _doc.ActiveView.Id);
            }
            else // For document in general
            {
                collector = new FilteredElementCollector(_doc);
            }

            List<Element> elements = new List<Element>();
            if (hasSuperComponent == false)
            {
                elements = collector.WherePasses(familyInstanceFilter).Where(e => /*e.LevelId == e.Document.ActiveView.LevelId &&*/
           IsCategoryFamilyInst(e as FamilyInstance, _targetСategories) && IsContainsParams(e as FamilyInstance, _targetTypeParams, _targetInstParams) && (e as FamilyInstance).SuperComponent == null).ToList();
            }
            else
            {
                elements = collector.WherePasses(familyInstanceFilter).Where(e => /*e.LevelId == e.Document.ActiveView.LevelId &&*/
          IsCategoryFamilyInst(e as FamilyInstance, _targetСategories) && IsContainsParams(e as FamilyInstance, _targetTypeParams, _targetInstParams) /*&& (e as FamilyInstance).SuperComponent == null*/).ToList();

            }     
            List<FamilyInstance> familyInstances = new List<FamilyInstance>();
            foreach (var element in elements)
            {
                if (element is FamilyInstance)
                {
                    familyInstances.Add(element as FamilyInstance);
                }
            }
            return familyInstances;
        }

        /// <summary>
        /// An overloaded version of the previous method.
        /// Перегруженная версия предыдущего метода.
        /// </summary>
        /// <param name="_fi">List of analyzed families for compliance with the specified criteria.</param>
        /// <param name="_targetСategories">Searched categories Revit.</param>
        /// <param name="_targetTypeParams">Searched type parameters.</param>
        /// <param name="_targetInstParams">Searched instance parameters.</param>
        /// <param name="hasSuperComponent">The presence of a parameter in the family.</param>
        /// <returns>Returns all instances of families that match the conditions (excluding nested families).</returns>
        public static List<FamilyInstance> GetFamilyInsance(List<FamilyInstance> _fi, List<BuiltInCategory> _targetСategories, List<string> _targetTypeParams, 
            List<string> _targetInstParams, bool hasSuperComponent = false)
        {
            List<FamilyInstance> familyInstances = new List<FamilyInstance>();
            foreach (var element in _fi)
            {
                if (hasSuperComponent == false)
                {
                    if (IsCategoryFamilyInst(element, _targetСategories) == true && IsContainsParams(element, _targetTypeParams, _targetInstParams) == true && element.SuperComponent == null)
                    {
                        familyInstances.Add(element);
                    }
                }
                else
                {
                    if (IsCategoryFamilyInst(element, _targetСategories) == true && IsContainsParams(element, _targetTypeParams, _targetInstParams) == true/* && element.SuperComponent == null*/)
                    {
                        familyInstances.Add(element);
                    }
                }
                

            }
            return familyInstances;
        }
        /// <summary>
        /// Gets a list of common families for the specified family categories.
        /// Получает список общих семейств для заданных категорий семейств.
        /// </summary>
        /// <param name="_doc">Document looking for common families.</param>
        /// <param name="_builtInParameter">Parameter type - must be BuiltInParameter.FAMILY_SHARED.</param>
        /// <param name="_builtInCategories">List of family categories for which common families are searched.</param>
        /// <returns>Returns a list of common families for the given criteria.</returns>
        public static List<FamilyInstance> GetSharedFamilies(Document _doc, BuiltInParameter _builtInParameter, List<BuiltInCategory> _builtInCategories)
        {

            ElementClassFilter familyInstanceFilter = new ElementClassFilter(typeof(FamilyInstance));

            FilteredElementCollector collector = new FilteredElementCollector(_doc);

            List<Element> elements = new List<Element>();
            elements = collector.WherePasses(familyInstanceFilter).Where(e => /*e.LevelId == e.Document.ActiveView.LevelId &&*/
            IsCategoryFamilyInst(e as FamilyInstance, _builtInCategories) && IsSharedFamily(e as FamilyInstance, _builtInParameter)).ToList();
            List<FamilyInstance> familyInstances = new List<FamilyInstance>();
            foreach (var element in elements)
            {
                if (element is FamilyInstance)
                {
                    familyInstances.Add(element as FamilyInstance);
                }
            }
            return familyInstances;
        }

        /// <summary>
        /// Checks if the family is common.
        /// Проверяет является ли семейтсво общим.
        /// </summary>
        /// <param name="_familyInstance">Family instance.</param>
        /// <param name="_builtInParameter">Parameter type - must be BuiltInParameter.FAMILY_SHARED.</param>
        /// <returns>Returns true if the family is shared, false otherwise.</returns>
        public static bool IsSharedFamily(FamilyInstance _familyInstance, BuiltInParameter _builtInParameter /* must be BuiltInParameter.FAMILY_SHARED*/)
        {
            Family family = _familyInstance.Symbol.Family;
            if (family != null)
            {
                foreach (var param in family.Parameters)
                {
                    Parameter parameter = param as Parameter;
                    InternalDefinition internalDefinition = null;
                    if (parameter != null)
                    {
                        internalDefinition = (InternalDefinition)parameter.Definition;
                        if (internalDefinition.BuiltInParameter == _builtInParameter && parameter.AsInteger() == 1)
                        {
                            return true;
                        }
                    }
                 
                }
            }
            return false;
        }

        /// <summary>
        /// Carries out a check of the list of family instances for the presence of nested "common" families that meet our criteria by category and the presence of service parameters.
        /// Осуществяет прорверку списка экземпляров семейств на наличие вложенных "общих" семейств, удовлетворяющих нашим критериям по категории и наличию служебных параметров.
        /// </summary>
        /// <param name="_fi">Parsed List of Family Instances.</param>
        /// <param name="_targetTypeParams">Searched type parameters.</param>
        /// <param name="_targetInstParams">Searched instance parameters.</param>
        /// <returns>Returns a dictionary where an instance of the parent family is used as a key, and a list of nested "common" family instances that meet our category criteria and service parameters is used as a value.</returns>
        public static Dictionary<FamilyInstance, List<FamilyInstance>> GetSubFamilyInstances(List<FamilyInstance> _fi, List<BuiltInCategory> _targetСategories,
            List<string> _targetTypeParams, List<string> _targetInstParams)
        {
            Dictionary<FamilyInstance, List<FamilyInstance>> subInstances = new Dictionary<FamilyInstance, List<FamilyInstance>>();
            // Проверяем наличие у экземпляра родительского семейства наличие вложенных семейств, удовлетворяющих заданным критериям (по категории и наличию служебных параметров).
            // Если таковые имеются, то находим их и формируем из них список для родительского экземпляра семейства, value = List<FamilyInstance>
            // Если не имеются или имеются, но удовлетворяют критериям, то в словарь добавляется value = null
            // Check if the parent family instance has nested families that meet the specified criteria (by category and the presence of service parameters).
            // If there are any, then we find them and form a list from them for the parent instance of the family, value = List <FamilyInstance>
            // If not present or present, but meet the criteria, then value = null is added to the dictionary
            foreach (var element in _fi)
            {
                ICollection<ElementId> subElemSet = element.GetSubComponentIds();
                // У семейства нет вложенных. 
                // The family has no nested
                if (subElemSet == null)
                {
                    subInstances.Add(element, null);
                }
                else
                {
                    List<FamilyInstance> subAllFi = new List<FamilyInstance>();
                    // Находим все вложенные семейства и формируем из них список
                    // Find all nested families and form a list from them
                    foreach (var elementId in subElemSet)
                    {
                        FamilyInstance f = element.Document.GetElement(elementId) as FamilyInstance;
                        subAllFi.Add(f);
                    }
                    // Проверяем список всех вложенных "общих" семейств и находим те, которые удовлетворяют нашим критериям
                    // Check the list of all nested "common" families and find those that meet our criteria
                    List<FamilyInstance> isСriteriaTrueFI = GetFamilyInsance(subAllFi, _targetСategories, _targetTypeParams, _targetInstParams);
                    // если таковые есть, то добавляем их в словарь
                    // if there are any, then add them to the dictionary
                    if (isСriteriaTrueFI.Count > 0)
                    {
                        subInstances.Add(element, isСriteriaTrueFI);
                    }
                    // если таковых нет, то добавляем в словарь null
                    // if there are none, then add null to the dictionary
                    else
                    {
                        subInstances.Add(element, null);
                    }

                }

            }
            return subInstances;
        }

        /// <summary>
        /// Carries out a check of the list of family instances for the presence of nested "common" families that meet our criteria by category and the presence of service parameters.
        /// Осуществяет прорверку списка экземпляров семейств на наличие вложенных "общих" семейств, удовлетворяющих нашим критериям по категории и наличию служебных параметров.
        /// </summary>
        /// <param name="_fi">Parsed List of Family Instances.</param>
        /// <param name="_targetTypeParams">Searched type parameters.</param>
        /// <param name="_targetInstParams">Searched instance parameters.</param>
        /// <returns>Returns a ordered dictionary where an instance of the parent family is used as a key, and a list of nested "common" family instances that meet our category criteria and service parameters is used as a value.</returns>
        public static OrderedDictionary GetSubFamilyInstancesOrdered(List<FamilyInstance> _fi, List<BuiltInCategory> _targetСategories,
            List<string> _targetTypeParams, List<string> _targetInstParams)
        {
            OrderedDictionary subInstances = new OrderedDictionary();
            // Проверяем наличие у экземпляра родительского семейства наличие вложенных семейств, удовлетворяющих заданным критериям (по категории и наличию служебных параметров).
            // Если таковые имеются, то находим их и формируем из них список для родительского экземпляра семейства, value = List<FamilyInstance>
            // Если не имеются или имеются, но удовлетворяют критериям, то в словарь добавляется value = null
            // Check if the parent family instance has nested families that meet the specified criteria (by category and the presence of service parameters).
            // If there are any, then we find them and form a list from them for the parent instance of the family, value = List <FamilyInstance>
            // If not present or present, but meet the criteria, then value = null is added to the dictionary
            foreach (var element in _fi)
            {
                ICollection<ElementId> subElemSet = element.GetSubComponentIds();
                // У семейства нет вложенных. 
                // The family has no nested
                if (subElemSet == null)
                {
                    subInstances.Add(element, null);
                }
                else
                {
                    List<FamilyInstance> subAllFi = new List<FamilyInstance>();
                    // Находим все вложенные семейства и формируем из них список
                    // Find all nested families and form a list from them
                    foreach (var elementId in subElemSet)
                    {
                        FamilyInstance f = element.Document.GetElement(elementId) as FamilyInstance;
                        subAllFi.Add(f);
                    }
                    // Проверяем список всех вложенных "общих" семейств и находим те, которые удовлетворяют нашим критериям
                    // Check the list of all nested "common" families and find those that meet our criteria
                    List<FamilyInstance> isСriteriaTrueFI = GetFamilyInsance(subAllFi, _targetСategories, _targetTypeParams, _targetInstParams, true);
                    // если таковые есть, то добавляем их в словарь
                    // if there are any, then add them to the dictionary
                    if (isСriteriaTrueFI.Count > 0)
                    {
                        subInstances.Add(element, isСriteriaTrueFI);
                    }
                    // если таковых нет, то добавляем в словарь null
                    // if there are none, then add null to the dictionary
                    else
                    {
                        subInstances.Add(element, null);
                    }

                }

            }
            return subInstances;
        }

        /// <summary>
        /// Возвращает true, если категория экз. семейства соответствует одному из значений из передаваемого списка.
        /// Returns true if the item category. family matches one of the values ​​from the passed list.
        /// </summary>
        /// <param name="_targetFamilyInst">Parsed family instance.</param>
        /// <param name="_targetСategories">Checked list of Revit categories.</param>
        /// <returns>Returns true if the item category. family matches one of the values ​​from the passed list.</returns> 
        public static bool IsCategoryFamilyInst(FamilyInstance _targetFamilyInst, List<BuiltInCategory> _targetСategories)
        {
            bool resultIsCategory = false;
            // Если список пустой, то маркируем оборудование всех категорий
            //if (targetСategories.Count == 0)
            //{
            //    resultIsCategory = true;
            //    return resultIsCategory;
            //}
            foreach (var category in _targetСategories)
            {
                if ((BuiltInCategory)_targetFamilyInst.Category.Id.IntegerValue == category)
                {
                    resultIsCategory = true;
                    return resultIsCategory;
                }
            }
            return resultIsCategory;
            //return new FilteredElementCollector(doc).OfClass(targetType).FirstOrDefault<Element>(e => e.Name.Equals(targetName));
        }

        /// <summary>
        /// Возвращает список тегов для текущего документа в активном представлении 
        /// у которых категория (одна из) OST_FireAlarmDevice Tags (Пожарная сигнализация)/OST_ElectricalEqupment Tags(Электрооборудование)/OST_DataDevice Tags(Датчики)/
        /// OST_CommunicationDevice Tags(Устройства связи)/OST_ElectricalFixture Tags(Электроприборы)/OST_LightingFixture Tags(Осветительные приборы)/
        /// OST_NurseCallDevice Tags(Устройства для вызова и оповещения)/OST_TelephoneDevice Tags(Телефонные устройства)/OST_SecurityDevice Tags(Охранная сигнализация)
        /// Returns a IndependentTag list for the current document in the active view
        /// which have a category(one of) OST_FireAlarmDevice Tags / OST_ElectricalEqupment Tags / OST_DataDevices(Sensors) /
        ///OST_CommunicationDevices / OST_ElectricalFixture Tags / OST_LightingFixture Tags /
        /// OST_NurseCallDevice Tags(Call and Paging Devices) / OST_TelephoneDevice Tags(Phone Devices) / OST_SecurityDevice Tags(Burglar Alarm)
        /// </summary>
        /// <param name="_doc">Current document.</param>
        /// <param name="_forActiveView">Only for active view.</param>
        /// <param name="_targetСategories">Searched categories Revit.</param>
        /// <returns>Returns all instances of IndependentTag that match the conditions.</returns>
        public static List<IndependentTag> GetIndependentTags(Document _doc, bool _forActiveView, List<BuiltInCategory> _targetСategories)
        {
            ElementClassFilter independentTagFilter = new ElementClassFilter(typeof(IndependentTag));
            FilteredElementCollector collector = null;

            if (_forActiveView == true)
            {
               collector = new FilteredElementCollector(_doc, _doc.ActiveView.Id);
            }
            else
            {
                collector = new FilteredElementCollector(_doc);
            }
           
            List<Element> elements = new List<Element>();
            elements = collector.WherePasses(independentTagFilter).Where(e =>
            IsCategoryTag(e as IndependentTag, _targetСategories)).ToList();
            List<IndependentTag> tags = new List<IndependentTag>();
            foreach (var element in elements)
            {
                if (element is IndependentTag)
                {
                    tags.Add(element as IndependentTag);
                }
            }
            return tags;
        }

        /// <summary>
        /// Возвращает true, если категория тега соответсвует одному из значений из передваваемого списка.
        /// Returns true if the tag category matches one of the values ​​from the passed list.
        /// </summary>
        /// <param name="_targetTag">Parsed tag.</param>
        /// <param name="_targetСategories">Checked list of Revit categories.</param>
        /// <returns>Returns true if the item category. family matches one of the values ​​from the passed list.</returns> 
        public static bool IsCategoryTag(IndependentTag _targetTag, List<BuiltInCategory> _targetСategories)
        {
            bool resultIsCategory = false;

            foreach (var category in _targetСategories)
            {
                if ((BuiltInCategory)_targetTag.Category.Id.IntegerValue == category)
                {
                    resultIsCategory = true;
                    return resultIsCategory;
                }
            }
            return resultIsCategory;

        }

        /// <summary>
        /// Возвращает true, если экз. семейства содержит все параметры с заданными именами.
        /// Returns true if instance family contains all parameters with the specified names.
        /// </summary>
        /// <param name="_targetFamilyInst">Parsed family instance.</param>
        /// <param name="_targetTypeParams">List of checked family parameters(type parameters).</param>
        /// <param name="_targetInstParams">List of checked family parameters(type parameters).</param>
        /// <returns>Returns true if instance family contains all parameters with the specified names.</returns> 
        public static bool IsContainsParams(FamilyInstance _targetFamilyInst, List<string> _targetTypeParams, List<string> _targetInstParams)
        {
            bool result = false;
            int countTypeParam = 0;
            int countInstParam = 0;
            // необходимо получить все параметры типа, поэтому получаем из через ссылку на FamilySymbol экз. семейства
            FamilySymbol familySymbol = _targetFamilyInst.Symbol;
            foreach (var element in _targetTypeParams)
            {
                if (IsContainsTypeParam(_targetFamilyInst, element))
                {
                    countTypeParam++;
                }
            }
            foreach (var element in _targetInstParams)
            {
                if (IsContainsInstParam(_targetFamilyInst, element))
                {
                    countInstParam++;
                }
            }
            if (countTypeParam == _targetTypeParams.Count && countInstParam == _targetInstParams.Count)
            {
                result = true;
            }
            return result;
        }
        /// <summary>
        /// Возвращает true, если тип семейства содержит параметр с заданным именем.
        /// Returns true if the family type contains a parameter with the specified name.
        /// </summary>
        /// <param name="_targetFamilyInst">Parsed family instance.</param>
        /// <param name="_targetTypeParams">Name of the required parameter (type parameter).</param>
        /// <returns>Returns true if the family type contains a parameter with the specified name.</returns> 
        public static bool IsContainsTypeParam(FamilyInstance _targetFamilyInst, string _targetTypeParam)
        {
            bool isContainsParam = false;
            // необходимо получить все параметры типа, поэтому получаем из через ссылку на FamilySymbol экз. семейства
            FamilySymbol familySymbol = _targetFamilyInst.Symbol;
            foreach (Parameter param in familySymbol.Parameters)
            {

                string nameParam = param.Definition.Name;
                if (nameParam.Equals(_targetTypeParam))
                {
                    isContainsParam = true;
                    return isContainsParam;
                }
            }

            return isContainsParam;
        }

        /// <summary>
        /// Возвращает true, если экз. семейства содержит параметр с заданным именем.
        /// Returns true if instance family contains a parameter with the specified name.
        /// </summary>
        /// <param name="_targetFamilyInst">Parsed family instance.</param>
        /// <param name="_targetInstParams">Name of the required parameter (instance parameter).</param>
        /// <returns>Returns true if instance family contains a parameter with the specified name.</returns> 
        public static bool IsContainsInstParam(FamilyInstance _targetFamilyInst, string _targetInstParam)
        {
            bool isContainsParam = false;
            foreach (Parameter param in _targetFamilyInst.Parameters)
            {

                string nameParam = param.Definition.Name;
                if (nameParam.Equals(_targetInstParam))
                {
                    isContainsParam = true;
                    return isContainsParam;
                }
            }

            return isContainsParam;
        }
        public static string GetValueFromTypeParam(FamilyInstance targetFamilyInst, string targetNameParam)
        {
            string paramValueRes = "";
            // необходимо получить все параметры типа, поэтому получаем из через ссылку на FamilySymbol экз. семейства
            FamilySymbol familySymbol = targetFamilyInst.Symbol;
            foreach (Parameter param in familySymbol.Parameters)
            {

                string nameParam = param.Definition.Name;
                if (nameParam.Equals(targetNameParam))
                {
                    paramValueRes = param.AsString();
                    return paramValueRes;
                }
            }
            return paramValueRes;

        }

        /// <summary>
        /// Находит значение строкового параметра экземпляра семейства по имени параметра и экземпляру семейства. 
        /// Finds the value of a string parameter of a family instance by parameter name and family instance.
        /// </summary>
        /// <param name="_targetFamilyInst">The family instance for which the parameter value is being sought.</param>
        /// <param name="_targetNameParam">Name of the required parameter.</param>
        /// <returns>Returns the value of the parameter.</returns>
        public static string GetValueFromInstParam(FamilyInstance _targetFamilyInst, string _targetNameParam)
        {
            string paramValueRes = "";
            foreach (Parameter param in _targetFamilyInst.Parameters)
            {

                string nameParam = param.Definition.Name;
                if (nameParam.Equals(_targetNameParam) && param.StorageType == StorageType.String)
                {
                    paramValueRes = param.AsString();
                    return paramValueRes;
                }
            }
            return paramValueRes;
        }

        public static void SetValueFromParam(Document doc, FamilyInstance targetFamilyInst, BuiltInParameter builtInParameter, string newValueForParam)
        {
            bool res = false;
            // Меняем значение в параметрах экземпляра семейства
            foreach (Parameter param in targetFamilyInst.Parameters)
            {

                InternalDefinition internalDefinition = param.Definition as InternalDefinition;
                if (internalDefinition != null && internalDefinition.BuiltInParameter == builtInParameter)
                {
                    Transaction trans = new Transaction(doc);
                    trans.Start("Properties._3DSD_Utilities_Resources.Transact_01");
                    res = param.Set(newValueForParam);
                    trans.Commit();
                }
            }
        }
        /// <summary>
        /// Находит параметр экземпляра семейства по имени и задает для него новое значение.
        /// Finds a parameter of a family instance by name and sets a new value for it. 
        /// </summary>
        /// <param name="_targetFamilyInst">An instance of the family for which the parameter value will be overwritten.</param>
        /// <param name="_targetNameParam">Name of the required parameter.</param>
        /// <param name="_newValue">Name of the required parameter.</param>
        ///  <param name="_isHasValue">Indicates whether the parameter had a previously written value.</param>
        ///  <param name="_isMultipleObj">Параметр, который определяет контекст вызова метода - для оного выбранного элемента (false) или для нескольких элементов (true).A parameter that determines the context of the method call - for this selected element (false) or for several elements (true)</param>
        /// <returns>Returns true if the parameter value was added or overwritten otherwise false.</returns>
        public static bool SetValueForParamInst(FamilyInstance _targetFamilyInst, string _targetNameParam, string _newValue, out bool? _isHasValue, bool _isMultipleObj = false)
        {
            bool res = false;
            _isHasValue = null;
            int count = 0;
            // Меняем значение в параметре экземпляра семейства
            foreach (Parameter param in _targetFamilyInst.Parameters)
            {

                if (param.Definition.Name.Equals(_targetNameParam) == true)
                {
                    //InternalDefinition internalDefinition = param.Definition as InternalDefinition;
                    if (param.AsString().Trim(new char[] { ' ' }) != "")
                    {
                        string msg = String.Format("Properties._3DSD_Utilities_Resources.msgHasValue, _targetFamilyInst.Name");
                        System.Windows.Forms.DialogResult dialogResult = System.Windows.Forms.MessageBox.Show(msg, "_3DSD_Shared_Resources.PluginName", System.Windows.Forms.MessageBoxButtons.YesNo,
                                                   System.Windows.Forms.MessageBoxIcon.Information);
                        if (dialogResult == System.Windows.Forms.DialogResult.Yes)
                        {
                            res = param.Set(_newValue);
                            _isHasValue = true;
                        }
                        else
                        {
                            if (_isMultipleObj == true)
                            {
                                res = true;
                            }
                        }

                    }
                    else
                    {
                        res = param.Set(_newValue);
                        _isHasValue = true;
                    }
                    count ++;
                    break;
                }
            }
            // Ни одно имя параметра не совпало с искомым
            // None of the parameter names matched the desired one
            if (count == 0)
            {
                _isHasValue = false;
            }
            return res;
        }


        /// <summary>
        /// Resets parameter values ​​for an instance of a family (only for int and string type parameters).
        /// Сбрасывает значения параметров у экземпляра семейства (только для типов int и string параметров).
        /// </summary>
        /// <param name="_doc">Current document.</param>
        /// <param name="_targetFamilyInst">Family instance with parameters being reset.</param>
        /// <param name=" _resetParam">Resettable parameter list.</param>
        public static void ResetParams(Document _doc, FamilyInstance _targetFamilyInst, List<string> _resetParam)
        {
            Transaction trans = new Transaction(_doc);
            trans.Start("Properties._3DSD_Utilities_Resources.Transact_02");
            // Меняем значение в параметрах экземпляра семейства
            foreach (Parameter param in _targetFamilyInst.Parameters)
            {
                string nameParam = param.Definition.Name;
                StorageType storageType = param.StorageType;
                for (int i = 0; i < _resetParam.Count; i++)
                {
                    if (nameParam.Equals(_resetParam[i]))
                    {
                        if (storageType == StorageType.Integer)
                        {
                            param.Set(0);
                            break;
                        }
                        else if (storageType == StorageType.String)
                        {
                            param.Set("");
                            break;
                        }
                    }

                }

            }
            trans.Commit();
            trans.Dispose();
        }

        /// <summary>
        /// Проверяет наличие требуемого парамерта у экземпляра семейства и сравнивает его с определенным передаваемым зачением.
        /// Checks the presence of the required parameter in an instance of the family and compares it with a certain transmitted value.
        /// </summary>
        /// <param name="_targetInstance">Family instance to look for parameter and value.</param>
        /// <param name=" _nameParam">Name of the required parameter.</param>
        /// <param name=" _valueParam">Parameter value to check.</param>
        /// <returns>Returns true if the parameter and its value were found and false otherwise.</returns>
        public static bool IsContainsEqualsValue(FamilyInstance _targetInstance, string _nameParam, string _valueParam)
        {
            bool res = false;

            foreach (Parameter param in _targetInstance.Parameters)
            {
                string nameParam = param.Definition.Name;
                if (nameParam.Equals(_nameParam))
                {
                    if (param.HasValue)
                    {
                        if (param.AsString() == _valueParam)
                        {
                            res = true;
                            return res;

                        }
                    }

                }

            }
            return res;
        }

        /// <summary>
        /// Проверяет наличие в текущем документе семейств с требуемым параметром и соответствующим значением этого параметра.
        /// Checks if the current document contains families with the required parameter and the corresponding value of this parameter.
        /// </summary>
        /// <param name="_doc">Current document.
        /// <param name=" _nameParam">Name of the required parameter.</param>
        /// <param name=" _valueParam">Parameter value to check.</param>
        /// <returns>Returns a list of family instances (with nested families) that match a given criterion (the presence of a parameter and its value).</returns>
        public static List<FamilyInstance> GetFamilyInstByParamValue(Document _doc, string _nameParam, string _valueParam)
        {
            ElementClassFilter familyInstanceFilter = new ElementClassFilter(typeof(FamilyInstance));

            FilteredElementCollector collector = new FilteredElementCollector(_doc, _doc.ActiveView.Id);

            List<Element> elements = new List<Element>();
            elements = collector.WherePasses(familyInstanceFilter).Where(e => IsContainsEqualsValue(e as FamilyInstance, _nameParam, _valueParam)).ToList();
            List<FamilyInstance> familyInstances = new List<FamilyInstance>();
            foreach (var element in elements)
            {
                if (element is FamilyInstance)
                {
                    familyInstances.Add(element as FamilyInstance);
                }
            }
            return familyInstances;
        }

        /// <summary>
        /// Удаляет указанные элементы из текущего документа. Элемент типа T должен быть унаследован от базового класса Element
        /// Removes the specified elements from the current document.
        /// </summary>
        /// <param name="_doc">Current document.
        /// <param name=" _deleteElements">List of elements to delete.</param>
        public static void DeleteElements<T>(Document _doc, List<T> _deleteElements, bool IsTransact = false)
        {

            ICollection<ElementId> elementIds = new List<ElementId>();
            // Преобразовываем Element в ElementId
            // Convert Element to ElementId
            foreach (var element in _deleteElements)
            {
                Element curElement = element as Element;
                if (curElement != null)
                {
                    elementIds.Add(curElement.Id);
                }

            }
            if (elementIds.Count > 0)
            {
                if (IsTransact == false)
                {
                    _doc.Delete(elementIds);
                }
                else
                {
                    Transaction t = new Transaction(_doc);
                    t.Start("Properties._3DSD_Utilities_Resources.Transact_03");
                    _doc.Delete(elementIds);
                    t.Commit();
                    t.Dispose();
                }

            }
        }
        /// <summary>
        /// Преобразовываем элемент типа T в Element. Элемент типа T должен быть унаследован от базового класса Element.
        /// Convert an element of type T to Element. Element of type T must inherit from base class Element.
        /// </summary>
        /// <param name="_doc">Current document.
        /// <param name=" _convertElements">List of convertible items.</param>
        public static List<Element> ConvertToElementType<T>(List<T> _convertElements)
        {

            List<Element> elements = new List<Element>();
            // Преобразовываем элемент типа T в Element
            // Convert an element of type T to Element
            foreach (var element in _convertElements)
            {
                Element curElement = element as Element;
                if (curElement != null)
                {
                    elements.Add(curElement);
                }
            }
            return elements;
        }
        /// <summary>
        /// Удаляет визуальное представление тега у заданных экземпляров семейств.
        /// Removes the visual representation of a tag from the specified family instances.
        /// </summary>
        /// <param name="_document">Current document.</param>
        /// <param name="_familyInstances">List of family instances from which you want to remove tags. </param>
        /// <param name="_tags">The list of tags from which you want to remove tags.</param>
        /// <returns>Returns true if tags to remove are found, false otherwise.</returns>
        public static bool DeleteVisualMark(Document _document, List<FamilyInstance> _familyInstances, List<IndependentTag> _tags)
        {
            List<IndependentTag> deleteTags = new List<IndependentTag>();
            foreach (var element in _familyInstances)
            {
                foreach (var tag in _tags)
                {
                    if (tag.TaggedElementId.HostElementId != ElementId.InvalidElementId && tag.TaggedElementId.HostElementId.Compare(element.Id) == 0)
                    {
                        deleteTags.Add(tag);
                        continue;
                    }

                }

            }
            if (deleteTags.Count > 0)
            {
                ArbmAPIElementHelper.DeleteElements<IndependentTag>(_document, deleteTags);
                return true;
            }
            return false;


        }

        /// <summary>
        /// Удаляет визуальное представление тега у заданных экземпляров семейств.
        /// Removes the visual representation of a tag from the specified family instances.
        /// </summary>
        /// <param name="_document">Current document.</param>
        /// <param name="_familyInstances">List of family instances from which you want to remove tags. </param>
        /// <param name="_tags">The list of tags from which you want to remove tags.</param>
        /// <returns>Returns true if tags to remove are found, false otherwise and ids for deleted views.</returns>
        public static bool DeleteVisualMark(Document _document, List<FamilyInstance> _familyInstances, List<IndependentTag> _tags, out List<ElementId> _viewIdsForDeletedTags)
        {
            List<IndependentTag> deleteTags = new List<IndependentTag>();
             _viewIdsForDeletedTags = new List<ElementId>();
            foreach (var element in _familyInstances)
            {
                foreach (var tag in _tags)
                {
                    if (tag.TaggedElementId.HostElementId != ElementId.InvalidElementId && tag.TaggedElementId.HostElementId.Compare(element.Id) == 0)
                    {
                        deleteTags.Add(tag);
                        if (_viewIdsForDeletedTags.Contains(tag.OwnerViewId) != true)
                        {
                            _viewIdsForDeletedTags.Add(tag.OwnerViewId);
                        }
                        
                        //continue;
                    }

                }

            }
            if (deleteTags.Count > 0)
            {
                ArbmAPIElementHelper.DeleteElements<IndependentTag>(_document, deleteTags);
                return true;
            }
            return false;


        }

        /// <summary>
        /// TODO!!!
        /// </summary>
        static List<IndependentTag> GetIndependentTagFoElement(Document document, FamilyInstance element, List<IndependentTag> tags)
        {
            List<IndependentTag> tagsForElement = new List<IndependentTag>();
            List<ElementId> tagIdsForElement = new List<ElementId>();
            foreach (var tag in tags)
            {
                if (tag.TaggedElementId.HostElementId != ElementId.InvalidElementId && tag.TaggedElementId.HostElementId.Compare(element.Id) == 0)
                {
                    if (tagIdsForElement.Contains(tag.Id) == false)
                    {
                        tagIdsForElement.Add(tag.Id);
                    }
                    
                }
            }
            if (tagIdsForElement.Count > 0)
            {
                tagsForElement = tagIdsForElement.ConvertAll(elementId => document.GetElement(elementId) as IndependentTag);
                return tagsForElement;
            }
            return tagsForElement;
        }

        /// <summary>
        /// TODO!!!
        /// </summary>
        public static Dictionary<ElementId, List<IndependentTag>> GetIndependentTagsFoElements(Document document, List<ElementId> elementIdsForCircuit, List<IndependentTag> tags)
        {

            Dictionary<ElementId, List<IndependentTag>> independentTagsFoElements = new Dictionary<ElementId, List<IndependentTag>>();
            if (tags == null || tags.Count == 0)
            {
                return independentTagsFoElements;
            }
            
            //List<IndependentTag> tagsForElement = new List<IndependentTag>();
            //List<ElementId> tagIdsForElement = new List<ElementId>();

            foreach (ElementId id in elementIdsForCircuit)
            {
                FamilyInstance element = document.GetElement(id) as FamilyInstance;
                List<IndependentTag> tagsForElement = GetIndependentTagFoElement(document, element, tags);
                independentTagsFoElements.Add(id, tagsForElement);
            }

            return independentTagsFoElements;
        }

        /// <summary>
        /// TODO!!! Получает список элементов для которых фактически необходимо создать новые теги
        /// </summary>
        public static List<ElementId> GetElementsForCreateTags(Dictionary<ElementId, List<IndependentTag>> independentTagsFoElements, List<ElementId> elementIdsForVisuslizationWithStepRevit)
        {
            List<ElementId> elementsForCreateTags = new List<ElementId>();
            foreach (ElementId id in elementIdsForVisuslizationWithStepRevit)
            {
                if (independentTagsFoElements.ContainsKey(id) == false)
                {
                    elementsForCreateTags.Add(id);
                }
            }
            return elementsForCreateTags;
        }

        /// <summary>
        /// TODO!!! Получает список элементов для которых необходимо удалить теги
        /// </summary>
        public static Dictionary<ElementId, List<IndependentTag>> GetElementsForDeleteTags(Dictionary<ElementId, List<IndependentTag>> independentTagsFoElements, List<ElementId> elementIdsForVisuslizationWithStepRevit)
        {
            Dictionary<ElementId, List<IndependentTag>> elementsForDeleteTags = new Dictionary<ElementId, List<IndependentTag>>();
            foreach (var element in independentTagsFoElements)
            {
                if (elementIdsForVisuslizationWithStepRevit.Contains(element.Key) == false)
                {
                    elementsForDeleteTags.Add(element.Key, element.Value);
                }
            }
            return elementsForDeleteTags;
        }

        /// <summary>
        /// TODO!!! Получает список видов для которых необходимо создавать теги
        /// </summary>
        public static List<ElementId> GetViewsForCreateTags(Dictionary<ElementId, List<IndependentTag>> independentTagsFoElements)
        {
            List<ElementId> viewsForCreateTags = new List<ElementId>();
            foreach (var element in independentTagsFoElements)
            {
                List<IndependentTag> tags = element.Value;
                foreach (IndependentTag tag in tags)
                {
                    if (viewsForCreateTags.Contains(tag.OwnerViewId) == false)
                    {
                        viewsForCreateTags.Add(tag.OwnerViewId);
                    }
                }
            }
            return viewsForCreateTags;

        }

        /// <summary>
        /// TODO!!! Получает словарь видов (KEY) для которых необходимо создавать теги и список элементов (VALUE), у которых необходимо создать теги на данном виде 
        /// </summary>
        public static Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>> GetViewsAnndElementsForCreateTags(Document document, List<ElementId> elementsForCreateTags, List<ElementId> viewsForCreateTags)
        {
            Dictionary<ElementId, List<ElementId>> viewIdAndElementIds = new Dictionary<ElementId, List<ElementId>>();
            foreach (ElementId elementId in elementsForCreateTags)
            {
                List<ElementId> viewsForElement = GetViewsIdsForElement(document, elementId, viewsForCreateTags);
                foreach (ElementId viewId in viewsForElement)
                {
                    if (viewIdAndElementIds.ContainsKey(viewId) == false)
                    {
                        List<ElementId> elementsForView = new List<ElementId>();
                        elementsForView.Add(elementId);
                        viewIdAndElementIds.Add(viewId, elementsForView);
                    }
                    else
                    {
                        List<ElementId> elementsForView = viewIdAndElementIds[viewId];
                        if (elementsForView.Contains(elementId) == false)
                        {
                            elementsForView.Add(elementId);
                            viewIdAndElementIds[viewId] = elementsForView;
                        }
                    }
                }
            }
            
            Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>> viewAndElements = new Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>>();
            foreach (var viewIdAndElementId in viewIdAndElementIds)
            {
                Autodesk.Revit.DB.View view = document.GetElement(viewIdAndElementId.Key) as Autodesk.Revit.DB.View;
                List<FamilyInstance> instances = viewIdAndElementId.Value.ConvertAll(elementId => document.GetElement(elementId) as FamilyInstance);

                viewAndElements.Add(view, instances);
            }

            return viewAndElements;
        }

    


        /// <summary>
        /// TODO!!!
        /// </summary>
        public static Dictionary<ElementId, List<ElementId>> GetElementsForCreateTags(Dictionary<ElementId, List<ElementId>> infoForVisualization, Dictionary<ElementId, List<IndependentTag>> independentTagsFoElements)
        {

            Dictionary<ElementId, List<ElementId>> elementsForCreateTags = new Dictionary<ElementId, List<ElementId>>();
            //List<IndependentTag> tagsForElement = new List<IndependentTag>();
            //List<ElementId> tagIdsForElement = new List<ElementId>();

            foreach (var element in infoForVisualization)
            {
                if (independentTagsFoElements.ContainsKey(element.Key) == false)
                {
                    elementsForCreateTags.Add(element.Key, element.Value);
                }
            }

            return elementsForCreateTags;
        }


        /// <summary>
        /// Удаляет визуальное представление тега у заданных экземпляров семейств. TODO!!!!!!!!!!
        /// Removes the visual representation of a tag from the specified family instances.
        /// </summary>
        /// <param name="_document">Current document.</param>
        /// <param name="_familyInstances">List of family instances from which you want to remove tags. </param>
        /// <param name="_tags">The list of tags from which you want to remove tags.</param>
        /// <param name="_elementsIdsAndViewIdsForDeletedTags">Dictionary with elements Ids of circuit and view ids, where was visualized marking.</param>
        /// <returns>Returns true if tags to remove are found, false otherwise and ids for deleted views.</returns>
        public static void DeleteVisualMark(Document document, Dictionary<ElementId, List<IndependentTag>> elementsAndTagsForDelete)
        {   
            foreach (var element in elementsAndTagsForDelete)
            {
                List<IndependentTag> deleteTags = element.Value;
                ArbmAPIElementHelper.DeleteElements<IndependentTag>(document, deleteTags);
            }
        }

        /// <summary>
        /// Удаляет визуальное представление тега у заданных экземпляров семейств.
        /// Removes the visual representation of a tag from the specified family instances.
        /// </summary>
        /// <param name="_document">Current document.</param>
        /// <param name="_familyInstances">List of family instances from which you want to remove tags. </param>
        /// <param name="_tags">The list of tags from which you want to remove tags.</param>
        /// <param name="_elementsIdsAndViewIdsForDeletedTags">Dictionary with elements Ids of circuit and view ids, where was visualized marking.</param>
        /// <returns>Returns true if tags to remove are found, false otherwise and ids for deleted views.</returns>
        public static bool DeleteVisualMark(Document _document, List<FamilyInstance> _familyInstances, List<IndependentTag> _tags, out Dictionary<ElementId, List<ElementId>> _elementsIdsAndViewIdsForDeletedTags)
        {
            List<IndependentTag> deleteTags = new List<IndependentTag>();
            _elementsIdsAndViewIdsForDeletedTags = new Dictionary<ElementId, List<ElementId>>();
            foreach (var element in _familyInstances)
            {
                foreach (var tag in _tags)
                {
                    if (tag.TaggedElementId.HostElementId != ElementId.InvalidElementId && tag.TaggedElementId.HostElementId.Compare(element.Id) == 0)
                    {
                        deleteTags.Add(tag);
                        if (_elementsIdsAndViewIdsForDeletedTags.ContainsKey(element.Id) != true)
                        {
                            List<ElementId> viewIdsForElement = new List<ElementId>();
                            viewIdsForElement.Add(tag.OwnerViewId);
                            //viewsForElement.Add(_document.GetElement(tag.OwnerViewId) as View);
                            _elementsIdsAndViewIdsForDeletedTags.Add(element.Id, viewIdsForElement);
                        }
                        else
                        {
                            List<ElementId> viewIdsForElement = _elementsIdsAndViewIdsForDeletedTags[element.Id];
                            //View viewForElement = _document.GetElement(tag.OwnerViewId) as View;
                            if (viewIdsForElement.Contains(tag.OwnerViewId) != true)
                            {
                                viewIdsForElement.Add(tag.OwnerViewId);
                            }
                        }

                        //continue;
                    }

                }

            }
            if (deleteTags.Count > 0)
            {
                ArbmAPIElementHelper.DeleteElements<IndependentTag>(_document, deleteTags);
                return true;
            }
            return false;

        }
        /// <summary>
        /// Converts a list of family file names to a list without file extensions.
        /// </summary>
        ///<param name="_fileNamerWithExtens">List of filenames with extensions.</param>
        /// <returns>Returns a list of filenames without extensions.</returns>
        public static List<string> ConvertListFileNames(List<string> _fileNamerWithExtens)
        {
            List<string> fileNamerWithoutExtens = new List<string>();
            foreach (var element in _fileNamerWithExtens)
            {
                // Trim the extension in the filename
                fileNamerWithoutExtens.Add(element.Substring(0, element.Length - 4));
            }
            return fileNamerWithoutExtens;
        }
        /// <summary>
        /// Checks for the presence of all types and annotation families required in the document.
        /// Проверяет наличие всех необходимых типов и семейств аннотаций в документе.
        /// </summary>
        /// <param name="_doc">Current document.</param>
        /// <param name="_familiesMarking">List of annotation families to check for in the document.</param>
        /// <param name="_symbolFamiliesMarking">List of annotation families to check for in the document.</param>
        ///  <param name="_family">For the case of visualization of the control zone of fire detectors.</param>
        /// <returns>Returns a list of families to load into the document.</returns>
        public static List<string> ChecksTypesFamilies(Document _doc, List<string> _fileNamerWithoutExtens, List<string> _familiesSymbolMarking, out Family _family)
        {
            List<string> familiesLoad = new List<string>();
            List<string> familiesTypes = new List<string>();
            //familiesTypes = _familiesSymbolMarking.ToList<string>();
            Family family = null;
            foreach (var element in _fileNamerWithoutExtens)
            {
                //FamilySymbol familySymbol = null;
                // Получаем семейство если оно уже загружено в проект
                family = ArbmAPIElementHelper.FindElementByName(_doc, typeof(Family), element) as Family;
                // Family is not loaded into the project
                if (family == null)
                {
                    familiesLoad.Add(element + ".rfa");
                }
                else //  Check the loaded family for the presence of all types
                {
                    FamilySymbolFilter symbolFilter = new FamilySymbolFilter(family.Id);
                    FilteredElementCollector fec = new FilteredElementCollector(_doc);
                    IList<Element> familySymbols = fec.WherePasses(symbolFilter).ToElements();
                    // Type name coincidence counter
                    int count = 0;
                    foreach (var familySymbol in familySymbols)
                    {
                        if (_familiesSymbolMarking.Contains(familySymbol.Name) == true)
                        {
                            familiesTypes.Remove(familySymbol.Name);
                            count++;
                        }
                    }
                    if (count != _familiesSymbolMarking.Count) // No required number of required types
                    {
                        familiesLoad.Add(element + ".rfa");
                    }
                }
            }
            _family = family;
            return familiesLoad;
        }

        /// <summary>
        /// Checks for the presence of all types and annotation required families  in the document.
        /// </summary>
        ///  <param name="_doc">Current document.</param>
        /// <param name="_familiesMarking">List of annotation families to check for in the document.</param>
        /// <param name="_symbolFamiliesMarking">List of annotation families to check for in the document.</param>
        /// <returns>Returns a dictionary of unloaded families and their types.</returns>
        public static Dictionary<string, List<string>> ChecksTypesFamiliesDict(Document _doc, List<string> _fileNamerWithoutExtens, List<string> _familiesSymbolMarking)
        {
            Dictionary<string, List<string>> familiesLoad = new Dictionary<string, List<string>>();
            List<string> familiesTypesLoad = new List<string>();

            foreach (var element in _fileNamerWithoutExtens)
            {
                familiesTypesLoad = _familiesSymbolMarking.ToList<string>();
                // Получаем семейство если оно уже загружено в проект
                Family family = ArbmAPIElementHelper.FindElementByName(_doc, typeof(Family), element) as Family;
                // Family is not loaded into the project
                if (family == null)
                {
                    familiesLoad.Add(element + ".rfa", null);
                }
                else //  Check the loaded family for the presence of all types
                {
                    FamilySymbolFilter symbolFilter = new FamilySymbolFilter(family.Id);
                    FilteredElementCollector fec = new FilteredElementCollector(_doc);
                    IList<Element> familySymbols = fec.WherePasses(symbolFilter).ToElements();
                    // Type name coincidence counter
                    int count = 0;
                    foreach (var familySymbol in familySymbols)
                    {
                        if (_familiesSymbolMarking.Contains(familySymbol.Name) == true)
                        {
                            familiesTypesLoad.Remove(familySymbol.Name);
                            count++;
                        }
                    }
                    if (count != _familiesSymbolMarking.Count) // No required number of required types
                    {
                        familiesLoad.Add(element + ".rfa", familiesTypesLoad);
                    }
                }
            }
            return familiesLoad;
        }

        /// <summary>
        /// The interface implementation to use when responding to conflicts during the load operation.
        /// </summary>
        class FamilyOption : IFamilyLoadOptions
        {
            public bool OnFamilyFound(
              bool familyInUse,
              out bool overwriteParameterValues)
            {
                //TaskDialog.Show("SampleFamilyLoadOptions", "The family has not been in use and will keep loading.");
                overwriteParameterValues = true;
                return true;
            }
            public bool OnSharedFamilyFound(
              Family sharedFamily,
              bool familyInUse,
              out FamilySource source,
              out bool overwriteParameterValues)
            {
                source = FamilySource.Family;
                overwriteParameterValues = true;
                return true;
            }
        }

        /// <summary>
        /// Отображает на экране и помещает в центр все элементы входящие в цепь, к которой необходимо добавить элементы.
        /// Displays on the screen and places in the center all elements included in the chain to which you want to add elements.
        /// </summary>
        /// <param name="_uidoc">This class represents a document opened in the user interface and therefore offers interfaces to work with settings and operations in the UI (for example, the active selection).</param>
        /// <param name="_instances">Family instances of the circuit to which you want to add items.</param>
        /// <returns>Returns List (T - generic parameter) </returns>
        public static void ZoomElementsCircuit(UIDocument _uidoc, List<FamilyInstance> _instances)
        {
            Autodesk.Revit.DB.View view = _uidoc.ActiveView;
            // Инициализация начальными значениями
            XYZ instMin = _instances[0].get_BoundingBox(view).Min;
            XYZ instMax = _instances[0].get_BoundingBox(view).Max;

            double minX = instMin.X;
            double minY = instMin.Y;
            double maxX = instMax.X;
            double maxY = instMax.Y;

            for (int i = 1; i < _instances.Count; i++)
            {
                // Get the BoundingBox instance for current view.
                instMin = _instances[i].get_BoundingBox(view).Min;
                instMax = _instances[i].get_BoundingBox(view).Max;
                if (instMin.X < minX)
                {
                    minX = instMin.X;
                }
                if (instMin.Y < minY)
                {
                    minY = instMin.Y;
                }
                if (instMax.X > maxX)
                {
                    maxX = instMax.X;
                }
                if (instMax.Y > maxY)
                {
                    maxY = instMax.Y;
                }
            }
            XYZ viewCorner1 = new XYZ(minX, minY, 0);
            XYZ viewCorner2 = new XYZ(maxX, maxY, 0);

            UIView uiview = null;
            IList<UIView> uiviews = _uidoc.GetOpenUIViews();

            foreach (UIView uv in uiviews)
            {
                if (uv.ViewId.Equals(view.Id))
                {
                    uiview = uv;
                    break;
                }
            }
            uiview.ZoomAndCenterRectangle(viewCorner1, viewCorner2);
        }

        /// <summary>
        /// Устанавливает язык для элементов управления плагина.
        /// Sets the language for plugin controls.
        /// </summary>
        /// <param name="_languageType">Type containing the supported Revit product languages.</param>
        /// <param name="_language">Plugin localization language.</param>
        public static void  SetLanguage(LanguageType _languageType, string _language = "")
        {
            if (_languageType != LanguageType.Russian)
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("_3DSD_Shared_Resources.Culture_en_US");
            }
            else
            {
                System.Threading.Thread.CurrentThread.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("_3DSD_Shared_Resources.Culture_ru_RU");
            }
        }
        /// <summary>
        /// Исключает повторяющиеся элементы из списка.
        /// Excludes duplicate elements from the list.
        /// </summary>
        /// <param name="_doc">Current document or Link Revit document.</param>
        /// <param name="_elements">General list of elements.</param>
        /// <returns>Returns a list of elements without duplicates. </returns>
        public static List<K> GetNoDuplicateListElements<T, K>(Document _doc, IList<T> _elements, bool _isLink = false) where T : Reference
                                                                                      where K : SpatialElement
        {
            List<K> elements = new List<K>();
            List<ElementId> elementIds = new List<ElementId>();
            K room = null;
            foreach (var element in _elements)
            {
                if (_isLink == true)
                {
                    room = _doc.GetElement(element.LinkedElementId) as K;
                }
                else
                {
                    room = _doc.GetElement(element) as K;
                }

                if (room == null)
                {
                    return new List<K>(); // возвращаем пустой список
                }
                if (_isLink == true)
                {
                    if (elementIds.Contains(element.LinkedElementId) != true)
                    {
                        elementIds.Add(element.LinkedElementId);
                        elements.Add(room);
                    }
                }
                else
                {
                    if (elementIds.Contains(element.ElementId) != true)
                    {
                        elementIds.Add(element.ElementId);
                        elements.Add(room);
                    }
                }
                  
            }
            return elements;
        }

        /// <summary>
        /// Returns views, which meet our criteria.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="viewTypes">List of ViewTypes, which must correspond each view.</param>

        public static List<Autodesk.Revit.DB.View> GetViews(Document document, List<ViewType> viewTypes)
        {
            FilteredElementCollector collector = new FilteredElementCollector(document);
            List<Autodesk.Revit.DB.View> elementsOfViews = collector.OfClass(typeof(Autodesk.Revit.DB.View)).Where(view => (view as Autodesk.Revit.DB.View).IsTemplate != true).ToList().ConvertAll<Autodesk.Revit.DB.View>(el => el as Autodesk.Revit.DB.View).Where(view => viewTypes.Contains(view.ViewType)).ToList();
            //List<View> elementsOfViews = collector.OfClass(typeof(View)).ToList().ConvertAll<View>(el => el as View).Where(view => viewTypes.Contains(view.ViewType)).ToList();

            if (elementsOfViews.Count > 0)
            {
                return elementsOfViews;
            }
            else
            {
                return new List<Autodesk.Revit.DB.View>();
            }
        }

        /// <summary>
        /// Deletes SelectionFilterElement from document.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="filtersForRemoving">List of names of SelectionFilterElement, which necessary to delete.</param>

        static public void DeleteSelectionFilterElements(Document document, List<string> filtersForRemoving)
        {
            // We check to exist SelectionFilterElement which has names are Properties._3DSD_Marking_Resources.NameSelectedCircuitFilter,
            // Properties._3DSD_Marking_Resources.NameAddedElementsFilter, Properties._3DSD_Marking_Resources.NameBaseElementCircuitFilter
            ElementClassFilter selectionFilter = new ElementClassFilter(typeof(SelectionFilterElement));
            FilteredElementCollector collector = new FilteredElementCollector(document);
            List<Element> elementSelectionFilters = collector.WherePasses(selectionFilter).ToList().Where(el => filtersForRemoving.Contains(el.Name)).ToList();
            // If SelectionFilterElement element exist, then delete his
            if (elementSelectionFilters.Count > 0)
            {
                document.Delete(elementSelectionFilters.ConvertAll(el => el.Id));
            }
        }

        /// <summary>
        /// It change settings of SelectionFilter for view.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="viewsAndTemplateViewIdsForAddFilter">Views, in which will add filters and their corresponding view template IDs.</param>
        /// <param name="activeView">Active view.</param>
        /// <param name="filterId">Id of filter, which should be added to active view.</param>
        /// <param name="color">Color for elements of filter.</param>
        /// <param name="lineWight">Wight of line for elements of filter.</param>
        static public void ChangeSelectionFilterSettingsForView(Document document, Dictionary<ElementId, ElementId> viewsAndTemplateViewIdsForAddFilter,
            Autodesk.Revit.DB.View activeView, ElementId filterId, Color color, int lineWeight)
        {

            if (viewsAndTemplateViewIdsForAddFilter.ContainsKey(activeView.Id) != true)
            {
                viewsAndTemplateViewIdsForAddFilter.Add(activeView.Id, activeView.ViewTemplateId);
                // if applied template view, then temporary removes it during this command execution
                activeView.ViewTemplateId = ElementId.InvalidElementId;
                // Remove all filters, which are visibility for active view
                List<ElementId> appliedFilterIds = activeView.GetFilters().Where(elementId => activeView.GetFilterVisibility(elementId)).ToList();
                // Temporary removes all filters for this view it during this command execution
                if (appliedFilterIds.Count > 0)
                {
                    foreach (ElementId id in appliedFilterIds)
                    {
                        activeView.RemoveFilter(id);
                    }

                }
            }

            if (activeView.IsFilterApplied(filterId) == true)
            {
                activeView.RemoveFilter(filterId);
            }

            activeView.AddFilter(filterId);
            // document.Regenerate();
            // Use the existing graphics settings, and change the color to green
            OverrideGraphicSettings overrideSettings = activeView.GetFilterOverrides(filterId);
            overrideSettings.SetProjectionLineColor(color);
            overrideSettings.SetProjectionLineWeight(lineWeight);
            activeView.SetFilterOverrides(filterId, overrideSettings);
        }

        /// <summary>
        /// Returns elementIds, which entered in the circuit on the active view.
        /// </summary>
        /// <param name="circuitElementIds">IDs of elements, which entered in the circuit.</param>
        /// <param name="allElementsFromActiveView">All elements, which correspond our criteria.</param>
        static List<ElementId> GetElementIdsOfCircuitFromActiveView(List<ElementId> circuitElementIds, List<ElementId> allElementsFromActiveView)
        {
            List<ElementId> elementIdsOfCircuit = new List<ElementId>();
            foreach (ElementId id in allElementsFromActiveView)
            {
                if (circuitElementIds.Contains(id) == true)
                {
                    elementIdsOfCircuit.Add(id);
                }

            }

            return elementIdsOfCircuit;
        }

        /// <summary>
        /// It sets Selection elements for circuit on the active view.
        /// </summary>
        /// <param name="uiDocument">Current UI document.</param>
        /// <param name="document">Current document.</param>
        /// <param name="elementsIdsOfCircuit">IDs of elements of circuit on the active view.</param>
        /// <param name="instancesFromActiveView">FamilyInstances on the active view, which correspond our criteria.</param>
        public static void SetZoomSelectionForCircuit(UIDocument uiDocument, Document document, List<ElementId> elementsIdsOfCircuit, List<FamilyInstance> instancesFromActiveView)
        {
            //FilterElement.Clear();

            //// Circuit no selected
            if (elementsIdsOfCircuit == null)
            {
                return;
            }

            // Get all elements, which correspond our criteria
            //List<FamilyInstance> instancesFromActiveView = RvtAPIElementHelper.GetFamilyInsance(document, true, NewCircuitEvent.categories, NewCircuitEvent.paramsType, NewCircuitEvent.paramsInstСriterion);
            List<ElementId> elementIdsFromActiveView = null;
            // The active view has elements, which correspond our criteria
            if (instancesFromActiveView.Count > 0)
            {

                elementIdsFromActiveView = instancesFromActiveView.ConvertAll<ElementId>(instance => instance.Id);

                List<ElementId> elementIdsOfCircuit = GetElementIdsOfCircuitFromActiveView(elementsIdsOfCircuit, elementIdsFromActiveView);
                // If active view has elements of circuit
                if (elementIdsOfCircuit.Count > 0)
                {
                    // Отображаем на экране и помещает в центр все элементы входящие в цепь на активном виде, к которой необходимо добавить элементы.
                    // We display on the screen and places in the center all elements included in the chain to which you want to add elements only on the active view at this moment.
                    List<FamilyInstance> elementsOfCircuit = elementIdsOfCircuit.ConvertAll<FamilyInstance>(id => document.GetElement(id) as FamilyInstance);
                    ArbmAPIElementHelper.ZoomElementsCircuit(uiDocument, elementsOfCircuit);

                } //  if (elementsOfCircuit.Count > 0)

            } //  if (instancesFromActiveView.Count > 0)

        }

        /// <summary>
        /// Returns actual dictionary, which contains elements and views for visualization.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="elementIdsForVisualization">IDs of elements for visualization marking.</param>
        /// <param name="elementIdsWithViewIdsWasMarking">Dictionary with IDs of elements (keys) and lists of views (values) for each elementId, where was marking early.</param>
        static public Dictionary<ElementId, List<ElementId>> GetInfoForVisualization(Document document, List<ElementId> elementIdsForVisualization, Dictionary<ElementId, List<ElementId>> elementIdsWithViewIdsWasMarking)
        {
            Dictionary<ElementId, List<ElementId>> infoForVisualization = new Dictionary<ElementId, List<ElementId>>();
            List<ElementId> elementIds = new List<ElementId>();
            foreach (ElementId id in elementIdsForVisualization)
            {
                if (elementIdsWithViewIdsWasMarking.ContainsKey(id) != true)
                {
                    elementIds.Add(id);
                }
                else
                {
                    infoForVisualization.Add(id, elementIdsWithViewIdsWasMarking[id]);
                }
            }

            if (elementIds.Count > 0)
            {
                // TO DO!!!
                List<ElementId> viewIdsForDeletedTags = GetViewIdsForDeletedTags(elementIdsWithViewIdsWasMarking);
                Dictionary<ElementId, List<ElementId>> elementsIdsAndViewIds = GetElementsIdsAndViewIds(document, elementIds, viewIdsForDeletedTags);

                foreach (var elementIdsAndViewIds in elementsIdsAndViewIds)
                {
                    infoForVisualization.Add(elementIdsAndViewIds.Key, elementIdsAndViewIds.Value);
                }
            }

            return infoForVisualization;
        }

        /// <summary>
        /// Returns all view ids for deleted tags for this circuit.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="elementIdsWithViewIdsWasMarking">Dictionary with IDs of elements (keys) and lists of views (values) for each elementId, where was marking early.</param>
        static List<ElementId> GetViewIdsForDeletedTags(Dictionary<ElementId, List<ElementId>> elementIdsWithViewIdsWasMarking)
        {
            List<ElementId> viewIdsForDeletedTags = new List<ElementId>();
            foreach (var elementIdWithViewIdsWasMarking in elementIdsWithViewIdsWasMarking)
            {
                foreach (ElementId viewId in elementIdWithViewIdsWasMarking.Value)
                {
                    if (viewIdsForDeletedTags.Contains(viewId) != true)
                    {
                        viewIdsForDeletedTags.Add(viewId);
                    }
                }

            }
            return viewIdsForDeletedTags;
        }

        /// <summary>
        /// Returns dictionary element Ids and list view Ids for each element id.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="elementIds">IDs of elements for which .</param>
        /// 
        public static Dictionary<ElementId, List<ElementId>> GetElementsIdsAndViewIds(Document document, List<ElementId> elementIds, List<ElementId> viewIdsForCreateTags)
        {
            Dictionary<ElementId, List<ElementId>> elementsIdsAndViewIds = new Dictionary<ElementId, List<ElementId>>();
            foreach (ElementId elementId in elementIds)
            {
                List<ElementId> viewsIdsForElement = GetViewsIdsForElement(document, elementId, viewIdsForCreateTags);
                if (viewsIdsForElement.Count > 0)
                {
                    elementsIdsAndViewIds.Add(elementId, viewsIdsForElement);
                }
                else
                {
                    elementsIdsAndViewIds.Add(elementId, new List<ElementId>());
                }
            }
            return elementsIdsAndViewIds;

        }

        /// <summary>
        /// Returns list view Ids for element id.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="elementId">Id of element for which we get list of views.</param>
        /// <param name="viewIdsForCreateTags">List of views for element id.</param>
        public static List<ElementId> GetViewsIdsForElement(Document document, ElementId elementId, List<ElementId> viewIdsForCreateTags)
        {
            List<ElementId> viewsIdsForElement = new List<ElementId>();
            FilteredElementCollector elementsCollector = null;
            foreach (ElementId viewId in viewIdsForCreateTags)
            {
                elementsCollector = new FilteredElementCollector(document, viewId);
                List<Element> element = elementsCollector.Where(e => e.Id == elementId).ToList();
                if (element.Count > 0)
                {
                    if (viewsIdsForElement.Contains(viewId) != true)
                    {
                        viewsIdsForElement.Add(viewId);
                    }
                }
            }
            return viewsIdsForElement;
        }

        /// <summary>
        /// This method get all elements of circuit for active view.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="activeView">Active document.</param>
        /// <param name="allElementsOfCircuit">Elements of circuit for active view.</param>
        static public List<FamilyInstance> GetElementsOfCircuitForActiveView(Document document, Autodesk.Revit.DB.View activeView, List<FamilyInstance> allElementsOfCircuit)
        {

            List<FamilyInstance> elements = new List<FamilyInstance>();
            ElementClassFilter familyInstanceFilter = new ElementClassFilter(typeof(FamilyInstance));
            FilteredElementCollector collector = new FilteredElementCollector(document, activeView.Id);
            List<ElementId> instancesIds = collector.WherePasses(familyInstanceFilter).ToList().ConvertAll(e => e.Id);

            foreach (FamilyInstance elementOfCircuit in allElementsOfCircuit)
            {
                if (instancesIds.Contains(elementOfCircuit.Id))
                {
                    // The view contains element of circuit
                    elements.Add(elementOfCircuit);
                }
            }
            return elements;
        }

        /// <summary> // TODO!!!!!
        /// Returns view Ids and list of elementIds for each view id.
        /// </summary>
        /// <param name="document">Current document.</param>
        /// <param name="elementId">Id of element for which we get list of views.</param>
        /// <param name="viewIdsForDeletedTags">List of views for element id.</param>
        public static Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>> GetElementIdsForViewIds(Document document, Dictionary<ElementId, List<ElementId>> infoForVisualization)
        {

            Dictionary<ElementId, List<ElementId>> viewIdAndElementIds = new Dictionary<ElementId, List<ElementId>>();
            // Create copy of input infoForVisualization
            Dictionary<ElementId, List<ElementId>> infoForVisualizationCopy = new Dictionary<ElementId, List<ElementId>>(infoForVisualization);
            int count = 0; 
            foreach (var value in infoForVisualizationCopy)
            {
                count++;
                List<ElementId> viewsPrevios = value.Value;

                if (viewsPrevios.Count > 0)
                {
                    for (int index = 0; index < viewsPrevios.Count; index++)
                    {
                        List<ElementId> elementIds = new List<ElementId>();
                        elementIds.Add(value.Key);

                        for (int indexForDict = count; indexForDict < infoForVisualizationCopy.Count; indexForDict++)
                        {
                            List<ElementId> viewsNext = infoForVisualizationCopy.ElementAt(indexForDict).Value;
                            ElementId elementId = infoForVisualizationCopy.ElementAt(indexForDict).Key;

                            if (viewsNext.Contains(viewsPrevios[index]) == true)
                            {
                                elementIds.Add(elementId);
                                viewsNext.Remove(viewsPrevios[index]);
                            }
                        }

                        if (viewIdAndElementIds.ContainsKey(viewsPrevios[index]) == false)
                        {
                            viewIdAndElementIds.Add(viewsPrevios[index], elementIds);
                        }

                    }

                }
                
            }

            Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>> viewAndElements = new Dictionary<Autodesk.Revit.DB.View, List<FamilyInstance>>();
            foreach (var viewIdAndElementId in viewIdAndElementIds)
            {
                Autodesk.Revit.DB.View view = document.GetElement(viewIdAndElementId.Key) as Autodesk.Revit.DB.View;
                List<FamilyInstance> instances = viewIdAndElementId.Value.ConvertAll(elementId => document.GetElement(elementId) as FamilyInstance);

                viewAndElements.Add(view, instances);
            }

            return viewAndElements;
        }


        /// <summary>
        /// load family parameters from the text file
        /// </summary>
        /// <param name="_exist">
        /// indicate whether the shared parameter file exists
        /// </param>
        /// <param name="_filePath">
        /// general parameters file path
        /// </param>
        /// <returns>
        /// return true if succeeded; otherwise false
        /// </returns>
        public static bool SetSharedParametersFile(Autodesk.Revit.ApplicationServices.Application revitApp, string filePath, StringBuilderLogger logger)
        {
            try
            {

                if (!File.Exists(filePath))
                {
                    string notLoadSharedParamFileWarning = $"The file of shared parameters {filePath} is not loaded.";
                    logger.LogWarning(notLoadSharedParamFileWarning);
                    MessageBox.Show($"'{notLoadSharedParamFileWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }

                revitApp.SharedParametersFilename = filePath;

            }
            catch (Exception exp)
            {
                string notLoadSharedParamFileWarning = $"EXCEPTION '{exp.Message}'. File '{filePath}' cant load.";
                logger.LogWarning(notLoadSharedParamFileWarning);
                MessageBox.Show($"'{notLoadSharedParamFileWarning}'", "Rename families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            return true;
        }

        // Get External Definition from file of shared parameters
        public static ExternalDefinition GetExternalDefinition(Autodesk.Revit.ApplicationServices.Application revitApp, Guid guidParameter, StringBuilderLogger logger, out bool isOpenDefinitionFile)
        {
            DefinitionFile definitionFile = revitApp.OpenSharedParameterFile();
            if (definitionFile == null)
            {
                isOpenDefinitionFile = false; // DefinitionFile wasn't opened
                logger.LogWarning($"Cant open Shared Parameter File '{revitApp.SharedParametersFilename}'.");
                return null;
            }
            foreach (DefinitionGroup group in definitionFile.Groups)
            {
                foreach (ExternalDefinition def in group.Definitions)
                {
                    if (def.GUID.Equals(guidParameter) == true)
                    {
                        isOpenDefinitionFile = true; // DefinitionFile was opened
                        return def;
                    }
                }
            }
            logger.LogWarning($"Cant get ExternalDefinition for GUID '{guidParameter.ToString()}'.");
            isOpenDefinitionFile = true; // DefinitionFile was opened
            return null;
        }


    }
}