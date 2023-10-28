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

            //List<ElementId> parameters = collector.WherePasses(parametersFilter).Where(e => e.Name.Equals("AER_ТП_Потребляемая мощность")).ToList().ConvertAll(e => e.Id);
            List<Element> parameters = collector.OfClass(typeof(ParameterElement)).WhereElementIsNotElementType().ToList();

            UserRemoveParameters userRemoveParameters = new UserRemoveParameters(parameters, doc);
            userRemoveParameters.ShowDialog();
            
            return Result.Succeeded;
        }

        public void RemoveParameters(ICollection<Element> parameters, Autodesk.Revit.DB.Document doc)
        {
            int count = 0;
            using (Transaction t = new Transaction(doc, "Delete Parameter"))
            {
                // start a transaction within the valid Revit API context
                t.Start();
                foreach (Element param in parameters)
                {
                    doc.Delete((ICollection<ElementId>)param);
                    count++;
                }
                t.Commit();
                t.Dispose();
            }
            string cmdRemoveInfo = $"Remove {count} parameters";
            MessageBox.Show($"'{cmdRemoveInfo}'", "Resave families command", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
