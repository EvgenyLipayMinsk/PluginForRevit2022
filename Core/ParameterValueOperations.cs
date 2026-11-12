using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class ValueCondition
    {
        public string Parameter { get; set; }
        /// <summary>equals, not_equals, contains, not_contains, begins_with, empty, not_empty, greater, less.</summary>
        public string Operator { get; set; } = "equals";
        public string Value { get; set; }
    }

    /// <summary>Какие элементы читать/менять.</summary>
    internal sealed class ElementSelection
    {
        public List<long> Ids { get; set; } = new List<long>();
        public List<string> Categories { get; set; } = new List<string>();
        /// <summary>Только элементы, видимые на этом виде.</summary>
        public string View { get; set; }
        /// <summary>Связь (часть имени): элементы читаются из связанного файла (только чтение).</summary>
        public string Link { get; set; }
        public List<ValueCondition> Where { get; set; } = new List<ValueCondition>();
        /// <summary>Включать типоразмеры (ElementType) вместо экземпляров.</summary>
        public bool Types { get; set; }
    }

    internal sealed class SetValuesRequest
    {
        public ElementSelection Selection { get; set; } = new ElementSelection();
        /// <summary>Одинаковые значения для всех выбранных элементов.</summary>
        public Dictionary<string, string> Values { get; set; } = new Dictionary<string, string>();
        /// <summary>Свои значения для каждого элемента (id → значения).</summary>
        public List<(long id, Dictionary<string, string> values)> Items { get; set; } = new List<(long, Dictionary<string, string>)>();
        /// <summary>Разрешить изменение параметров типа (меняет все экземпляры типа).</summary>
        public bool AllowTypeParameters { get; set; }
        public int MaxElements { get; set; } = 1000;
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Чтение и запись значений параметров элементов. Без UI.</summary>
    internal static class ParameterValueOperations
    {
        private const int MaxRowsInAnswer = 300;

        // ===================== Отбор =====================

        public static (List<Element> elements, Document source, RevitLinkInstance link) Select(Document doc, ElementSelection selection, List<string> warnings)
        {
            Document source = doc;
            RevitLinkInstance link = null;
            if (!string.IsNullOrWhiteSpace(selection.Link))
            {
                link = LinkScope.ResolveLinks(doc, new[] { selection.Link }, warnings).First();
                source = link.GetLinkDocument();
            }

            IEnumerable<Element> elements;
            if (selection.Ids.Count > 0)
            {
                elements = selection.Ids.Select(id => source.GetElement(RevitIds.ToElementId(id)))
                    .Where(e => e != null);
                List<long> missing = selection.Ids.Where(id => source.GetElement(RevitIds.ToElementId(id)) == null).ToList();
                if (missing.Count > 0)
                {
                    warnings.Add("не найдены id: " + string.Join(", ", missing.Take(20)));
                }
            }
            else
            {
                if (selection.Categories.Count == 0)
                {
                    throw new ArgumentException("Ограничьте выбор: ids или categories (по всем элементам проекта команда не работает).");
                }
                var catIds = new List<ElementId>();
                foreach (string c in selection.Categories)
                {
                    ElementId id = DimensionOperations.CategoryId(source, c) ?? throw new ArgumentException($"Категория «{c}» не найдена.");
                    catIds.Add(id);
                }

                FilteredElementCollector collector;
                if (!string.IsNullOrWhiteSpace(selection.View))
                {
                    if (link != null)
                    {
                        throw new ArgumentException("view и link вместе не поддерживаются: для связи отбирайте по категориям и условиям.");
                    }
                    View view = ViewOperations.ResolveView(doc, selection.View, ViewType.FloorPlan);
                    collector = new FilteredElementCollector(doc, view.Id);
                }
                else
                {
                    collector = new FilteredElementCollector(source);
                }
                collector = collector.WherePasses(new ElementMulticategoryFilter(catIds));
                elements = selection.Types ? collector.WhereElementIsElementType() : collector.WhereElementIsNotElementType();
            }

            List<Element> list = elements.ToList();
            foreach (ValueCondition condition in selection.Where)
            {
                list = list.Where(e => Matches(e, condition)).ToList();
            }
            return (list, source, link);
        }

        private static bool Matches(Element element, ValueCondition condition)
        {
            Parameter p = Find(element, condition.Parameter, out _);
            string op = (condition.Operator ?? "equals").Trim().ToLowerInvariant();
            string text = p == null ? null : (RevitText.ValueText(p) ?? string.Empty);
            string raw = p?.StorageType == StorageType.String ? p.AsString() ?? string.Empty : text;
            string value = condition.Value ?? string.Empty;
            switch (op)
            {
                case "empty": return p == null || string.IsNullOrEmpty(text);
                case "not_empty": return p != null && !string.IsNullOrEmpty(text);
            }
            if (p == null) return op == "not_equals" || op == "not_contains";
            bool Eq(string s) => string.Equals(s?.Trim(), value.Trim(), StringComparison.CurrentCultureIgnoreCase);
            switch (op)
            {
                case "equals": return Eq(text) || Eq(raw);
                case "not_equals": return !(Eq(text) || Eq(raw));
                case "contains": return text.IndexOf(value, StringComparison.CurrentCultureIgnoreCase) >= 0;
                case "not_contains": return text.IndexOf(value, StringComparison.CurrentCultureIgnoreCase) < 0;
                case "begins_with": return text.StartsWith(value, StringComparison.CurrentCultureIgnoreCase);
                case "greater":
                case "less":
                    if (!TryNumber(text, out double a) || !TryNumber(value, out double b)) return false;
                    return op == "greater" ? a > b : a < b;
                default:
                    throw new ArgumentException($"Условие: неизвестный оператор «{op}».");
            }
        }

        private static bool TryNumber(string text, out double number)
        {
            string digits = new string((text ?? string.Empty).Replace(',', '.').Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        /// <summary>Параметр экземпляра или, если его нет, параметр типа.</summary>
        private static Parameter Find(Element element, string name, out bool isType)
        {
            isType = false;
            Parameter p = element.LookupParameter(name);
            if (p != null) return p;
            ElementId typeId = element.GetTypeId();
            if (typeId != ElementId.InvalidElementId && element.Document.GetElement(typeId) is Element type)
            {
                p = type.LookupParameter(name);
                isType = p != null;
            }
            return p;
        }

        private static Dictionary<string, object> Describe(Element e, RevitLinkInstance link)
        {
            var type = e.Document.GetElement(e.GetTypeId()) as ElementType;
            return new Dictionary<string, object>
            {
                ["id"] = RevitIds.IdOf(e.Id),
                ["category"] = e.Category?.Name,
                ["family"] = (e as FamilyInstance)?.Symbol?.FamilyName ?? (e as ElementType)?.FamilyName ?? type?.FamilyName,
                ["type"] = type?.Name ?? (e is ElementType ? e.Name : null),
                ["name"] = e.Name,
                ["link"] = link?.Name,
            };
        }

        // ===================== Чтение =====================

        public static Dictionary<string, object> Get(Document doc, ElementSelection selection, List<string> parameters, bool includeAllNames)
        {
            var warnings = new List<string>();
            var (elements, _, link) = Select(doc, selection, warnings);
            var rows = new List<Dictionary<string, object>>();

            foreach (Element e in elements.Take(MaxRowsInAnswer))
            {
                Dictionary<string, object> row = Describe(e, link);
                if (parameters.Count > 0)
                {
                    var values = new Dictionary<string, object>();
                    foreach (string name in parameters)
                    {
                        Parameter p = Find(e, name, out bool isType);
                        values[name] = p == null ? null : new Dictionary<string, object>
                        {
                            ["value"] = RevitText.ValueText(p),
                            ["storage"] = p.StorageType.ToString(),
                            ["isType"] = isType,
                            ["readOnly"] = p.IsReadOnly,
                        };
                    }
                    row["values"] = values;
                }
                rows.Add(row);
            }

            var result = new Dictionary<string, object>
            {
                ["total"] = elements.Count,
                ["returned"] = rows.Count,
                ["elements"] = rows,
                ["warnings"] = warnings,
            };
            if (elements.Count > MaxRowsInAnswer)
            {
                warnings.Add($"показаны первые {MaxRowsInAnswer} из {elements.Count}: сузьте выбор условиями (where) или категориями");
            }

            if (parameters.Count == 0 || includeAllNames)
            {
                // Доступные параметры по одному образцу каждой категории: экземпляр и тип.
                result["availableParameters"] = elements.GroupBy(e => e.Category?.Name ?? "—").Select(g =>
                {
                    Element sample = g.First();
                    Element type = sample.Document.GetElement(sample.GetTypeId());
                    return new Dictionary<string, object>
                    {
                        ["category"] = g.Key,
                        ["instance"] = Names(sample),
                        ["type"] = type == null ? new List<string>() : Names(type),
                    };
                }).ToList();
            }
            return result;
        }

        private static List<string> Names(Element e) =>
            e.Parameters.Cast<Parameter>().Select(p => p.Definition?.Name).Where(n => !string.IsNullOrEmpty(n))
                .Distinct().OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase).ToList();

        // ===================== Запись =====================

        public static Dictionary<string, object> Set(Document doc, SetValuesRequest request)
        {
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }
            if (!string.IsNullOrWhiteSpace(request.Selection.Link))
            {
                throw new InvalidOperationException("Элементы связанных файлов изменить нельзя: значения меняются в самом файле связи.");
            }

            var warnings = new List<string>();
            List<(Element element, Dictionary<string, string> values)> work;
            if (request.Items.Count > 0)
            {
                work = new List<(Element, Dictionary<string, string>)>();
                foreach (var (id, values) in request.Items)
                {
                    Element e = doc.GetElement(RevitIds.ToElementId(id));
                    if (e == null) { warnings.Add($"id {id} не найден"); continue; }
                    work.Add((e, values));
                }
            }
            else
            {
                if (request.Values.Count == 0)
                {
                    throw new ArgumentException("Укажите values (одинаковые значения) или items (свои значения для каждого id).");
                }
                List<Element> elements = Select(doc, request.Selection, warnings).elements;
                work = elements.Select(e => (e, request.Values)).ToList();
            }

            if (work.Count == 0)
            {
                throw new InvalidOperationException("Не выбрано ни одного элемента.");
            }
            if (work.Count > request.MaxElements)
            {
                throw new InvalidOperationException($"Выбрано {work.Count} элементов — больше лимита {request.MaxElements}. Сузьте выбор или увеличьте maxElements.");
            }

            var changes = new List<Dictionary<string, object>>();
            var typesTouched = new Dictionary<ElementId, (string name, HashSet<string> parameters)>();
            var typeDone = new HashSet<string>(); // typeId:param — параметр типа меняем один раз
            int changed = 0, skipped = 0;

            using (var transaction = new Transaction(doc, "AerBIM: значения параметров"))
            {
                transaction.Start();
                foreach (var (element, values) in work)
                {
                    foreach (KeyValuePair<string, string> kv in values)
                    {
                        Parameter p = Find(element, kv.Key, out bool isType);
                        string status;
                        string message = null;
                        string oldValue = p == null ? null : RevitText.ValueText(p);
                        string newValue = oldValue;

                        if (p == null)
                        {
                            status = "not_found";
                        }
                        else if (isType && !request.AllowTypeParameters)
                        {
                            status = "type_parameter_blocked";
                            message = "это параметр типа: изменение затронет все экземпляры типа — разрешите allowTypeParameters";
                        }
                        else if (isType && typeDone.Contains(element.GetTypeId() + ":" + kv.Key))
                        {
                            status = "type_already_set";
                            newValue = RevitText.ValueText(p);
                        }
                        else if (p.IsReadOnly)
                        {
                            status = "read_only";
                            message = "только для чтения (вычисляемый, формула или системный)";
                        }
                        else
                        {
                            string error = RevitText.TrySet(p, kv.Key, kv.Value);
                            newValue = RevitText.ValueText(p);
                            if (error != null)
                            {
                                status = "failed";
                                message = error;
                            }
                            else
                            {
                                status = string.Equals(oldValue ?? "", newValue ?? "", StringComparison.CurrentCulture) ? "unchanged" : "changed";
                                if (isType)
                                {
                                    ElementId typeId = element.GetTypeId();
                                    typeDone.Add(typeId + ":" + kv.Key);
                                    if (!typesTouched.TryGetValue(typeId, out var info))
                                    {
                                        info = (doc.GetElement(typeId)?.Name, new HashSet<string>());
                                        typesTouched[typeId] = info;
                                    }
                                    info.parameters.Add(kv.Key);
                                }
                            }
                        }

                        if (status == "changed") changed++; else if (status != "unchanged" && status != "type_already_set") skipped++;
                        if (changes.Count < MaxRowsInAnswer)
                        {
                            changes.Add(new Dictionary<string, object>
                            {
                                ["id"] = RevitIds.IdOf(element.Id),
                                ["element"] = element.Name,
                                ["parameter"] = kv.Key,
                                ["isType"] = isType,
                                ["old"] = oldValue,
                                ["new"] = newValue,
                                ["status"] = status,
                                ["message"] = message,
                            });
                        }
                    }
                }

                if (request.DryRun || changed == 0)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил изменения.");
                }
            }

            // Сколько экземпляров затронут изменения типов.
            var typeImpact = typesTouched.Select(t => new Dictionary<string, object>
            {
                ["type"] = t.Value.name,
                ["typeId"] = RevitIds.IdOf(t.Key),
                ["parameters"] = t.Value.parameters.ToList(),
                ["instancesInProject"] = new FilteredElementCollector(doc).WhereElementIsNotElementType()
                    .Count(e => e.GetTypeId() == t.Key),
            }).ToList();

            if (work.Count * Math.Max(1, work.Max(w => w.values.Count)) > MaxRowsInAnswer)
            {
                warnings.Add($"в ответе показаны первые {MaxRowsInAnswer} строк");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["elements"] = work.Count,
                ["changed"] = changed,
                ["skipped"] = skipped,
                ["typeChanges"] = typeImpact,
                ["changes"] = changes,
                ["warnings"] = warnings,
                ["summary"] = (request.DryRun ? "Предпросмотр: будет изменено значений: " : "Изменено значений: ") + changed +
                              (skipped > 0 ? $", пропущено: {skipped}" : "") +
                              (typeImpact.Count > 0 ? $"; изменяются типы: {typeImpact.Count} (см. typeChanges)" : "") +
                              (request.DryRun ? ". Для применения повторите с dryRun=false." : ". Отменить можно в Revit через Ctrl+Z."),
            };
        }
    }
}
