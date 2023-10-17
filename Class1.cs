using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;

namespace PluginForRevit2022
{
    [Transaction(TransactionMode.Manual)]
    [Regeneration(RegenerationOption.Manual)]
    internal class Class1 : IExternalCommand
    {

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument.Document;

            FilteredElementCollector parametrsFilter = new FilteredElementCollector(doc);
            //ICollection<Element> allParametrs = parametrsFilter


            //Tuple<List<Room>, List<Level>> result = WorkClass.GetAllRooms(doc);

            //List<Room> allRooms = result.Item1;
            //List<Level> allRoomLevel = result.Item2;

            ////Передаем все помещения и уровни в класс для отображения
            //UserWindRoom userWind = new UserWindRoom(allRooms, allRoomLevel, doc);
            //userWind.ShowDialog();

            return Result.Succeeded;
        }
    }
}
