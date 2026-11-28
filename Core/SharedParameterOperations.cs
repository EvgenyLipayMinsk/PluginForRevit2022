using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Общий параметр из файла общих параметров (ФОП).</summary>
    internal sealed class SharedParameterDef
    {
        public string Name { get; set; }
        public Guid Guid { get; set; }
        public string Group { get; set; }
        public string DataType { get; set; }
        public string Description { get; set; }
        public bool Visible { get; set; }

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["name"] = Name,
            ["guid"] = Guid.ToString(),
            ["group"] = Group,
            ["dataType"] = DataType,
            ["description"] = Description,
            ["visible"] = Visible,
        };
    }

    /// <summary>Группа свойств, в которой параметр показывается в палитре свойств.</summary>
    internal sealed class ParameterGroupInfo
    {
        public string Key => Group.Key;
        public string Label => Group.Label;
        /// <summary>Строковый id группы: TypeId (2022+) или имя BuiltInParameterGroup (2021).</summary>
        public string Id => Group.Id;
        internal RevitCompat.Group Group { get; set; }

        public override string ToString() => Label;

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["key"] = Key,
            ["label"] = Label,
            ["typeId"] = Id,
        };
    }

    internal sealed class AddSharedParametersRequest
    {
        /// <summary>Путь к ФОП; null — файл, выбранный в Revit.</summary>
        public string FilePath { get; set; }

        /// <summary>Параметры: GUID, имя или «Группа/Имя».</summary>
        public List<string> Parameters { get; set; } = new List<string>();

        /// <summary>Категории (только для проекта): имя или OST_*.</summary>
        public List<string> Categories { get; set; } = new List<string>();

        public bool IsInstance { get; set; } = true;

        /// <summary>Группа свойств: ключ (data), TypeId или подпись. null — «Данные».</summary>
        public string ParameterGroup { get; set; }

        /// <summary>В семействе: переключать тип/экземпляр у уже добавленного параметра.</summary>
        public bool AllowKindChange { get; set; }

        public bool DryRun { get; set; } = true;
    }

    internal sealed class AddSharedParameterItemResult
    {
        public string Parameter { get; set; }
        public string Guid { get; set; }
        public string Status { get; set; }
        public string Message { get; set; }
        public List<string> CategoriesAdded { get; set; } = new List<string>();

        public bool IsChange => Status == "added" || Status == "categories_added" || Status == "kind_changed";

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["parameter"] = Parameter,
            ["guid"] = Guid,
            ["status"] = Status,
            ["message"] = Message,
            ["categoriesAdded"] = CategoriesAdded,
        };
    }

    internal sealed class AddSharedParametersResult
    {
        public bool DryRun { get; set; }
        public string DocumentKind { get; set; }
        public string Document { get; set; }
        public string SharedParameterFile { get; set; }
        public string Binding { get; set; }
        public string Group { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public List<AddSharedParameterItemResult> Items { get; set; } = new List<AddSharedParameterItemResult>();

        public int ChangeCount => Items.Count(i => i.IsChange);

        public string Summary =>
            (DryRun ? "Предпросмотр: " : "") +
            $"изменений {ChangeCount} из {Items.Count}" +
            (DryRun ? ". Для применения повторите с dryRun=false." : ". Отменить можно в Revit через Ctrl+Z.");

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["dryRun"] = DryRun,
            ["documentKind"] = DocumentKind,
            ["document"] = Document,
            ["sharedParameterFile"] = SharedParameterFile,
            ["binding"] = Binding,
            ["group"] = Group,
            ["categories"] = Categories,
            ["results"] = Items.Select(i => i.ToDictionary()).ToList(),
            ["summary"] = Summary,
        };

        public string ToText()
        {
            var lines = new List<string>
            {
                Summary,
                $"Документ: {Document} ({(DocumentKind == "family" ? "семейство" : "проект")})",
                $"ФОП: {SharedParameterFile}",
                $"Тип привязки: {(Binding == "instance" ? "экземпляр" : "тип")}, группа: {Group}",
            };
            if (Categories.Count > 0)
            {
                lines.Add("Категории: " + string.Join(", ", Categories));
            }
            lines.Add("");
            lines.AddRange(Items.Select(i => $"{StatusLabel(i.Status)}  {i.Parameter} — {i.Message}"));
            return string.Join(Environment.NewLine, lines);
        }

        private static string StatusLabel(string status)
        {
            switch (status)
            {
                case "added": return "[добавлен]";
                case "categories_added": return "[+категории]";
                case "kind_changed": return "[тип/экз. изменён]";
                case "already_bound":
                case "already_exists": return "[уже есть]";
                default: return "[пропущен]";
            }
        }
    }

    /// <summary>
    /// Работа с файлом общих параметров (ФОП): чтение, добавление параметров в проект
    /// (с привязкой к категориям) и в семейство. Без UI: используется кнопкой и MCP.
    /// </summary>
    internal static class SharedParameterOperations
    {
        private const string DefaultGroupKey = "data";

        // ---------- ФОП ----------

        /// <summary>
        /// Временно переключает файл общих параметров Revit и возвращает исходный при Dispose.
        /// Все ExternalDefinition из файла нужно использовать внутри области.
        /// </summary>
        internal sealed class SharedParameterFileScope : IDisposable
        {
            private readonly Application _app;
            private readonly string _previous;

            public SharedParameterFileScope(Application app, string path)
            {
                _app = app;
                _previous = app.SharedParametersFilename;

                string target = string.IsNullOrWhiteSpace(path) ? _previous : path;
                if (string.IsNullOrWhiteSpace(target))
                {
                    throw new InvalidOperationException(
                        "Файл общих параметров не выбран в Revit. Укажите путь к ФОП (filePath).");
                }
                if (!System.IO.File.Exists(target))
                {
                    throw new FileNotFoundException($"Файл общих параметров не найден: {target}");
                }

                Path = target;
                if (!string.Equals(target, _previous, StringComparison.OrdinalIgnoreCase))
                {
                    app.SharedParametersFilename = target;
                }

                File = app.OpenSharedParameterFile()
                    ?? throw new InvalidOperationException($"Revit не смог прочитать файл общих параметров: {target}");
            }

            public string Path { get; }
            public DefinitionFile File { get; }

            public IEnumerable<(string group, ExternalDefinition definition)> All()
            {
                foreach (DefinitionGroup group in File.Groups)
                {
                    foreach (Definition definition in group.Definitions)
                    {
                        if (definition is ExternalDefinition external)
                        {
                            yield return (group.Name, external);
                        }
                    }
                }
            }

            public void Dispose()
            {
                try
                {
                    if (!string.Equals(_app.SharedParametersFilename, _previous, StringComparison.OrdinalIgnoreCase))
                    {
                        _app.SharedParametersFilename = _previous ?? string.Empty;
                    }
                }
                catch
                {
                    // Восстановление настройки не должно маскировать основную ошибку.
                }
            }
        }

        public static (string path, List<SharedParameterDef> parameters) ReadFile(Application app, string path)
        {
            using (var scope = new SharedParameterFileScope(app, path))
            {
                List<SharedParameterDef> list = scope.All()
                    .Select(x => new SharedParameterDef
                    {
                        Name = x.definition.Name,
                        Guid = x.definition.GUID,
                        Group = x.group,
                        DataType = SafeSpecLabel(x.definition),
                        Description = x.definition.Description,
                        Visible = x.definition.Visible,
                    })
                    .OrderBy(p => p.Group, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
                return (scope.Path, list);
            }
        }

        // ---------- Категории и группы ----------

        /// <summary>Категории документа, к которым можно привязать параметр проекта.</summary>
        public static List<Category> BindableCategories(Document doc) =>
            doc.Settings.Categories.Cast<Category>()
                .Where(c => c.AllowsBoundParameters)
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        public static string BuiltInCategoryName(Category category)
        {
            long id = RevitIds.IdOf(category.Id);
            return id < 0 && Enum.IsDefined(typeof(BuiltInCategory), checked((int)id))
                ? ((BuiltInCategory)(int)id).ToString()
                : null;
        }

        public static List<ParameterGroupInfo> ParameterGroups() =>
            RevitCompat.AllGroups()
                .Select(g => new ParameterGroupInfo { Group = g })
                .OrderBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        public static ParameterGroupInfo ResolveGroup(string value)
        {
            List<ParameterGroupInfo> groups = ParameterGroups();
            string wanted = string.IsNullOrWhiteSpace(value) ? DefaultGroupKey : value.Trim();
            ParameterGroupInfo match = groups.FirstOrDefault(g =>
                string.Equals(g.Key, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(g.Label, wanted, StringComparison.CurrentCultureIgnoreCase));
            if (match == null)
            {
                throw new ArgumentException(
                    $"Группа параметров «{value}» не найдена. Список: list_parameter_groups.");
            }
            return match;
        }

        public static List<Category> ResolveCategories(Document doc, IEnumerable<string> values)
        {
            List<Category> bindable = BindableCategories(doc);
            var result = new List<Category>();
            var errors = new List<string>();
            foreach (string raw in values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()))
            {
                Category category = null;
                if (raw.StartsWith("OST_", StringComparison.OrdinalIgnoreCase) &&
                    Enum.TryParse(raw, true, out BuiltInCategory bic))
                {
                    try { category = Category.GetCategory(doc, bic); } catch { category = null; }
                }
                if (category == null)
                {
                    category = doc.Settings.Categories.Cast<Category>()
                        .FirstOrDefault(c => string.Equals(c.Name, raw, StringComparison.CurrentCultureIgnoreCase));
                }

                if (category == null)
                {
                    errors.Add($"«{raw}» — категория не найдена");
                }
                else if (!bindable.Any(c => c.Id == category.Id))
                {
                    errors.Add($"«{raw}» — к этой категории нельзя привязать параметр");
                }
                else if (!result.Any(c => c.Id == category.Id))
                {
                    result.Add(category);
                }
            }

            if (errors.Count > 0)
            {
                throw new ArgumentException("Категории: " + string.Join("; ", errors) + ". Список: list_bindable_categories.");
            }
            return result;
        }

        // ---------- Добавление ----------

        public static AddSharedParametersResult Add(Document doc, Application app, AddSharedParametersRequest request)
        {
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }
            if (request.Parameters == null || request.Parameters.Count == 0)
            {
                throw new ArgumentException("Не выбраны параметры из ФОП.");
            }

            bool isFamily = doc.IsFamilyDocument;
            ParameterGroupInfo group = ResolveGroup(request.ParameterGroup);
            List<Category> categories = new List<Category>();
            if (!isFamily)
            {
                categories = ResolveCategories(doc, request.Categories ?? new List<string>());
                if (categories.Count == 0)
                {
                    throw new ArgumentException("Для проекта укажите хотя бы одну категорию.");
                }
            }

            using (var scope = new SharedParameterFileScope(app, request.FilePath))
            {
                List<(string group, ExternalDefinition definition)> all = scope.All().ToList();
                List<ExternalDefinition> definitions = request.Parameters
                    .Select(p => ResolveDefinition(all, p))
                    .GroupBy(d => d.GUID).Select(g => g.First())
                    .ToList();

                var result = new AddSharedParametersResult
                {
                    DryRun = request.DryRun,
                    DocumentKind = isFamily ? "family" : "project",
                    Document = doc.Title,
                    SharedParameterFile = scope.Path,
                    Binding = request.IsInstance ? "instance" : "type",
                    Group = group.Label,
                    Categories = categories.Select(c => c.Name).ToList(),
                };

                using (var transaction = new Transaction(doc, "AerBIM: добавить параметры из ФОП"))
                {
                    transaction.Start();
                    foreach (ExternalDefinition definition in definitions)
                    {
                        AddSharedParameterItemResult item;
                        try
                        {
                            item = isFamily
                                ? AddToFamily(doc, definition, group, request)
                                : AddToProject(doc, app, definition, group, categories, request.IsInstance);
                        }
                        catch (Exception ex)
                        {
                            item = Item(definition, "failed", ex.Message);
                        }
                        result.Items.Add(item);
                    }

                    if (request.DryRun || result.ChangeCount == 0)
                    {
                        transaction.RollBack();
                    }
                    else if (transaction.Commit() != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException("Revit не применил изменения.");
                    }
                }
                return result;
            }
        }

        private static AddSharedParameterItemResult AddToProject(
            Document doc, Application app, ExternalDefinition definition, ParameterGroupInfo group,
            List<Category> categories, bool isInstance)
        {
            ProjectParameterInfo existing = ProjectParameterOperations.FindByGuid(doc, definition.GUID);
            if (existing != null)
            {
                if (existing.IsInstance != isInstance)
                {
                    return Item(definition, "kind_mismatch",
                        $"уже привязан как {(existing.IsInstance ? "экземпляр" : "тип")}. Смена тип/экземпляр в проекте " +
                        "стирает значения, поэтому не выполняется автоматически.");
                }

                CategorySet merged = app.Create.NewCategorySet();
                foreach (Category c in existing.Binding.Categories)
                {
                    merged.Insert(c);
                }
                List<string> added = new List<string>();
                foreach (Category c in categories)
                {
                    if (!merged.Contains(c))
                    {
                        merged.Insert(c);
                        added.Add(c.Name);
                    }
                }
                if (added.Count == 0)
                {
                    return Item(definition, "already_bound", "уже привязан ко всем указанным категориям");
                }

                ElementBinding binding = isInstance
                    ? (ElementBinding)app.Create.NewInstanceBinding(merged)
                    : app.Create.NewTypeBinding(merged);
                if (!RevitCompat.ReInsertKeepGroup(doc.ParameterBindings, existing.Definition, binding))
                {
                    return Item(definition, "failed", "Revit не обновил привязку");
                }
                var extended = Item(definition, "categories_added", "добавлены категории: " + string.Join(", ", added));
                extended.CategoriesAdded = added;
                return extended;
            }

            ProjectParameterInfo sameName = ProjectParameterOperations.List(doc)
                .FirstOrDefault(p => string.Equals(p.Name, definition.Name, StringComparison.CurrentCulture));
            if (sameName != null)
            {
                return Item(definition, "name_conflict",
                    $"в проекте уже есть параметр «{sameName.Name}» с другим GUID ({(sameName.IsShared ? sameName.Guid.ToString() : "параметр проекта")})");
            }

            CategorySet set = app.Create.NewCategorySet();
            foreach (Category c in categories)
            {
                set.Insert(c);
            }
            ElementBinding newBinding = isInstance
                ? (ElementBinding)app.Create.NewInstanceBinding(set)
                : app.Create.NewTypeBinding(set);
            if (!RevitCompat.Insert(doc.ParameterBindings, definition, newBinding, group.Group))
            {
                return Item(definition, "failed", "Revit не добавил привязку");
            }
            var item = Item(definition, "added", $"добавлен как {(isInstance ? "экземпляр" : "тип")}, категорий: {categories.Count}");
            item.CategoriesAdded = categories.Select(c => c.Name).ToList();
            return item;
        }

        private static AddSharedParameterItemResult AddToFamily(
            Document doc, ExternalDefinition definition, ParameterGroupInfo group, AddSharedParametersRequest request)
        {
            FamilyManager manager = doc.FamilyManager;
            FamilyParameter existing = manager.get_Parameter(definition.GUID);
            if (existing != null)
            {
                if (existing.IsInstance == request.IsInstance)
                {
                    return Item(definition, "already_exists", $"уже есть в семействе как {(existing.IsInstance ? "экземпляр" : "тип")}");
                }
                if (!request.AllowKindChange)
                {
                    return Item(definition, "kind_mismatch",
                        $"уже есть как {(existing.IsInstance ? "экземпляр" : "тип")}. Разрешите смену тип/экземпляр, чтобы переключить.");
                }
                if (request.IsInstance)
                {
                    manager.MakeInstance(existing);
                }
                else
                {
                    manager.MakeType(existing);
                }
                return Item(definition, "kind_changed", $"переключён на {(request.IsInstance ? "экземпляр" : "тип")}");
            }

            FamilyParameter sameName = manager.get_Parameter(definition.Name);
            if (sameName != null)
            {
                return Item(definition, "name_conflict",
                    $"в семействе уже есть параметр «{definition.Name}» " + (sameName.IsShared ? $"с другим GUID ({sameName.GUID})" : "(параметр семейства)"));
            }

            RevitCompat.AddFamilyParameter(manager, definition, group.Group, request.IsInstance);
            return Item(definition, "added", $"добавлен как {(request.IsInstance ? "экземпляр" : "тип")}");
        }

        private static ExternalDefinition ResolveDefinition(List<(string group, ExternalDefinition definition)> all, string value)
        {
            string wanted = (value ?? string.Empty).Trim();
            if (Guid.TryParse(wanted, out Guid guid))
            {
                return all.FirstOrDefault(x => x.definition.GUID == guid).definition
                    ?? throw new ArgumentException($"В ФОП нет параметра с GUID {guid}.");
            }

            List<(string group, ExternalDefinition definition)> byName =
                all.Where(x => string.Equals(x.definition.Name, wanted, StringComparison.CurrentCulture)).ToList();
            if (byName.Count == 0)
            {
                int slash = wanted.IndexOf('/');
                if (slash > 0)
                {
                    string groupName = wanted.Substring(0, slash).Trim();
                    string name = wanted.Substring(slash + 1).Trim();
                    byName = all.Where(x =>
                        string.Equals(x.group, groupName, StringComparison.CurrentCulture) &&
                        string.Equals(x.definition.Name, name, StringComparison.CurrentCulture)).ToList();
                }
            }

            if (byName.Count == 0)
            {
                throw new ArgumentException($"В ФОП нет параметра «{wanted}». Список: read_shared_parameter_file.");
            }
            if (byName.Count > 1)
            {
                throw new ArgumentException(
                    $"В ФОП несколько параметров «{wanted}» (группы: {string.Join(", ", byName.Select(x => x.group))}). " +
                    "Укажите GUID или «Группа/Имя».");
            }
            return byName[0].definition;
        }

        private static AddSharedParameterItemResult Item(ExternalDefinition definition, string status, string message) =>
            new AddSharedParameterItemResult
            {
                Parameter = definition.Name,
                Guid = definition.GUID.ToString(),
                Status = status,
                Message = message,
            };

        private static string SafeSpecLabel(ExternalDefinition definition) => RevitCompat.SpecLabel(definition);
    }
}
