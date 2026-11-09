using Microsoft.Win32;

namespace ARBM.CableJournalExporter.Services
{
    public class FileDialogService
    {
        /// <summary>
        /// Диалог выбора шаблона Excel (templ_kzh.xlsx).
        /// </summary>
        public string SelectTemplateFile()
        {
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
