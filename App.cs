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
        static string ButtonIconsFolder_Test = @"D:\Visual Studio\Examples\Aerbim.Multicompilation.Test\Aerbim.Multicompilation.Test\Ribbon";
        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        public Result OnStartup(UIControlledApplication application)
        {
            string tabName = "AerBIM Manager";
            string panelNameWorkTools = "Work Tools";
            application.CreateRibbonTab(tabName);
           
            var workPanel = application.CreateRibbonPanel(tabName, panelNameWorkTools);
            



            string curAssembly = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string curAssemblyPath = Path.GetDirectoryName(curAssembly);

            PushButtonData pbd1 = new PushButtonData("Convert to RVT2022", "Convert File\rto RVT2022", curAssembly, "PluginForRevit2022.CmdConvertationForRevitVersion");
            pbd1.ToolTip = "This is ToolTip";
            pbd1.LongDescription = "This is Long description";

            pbd1.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "DB.ico"), UriKind.Absolute));
            
            var pb1 = workPanel.AddItem(pbd1) as PushButton;

            SplitButtonData splitMainButton = new SplitButtonData("Main Button", "Main Button");
            //splitMainButton.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "settingsMain.ico"), UriKind.Absolute));
            SplitButton sButton = workPanel.AddItem(splitMainButton) as SplitButton;
            

            PushButtonData pbd2 = new PushButtonData("Button1", "Main\rButton", curAssembly, "Main Button");
            pbd2.ToolTip = "This is ToolTip";
            pbd2.LongDescription = "This is Long description";

            pbd2.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "settingsMain.ico"), UriKind.Absolute));

            var pb2 = sButton.AddPushButton(pbd2);
            pb2 = (PushButton)workPanel.AddItem(pbd2);

            workPanel.AddSeparator();

            PushButtonData pbd3 = new PushButtonData("Button2", "Button2", curAssembly, "Button2");
            pbd3.ToolTip = "This is ToolTip";
            pbd3.LongDescription = "This is Long description of button2";

            pbd3.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "button1.ico"), UriKind.Absolute));

            var pb3 = sButton.AddPushButton(pbd3);
            pb3 = (PushButton)workPanel.AddItem(pbd3);

            PushButtonData pbd4 = new PushButtonData("Button3", "Button3", curAssembly, "Button3");
            pbd4.ToolTip = "This is ToolTip";
            pbd4.LongDescription = "This is Long description of button3";

            
            pbd4.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "button2.ico"), UriKind.Absolute));

            var pb4 = sButton.AddPushButton(pbd4);
            pb4 = (PushButton)workPanel.AddItem(pbd4);

            workPanel.AddSlideOut();

            TextBoxData testBoxData1 = new TextBoxData("WallMark");
            Autodesk.Revit.UI.TextBox textBox1 = (Autodesk.Revit.UI.TextBox)(workPanel.AddItem(testBoxData1));
            textBox1.Value = "Example of text"; //default wall mark
            textBox1.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-folders-32.ico"), UriKind.Absolute));
            textBox1.ToolTip = "Set the mark for new wall";
            textBox1.ShowImageAsButton = true;
            textBox1.EnterPressed += new EventHandler<Autodesk.Revit.UI.Events.TextBoxEnterPressedEventArgs>(SetTextBoxValue);


            string panelNameExampleTools = "Example Tools";
            var examplePanel = application.CreateRibbonPanel(tabName, panelNameExampleTools);

            


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
            ComboBoxMemberData comboBoxMemberDataEx2 = new ComboBoxMemberData("Папки2", "Папки2");
            ComboBoxMember comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-folders-32.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("Корзина", "Корзина");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-корзина-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("Персональная папка", "Персональная папка");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-папка-пользователя-16.ico"), UriKind.Absolute));
            comboBoxMemberDataEx2 = new ComboBoxMemberData("Проводник", "Проводник");
            comboboxMemberEx2 = comboBoxExample2.AddItem(comboBoxMemberDataEx2);
            //comboboxMemberEx.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder, "icons8-проводник-windows-16.ico"), UriKind.Absolute));

            examplePanel.AddSlideOut();

            string firstPanelName = "Ribbon Sample";
            RibbonPanel ribbonSamplePanel = application.CreateRibbonPanel(tabName,firstPanelName);
            //Create a SplitButton for user to create Non - Structural or Structural Wall
            #region Create a SplitButton for user to create Non-Structural or Structural Wall
            SplitButtonData splitButtonData = new SplitButtonData("NewWallSplit", "Create Wall");
            SplitButton splitButton = ribbonSamplePanel.AddItem(splitButtonData) as SplitButton;
            PushButton pushButton = splitButton.AddPushButton(new PushButtonData("WallPush", "Wall", curAssembly, "Revit.SDK.Samples.Ribbon.CS.CreateWall"));
            pushButton.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "CreateWall.png"), UriKind.Absolute));
            pushButton.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "CreateWall-S.png"), UriKind.Absolute));
            pushButton.ToolTip = "Creates a partition wall in the building model.";
            pushButton.ToolTipImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "CreateWallTooltip.bmp"), UriKind.Absolute));
            pushButton = splitButton.AddPushButton(new PushButtonData("StrWallPush", "Structure Wall", curAssembly, "Revit.SDK.Samples.Ribbon.CS.CreateStructureWall"));
            pushButton.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "StrcturalWall.png"), UriKind.Absolute));
            pushButton.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "StrcturalWall-S.png"), UriKind.Absolute));
            #endregion

            ribbonSamplePanel.AddSeparator();

            #region Add a StackedButton which is consisted of one PushButton and two Comboboxes
            PushButtonData pushButtonData = new PushButtonData("Reset", "Reset", curAssembly, "Revit.SDK.Samples.Ribbon.CS.ResetSetting");
            ComboBoxData comboBoxDataLevel = new ComboBoxData("LevelsSelector");
            ComboBoxData comboBoxDataShape = new ComboBoxData("WallShapeComboBox");
            IList<RibbonItem> ribbonItemsStacked = ribbonSamplePanel.AddStackedItems(pushButtonData, comboBoxDataLevel, comboBoxDataShape);
            ((PushButton)(ribbonItemsStacked[0])).Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "Reset.png"), UriKind.Absolute));
            //Add options to WallShapeComboBox
            Autodesk.Revit.UI.ComboBox comboboxWallShape = (Autodesk.Revit.UI.ComboBox)(ribbonItemsStacked[2]);
            ComboBoxMemberData comboBoxMemberData = new ComboBoxMemberData("RectangleWall", "RectangleWall");
            ComboBoxMember comboboxMember = comboboxWallShape.AddItem(comboBoxMemberData);
            comboboxMember.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "RectangleWall.png"), UriKind.Absolute));
            comboBoxMemberData = new ComboBoxMemberData("CircleWall", "CircleWall");
            comboboxMember = comboboxWallShape.AddItem(comboBoxMemberData);
            comboboxMember.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "CircleWall.png"), UriKind.Absolute));
            comboBoxMemberData = new ComboBoxMemberData("TriangleWall", "TriangleWall");
            comboboxMember = comboboxWallShape.AddItem(comboBoxMemberData);
            comboboxMember.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "TriangleWall.png"), UriKind.Absolute));
            comboBoxMemberData = new ComboBoxMemberData("SquareWall", "SquareWall");
            comboboxMember = comboboxWallShape.AddItem(comboBoxMemberData);
            comboboxMember.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "SquareWall.png"), UriKind.Absolute));
            #endregion

            ribbonSamplePanel.AddSeparator();

            #region Add a RadioButtonGroup for user to select WallType
            RadioButtonGroupData radioButtonGroupData = new RadioButtonGroupData("WallTypeSelector");
            RadioButtonGroup radioButtonGroup = (RadioButtonGroup)(ribbonSamplePanel.AddItem(radioButtonGroupData));
            ToggleButton toggleButton = radioButtonGroup.AddItem(new ToggleButtonData("Generic8", "Generic - 8\"", curAssembly, "Revit.SDK.Samples.Ribbon.CS.Dummy"));
            toggleButton.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "Generic8.png"), UriKind.Absolute));
            toggleButton.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "Generic8-S.png"), UriKind.Absolute));
            toggleButton = radioButtonGroup.AddItem(new ToggleButtonData("ExteriorBrick", "Exterior - Brick", curAssembly, "Revit.SDK.Samples.Ribbon.CS.Dummy"));
            toggleButton.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "ExteriorBrick.png"), UriKind.Absolute));
            toggleButton.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "ExteriorBrick-S.png"), UriKind.Absolute));
            #endregion

            //slide-out panel:
            ribbonSamplePanel.AddSlideOut();

            #region add a Text box to set the mark for new wall
            TextBoxData testBoxData = new TextBoxData("WallMark");
            Autodesk.Revit.UI.TextBox textBox = (Autodesk.Revit.UI.TextBox)(ribbonSamplePanel.AddItem(testBoxData));
            textBox.Value = "new wall"; //default wall mark
            textBox.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "WallMark.png"), UriKind.Absolute));
            textBox.ToolTip = "Set the mark for new wall";
            textBox.ShowImageAsButton = true;
            textBox.EnterPressed += new EventHandler<Autodesk.Revit.UI.Events.TextBoxEnterPressedEventArgs>(SetTextBoxValue);
            #endregion

            ribbonSamplePanel.AddSeparator();

            #region Create a StackedButton which consisted of a PushButton (delete all the walls) and a PulldownButton (move all the walls in X or Y direction)
            PushButtonData deleteWallsButtonData = new PushButtonData("deleteWalls", "Delete Walls", curAssembly, "Revit.SDK.Samples.Ribbon.CS.DeleteWalls");
            deleteWallsButtonData.ToolTip = "Delete all the walls created by the Create Wall tool.";
            deleteWallsButtonData.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "DeleteWalls.png"), UriKind.Absolute));

            PulldownButtonData moveWallsButtonData = new PulldownButtonData("moveWalls", "Move Walls");
            moveWallsButtonData.ToolTip = "Move all the walls in X or Y direction";
            moveWallsButtonData.Image = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "MoveWalls.png"), UriKind.Absolute));

            // create stackable buttons
            IList<RibbonItem> ribbonItems = ribbonSamplePanel.AddStackedItems(deleteWallsButtonData, moveWallsButtonData);

            // add two push buttons as sub-items of the moveWalls PulldownButton. 
            PulldownButton moveWallItem = ribbonItems[1] as PulldownButton;

            PushButton moveX = moveWallItem.AddPushButton(new PushButtonData("XDirection", "X Direction", curAssembly, "Revit.SDK.Samples.Ribbon.CS.XMoveWalls"));
            moveX.ToolTip = "move all walls 10 feet in X direction.";
            moveX.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "MoveWallsXLarge.png"), UriKind.Absolute));

            PushButton moveY = moveWallItem.AddPushButton(new PushButtonData("YDirection", "Y Direction", curAssembly, "Revit.SDK.Samples.Ribbon.CS.YMoveWalls"));
            moveY.ToolTip = "move all walls 10 feet in Y direction.";
            moveY.LargeImage = new BitmapImage(new Uri(Path.Combine(ButtonIconsFolder_Test, "MoveWallsYLarge.png"), UriKind.Absolute));
            #endregion

            ribbonSamplePanel.AddSeparator();

           // application.ControlledApplication.DocumentCreated += new EventHandler<Autodesk.Revit.DB.Events.DocumentCreatedEventArgs>(DocumentCreated);

           

            return Result.Succeeded;   
        }
        public void SetTextBoxValue(object sender, TextBoxEnterPressedEventArgs args)
        {
            TaskDialog.Show("TextBox EnterPressed Event", "New wall's mark changed.");
        }
    }
}
