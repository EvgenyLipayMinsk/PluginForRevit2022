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

namespace PluginForRevit2022
{
    internal class App : IExternalApplication
    {
        static string ButtonIconsFolder = @"D:\Visual Studio\PluginForRevit2022\Resourses";
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
            pbd1.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "Folders-32.ico"), UriKind.Absolute));
            var pb1 = workPanel.AddItem(pbd1) as PushButton;
            #endregion

            #region Создание SplitButton 
            SplitButtonData splitMainButton = new SplitButtonData("Main Button", "Main Button");
            SplitButton sButton = workPanel.AddItem(splitMainButton) as SplitButton;

            PushButtonData pbd2 = new PushButtonData("Remove Parameters", "Remove\rParameters", curAssembly, "PluginForRevit2022.CmdRemoveParameters");
            pbd2.ToolTip = "Remove parameters";
            pbd2.LongDescription = "This is Long description of button1";
            pbd2.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "settingsMain.ico"), UriKind.Absolute));
            var pb2 = sButton.AddPushButton(pbd2);

            PushButtonData pbd3 = new PushButtonData("Button2", "Button2", curAssembly, "Button2");
            pbd3.ToolTip = "This is ToolTip";
            pbd3.LongDescription = "This is Long description of button2";
            pbd3.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "button1.ico"), UriKind.Absolute));
            var pb3 = sButton.AddPushButton(pbd3);

            PushButtonData pbd4 = new PushButtonData("Button3", "Button3", curAssembly, "Button3");
            pbd4.ToolTip = "This is ToolTip";
            pbd4.LongDescription = "This is Long description of button3";
            pbd4.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "button2.ico"), UriKind.Absolute));
            var pb4 = sButton.AddPushButton(pbd4);
            #endregion

            workPanel.AddSlideOut(); // Создание слайдера

            #region Создание TextBox 
            TextBoxData testBoxData = new TextBoxData("WallMark");
            Autodesk.Revit.UI.TextBox textBox = (TextBox)(workPanel.AddItem(testBoxData));
            textBox.Value = "Example of text"; //default wall mark
            textBox.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-папка-16.ico"), UriKind.Absolute));
            textBox.ToolTip = "Set some text";
            textBox.ShowImageAsButton = true;
            textBox.EnterPressed += new EventHandler<TextBoxEnterPressedEventArgs>(SetTextBoxValue);
            #endregion

            string panelNameExampleTools = "Example Tools";
            var examplePanel = application.CreateRibbonPanel(tabName, panelNameExampleTools); //Создание подпанели Example Tools

            #region Создание ComboBox
            ComboBoxData comboBoxDataExample1 = new ComboBoxData("Example1ComboBox");
            ComboBoxData comboBoxDataExample2 = new ComboBoxData("Example2ComboBox");
            IList<RibbonItem> ribbonItemsStackedExample = examplePanel.AddStackedItems(comboBoxDataExample1, comboBoxDataExample2);
            ComboBox comboBoxExample1 = (ComboBox)(ribbonItemsStackedExample[0]);
            ComboBoxMemberData comboBoxMemberDataEx = new ComboBoxMemberData("Папки", "Папки");
            ComboBoxMember comboboxMemberEx = comboBoxExample1.AddItem(comboBoxMemberDataEx);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-folders-32.ico"), UriKind.Absolute));
            comboBoxMemberDataEx = new ComboBoxMemberData("Корзина", "Корзина");
            comboboxMemberEx = comboBoxExample1.AddItem(comboBoxMemberDataEx);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-корзина-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx = new ComboBoxMemberData("Персональная папка", "Персональная папка");
            comboboxMemberEx = comboBoxExample1.AddItem(comboBoxMemberDataEx);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-папка-пользователя-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx = new ComboBoxMemberData("Проводник", "Проводник");
            comboboxMemberEx = comboBoxExample1.AddItem(comboBoxMemberDataEx);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-проводник-windows-16.ico"), UriKind.Absolute));

           
            ComboBox comboBoxExample2 = (ComboBox)(ribbonItemsStackedExample[1]);
            ComboBoxMemberData comboBoxMemberDataEx2 = new ComboBoxMemberData("Раздел проектирования", "Раздел проектирования");
            ComboBoxMember comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-folders-32.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("ОПС", "ОПС");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-корзина-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("СКУД", "СКУД");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-папка-пользователя-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("СОТ", "СОТ");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-проводник-windows-16.ico"), UriKind.Absolute));
            #endregion

            return Result.Succeeded;   
        }
        public void SetTextBoxValue(object sender, TextBoxEnterPressedEventArgs args)
        {
            TaskDialog.Show("TextBox EnterPressed Event", "We Are the Champions");
        }
    }
}
