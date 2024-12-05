using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PluginForRevit2022
{

    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    internal class CmdGetLocationOfCategory : IExternalCommand
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
            BuiltInCategory.OST_SecurityDevices
            //BuiltInCategory.OST_AnnotationCrop

        };

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            ElementClassFilter parametersFilter = new ElementClassFilter(typeof(ParameterElement));

            Document doc = commandData.Application.ActiveUIDocument.Document;

            FilteredElementCollector collector = new FilteredElementCollector(doc);

            List<Element> parameters = collector.OfCategory(BuiltInCategory.OST_FireAlarmDevices).ToElements().ToList();
            //List<Element> parameters = collector.OfClass(typeof(ParameterElement)).WhereElementIsNotElementType().ToList();

            MessageBox.Show($"{parameters.Count}", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);


            return Result.Succeeded;
        }
    }
}
