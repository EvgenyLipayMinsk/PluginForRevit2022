using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.Exceptions;
using Autodesk.Revit.UI;
using System.Windows.Media.Imaging;
using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI.Events;
using System.Windows.Interop;
using System.Windows;

namespace PluginForRevit2022
{
    internal class App : IExternalApplication
    {
        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        public Result OnStartup(UIControlledApplication application)
        {
            //Создание новой панели AerBIM Manager
            string tabName = "AerBIM Manager";
            string panelNameWorkTools = "Work Tools";
            application.CreateRibbonTab(tabName); 
           
            var workPanel = application.CreateRibbonPanel(tabName, panelNameWorkTools); //Создание подпанели Work Tools

            string curAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location; //получение пути сборки до .dll
            string curAssemblyPath = Path.GetDirectoryName(curAssembly); //Получение пути 

            #region Создание PushButton Convert to RVT2022
            PushButtonData pbd1 = new PushButtonData("Convert to RVT2022", "Convert File\rto RVT2022", curAssembly, "PluginForRevit2022.CmdConvertationForRevitVersion");
            pbd1.ToolTip = "Resave family to Revit 2022";
            pbd1.LongDescription = "This is Long description of Resave family to Revit 2022";
            //pbd1.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "Folders-32.ico"), UriKind.RelativeOrAbsolute));
            pbd1.LargeImage = Imaging.CreateBitmapSourceFromHIcon(Properties.Resources.Convert_to_RVT2022.GetHicon(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var pb1 = workPanel.AddItem(pbd1) as PushButton;
            #endregion

            #region Создание Remove Parameters
            PushButtonData pbd2 = new PushButtonData("Remove Parameters", "Remove\rParameters", curAssembly, "PluginForRevit2022.CmdRemoveParameters");
            pbd2.ToolTip = "Remove parameters";
            pbd2.LongDescription = "This is Long description of remove parameters";
            //pbd1.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "Folders-32.ico"), UriKind.RelativeOrAbsolute));
            pbd2.LargeImage = Imaging.CreateBitmapSourceFromHIcon(Properties.Resources.Remove_Parameters.GetHicon(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var pb2 = workPanel.AddItem(pbd2) as PushButton;
            #endregion

            return Result.Succeeded; 
        }
       
    }
}
