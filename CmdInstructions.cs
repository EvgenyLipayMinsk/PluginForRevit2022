using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;

namespace PluginForRevit2022
{
    /// <summary>
    /// Инструкции «что заполнить, чтобы получился кабельный журнал» — своя для каждой системы.
    /// Текст сверен с проверками журнала (Core/CableJournal) и маркировки плагина.
    /// </summary>
    internal static class JournalInstructions
    {
        private const string Template = @"C:\Program Files\AerBIM\PluginForRevit\Шаблоны\templ_kzh.xlsx";

        public static IReadOnlyList<(string Title, string[] Lines)> Aps => new[]
        {
            ("1. Параметры проекта", new[]
            {
                "Нажмите любую кнопку плагина: он проверит параметры ФОП и предложит добавить недостающие. Можно сделать и кнопкой «Параметры из ФОП».",
            }),
            ("2. Настройки маркировки (один раз на проект или профиль)", new[]
            {
                "Кнопка «Настройки маркировки»: отметьте галочкой «АПС» семейства линии — извещатели, ИПР, изоляторы, УДП.",
                "Для каждого семейства задайте БЦО (например BTH — дымовой, BTM — ручной, IZ — изолятор).",
                "Там же — запас кабеля АПС в % и запас на линию в метрах; журнал берёт их из профиля.",
            }),
            ("3. Приборы (категория «Электрооборудование»)", new[]
            {
                "Заполните вручную: «ARBM_ПСБ_БЦО» = ARK и «ARBM_SP_ElementMark» = номер прибора (1, 2, 3…).",
                "«ARBM_Корпус» — латиницей и цифрами (M2, а не М2 с русской «М»). Пустой корпус маркировка заполнит сама.",
                "«Комментарии», «ADSK_Этаж» и «ARBM_ПС_Имя изделия» (вида M2.ARK.3) маркировка заполнит сама, когда вы кликнете прибор.",
                "Имена приборов должны быть уникальными. У прибора не больше 2 линий.",
            }),
            ("4. Трассы (семейства кабеля, категория «Оборудование»)", new[]
            {
                "«ADSK_Группирование» = СПС_АЛС.<прибор>.<линия>, например СПС_АЛС.3.1 — прибор 3, линия 1. По нему маркировка строит порядок номеров. Плагин его не заполняет.",
                "«Комментарии» = значение системы, например «АПС». По нему журнал отбирает трассы и считает длины кабеля.",
                "«ADSK_Этаж» — вручную, только цифрой: этаж устройств, которые висят на трассе (кабель под перекрытием Этажа 04 над устройствами Этажа 03 — это 3). Журнал кладёт трассу и устройства на один этаж по этой цифре. Плагин его не заполняет.",
                "Переход между этажами — семейство «ARBM_Межэтажный переход» на каждом этаже в одной точке плана. Два перехода в одной точке одного этажа — ошибка.",
                "Трасса должна доходить до прибора и до каждого устройства (не дальше ~0,5 м).",
            }),
            ("5. Маркировка линии", new[]
            {
                "«Маркировка» → «Создать маркировку»: система АПС, корпус, номер прибора, номер линии, «Комментарии» = АПС.",
                "Порядок «по маршруту»: кликните прибор, выделите устройства линии, нажмите «Готово» — номера пойдут по ходу трассы от прибора.",
                "Порядок «ручной»: кликните прибор, затем устройства по порядку (выбранные подсвечиваются зелёным), Esc — конец.",
                "Проверьте окно подтверждения: что запишется в прибор, сколько адресов занято (не больше 250).",
                "Устройству записываются: ARBM_Корпус, ARBM_Номер прибора, ARBM_Номер линии, ARBM_ПСБ_БЦО, ARBM_SP_ElementMark, ADSK_Этаж (числом), «Комментарии».",
                "Если уровень без номера в имени (например «Тех. Пространство»), укажите этаж числом в поле «Этаж» — журнал не принимает этаж текстом.",
                "Правки: «Добавить устройство» (номера сдвигаются) и «Удалить устройство» (пропуск закрывается, маркировка снимается).",
            }),
            ("6. Кабельный журнал", new[]
            {
                "Кнопка «АПС» → кликните элемент с нужным значением «Комментарии» (например прибор).",
                "Выберите шаблон: " + Template + ".",
                "Укажите, куда сохранить файл Excel.",
                "Если журнал сообщает об ошибке — проверьте: этаж числом, номер линии 1 или 2, номера на линии без повторов, имя прибора вида Корпус.ARK.Номер.",
            }),
        };

        public static IReadOnlyList<(string Title, string[] Lines)> Security => new[]
        {
            ("1. Параметры проекта", new[]
            {
                "Нажмите любую кнопку плагина: он проверит параметры ФОП и предложит добавить недостающие.",
            }),
            ("2. Настройки маркировки", new[]
            {
                "Кнопка «Настройки маркировки»: отметьте галочкой «СОТС» семейства извещателей (категория «Датчики») и задайте им БЦО.",
                "Там же — запас кабеля СОТС в % и запас на линию.",
            }),
            ("3. Приборы (категория «Электрооборудование»)", new[]
            {
                "«ARBM_ПС_Имя изделия» = ARK.<этаж>.<номер прибора>, например ARK.3.1 или ARK.-1.2.",
                "«ADSK_Этаж» — тот же этаж числом, что в имени. Имена уникальны.",
                "Если при маркировке кликнуть прибор, плагин заполнит имя, этаж и «Комментарии» сам.",
            }),
            ("4. Трассы (семейства кабеля, категория «Оборудование»)", new[]
            {
                "Для журнала «СОТС по параметру» — «Комментарии» трасс = значение системы.",
                "Для автоматического порядка маркировки — «ADSK_Группирование» = …АЛС.<прибор>.<шлейф>.",
                "Переходы между этажами — «ARBM_Межэтажный переход» на каждом этаже в одной точке.",
            }),
            ("5. Маркировка шлейфа", new[]
            {
                "«Маркировка» → «Создать маркировку»: система СОТС, номер прибора, номер шлейфа.",
                "Устройству записываются: «ARBM_ПСБ_Адрес устройства» = <прибор>.<шлейф> (например 1.2), ARBM_SP_ElementMark, ARBM_ПСБ_БЦО, ADSK_Этаж (числом).",
                "Порядок — по маршруту или кликами, как для АПС.",
            }),
            ("6. Кабельный журнал", new[]
            {
                "«СОТС все» — по всем приборам и устройствам проекта; «СОТС по параметру» — только по значению «Комментарии» кликнутого элемента.",
                "Выберите шаблон: " + Template + ", затем место сохранения.",
            }),
        };

        public static IReadOnlyList<(string Title, string[] Lines)> Souye => new[]
        {
            ("1. Параметры проекта", new[]
            {
                "Нажмите любую кнопку плагина: он проверит параметры ФОП и предложит добавить недостающие.",
            }),
            ("2. Настройки маркировки", new[]
            {
                "Кнопка «Настройки маркировки»: отметьте галочкой «СОУЭ» семейства оповещателей (категория «Устройства вызова и оповещения») и задайте им БЦО.",
                "Там же — запас кабеля СОУЭ в % и запас на линию.",
            }),
            ("3. Прибор СОУЭ (категория «Электрооборудование»)", new[]
            {
                "С данным значением «Комментарии» в проекте должен быть ровно один прибор.",
                "Нужны: «ARBM_ПС_Имя изделия» (не пустое), «ARBM_Корпус», этаж («ADSK_Этаж» или «О_Этаж» — по выбранному стандарту).",
                "Если при маркировке кликнуть прибор, плагин заполнит «Комментарии», этаж, пустой корпус и пустое имя сам.",
            }),
            ("4. Оповещатели", new[]
            {
                "У каждого оповещателя должна быть мощность больше нуля: «ADSK_Номинальная мощность» (стандарт ADSK) или «ЭОМ_Мощность_Номинальная» (стандарт ЭОМ). Обычно задаётся в семействе.",
            }),
            ("5. Трассы (семейства кабеля, категория «Оборудование»)", new[]
            {
                "«Комментарии» трасс = то же значение, что у прибора и оповещателей.",
                "Для автоматического порядка маркировки — «ADSK_Группирование» = …<прибор>.<шлейф>.",
                "Переходы между этажами — «ARBM_Межэтажный переход» на каждом этаже в одной точке.",
            }),
            ("6. Маркировка шлейфа", new[]
            {
                "«Маркировка» → «Создать маркировку»: система СОУЭ, корпус, номер прибора, номер шлейфа, «Комментарии», стандарт параметров (ЭОМ или ADSK).",
                "Оповещателю записываются: «ARBM_ПСБ_Адрес устройства» = номер шлейфа (целое число), ARBM_SP_ElementMark (без повторов в шлейфе), ARBM_ПСБ_БЦО, ARBM_Корпус, этаж, «Комментарии».",
            }),
            ("7. Кабельный журнал", new[]
            {
                "Кнопка «СОУЭ» → кликните прибор → выберите стандарт параметров (ЭОМ или ADSK).",
                "Выберите шаблон: " + Template + ", затем место сохранения.",
            }),
        };

        public static void Show(UIApplication app, string title, IReadOnlyList<(string Title, string[] Lines)> sections)
        {
            var content = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
            content.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
            foreach ((string sectionTitle, string[] lines) in sections)
            {
                content.Children.Add(new TextBlock { Text = sectionTitle, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) });
                foreach (string line in lines)
                {
                    var row = new DockPanel { Margin = new Thickness(8, 2, 0, 2) };
                    var bullet = new TextBlock { Text = "•", Width = 14, Foreground = Brushes.DimGray };
                    DockPanel.SetDock(bullet, Dock.Left);
                    row.Children.Add(bullet);
                    row.Children.Add(new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap });
                    content.Children.Add(row);
                }
            }

            var close = new Button { Content = "Закрыть", Width = 110, Margin = new Thickness(0, 8, 20, 12), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true, IsDefault = true };
            var root = new DockPanel();
            DockPanel.SetDock(close, Dock.Bottom);
            root.Children.Add(close);
            root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

            var window = new Window
            {
                Title = title,
                Content = root,
                Width = 820,
                Height = 720,
                MinWidth = 560,
                MinHeight = 400,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontSize = 13,
            };
            close.Click += (s, e) => window.Close();
            new WindowInteropHelper(window).Owner = app.MainWindowHandle;
            window.ShowDialog();
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class CmdInstructionAps : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            PluginLog.Info("command.start", "Инструкция АПС");
            JournalInstructions.Show(commandData.Application, "Кабельный журнал АПС: что сделать по порядку", JournalInstructions.Aps);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class CmdInstructionSecurity : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            PluginLog.Info("command.start", "Инструкция СОТС");
            JournalInstructions.Show(commandData.Application, "Кабельный журнал СОТС: что сделать по порядку", JournalInstructions.Security);
            return Result.Succeeded;
        }
    }

    [Transaction(TransactionMode.ReadOnly)]
    public class CmdInstructionSouye : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            PluginLog.Info("command.start", "Инструкция СОУЭ");
            JournalInstructions.Show(commandData.Application, "Кабельный журнал СОУЭ: что сделать по порядку", JournalInstructions.Souye);
            return Result.Succeeded;
        }
    }
}
