using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using Autodesk.Revit.UI.Selection;
using PluginForRevit2022.Core;
using View = Autodesk.Revit.DB.View;

namespace PluginForRevit2022.UI
{
    internal sealed class ManualMarkingWindow : Window
    {
        private sealed class Choice
        {
            public ElementId Id { get; set; }
            public string UniqueId { get; set; }
            public string Name { get; set; }
            public string Level { get; set; }
        }

        private sealed class SettingsSnapshot
        {
            private readonly string _profileName, _profilePath, _floorParameter;
            private readonly bool _functionalZones;
            private readonly List<(string Name, string Bco)> _families;

            public SettingsSnapshot(string system)
            {
                ProfileEntry profile = MarkingSettings.Current();
                _profileName = profile.Name;
                _profilePath = profile.FilePath;
                _floorParameter = MarkingSettings.CurrentFloorParameter();
                _functionalZones = system == "souye" && MarkingSettings.CurrentJournal().SouyeFunctionalZones;
                _families = MarkingSettings.Load().Families.Where(family => family.InSystem(system))
                    .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(family => (family.Name, family.Bco?.Trim() ?? ""))
                    .ToList();
            }

            public bool Matches(SettingsSnapshot other) =>
                string.Equals(_profileName, other._profileName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_profilePath, other._profilePath, StringComparison.OrdinalIgnoreCase) &&
                _floorParameter == other._floorParameter && _functionalZones == other._functionalZones &&
                _families.SequenceEqual(other._families);
        }

        private static ManualMarkingWindow _current;
        private readonly UIApplication _app;
        private readonly Document _document;
        private readonly string _documentTitle;
        private readonly string _controllerUniqueId;
        private readonly MarkDevicesRequest _request;
        private readonly ISelectionFilter _filter;
        private readonly SettingsSnapshot _settings;
        private readonly RevitActionQueue _actions;
        private readonly List<Choice> _choices = new List<Choice>();
        private readonly HashSet<ElementId> _chosenIds = new HashSet<ElementId>();
        private HashSet<ElementId> _previousSelection;
        private ElementId _viewId;
        private bool _active = true, _busy, _closed, _batchRecorded, _completed;

        private readonly TextBlock _summary = new TextBlock { FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly ListBox _order = new ListBox { MaxHeight = 170 };
        private readonly Button _mark, _cancel, _exact, _show, _floors, _up, _down, _remove, _clear, _addCurrent;

        private ManualMarkingWindow(UIApplication app, MarkDevicesRequest request, ISelectionFilter filter, string systemTitle)
        {
            _app = app;
            UIDocument ui = app.ActiveUIDocument;
            _document = ui.Document;
            _documentTitle = _document.Title;
            Element controller = request.ControllerElementId.HasValue
                ? _document.GetElement(RevitIds.ToElementId(request.ControllerElementId.Value)) : null;
            if (controller == null) throw new InvalidOperationException("Выбранный прибор недоступен. Выберите прибор заново.");
            _controllerUniqueId = controller.UniqueId;
            _request = request;
            _filter = filter;
            _settings = new SettingsSnapshot(request.System);
            _viewId = ui.ActiveView.Id;
            _previousSelection = new HashSet<ElementId>(ui.Selection.GetElementIds());
            _actions = new RevitActionQueue();

            Title = $"Ручная маркировка {systemTitle}";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            new WindowInteropHelper(this).Owner = app.MainWindowHandle;
            AerBimAppearance.Apply(this);

            var panel = new StackPanel { Margin = new Thickness(14) };
            string location = string.IsNullOrWhiteSpace(request.Building) || request.System == "security"
                ? "" : $"{(request.FunctionalZones ? "Зона" : "Корпус")} {request.Building} · ";
            panel.Children.Add(new TextBlock
            {
                Text = $"{location}Прибор {request.Controller} · линия {request.Line}",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6),
            });
            panel.Children.Add(_summary);
            _status.Margin = new Thickness(0, 4, 0, 8);
            panel.Children.Add(_status);

            var selection = new WrapPanel();
            _exact = AddButton(selection, "Точные клики", () => Run(PickExact));
            _show = AddButton(selection, "Выделить накопленное на виде", () => Run(ShowOnView));
            _floors = AddButton(selection, "Этажи уровней без номера…", () => Run(_ =>
            {
                ValidateSettings();
                new LevelFloorsWindow(_document) { Owner = this }.ShowDialog();
                SetReadyStatus();
            }));
            panel.Children.Add(selection);

            var details = new StackPanel();
            details.Children.Add(AerBimAppearance.Label("Порядок нумерации", _order));
            details.Children.Add(_order);
            var edit = new WrapPanel();
            _addCurrent = AddButton(edit, "Добавить текущее выделение", () => Run(activeUi =>
            {
                AddSelected(activeUi.ActiveView, activeUi.Selection.GetElementIds());
                ResetSelection(activeUi);
            }));
            _up = AddButton(edit, "Выше", () => Move(-1));
            _down = AddButton(edit, "Ниже", () => Move(1));
            _remove = AddButton(edit, "Исключить", Remove);
            _clear = AddButton(edit, "Очистить", Clear);
            details.Children.Add(edit);
            panel.Children.Add(new Expander { Header = "Устройства и порядок", Content = details, Margin = new Thickness(0, 8, 0, 8) });

            var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
            _cancel = AddButton(footer, "Отмена", Close);
            _mark = AddButton(footer, "Промаркировать выбранное", () =>
            {
                if (_completed) Close();
                else Run(Apply);
            });
            AerBimAppearance.Primary(this, _mark);
            panel.Children.Add(footer);
            Content = panel;

            _order.SelectionChanged += (sender, args) => UpdateButtons();
            PreviewKeyDown += (sender, args) =>
            {
                if (args.Key != Key.Escape) return;
                args.Handled = true;
                Close();
            };
            Refresh();
            SetReadyStatus();
        }

        internal static bool ActivateExisting()
        {
            if (_current == null) return false;
            if (_current._completed || !_current._document.IsValidObject)
            {
                _current.Close();
                return false;
            }
            _current.Activate();
            return true;
        }

        internal static void Open(UIApplication app, MarkDevicesRequest request, ISelectionFilter filter, string systemTitle)
        {
            if (ActivateExisting()) return;
            var window = new ManualMarkingWindow(app, request, filter, systemTitle);
            _current = window;
            try
            {
                app.ViewActivated += window.ViewActivated;
                app.Application.DocumentClosed += window.DocumentClosed;
#if REVIT2023 || REVIT2024
                app.SelectionChanged += window.SelectionChanged;
#else
                app.Idling += window.Idling;
#endif
                window.Show();
            }
            catch
            {
                _current = null;
                window.Unsubscribe();
                window._actions.Close();
                throw;
            }
        }

        protected override void OnClosed(EventArgs args)
        {
            _closed = true;
            if (_current == this) _current = null;
            // Подписки Revit снимаем в контексте API, включая обычное закрытие окна по Esc.
            _actions.Run(_ => DisposeSession(), _ => DisposeSession());
            base.OnClosed(args);
        }

        private void DisposeSession()
        {
            try { Unsubscribe(); }
            catch (Exception ex) { PluginLog.Warn("marking.selection", ex.Message); }
            finally { _actions.Close(); }
        }

        private void Unsubscribe()
        {
            _app.ViewActivated -= ViewActivated;
            _app.Application.DocumentClosed -= DocumentClosed;
#if REVIT2023 || REVIT2024
            _app.SelectionChanged -= SelectionChanged;
#else
            _app.Idling -= Idling;
#endif
        }

        private bool IsOriginal(UIDocument ui) => ui != null && _document.IsValidObject && _document.Equals(ui.Document);

        private void DocumentClosed(object sender, DocumentClosedEventArgs args)
        {
            if (!_closed && !_document.IsValidObject)
                Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Close(); }));
        }

        private void ViewActivated(object sender, ViewActivatedEventArgs args)
        {
            if (_closed) return;
            UIDocument ui = _app.ActiveUIDocument;
            _active = IsOriginal(ui);
            if (_active) ResetSelection(ui);
            SetReadyStatus();
            UpdateButtons();
        }

#if REVIT2023 || REVIT2024
        private void SelectionChanged(object sender, Autodesk.Revit.UI.Events.SelectionChangedEventArgs args)
        {
            if (_closed || _completed || _busy || !_active) return;
            ObserveSelection(_app.ActiveUIDocument, args.GetSelectedElements());
        }
#else
        private void Idling(object sender, IdlingEventArgs args)
        {
            if (_closed || _completed || _busy || !_active) return;
            UIDocument ui = _app.ActiveUIDocument;
            if (IsOriginal(ui)) ObserveSelection(ui, ui.Selection.GetElementIds());
        }
#endif

        private void ObserveSelection(UIDocument ui, IEnumerable<ElementId> selected)
        {
            if (_closed || _completed || _busy || !_active || !IsOriginal(ui)) return;
            View view = ui.ActiveView;
            if (view.Id != _viewId)
            {
                ResetSelection(ui);
                return;
            }

            CaptureChanges(view, selected);
        }

        private void CaptureChanges(View view, IEnumerable<ElementId> selected)
        {
            var current = new HashSet<ElementId>(selected);
            if (current.SetEquals(_previousSelection)) return;
            IEnumerable<ElementId> added = current.Except(_previousSelection);
            _previousSelection = current;
            AddSelected(view, added);
        }

        private void AddSelected(View view, IEnumerable<ElementId> ids)
        {
            List<Element> added = ids.Where(id => !_chosenIds.Contains(id))
                .Select(id => _document.GetElement(id)).Where(_filter.AllowElement).ToList();
            if (added.Count == 0) return;

            if (added.Count > 1)
            {
                _batchRecorded = true;
                added = added.Select(element => (element, point: SouyeMarkingSelection.PointOnView(element, view)))
                    .OrderByDescending(item => item.point.DotProduct(view.UpDirection))
                    .ThenBy(item => item.point.DotProduct(view.RightDirection))
                    .ThenBy(item => RevitIds.IdOf(item.element.Id))
                    .Select(item => item.element).ToList();
            }
            foreach (Element element in added) AddChoice(element);
            Refresh(_choices.Count - 1);
            SetReadyStatus();
        }

        private void ResetSelection(UIDocument ui)
        {
            _viewId = ui.ActiveView.Id;
            _previousSelection = new HashSet<ElementId>(ui.Selection.GetElementIds());
        }

        private void AddChoice(Element element)
        {
            if (!_chosenIds.Add(element.Id)) return;
            _choices.Add(new Choice
            {
                Id = element.Id,
                UniqueId = element.UniqueId,
                Name = element.Name,
                Level = DeviceMarkingOperations.InstanceLevel(_document, (FamilyInstance)element)?.Name,
            });
        }

        private void PickExact(UIDocument ui)
        {
            _status.Text = "Указывайте устройства по порядку. Esc завершает точные клики на этом виде.";
            var visible = new HashSet<ElementId>(VisibleChoiceIds(ui.ActiveView));
            if (visible.Count > 0) ui.Selection.SetElementIds(visible);
            try
            {
                while (!_closed)
                {
                    Reference picked = ui.Selection.PickObject(ObjectType.Element, _filter,
                        $"Следующий номер: {(long)_request.StartNumber + _choices.Count}. Esc — закончить выбор на этом виде");
                    if (_closed) return;
                    AddChoice(_document.GetElement(picked.ElementId));
                    if (visible.Add(picked.ElementId)) ui.Selection.SetElementIds(visible);
                    Refresh(_choices.Count - 1);
                }
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException) { }
            finally { if (!_closed && IsOriginal(ui)) ResetSelection(ui); }
            if (!_closed) SetReadyStatus();
        }

        private List<ElementId> VisibleChoiceIds(View view)
        {
            if (_chosenIds.Count == 0 || !FilteredElementCollector.IsViewValidForElementIteration(_document, view.Id))
                return new List<ElementId>();
            return new FilteredElementCollector(_document, view.Id).WherePasses(new ElementIdSetFilter(_chosenIds)).ToElementIds().ToList();
        }

        private void ShowOnView(UIDocument ui)
        {
            List<ElementId> visible = VisibleChoiceIds(ui.ActiveView);
            if (visible.Count == 0)
            {
                _status.Text = "На текущем виде нет накопленных устройств.";
                return;
            }
            ui.Selection.SetElementIds(visible);
            ResetSelection(ui);
            _status.Text = $"Выделено на текущем виде: {visible.Count}. Все устройства сохранены в очереди.";
        }

        private void Apply(UIDocument ui)
        {
            // Последнее изменение выбора могло произойти до ближайшего Idling.
            if (ui.ActiveView.Id == _viewId) CaptureChanges(ui.ActiveView, ui.Selection.GetElementIds());
            _request.Ids = _choices.Select(choice => RevitIds.IdOf(choice.Id)).ToList();
            ValidateSnapshot(ui);
            _request.DryRun = true;
            Dictionary<string, object> preview = DeviceMarkingOperations.Mark(_document, _request);
            if (ShowFailures(preview)) return;

            var warnings = (List<string>)preview["warnings"];
            var changes = preview["controller"] is Dictionary<string, object> controller
                ? (List<Dictionary<string, object>>)controller["changes"]
                : new List<Dictionary<string, object>>();
            var devices = (List<Dictionary<string, object>>)preview["devices"];
            bool hasExistingMarking = devices.Any(device =>
                !string.IsNullOrWhiteSpace(RevitText.ValueText(RevitText.FindParameter(
                    _document.GetElement(RevitIds.ToElementId((long)device["id"])), DeviceMarkingOperations.ParamMark))));

            if (warnings.Count > 0 || changes.Count > 0 || hasExistingMarking)
            {
                var lines = new List<string> { $"Устройств к маркировке: {devices.Count} из {_choices.Count}." };
                if (hasExistingMarking) lines.Add("У некоторых выбранных устройств уже есть маркировка.");
                if (changes.Count > 0)
                {
                    lines.Add("Будут изменены параметры прибора.");
                    lines.AddRange(changes.Select(change => $"{change["parameter"]}: {change["old"]} → {change["new"]}"));
                }
                lines.AddRange(warnings);
                var dialog = new TaskDialog("Маркировка устройств")
                {
                    MainInstruction = "Применить маркировку?",
                    MainContent = string.Join("\n", lines),
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No,
                };
                if (dialog.Show() != TaskDialogResult.Yes)
                {
                    _status.Text = "Маркировка отменена. Выбор сохранён.";
                    return;
                }
            }

            if (_closed) return;
            ValidateSnapshot(_app.ActiveUIDocument);
            _request.DryRun = false;
            Dictionary<string, object> result = DeviceMarkingOperations.Mark(_document, _request);
            if (!(bool)result["applied"])
            {
                if (!ShowFailures(result)) _status.Text = "Revit не применил маркировку. Проверьте состояние модели и повторите.";
                return;
            }
            _completed = true;
            int count = ((List<Dictionary<string, object>>)result["devices"]).Count;
            _status.Text = $"Промаркировано устройств: {count}. Отменить запись можно в Revit через Ctrl+Z.";
            _mark.Content = "Закрыть";
            _cancel.Visibility = System.Windows.Visibility.Collapsed;
        }

        private void ValidateSettings()
        {
            if (!_settings.Matches(new SettingsSnapshot(_request.System)))
                throw new InvalidOperationException("Профиль или настройки маркировки изменились. Закройте окно и запустите маркировку заново.");
        }

        private void ValidateSnapshot(UIDocument ui)
        {
            if (!IsOriginal(ui)) throw new InvalidOperationException("Вернитесь в исходную модель для маркировки.");
            ValidateSettings();
            if (_document.GetElement(_controllerUniqueId) == null)
                throw new InvalidOperationException("Выбранный прибор удалён. Откройте маркировку заново.");
            Choice invalid = _choices.FirstOrDefault(choice => _document.GetElement(choice.Id)?.UniqueId != choice.UniqueId);
            if (invalid != null)
                throw new InvalidOperationException($"Устройство «{invalid.Name}» (ID {RevitIds.IdOf(invalid.Id)}) удалено. Исключите его из списка.");
        }

        private bool ShowFailures(Dictionary<string, object> result)
        {
            var failures = (List<string>)result["failures"];
            if (failures.Count == 0) return false;
            _status.Text = (string)result["summary"];
            TaskDialog.Show("Маркировка", result["summary"] + "\n\n" + string.Join("\n", failures.Take(15)));
            return true;
        }

        private void Run(Action<UIDocument> action)
        {
            if (_busy || !_active || _closed || _completed) return;
            _busy = true;
            UpdateButtons();
            _actions.Run(app =>
            {
                if (_closed) return;
                try
                {
                    UIDocument ui = app.ActiveUIDocument;
                    if (!IsOriginal(ui)) throw new InvalidOperationException("Вернитесь в исходную модель для маркировки.");
                    action(ui);
                }
                finally
                {
                    _busy = false;
                    if (!_closed) UpdateButtons();
                }
            }, error =>
            {
                if (_closed) return;
                _busy = false;
                _status.Text = error;
                UpdateButtons();
            });
        }

        private void Move(int delta)
        {
            int index = _order.SelectedIndex, target = index + delta;
            if (index < 0 || target < 0 || target >= _choices.Count) return;
            Choice choice = _choices[index];
            _choices.RemoveAt(index);
            _choices.Insert(target, choice);
            Refresh(target);
        }

        private void Remove()
        {
            int index = _order.SelectedIndex;
            if (index < 0) return;
            _chosenIds.Remove(_choices[index].Id);
            _choices.RemoveAt(index);
            Refresh(Math.Min(index, _choices.Count - 1));
            SetReadyStatus();
        }

        private void Clear()
        {
            _choices.Clear();
            _chosenIds.Clear();
            _batchRecorded = false;
            Refresh();
            SetReadyStatus();
        }

        private void SetReadyStatus()
        {
            if (_completed) return;
            _status.Text = !_active ? $"Выбор приостановлен. Вернитесь в модель «{_documentTitle}»."
                : _batchRecorded ? "Группы из рамки или быстрых кликов добавлены сверху вниз, затем слева направо. Порядок можно изменить."
                : "Добавляйте устройства на любом открытом виде кликом, Ctrl или рамкой.";
        }

        private void Refresh(int selected = -1)
        {
            int levels = _choices.Select(choice => choice.Level).Where(level => level != null).Distinct().Count();
            _summary.Text = $"Выбрано: {_choices.Count} · уровней: {levels} · следующий номер: {(long)_request.StartNumber + _choices.Count}";
            _order.ItemsSource = _choices.Select((choice, index) =>
                $"{(long)_request.StartNumber + index}. {choice.Name} · {choice.Level ?? "уровень не определён"} · ID {RevitIds.IdOf(choice.Id)}").ToList();
            _order.SelectedIndex = selected;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool editable = !_busy && !_completed;
            bool ready = _active && editable;
            _show.IsEnabled = ready && _choices.Count > 0;
            _mark.IsEnabled = _completed || _show.IsEnabled;
            _exact.IsEnabled = _floors.IsEnabled = _addCurrent.IsEnabled = ready;
            _clear.IsEnabled = editable && _choices.Count > 0;
            _remove.IsEnabled = editable && _order.SelectedIndex >= 0;
            _up.IsEnabled = editable && _order.SelectedIndex > 0;
            _down.IsEnabled = editable && _order.SelectedIndex >= 0 && _order.SelectedIndex < _choices.Count - 1;
        }

        private static Button AddButton(System.Windows.Controls.Panel panel, string text, Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 2, 6, 2) };
            button.Click += (sender, args) => action();
            panel.Children.Add(button);
            return button;
        }
    }
}
