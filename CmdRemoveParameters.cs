using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.Creation;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace PluginForRevit2022
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    internal class CmdRemoveParameters : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {

            ElementClassFilter parametersFilter = new ElementClassFilter(typeof(ParameterElement));

            Autodesk.Revit.DB.Document doc = commandData.Application.ActiveUIDocument.Document;

            FilteredElementCollector collector = new FilteredElementCollector(doc);

            List<Element> parameters = collector.OfClass(typeof(ParameterElement)).WhereElementIsNotElementType().ToList();

            UserRemoveParameters userRemoveParameters = new UserRemoveParameters(parameters, doc);

            userRemoveParameters.ShowDialog();
            
            return Result.Succeeded;
        }
    }
}
