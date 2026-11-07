using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Параметр проекта: определение, привязанное к категориям (Управление → Параметры проекта).</summary>
    internal sealed class ProjectParameterInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public bool IsShared { get; set; }
        public Guid? Guid { get; set; }
        public bool IsInstance { get; set; }
        public List<string> Categories { get; set; } = new List<string>();
        public string GroupLabel { get; set; }

        internal InternalDefinition Definition { get; set; }
        internal ElementBinding Binding { get; set; }

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["id"] = Id,
            ["name"] = Name,
            ["kind"] = IsShared ? "shared" : "project",
            ["guid"] = Guid?.ToString(),
            ["binding"] = IsInstance ? "instance" : "type",
            ["group"] = GroupLabel,
            ["categories"] = Categories,
        };
    }

    /// <summary>Операции с параметрами проекта. Без UI: используются кнопками и MCP.</summary>
    internal static class ProjectParameterOperations
    {
        public static List<ProjectParameterInfo> List(Document doc)
        {
            var result = new List<ProjectParameterInfo>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is InternalDefinition definition) || !(it.Current is ElementBinding binding))
                {
                    continue;
                }

                var shared = doc.GetElement(definition.Id) as SharedParameterElement;
                result.Add(new ProjectParameterInfo
                {
                    Id = RevitIds.IdOf(definition.Id),
                    Name = definition.Name,
                    IsShared = shared != null,
                    Guid = shared?.GuidValue,
                    IsInstance = binding is InstanceBinding,
                    Categories = binding.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList(),
                    GroupLabel = SafeGroupLabel(definition),
                    Definition = definition,
                    Binding = binding,
                });
            }
            return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        /// <summary>Привязанный параметр проекта с этим GUID общего параметра, или null.</summary>
        public static ProjectParameterInfo FindByGuid(Document doc, Guid guid) =>
            List(doc).FirstOrDefault(p => p.Guid == guid);

        public static ProjectParameterInfo Resolve(Document doc, long? id, string name)
        {
            if (id == null && string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Укажите id или name параметра. Список: list_project_parameters.");
            }

            List<ProjectParameterInfo> all = List(doc);
            if (id != null)
            {
                ProjectParameterInfo byId = all.FirstOrDefault(p => p.Id == id.Value)
                    ?? throw new ArgumentException($"Параметр проекта с id {id} не найден.");
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(byId.Name, name, StringComparison.CurrentCulture))
                {
                    throw new ArgumentException($"id {id} принадлежит параметру «{byId.Name}», а не «{name}».");
                }
                return byId;
            }

            List<ProjectParameterInfo> matches = all.Where(p => string.Equals(p.Name, name, StringComparison.CurrentCulture)).ToList();
            if (matches.Count == 0)
            {
                throw new ArgumentException($"Параметр проекта «{name}» не найден.");
            }
            if (matches.Count > 1)
            {
                throw new ArgumentException(
                    $"Найдено {matches.Count} параметра с именем «{name}» (id: {string.Join(", ", matches.Select(m => m.Id))}). Укажите id.");
            }
            return matches[0];
        }

        /// <summary>
        /// Удаляет параметр проекта. dryRun: удаление в транзакции с откатом — показывает,
        /// что будет удалено вместе с параметром.
        /// </summary>
        public static Dictionary<string, object> Remove(Document doc, ProjectParameterInfo target, bool dryRun)
        {
            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }

            ICollection<ElementId> deleted;
            using (var transaction = new Transaction(doc, $"AerBIM: удалить параметр «{target.Name}»"))
            {
                transaction.Start();
                deleted = doc.Delete(RevitIds.ToElementId(target.Id));
                if (dryRun)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил удаление.");
                }
            }

            List<long> dependents = deleted.Select(RevitIds.IdOf).Where(x => x != target.Id).ToList();
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["parameter"] = target.ToDictionary(),
                ["deletedElementCount"] = deleted.Count,
                ["dependentElementIds"] = dependents.Take(50).ToList(),
                ["message"] = dryRun
                    ? $"Предпросмотр: будет удалён параметр «{target.Name}» и зависимых элементов: {dependents.Count}. " +
                      "Значения параметра во всех элементах будут потеряны. Для удаления повторите с dryRun=false."
                    : $"Параметр «{target.Name}» удалён (зависимых элементов: {dependents.Count}). Отменить можно в Revit через Ctrl+Z.",
            };
        }

        internal static string SafeGroupLabel(Definition definition)
        {
            try
            {
                ForgeTypeId group = definition.GetGroupTypeId();
                return group == null || string.IsNullOrEmpty(group.TypeId) ? "Прочее" : LabelUtils.GetLabelForGroup(group);
            }
            catch
            {
                return null;
            }
        }
    }
}
