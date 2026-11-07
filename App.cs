using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

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

            #region Создание Remove Parameters
            PushButtonData pbdRemoveParameters = new PushButtonData("Remove Parameters", "Remove\rParameters", curAssembly, "PluginForRevit2022.CmdRemoveParameters");
            pbdRemoveParameters.ToolTip = "Remove parameters";
            pbdRemoveParameters.LongDescription = "This is Long description of remove parameters";
            pbdRemoveParameters.LargeImage = Imaging.CreateBitmapSourceFromHIcon(Properties.Resources.Remove_Parameters.GetHicon(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            workPanel.AddItem(pbdRemoveParameters);
            #endregion

            return Result.Succeeded;
        }
    }
}
