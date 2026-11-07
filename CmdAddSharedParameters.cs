using Aerbim._3DSD.Test;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using ScanFolderCopyAndProcess;

namespace PluginForRevit2022
{


    internal class CmdAddSharedParameters : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var App = commandData.Application;
            var uApp = App.Application;
            var uDocument = App.ActiveUIDocument;
            var document = uDocument.Document;
            var bindingMap = document.ParameterBindings;

            var SharedFile = uApp.OpenSharedParameterFile();
            

            return Result.Succeeded;
        }
    }
}
