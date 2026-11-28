using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class RenameItem
    {
        public long? Id { get; set; }
        /// <summary>Текущее имя вида или номер листа (если id не задан).</summary>
        public string Current { get; set; }
        public string NewName { get; set; }
        public string NewNumber { get; set; }
    }

    internal sealed class RenameRequest
    {
        /// <summary>views (виды, легенды, спецификации) или sheets.</summary>
        public string Target { get; set; } = "views";

        /// <summary>Явные новые значения. Если заданы — остальные способы не используются.</summary>
        public List<RenameItem> Items { get; set; } = new List<RenameItem>();

        // Отбор для пакетных операций
        public List<long> Ids { get; set; } = new List<long>();
        public string ViewType { get; set; }
        public string NameContains { get; set; }
        public string NumberContains { get; set; }

        /// <summary>Листы: name или number; виды — всегда name.</summary>
        public string Field { get; set; } = "name";

        public string Find { get; set; }
        public string Replace { get; set; }
        public bool Regex { get; set; }
        public string Prefix { get; set; }
        public string Suffix { get; set; }

        /// <summary>Шаблон нумерации: «АР-{n:00}». {n} — порядковый номер.</summary>
        public string Pattern { get; set; }
        public int Start { get; set; } = 1;
        public int Step { get; set; } = 1;
        /// <summary>Порядок нумерации: number (по текущему номеру), name, id.</summary>
        public string Order { get; set; } = "number";

        public bool DryRun { get; set; } = true;
    }

    /// <summary>Переименование видов и листов (имя, номер листа). Без UI.</summary>
    internal static class RenameOperations
    {
        private static readonly Regex PatternToken = new Regex(@"\{n(?::([^}]+))?\}");

        private sealed class Change
        {
            public Element Element;
            public string Field;      // name | number
            public string Old;
            public string New;
            public string Status;     // renamed | unchanged | conflict | invalid
            public string Message;

            public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(Element.Id),
                ["kind"] = Element is ViewSheet ? "Sheet" : Element is ViewSchedule ? "Schedule" : ((View)Element).ViewType.ToString(),
                ["field"] = Field,
                ["old"] = Old,
                ["new"] = New,
                ["status"] = Status,
                ["message"] = Message,
            };
        }

        public static Dictionary<string, object> Rename(Document doc, RenameRequest request)
        {
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }
            bool sheets = string.Equals(request.Target, "sheets", StringComparison.OrdinalIgnoreCase);
            if (!sheets && !string.Equals(request.Target, "views", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("target: views или sheets.");
            }

            List<Change> changes = request.Items.Count > 0 ? Explicit(doc, request, sheets) : Bulk(doc, request, sheets);
            if (changes.Count == 0)
            {
                throw new ArgumentException("Не найдено ни одного вида/листа для переименования.");
            }
            Validate(doc, changes, sheets);

            List<Change> toApply = changes.Where(c => c.Status == "renamed").ToList();
            using (var transaction = new Transaction(doc, sheets ? "AerBIM: переименовать листы" : "AerBIM: переименовать виды"))
            {
                transaction.Start();
                try
                {
                    // Две фазы: сначала временные значения, затем итоговые —
                    // так проходят перестановки (А→Б, Б→А) без конфликтов имён.
                    // Только разрешённые Revit символы (не \ : { } [ ] | ; < > ? ` ~).
                    string tag = "_aerbim_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    foreach (Change c in toApply)
                    {
                        Set(c.Element, c.Field, c.Old + tag);
                    }
                    foreach (Change c in toApply)
                    {
                        Set(c.Element, c.Field, c.New);
                    }
                }
                catch (Exception ex)
                {
                    transaction.RollBack();
                    throw new InvalidOperationException("Revit отклонил переименование: " + ex.Message, ex);
                }

                if (request.DryRun || toApply.Count == 0)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил переименование.");
                }
            }

            int conflicts = changes.Count(c => c.Status == "conflict" || c.Status == "invalid");
            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["target"] = sheets ? "sheets" : "views",
                ["renamed"] = toApply.Count,
                ["unchanged"] = changes.Count(c => c.Status == "unchanged"),
                ["conflicts"] = conflicts,
                ["changes"] = changes.Select(c => c.ToDictionary()).ToList(),
                ["summary"] = (request.DryRun ? "Предпросмотр: будет переименовано " : "Переименовано ") +
                              $"{toApply.Count} из {changes.Count}" +
                              (conflicts > 0 ? $", с конфликтами {conflicts} (не изменяются)" : "") +
                              (request.DryRun ? ". Для применения повторите с dryRun=false." : ". Отменить можно в Revit через Ctrl+Z."),
            };
        }

        // ---------- Сбор изменений ----------

        private static List<Change> Explicit(Document doc, RenameRequest request, bool sheets)
        {
            var result = new List<Change>();
            foreach (RenameItem item in request.Items)
            {
                Element element = sheets
                    ? (Element)SheetOperations.ResolveSheet(doc, item.Id, item.Id == null ? item.Current : null)
                    : ViewOperations.ResolveView(doc, item.Id?.ToString(CultureInfo.InvariantCulture) ?? item.Current);
                if (element == null)
                {
                    throw new ArgumentException("Укажите id или текущее имя/номер для каждого элемента items.");
                }
                if (item.NewName != null)
                {
                    result.Add(NewChange(element, "name", item.NewName));
                }
                if (item.NewNumber != null)
                {
                    if (!sheets)
                    {
                        throw new ArgumentException("newNumber доступен только для листов.");
                    }
                    result.Add(NewChange(element, "number", item.NewNumber));
                }
            }
            return result;
        }

        private static List<Change> Bulk(Document doc, RenameRequest request, bool sheets)
        {
            string field = sheets && string.Equals(request.Field, "number", StringComparison.OrdinalIgnoreCase) ? "number" : "name";
            List<Element> selected = Select(doc, request, sheets);

            bool hasPattern = !string.IsNullOrEmpty(request.Pattern);
            bool hasEdit = !string.IsNullOrEmpty(request.Find) || !string.IsNullOrEmpty(request.Prefix) || !string.IsNullOrEmpty(request.Suffix);
            if (!hasPattern && !hasEdit)
            {
                throw new ArgumentException("Укажите способ: items, find/replace, prefix/suffix или pattern.");
            }
            if (hasPattern && !PatternToken.IsMatch(request.Pattern))
            {
                throw new ArgumentException("pattern должен содержать {n}, например «АР-{n:00}».");
            }

            if (hasPattern)
            {
                selected = Order(selected, request.Order, sheets);
            }

            var result = new List<Change>();
            int n = request.Start;
            foreach (Element element in selected)
            {
                string value = Get(element, field);
                if (hasPattern)
                {
                    int current = n;
                    value = PatternToken.Replace(request.Pattern, m =>
                        string.IsNullOrEmpty(m.Groups[1].Value)
                            ? current.ToString(CultureInfo.InvariantCulture)
                            : current.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));
                    n += request.Step;
                }
                if (!string.IsNullOrEmpty(request.Find))
                {
                    value = request.Regex
                        ? System.Text.RegularExpressions.Regex.Replace(value, request.Find, request.Replace ?? string.Empty)
                        : value.Replace(request.Find, request.Replace ?? string.Empty);
                }
                if (!string.IsNullOrEmpty(request.Prefix) && !value.StartsWith(request.Prefix, StringComparison.CurrentCulture))
                {
                    value = request.Prefix + value;
                }
                if (!string.IsNullOrEmpty(request.Suffix) && !value.EndsWith(request.Suffix, StringComparison.CurrentCulture))
                {
                    value += request.Suffix;
                }
                result.Add(NewChange(element, field, value));
            }
            return result;
        }

        private static List<Element> Select(Document doc, RenameRequest request, bool sheets)
        {
            IEnumerable<Element> items = sheets
                ? SheetOperations.Sheets(doc).Cast<Element>()
                : ViewOperations.ListViews(doc, request.ViewType ?? "all").Cast<Element>();

            if (request.Ids.Count > 0)
            {
                var ids = new HashSet<long>(request.Ids);
                items = items.Where(e => ids.Contains(RevitIds.IdOf(e.Id)));
            }
            if (!string.IsNullOrEmpty(request.NameContains))
            {
                items = items.Where(e => e.Name.IndexOf(request.NameContains, StringComparison.CurrentCultureIgnoreCase) >= 0);
            }
            if (sheets && !string.IsNullOrEmpty(request.NumberContains))
            {
                items = items.Where(e => ((ViewSheet)e).SheetNumber.IndexOf(request.NumberContains, StringComparison.CurrentCultureIgnoreCase) >= 0);
            }

            List<Element> list = items.ToList();
            if (request.Ids.Count == 0 && string.IsNullOrEmpty(request.NameContains) &&
                string.IsNullOrEmpty(request.NumberContains) && string.IsNullOrEmpty(request.ViewType))
            {
                throw new ArgumentException("Ограничьте выбор: ids, viewType, nameContains или numberContains (переименование всех подряд не выполняется).");
            }
            return list;
        }

        private static List<Element> Order(List<Element> items, string order, bool sheets)
        {
            switch ((order ?? "number").ToLowerInvariant())
            {
                case "name":
                    return items.OrderBy(e => e.Name, NaturalComparer.Instance).ToList();
                case "id":
                    return items.OrderBy(e => RevitIds.IdOf(e.Id)).ToList();
                default:
                    return sheets
                        ? items.OrderBy(e => ((ViewSheet)e).SheetNumber, NaturalComparer.Instance).ToList()
                        : items.OrderBy(e => e.Name, NaturalComparer.Instance).ToList();
            }
        }

        private static Change NewChange(Element element, string field, string newValue)
        {
            string old = Get(element, field);
            string value = (newValue ?? string.Empty).Trim();
            return new Change
            {
                Element = element,
                Field = field,
                Old = old,
                New = value,
                Status = string.Equals(old, value, StringComparison.CurrentCulture) ? "unchanged" : "renamed",
            };
        }

        // ---------- Проверки ----------

        private static readonly char[] ForbiddenChars = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        private static void Validate(Document doc, List<Change> changes, bool sheets)
        {
            foreach (Change c in changes.Where(c => c.Status == "renamed"))
            {
                if (string.IsNullOrWhiteSpace(c.New))
                {
                    Mark(c, "invalid", "пустое значение");
                }
                else if (c.New.IndexOfAny(ForbiddenChars) >= 0)
                {
                    Mark(c, "invalid", "недопустимые символы: \\ : { } [ ] | ; < > ? ` ~");
                }
            }

            // Номера листов уникальны среди всех листов; имена видов — среди видов того же типа;
            // имена листов могут повторяться.
            List<Element> pool = sheets
                ? SheetOperations.Sheets(doc).Cast<Element>().ToList()
                : ViewOperations.ListViews(doc, "all").Cast<Element>().ToList();

            // Повторяем, пока появляются новые конфликты: элемент с конфликтом сохраняет
            // старое значение, и оно может помешать другому переименованию.
            bool changed = true;
            while (changed)
            {
                changed = false;
                foreach (IGrouping<string, Change> fieldGroup in changes.GroupBy(c => c.Field))
                {
                    string field = fieldGroup.Key;
                    if (sheets && field == "name")
                    {
                        continue;
                    }

                    var finalValues = pool.Select(e =>
                    {
                        Change change = fieldGroup.FirstOrDefault(c => c.Element.Id == e.Id && c.Status == "renamed");
                        return (element: e, value: change != null ? change.New : Get(e, field), change);
                    }).ToList();

                    foreach (Change c in fieldGroup.Where(c => c.Status == "renamed"))
                    {
                        string key = GroupKey(c.Element, sheets);
                        var clash = finalValues.FirstOrDefault(f =>
                            f.element.Id != c.Element.Id &&
                            GroupKey(f.element, sheets) == key &&
                            string.Equals(f.value, c.New, StringComparison.CurrentCultureIgnoreCase));
                        if (clash.element != null)
                        {
                            Mark(c, "conflict", clash.change != null
                                ? $"такое же значение получает и {Describe(clash.element)}"
                                : $"значение уже занято: {Describe(clash.element)}");
                            changed = true;
                        }
                    }
                }
            }
        }

        private static string GroupKey(Element e, bool sheets) => sheets ? "sheet" : (e is ViewSchedule ? "Schedule" : ((View)e).ViewType.ToString());

        private static string Describe(Element e) => e is ViewSheet s ? $"лист {s.SheetNumber} «{s.Name}»" : $"вид «{e.Name}» (id {RevitIds.IdOf(e.Id)})";

        private static void Mark(Change c, string status, string message)
        {
            c.Status = status;
            c.Message = message;
        }

        // ---------- Чтение/запись ----------

        private static string Get(Element element, string field) =>
            field == "number" ? ((ViewSheet)element).SheetNumber : element.Name;

        private static void Set(Element element, string field, string value)
        {
            if (field == "number")
            {
                ((ViewSheet)element).SheetNumber = value;
            }
            else
            {
                element.Name = value;
            }
        }

        /// <summary>«АР-2» &lt; «АР-10».</summary>
        private sealed class NaturalComparer : IComparer<string>
        {
            public static readonly NaturalComparer Instance = new NaturalComparer();
            private static readonly Regex Chunks = new Regex(@"\d+|\D+");

            public int Compare(string x, string y)
            {
                MatchCollection a = Chunks.Matches(x ?? string.Empty);
                MatchCollection b = Chunks.Matches(y ?? string.Empty);
                for (int i = 0; i < Math.Min(a.Count, b.Count); i++)
                {
                    string p = a[i].Value, q = b[i].Value;
                    int result = char.IsDigit(p[0]) && char.IsDigit(q[0]) && p.Length < 19 && q.Length < 19
                        ? long.Parse(p, CultureInfo.InvariantCulture).CompareTo(long.Parse(q, CultureInfo.InvariantCulture))
                        : string.Compare(p, q, StringComparison.CurrentCultureIgnoreCase);
                    if (result != 0)
                    {
                        return result;
                    }
                }
                return a.Count.CompareTo(b.Count);
            }
        }
    }
}
