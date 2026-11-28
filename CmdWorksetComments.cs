using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;
using Binding = System.Windows.Data.Binding;
using Grid = System.Windows.Controls.Grid;

namespace PluginForRevit2022
{
    /// <summary>Строка окна: рабочий набор и комментарий для его элементов. Публичный класс — на него привязана таблица.</summary>
    public sealed class WorksetCommentRow : INotifyPropertyChanged
    {
        private string _comment = "";
        internal WorksetId Id;
        public string Workset { get; set; }
        public int Elements { get; set; }
        /// <summary>Что сейчас в «Комментарии» у элементов набора: «АПС: 80, пусто: 674».</summary>
        public string Current { get; set; }

        public string Comment
        {
            get => _comment;
            set
            {
                if (_comment == value) return;
                _comment = value ?? "";
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Comment)));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>
    /// «Комментарии» элементов по рабочим наборам: каждому набору — свой комментарий (любой). По нему маркировка
    /// и кабельный журнал отбирают систему. Вложенные общие семейства (АКБ в ИВЭПР и т.п.) получают комментарий родителя.
    /// </summary>
    internal static class WorksetCommentOperations
    {
        /// <summary>Элементы набора, которым можно записать «Комментарии» (экземпляры с этим параметром).</summary>
        public static List<Element> ElementsOf(Document doc, WorksetId id) =>
            new FilteredElementCollector(doc).WherePasses(new ElementWorksetFilter(id)).WhereElementIsNotElementType()
                .Where(e => e.Category != null && e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS) is Parameter p && !p.IsReadOnly)
                .ToList();

        public static List<WorksetCommentRow> Rows(Document doc)
        {
            var rows = new List<WorksetCommentRow>();
            foreach (Workset ws in new FilteredWorksetCollector(doc).OfKind(WorksetKind.UserWorkset).ToWorksets().OrderBy(w => w.Name))
            {
                List<Element> elements = ElementsOf(doc, ws.Id);
                string current = string.Join(", ", elements.GroupBy(DeviceMarkingOperations.CommentOf)
                    .OrderByDescending(g => g.Count()).Take(4)
                    .Select(g => $"{(g.Key.Length == 0 ? "пусто" : g.Key)}: {g.Count()}"));
                rows.Add(new WorksetCommentRow
                {
                    Id = ws.Id,
                    Workset = ws.Name,
                    Elements = elements.Count,
                    Current = elements.Count == 0 ? "—" : current,
                    Comment = MarkingSettings.WorksetComment(doc, ws.Name) ?? "",
                });
            }
            return rows;
        }

        /// <summary>Пишет комментарии. overwrite=false — элементы с другим непустым комментарием не трогаются.</summary>
        public static (int written, int same, int other, int busy, List<string> failures) Apply(Document doc, List<WorksetCommentRow> rows, bool overwrite, bool dryRun)
        {
            int written = 0, same = 0, other = 0, busy = 0;
            var failures = new List<string>();
            using (var t = new Transaction(doc, "AerBIM: комментарии по рабочим наборам"))
            {
                t.Start();
                foreach (WorksetCommentRow row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Comment)))
                {
                    string value = row.Comment.Trim();
                    foreach (Element e in ElementsOf(doc, row.Id))
                    {
                        // Вложенные общие семейства — по родителю (отдельным шагом), а не по своему набору
                        if (e is FamilyInstance fi && fi.SuperComponent != null) continue;
                        string currentValue = DeviceMarkingOperations.CommentOf(e);
                        if (string.Equals(currentValue, value, StringComparison.OrdinalIgnoreCase)) { same++; continue; }
                        if (currentValue.Length > 0 && !overwrite) { other++; continue; }
                        // Элемент занят другим пользователем — Revit не даст его изменить
                        if (doc.IsWorkshared && WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser) { busy++; continue; }
                        string error = RevitText.TrySet(e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS), "Комментарии", value);
                        if (error != null) failures.Add($"id {RevitIds.IdOf(e.Id)}: {error}");
                        else written++;
                    }
                }
                if (dryRun || failures.Count > 0) t.RollBack();
                else if (t.Commit() != TransactionStatus.Committed) throw new InvalidOperationException("Revit не применил изменения.");
            }
            return (written, same, other, busy, failures);
        }
    }

    /// <summary>Окно «Комментарии по рабочим наборам».</summary>
    [Transaction(TransactionMode.Manual)]
    public class CmdWorksetComments : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            Document doc = commandData.Application.ActiveUIDocument?.Document;
            if (doc == null || doc.IsFamilyDocument)
            {
                message = "Откройте проект.";
                return Result.Failed;
            }
            if (!doc.IsWorkshared)
            {
                TaskDialog.Show("Комментарии по рабочим наборам", "В этом проекте не включена совместная работа: рабочих наборов нет.");
                return Result.Cancelled;
            }
            var window = new WorksetCommentsWindow(doc);
            new WindowInteropHelper(window).Owner = commandData.Application.MainWindowHandle;
            window.ShowDialog();
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.Manual)] public class GuardWorksetComments : GuardedCommand<CmdWorksetComments> { }

    internal sealed class WorksetCommentsWindow : Window
    {
        private readonly Document _doc;
        private List<WorksetCommentRow> _rows;
        private readonly DataGrid _table = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            CanUserDeleteRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionUnit = DataGridSelectionUnit.Cell,
        };
        private readonly CheckBox _overwrite = new CheckBox
        {
            Content = "Перезаписывать другой комментарий (иначе меняются только пустые)",
            Margin = new Thickness(0, 0, 0, 4),
        };
        private readonly CheckBox _nested = new CheckBox
        {
            Content = "Вложенным семействам — комментарий родителя (АКБ в ИВЭПР, коробка в извещателе и т.п.)",
            IsChecked = true,
        };

        public WorksetCommentsWindow(Document doc)
        {
            _doc = doc;
            UI.AerBimAppearance.Apply(this);
            Title = "Комментарии по рабочим наборам";
            Width = 900;
            Height = 580;
            MinWidth = 640;
            MinHeight = 380;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _table.Columns.Add(new DataGridTextColumn { Header = "Рабочий набор", Binding = new Binding(nameof(WorksetCommentRow.Workset)), IsReadOnly = true, Width = 200 });
            _table.Columns.Add(new DataGridTextColumn { Header = "Элементов", Binding = new Binding(nameof(WorksetCommentRow.Elements)), IsReadOnly = true, Width = 80 });
            _table.Columns.Add(new DataGridTextColumn { Header = "Сейчас в «Комментарии»", Binding = new Binding(nameof(WorksetCommentRow.Current)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            _table.Columns.Add(new DataGridTextColumn
            {
                Header = "Новый комментарий",
                Binding = new Binding(nameof(WorksetCommentRow.Comment)) { UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged },
                Width = 180,
            });

            var intro = new TextBlock
            {
                Text = "Впишите комментарий наборам, элементам которых его нужно записать (любое значение, например АПС или АГПТ). Пустое поле — набор не трогается. " +
                       "Значения запоминаются для проекта. Перед записью — предпросмотр.",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            };

            var apply = Btn("Записать", Apply);
            UI.AerBimAppearance.Primary(this, apply);
            var close = Btn("Закрыть", Close);
            close.IsCancel = true;
            var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            right.Children.Add(apply);
            right.Children.Add(close);
            var options = new StackPanel();
            options.Children.Add(_overwrite);
            options.Children.Add(_nested);
            var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
            DockPanel.SetDock(right, Dock.Right);
            bottom.Children.Add(right);
            bottom.Children.Add(options);

            var layout = new Grid { Margin = new Thickness(12) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition());
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_table, 1);
            Grid.SetRow(bottom, 2);
            layout.Children.Add(intro);
            layout.Children.Add(_table);
            layout.Children.Add(bottom);
            Content = layout;
            Reload();
        }

        private void Reload()
        {
            _rows = WorksetCommentOperations.Rows(_doc);
            _table.ItemsSource = _rows;
        }

        private void Apply()
        {
            _table.CommitEdit(DataGridEditingUnit.Row, true);
            List<WorksetCommentRow> chosen = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Comment)).ToList();
            bool nested = _nested.IsChecked == true;
            if (chosen.Count == 0 && !nested)
            {
                MessageBox.Show(this, "Впишите комментарий хотя бы одному набору.", Title);
                return;
            }
            try
            {
                bool overwrite = _overwrite.IsChecked == true;
                var preview = WorksetCommentOperations.Apply(_doc, chosen, overwrite, true);
                if (preview.failures.Count > 0)
                {
                    MessageBox.Show(this, "Не всем элементам можно записать комментарий:\n" + string.Join("\n", preview.failures.Take(10)), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // Вложенные: предпросмотр считается по текущим комментариям родителей (до записи по наборам)
                int nestedCount = 0;
                if (nested) nestedCount = (int)NestedCommentOperations.Sync(_doc, null, overwrite, true)["changed"];

                string sets = chosen.Count == 0 ? "" : string.Join("\n", chosen.Select(r => $"• {r.Workset} → «{r.Comment.Trim()}»")) + "\n\n";
                string text = $"{sets}Будет записано по наборам: {preview.written}\nУже с этим комментарием: {preview.same}" +
                              (preview.other > 0 ? $"\nС другим комментарием, не меняются: {preview.other} (включите «Перезаписывать», чтобы заменить)" : "") +
                              (preview.busy > 0 ? $"\nЗаняты другими пользователями, пропущены: {preview.busy}" : "") +
                              (nested ? $"\nВложенным семействам — как у родителя: {nestedCount}{(chosen.Count > 0 ? " (после записи по наборам может стать больше)" : "")}" : "");
                MarkingSettings.SaveWorksetComments(_doc, _rows.ToDictionary(r => r.Workset, r => r.Comment));
                if (preview.written == 0 && nestedCount == 0 && chosen.Count == 0)
                {
                    MessageBox.Show(this, text + "\n\nМенять нечего.", Title);
                    return;
                }
                if (MessageBox.Show(this, text + "\n\nЗаписать?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

                var result = WorksetCommentOperations.Apply(_doc, chosen, overwrite, false);
                int nestedWritten = nested ? (int)NestedCommentOperations.Sync(_doc, null, overwrite, false)["changed"] : 0;
                PluginLog.Info("worksets.comments", string.Join("; ", chosen.Select(r => r.Workset + "=" + r.Comment.Trim())) + $" | по наборам {result.written}, вложенным {nestedWritten}");
                Reload();
                MessageBox.Show(this, $"Записано по наборам: {result.written}" + (nested ? $"\nВложенным семействам: {nestedWritten}" : "") + "\n\nОтменить можно через Ctrl+Z.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось записать: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Button Btn(string text, Action action)
        {
            var b = new Button { Content = text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0), MinWidth = 90 };
            b.Click += (s, e) => action();
            return b;
        }
    }
}
