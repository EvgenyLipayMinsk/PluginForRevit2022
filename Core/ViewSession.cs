using System;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// «Открыть вид → сделать в нём все изменения → закрыть». На неоткрытом виде Revit формирует его графику
    /// заново на каждый созданный элемент (марку, размер), и сотня марок идёт минутами. Открытый вид
    /// формирует графику один раз. После работы возвращается прежний активный вид, а вид, открытый здесь,
    /// закрывается. Вид нельзя переключить при открытой транзакции — Open вызывается до Transaction.Start.
    /// </summary>
    internal sealed class ViewSession : IDisposable
    {
        /// <summary>UI-документ текущей команды; задаёт исполнитель MCP перед вызовом обработчика.</summary>
        [ThreadStatic] public static UIDocument Ui;

        private readonly UIDocument _ui;
        private readonly View _previous;
        private readonly ElementId _opened;
        private readonly bool _close;

        private ViewSession(UIDocument ui, View previous, ElementId opened, bool close)
        {
            _ui = ui;
            _previous = previous;
            _opened = opened;
            _close = close;
        }

        /// <summary>Открывает вид, если он не активен. Если открыть нельзя (нет UI, шаблон, другой документ) — ничего не делает.</summary>
        public static IDisposable Open(Document doc, View view)
        {
            UIDocument ui = Ui;
            if (ui == null || view == null || view.IsTemplate || !ui.Document.Equals(doc)) return new ViewSession(null, null, null, false);
            View active = ui.ActiveView;
            if (active != null && active.Id == view.Id) return new ViewSession(null, null, null, false);
            bool wasOpen = ui.GetOpenUIViews().Any(v => v.ViewId == view.Id);
            try
            {
                ui.ActiveView = view;
                PluginLog.Info("view.open", $"{view.Name}{(wasOpen ? " (уже был открыт)" : "")}");
                return new ViewSession(ui, active, view.Id, !wasOpen);
            }
            catch (Exception ex)
            {
                PluginLog.Warn("view.open", $"{view.Name}: {ex.Message}");
                return new ViewSession(null, null, null, false);
            }
        }

        public void Dispose()
        {
            if (_ui == null) return;
            try
            {
                if (_previous != null && _previous.IsValidObject) _ui.ActiveView = _previous;
                if (_close) _ui.GetOpenUIViews().FirstOrDefault(v => v.ViewId == _opened)?.Close();
            }
            catch (Exception ex)
            {
                PluginLog.Warn("view.close", ex.Message);
            }
        }
    }
}
