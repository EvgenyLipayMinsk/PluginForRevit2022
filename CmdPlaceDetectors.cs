using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PluginForRevit2022.Core;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Control = System.Windows.Controls.Control;

namespace PluginForRevit2022
{
    /// <summary>Размещение извещателей в одном помещении кликом на плане.</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdPlaceDetectors : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            if (!(doc.ActiveView is ViewPlan plan) || plan.GenLevel == null)
            {
                TaskDialog.Show("Извещатели", "Откройте план этажа.");
                return Result.Cancelled;
            }

            List<string> families = DetectorPlacementOperations.PlaceableSymbols(doc).Select(s => $"{s.FamilyName} : {s.Name}").ToList();
            if (families.Count == 0)
            {
                TaskDialog.Show("Извещатели", "В проекте нет семейств категории «Пожарная сигнализация», размещаемых без основы.");
                return Result.Cancelled;
            }

            const string windowKey = "place-detectors";
            PlaceDetectorsRequest request = SessionWindows.Input<PlaceDetectorsRequest>(windowKey);
            if (request == null || !families.Contains(request.FamilyType))
            {
                var window = new PlaceDetectorsWindow(families);
                new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
                if (window.ShowDialog() != true)
                {
                    return Result.Cancelled;
                }
                request = window.Request;
                if (window.DontShowAgain) SessionWindows.Hide(windowKey, request);
            }
            request.View = RevitIds.IdOf(plan.Id).ToString();

            try
            {
                if (plan.SketchPlane == null)
                {
                    using (var transaction = new Transaction(doc, "AerBIM: рабочая плоскость"))
                    {
                        transaction.Start();
                        plan.SketchPlane = SketchPlane.Create(doc, Plane.CreateByNormalAndOrigin(XYZ.BasisZ, new XYZ(0, 0, plan.GenLevel.ProjectElevation)));
                        transaction.Commit();
                    }
                }
                request.Point = uiDoc.Selection.PickPoint(ObjectSnapTypes.None, "Кликните внутри помещения");
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }

            try
            {
                request.DryRun = true;
                Dictionary<string, object> preview = DetectorPlacementOperations.Place(doc, request);
                var warnings = (List<string>)preview["warnings"];
                string text = $"Помещение: {preview["room"]}\nИзвещателей: {preview["count"]}\nВысота потолка: {preview["maxHeightMm"]} мм\nРадиус зоны контроля: {preview["radiusM"]} м" +
                              (warnings.Count > 0 ? "\n\nВнимание:\n" + string.Join("\n", warnings) : "");
                if (TaskDialog.Show("Извещатели", text + "\n\nРазместить?", TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No) != TaskDialogResult.Yes)
                {
                    return Result.Cancelled;
                }
                request.DryRun = false;
                TaskDialog.Show("Извещатели", (string)DetectorPlacementOperations.Place(doc, request)["summary"]);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Извещатели", ex.Message);
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardPlaceDetectors : GuardedCommand<CmdPlaceDetectors> { }

    internal sealed class PlaceDetectorsWindow : Window
    {
        private readonly ComboBox _type = new ComboBox();
        private readonly ComboBox _family = new ComboBox();
        private readonly ComboBox _coverage = new ComboBox();
        private readonly TextBox _height = new TextBox();
        private readonly TextBox _offset = new TextBox { Text = "500" };

        private readonly System.Windows.Controls.CheckBox _dontShow = SessionWindows.NewCheckBox();

        public PlaceDetectorsRequest Request { get; private set; }

        public bool DontShowAgain => _dontShow.IsChecked == true;

        public PlaceDetectorsWindow(IEnumerable<string> families)
        {
            Title = "Размещение извещателей";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Fill(_type, ("Дымовой точечный (R до 6,4 м; до 12 м)", "smoke"), ("Тепловой точечный (R до 3,5 м; до 9 м)", "heat"));
            Fill(_coverage, ("Каждая точка: один адресный извещатель", 1), ("Каждая точка: два извещателя", 2));
            foreach (string family in families) _family.Items.Add(family);
            _family.SelectedIndex = 0;

            var panel = new StackPanel { Margin = new Thickness(12) };
            void Row(string label, Control control)
            {
                panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
                panel.Children.Add(control);
            }
            Row("Тип извещателя", _type);
            Row("Семейство", _family);
            Row("Контроль (СП 484, п. 6.6.1, 6.6.2)", _coverage);
            Row("Высота потолка от пола, мм (пусто — потолок из связей)", _height);
            Row("Отступ от стен, мм", _offset);

            var ok = new Button { Content = "Далее: кликнуть помещение", IsDefault = true, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(6) };
            ok.Click += (s, e) => Accept();
            panel.Children.Add(_dontShow);
            panel.Children.Add(ok);
            Content = panel;
        }

        private void Accept()
        {
            double? height = null;
            if (!string.IsNullOrWhiteSpace(_height.Text))
            {
                if (!TryNumber(_height.Text, out double h))
                {
                    MessageBox.Show(this, "Высота должна быть числом.", Title);
                    return;
                }
                height = h;
            }
            if (!TryNumber(_offset.Text, out double offset))
            {
                MessageBox.Show(this, "Отступ должен быть числом.", Title);
                return;
            }
            Request = new PlaceDetectorsRequest
            {
                Detector = (string)((ComboBoxItem)_type.SelectedItem).Tag,
                FamilyType = (string)_family.SelectedItem,
                Coverage = (int)((ComboBoxItem)_coverage.SelectedItem).Tag,
                CeilingHeightMm = height,
                WallOffsetMm = offset,
            };
            DialogResult = true;
        }

        private static bool TryNumber(string text, out double value) =>
            double.TryParse(text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        private static void Fill(ComboBox box, params (string text, object tag)[] items)
        {
            foreach (var (text, tag) in items) box.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            box.SelectedIndex = 0;
        }
    }
}
