using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using PluginForRevit2022.Core;
using PluginForRevit2022.UI;
using ComboBox = System.Windows.Controls.ComboBox;
using View = Autodesk.Revit.DB.View;

namespace PluginForRevit2022
{
    internal static class SouyeMarkingSelection
    {
        private enum Action { Clicks, Rectangle, Finish }

        private sealed class Step
        {
            public Action Action;
            public View View;
        }

        internal static bool ChooseNumber(IntPtr owner, MarkDevicesRequest request, FamilyInstance rack)
        {
            string raw = SouyeLineIdentity.Read(rack, DeviceMarkingOperations.ParamController);
            if (!SouyeLineIdentity.TryDeviceNumbers(raw, out var numbers))
                throw new InvalidOperationException($"«ARBM_Номер прибора» стойки: «{raw}». Укажите различные положительные номера через запятую.");

            bool AcceptNumber(int number)
            {
                if (SouyeLineIdentity.Number(request.Line, out int line))
                    request.FunctionalZones |= DeviceMarkingOperations.HasLinkedSouyeLine(rack.Document, rack, number, line);

                if (!request.FunctionalZones)
                {
                    if (string.IsNullOrWhiteSpace(request.Building))
                        request.Building = SouyeLineIdentity.Read(rack, DeviceMarkingOperations.ParamBuilding);
                    if (string.IsNullOrWhiteSpace(request.Building))
                        throw new InvalidOperationException("Укажите корпус в стойке или в окне маркировки.");
                }
                request.Building = SouyeLineIdentity.NormalizeBuilding(request.Building);
                request.Controller = number.ToString();
                return true;
            }

            if (numbers.Count == 0)
            {
                if (!SouyeLineIdentity.Number(request.Controller, out int entered))
                    throw new InvalidOperationException("У стойки не задан «ARBM_Номер прибора». Укажите номер в стойке или в окне маркировки.");
                return AcceptNumber(entered);
            }
            if (numbers.Count == 1)
                return AcceptNumber(numbers[0]);

            var window = NewWindow("Выбрать прибор СОУЭ", owner, 390);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Номер прибора в выбранной стойке", Margin = new Thickness(0, 0, 0, 6) });
            var box = new ComboBox { ItemsSource = numbers, MinWidth = 100 };
            box.SelectedItem = int.TryParse(request.Controller, out int initial) && numbers.Contains(initial) ? initial : numbers[0];
            panel.Children.Add(box);
            var actions = Buttons();
            actions.Children.Add(Button("Отмена", () => window.DialogResult = false, isCancel: true));
            var accept = Button("Выбрать", () => window.DialogResult = true, isDefault: true);
            AerBimAppearance.Primary(window, accept);
            actions.Children.Add(accept);
            panel.Children.Add(actions);
            window.Content = panel;
            if (window.ShowDialog() != true) return false;
            return AcceptNumber((int)box.SelectedItem);
        }

        internal static List<long> Pick(UIDocument ui, IntPtr owner, ISelectionFilter filter, MarkDevicesRequest request)
        {
            Document doc = ui.Document;
            View initial = ui.ActiveView;
            var views = new FilteredElementCollector(doc).OfClass(typeof(ViewPlan)).Cast<ViewPlan>()
                .Where(view => !view.IsTemplate && view.ViewType == ViewType.FloorPlan && view.Id != initial.Id)
                .OrderBy(view => view.GenLevel?.ProjectElevation ?? 0).ThenBy(view => view.Name)
                .Cast<View>().Prepend(initial).ToList();
            var original = ui.Selection.GetElementIds().ToList();
            var selected = new List<ElementId>();
            var seen = new HashSet<ElementId>();

            bool showOrder = false;
            bool finished = false;
            try
            {
                Step step = Ask(doc, owner, views, initial, selected, request, showOrder);
                if (step != null) ui.Selection.SetElementIds(new List<ElementId>());
                while (step != null && step.Action != Action.Finish)
                {
                    View target = step.View;
                    using (ViewSession.Open(doc, target))
                    {
                        if (ui.ActiveView.Id != target.Id)
                            throw new InvalidOperationException($"Не удалось открыть вид «{target.Name}» для выбора устройств.");
                        using (var paint = new SelectionPaint(ui))
                        {
                            paint.Show(selected, visibleOnly: true);
                            try
                            {
                                if (step.Action == Action.Rectangle)
                                {
                                    var elements = ui.Selection.PickElementsByRectangle(filter, "Выделите устройства рамкой");
                                    var added = elements.Select(element => (element, point: PointOnView(element, target)))
                                        .OrderByDescending(item => item.point.DotProduct(target.UpDirection))
                                        .ThenBy(item => item.point.DotProduct(target.RightDirection))
                                        .ThenBy(item => RevitIds.IdOf(item.element.Id))
                                        .Select(item => item.element.Id).Where(seen.Add).ToList();
                                    selected.AddRange(added);
                                    paint.Show(added);
                                    showOrder |= request.Order != "route" && added.Count > 0;
                                }
                                else
                                {
                                    while (true)
                                    {
                                        string prompt = request.Order == "route"
                                            ? $"Выбрано: {selected.Count}. Esc — закончить выбор на этом виде"
                                            : $"Следующий номер: {(long)request.StartNumber + selected.Count}. Esc — закончить выбор на этом виде";
                                        ElementId id = ui.Selection.PickObject(ObjectType.Element, filter, prompt).ElementId;
                                        if (!seen.Add(id)) continue;
                                        selected.Add(id);
                                        paint.Show(new[] { id });
                                    }
                                }
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                // Выход из штатного выбора возвращает к списку без записи маркировки.
                            }
                            step = Ask(doc, owner, views, target, selected, request, showOrder);
                        }
                    }
                }
                finished = step != null;
                return finished ? selected.Select(RevitIds.IdOf).ToList() : new List<long>();
            }
            finally
            {
                ui.Selection.SetElementIds(finished ? selected : original);
                ui.RefreshActiveView();
            }
        }

        internal static XYZ PointOnView(Element element, View view)
        {
            if (element.Location is LocationPoint point) return point.Point;
            BoundingBoxXYZ box = element.get_BoundingBox(view);
            return box == null ? XYZ.Zero : box.Transform.OfPoint((box.Min + box.Max) / 2);
        }

        private static Step Ask(Document doc, IntPtr owner, List<View> views, View current, List<ElementId> selected,
            MarkDevicesRequest request, bool showOrder)
        {
            var window = NewWindow(request.System == "souye" ? "Выбор оповещателей СОУЭ" : "Выбор устройств линии", owner, 590);
            var panel = new StackPanel { Margin = new Thickness(12) };
            string next = request.Order == "route" ? "" : $" Следующий номер: {(long)request.StartNumber + selected.Count}.";
            string location = string.IsNullOrWhiteSpace(request.Building) || request.System == "security" ? "" :
                (request.FunctionalZones ? $"Зона {request.Building}, " : $"Корпус {request.Building}, ");
            panel.Children.Add(new TextBlock
            {
                Text = $"{location}прибор {request.Controller}, линия {request.Line}. Выбрано: {selected.Count}.{next}",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
            });

            var box = new ComboBox
            {
                ItemsSource = views.Select(view => view.Name).ToList(),
                SelectedIndex = Math.Max(0, views.FindIndex(view => view.Id == current.Id)),
            };
            panel.Children.Add(AerBimAppearance.Label("Вид для следующего выбора", box));
            panel.Children.Add(box);

            Step result = null;
            void Choose(Action action)
            {
                result = new Step { Action = action, View = views[box.SelectedIndex] };
                window.DialogResult = true;
            }

            var selection = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            selection.Children.Add(Button("Выбрать кликами", () => Choose(Action.Clicks)));
            selection.Children.Add(Button("Выбрать рамкой", () => Choose(Action.Rectangle)));
            selection.Children.Add(Button("Этажи уровней без номера…", () =>
            {
                var dialog = new LevelFloorsWindow(doc) { Owner = window };
                dialog.ShowDialog();
            }));
            panel.Children.Add(selection);
            if (showOrder) AddOrderList(panel, doc, selected, request.StartNumber);

            var actions = Buttons();
            actions.Children.Add(Button("Отмена", () => window.DialogResult = false, isCancel: true));
            var finish = Button("К предпросмотру", () => Choose(Action.Finish));
            finish.IsEnabled = selected.Count > 0;
            AerBimAppearance.Primary(window, finish);
            actions.Children.Add(finish);
            panel.Children.Add(actions);
            window.Content = panel;
            return window.ShowDialog() == true ? result : null;
        }

        private static void AddOrderList(StackPanel panel, Document doc, List<ElementId> selected, int start)
        {
            var list = new ListBox { MaxHeight = 148 };
            panel.Children.Add(AerBimAppearance.Label("Порядок нумерации", list));
            panel.Children.Add(new TextBlock
            {
                Text = "Рамка добавляет устройства сверху вниз, при совпадении — слева направо. При необходимости измените порядок.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            });
            panel.Children.Add(list);

            void Refresh(int selectedIndex)
            {
                list.ItemsSource = selected.Select((id, index) =>
                    $"{(long)start + index}. {doc.GetElement(id)?.Name} (ID {RevitIds.IdOf(id)})").ToList();
                list.SelectedIndex = selectedIndex;
                if (list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
            }

            void Move(int shift)
            {
                int source = list.SelectedIndex;
                int target = source + shift;
                if (source < 0 || target < 0 || target >= selected.Count) return;
                ElementId id = selected[source];
                selected.RemoveAt(source);
                selected.Insert(target, id);
                Refresh(target);
            }

            var buttons = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var up = Button("Выше", () => Move(-1));
            var down = Button("Ниже", () => Move(1));
            list.SelectionChanged += (sender, args) =>
            {
                up.IsEnabled = list.SelectedIndex > 0;
                down.IsEnabled = list.SelectedIndex >= 0 && list.SelectedIndex < selected.Count - 1;
            };
            buttons.Children.Add(up);
            buttons.Children.Add(down);
            panel.Children.Add(buttons);
            Refresh(selected.Count > 0 ? selected.Count - 1 : -1);
        }

        private static Window NewWindow(string title, IntPtr owner, double width)
        {
            var window = new Window
            {
                Title = title, Width = width, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            new WindowInteropHelper(window).Owner = owner;
            AerBimAppearance.Apply(window);
            return window;
        }

        private static WrapPanel Buttons() =>
            new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };

        private static Button Button(string text, System.Action action, bool isCancel = false, bool isDefault = false)
        {
            var button = new Button
            {
                Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 6, 4),
                IsCancel = isCancel, IsDefault = isDefault,
            };
            button.Click += (sender, args) => action();
            return button;
        }

        private sealed class SelectionPaint : IDisposable
        {
            private readonly UIDocument _ui;
            private readonly View _view;
            private readonly TransactionGroup _group;
            private readonly OverrideGraphicSettings _settings;
            private readonly List<ElementId> _visible = new List<ElementId>();
            private bool _canPaint = true;

            public SelectionPaint(UIDocument ui)
            {
                _ui = ui;
                _view = ui.ActiveView;
                _settings = CmdMarkDevices.PickedOverride(ui.Document);
                _group = new TransactionGroup(ui.Document, "AerBIM: временная подсветка устройств");
                try { _canPaint = _group.Start() == TransactionStatus.Started; }
                catch (Exception) { _canPaint = false; }
            }

            public void Show(ICollection<ElementId> ids, bool visibleOnly = false)
            {
                if (ids.Count == 0) return;
                var visible = visibleOnly && FilteredElementCollector.IsViewValidForElementIteration(_ui.Document, _view.Id)
                    ? new FilteredElementCollector(_ui.Document, _view.Id).WherePasses(new ElementIdSetFilter(ids)).ToElementIds()
                    : ids;
                _visible.AddRange(visible);
                if (_canPaint && visible.Count > 0)
                {
                    try
                    {
                        using (var transaction = new Transaction(_ui.Document, "Подсветка выбранных устройств"))
                        {
                            transaction.Start();
                            foreach (ElementId id in visible) _view.SetElementOverrides(id, _settings);
                            _canPaint = transaction.Commit() == TransactionStatus.Committed;
                        }
                    }
                    catch (Exception)
                    {
                        _canPaint = false;
                    }
                }
                _ui.Selection.SetElementIds(_visible);
                _ui.RefreshActiveView();
            }

            public void Dispose()
            {
                try
                {
                    if (_group.GetStatus() == TransactionStatus.Started) _group.RollBack();
                }
                finally { _group.Dispose(); }
            }
        }
    }
}
