using Microsoft.Win32;

namespace ARBM.CableJournalExporter.Services
{
    public class FileDialogService
    {
        /// <summary>
        /// Диалог выбора шаблона Excel (templ_kzh.xlsx).
        /// </summary>
        public CableJournalForm SelectedForm { get; private set; }

        public string SelectTemplateFile()
        {
            var choice = new Autodesk.Revit.UI.TaskDialog("Форма кабельного журнала")
            {
                MainInstruction = "Выберите форму кабельного журнала",
                MainContent = "А3: форма по образцу СПС.КЖ. Длины в трубе и лотке берутся из «УГО Вид Засечки» участков маршрута. Их сумма равна проектной длине. Редактирование — на вкладке «Полный КЖ».",
                CommonButtons = Autodesk.Revit.UI.TaskDialogCommonButtons.Cancel
            };
            choice.AddCommandLink(Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink1, "Текущая форма AerBIM — выбрать шаблон Excel");
            choice.AddCommandLink(Autodesk.Revit.UI.TaskDialogCommandLinkId.CommandLink2, "Форма А3 — встроенный шаблон по образцу СПС.КЖ");
            var selected = choice.Show();
            if (selected == Autodesk.Revit.UI.TaskDialogResult.CommandLink2)
            {
                SelectedForm = CableJournalForm.A3;
                return JournalFormExporter.BuiltInA3;
            }
            if (selected != Autodesk.Revit.UI.TaskDialogResult.CommandLink1) return null;
            SelectedForm = CableJournalForm.AerBim;
            var dialog = new OpenFileDialog
            {
                Title = "Выберите шаблон кабельного журнала (templ_kzh.xlsx)",
                Filter = "Excel файлы (*.xlsx)|*.xlsx",
                CheckFileExists = true,
                Multiselect = false
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
                return dialog.FileName;

            return null;
        }

        /// <summary>
        /// Диалог выбора пути и имени итогового файла.
        /// </summary>
        public string SelectSaveLocation()
        {
            var dialog = new SaveFileDialog
            {
                Title = "Сохранить кабельный журнал",
                Filter = "Excel файлы (*.xlsx)|*.xlsx",
                AddExtension = true,
                DefaultExt = "xlsx",
                OverwritePrompt = true
            };

            bool? result = dialog.ShowDialog();

            if (result == true)
                return dialog.FileName;

            return null;
        }
    }
}
