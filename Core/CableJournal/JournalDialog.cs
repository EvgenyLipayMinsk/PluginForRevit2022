using System;
using System.Collections.Generic;

namespace PluginForRevit2022.Core.CableJournal
{
    /// <summary>
    /// Замена TaskDialog для ядра кабельного журнала. Команды с ленты показывают обычные окна;
    /// внутри Capture (MCP-версия) сообщения собираются в список, иначе Revit ждал бы нажатия.
    /// </summary>
    internal static class JournalDialog
    {
        [ThreadStatic]
        private static List<string> _messages;

        public static Autodesk.Revit.UI.TaskDialogResult Show(string title, string text)
        {
            if (_messages == null)
            {
                return Autodesk.Revit.UI.TaskDialog.Show(title, text);
            }
            _messages.Add($"{title}: {text}");
            return Autodesk.Revit.UI.TaskDialogResult.Close;
        }

        /// <summary>Пока объект не освобождён, окна на этом потоке не показываются.</summary>
        public static IDisposable Capture(List<string> messages) => new Scope(messages);

        private sealed class Scope : IDisposable
        {
            private readonly List<string> _previous = _messages;

            public Scope(List<string> messages)
            {
                _messages = messages;
            }

            public void Dispose()
            {
                _messages = _previous;
            }
        }
    }
}
