using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Команды надстройки, доступные через MCP. Выполняется только в UI-потоке Revit.
    /// Чтобы добавить команду: новый case в Execute + метод + инструмент в McpServer/index.js.
    /// </summary>
    internal sealed class McpCommandExecutor
    {
        private const int MaxListedDependents = 50;

        private readonly UIApplication _uiApp;

        public McpCommandExecutor(UIApplication uiApp)
        {
            _uiApp = uiApp;
        }

        public McpResponse Execute(McpRequest request)
        {
            try
            {
                object data;
                switch (request.CommandName.Trim().ToLowerInvariant())
                {
                    case "ping":
                        data = Ping();
                        break;
                    case "list_project_parameters":
                        data = ListProjectParameters(request.Parameters);
                        break;
                    case "remove_project_parameter":
                        data = RemoveProjectParameter(request.Parameters);
                        break;
                    default:
                        return McpResponse.Fail(request.RequestId, $"Неизвестная команда: {request.CommandName}");
                }
                return McpResponse.Ok(request.RequestId, data);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Команда {request.CommandName} завершилась с ошибкой", ex);
                return McpResponse.Fail(request.RequestId, ex.Message);
            }
        }

        private object Ping()
        {
            Document doc = _uiApp.ActiveUIDocument?.Document;
            return new Dictionary<string, object>
            {
                ["addin"] = "AerBIM Manager (PluginForRevit2022)",
                ["revitVersion"] = _uiApp.Application.VersionNumber,
                ["activeDocument"] = doc?.Title,
                ["isFamilyDocument"] = doc?.IsFamilyDocument,
            };
        }

        /// <summary>
        /// Параметры проекта = определения, привязанные к категориям (Управление → Параметры проекта).
        /// </summary>
        private object ListProjectParameters(Dictionary<string, object> parameters)
        {
            Document doc = GetProjectDocument();
            string nameContains = GetString(parameters, "nameContains");

            List<Dictionary<string, object>> items = GetProjectParameters(doc)
                .Where(p => string.IsNullOrEmpty(nameContains) ||
                            p.Name.IndexOf(nameContains, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(p => p.ToDictionary())
                .ToList();

            return new Dictionary<string, object>
            {
                ["document"] = doc.Title,
                ["count"] = items.Count,
                ["parameters"] = items,
            };
        }

        /// <summary>
        /// Удаляет параметр проекта по id или имени.
        /// По умолчанию dryRun = true: удаление выполняется в транзакции и откатывается,
        /// чтобы показать, что именно будет удалено (включая зависимые элементы).
        /// </summary>
        private object RemoveProjectParameter(Dictionary<string, object> parameters)
        {
            Document doc = GetProjectDocument();
            long? id = GetLong(parameters, "id");
            string name = GetString(parameters, "name");
            bool dryRun = GetBool(parameters, "dryRun") ?? true;

            if (id == null && string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Укажите id или name параметра. Список: list_project_parameters.");
            }

            List<ProjectParameterInfo> all = GetProjectParameters(doc);
            ProjectParameterInfo target;
            if (id != null)
            {
                target = all.FirstOrDefault(p => p.Id == id.Value)
                    ?? throw new ArgumentException($"Параметр проекта с id {id} не найден.");
                if (!string.IsNullOrWhiteSpace(name) && !string.Equals(target.Name, name, StringComparison.CurrentCulture))
                {
                    throw new ArgumentException($"id {id} принадлежит параметру «{target.Name}», а не «{name}».");
                }
            }
            else
            {
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
                target = matches[0];
            }

            if (doc.IsReadOnly)
            {
                throw new InvalidOperationException("Документ открыт только для чтения.");
            }

            ICollection<ElementId> deleted;
            using (var transaction = new Transaction(doc, $"AerBIM MCP: удалить параметр «{target.Name}»"))
            {
                transaction.Start();
                deleted = doc.Delete(ToElementId(target.Id));
                if (dryRun)
                {
                    transaction.RollBack();
                }
                else
                {
                    TransactionStatus status = transaction.Commit();
                    if (status != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException($"Revit не применил удаление (статус транзакции: {status}).");
                    }
                }
            }

            List<long> dependentIds = deleted.Select(IdOf).Where(x => x != target.Id).ToList();
            var result = new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["parameter"] = target.ToDictionary(),
                ["deletedElementCount"] = deleted.Count,
                ["dependentElementIds"] = dependentIds.Take(MaxListedDependents).ToList(),
                ["message"] = dryRun
                    ? $"Предпросмотр: будет удалён параметр «{target.Name}» и зависимых элементов: {dependentIds.Count}. " +
                      "Значения параметра во всех элементах будут потеряны. Для удаления повторите с dryRun=false."
                    : $"Параметр «{target.Name}» удалён (зависимых элементов: {dependentIds.Count}). Отменить можно в Revit через Ctrl+Z.",
            };
            if (!dryRun)
            {
                McpLog.Info($"Удалён параметр проекта «{target.Name}» (id {target.Id})");
            }
            return result;
        }

        private Document GetProjectDocument()
        {
            Document doc = _uiApp.ActiveUIDocument?.Document
                ?? throw new InvalidOperationException("В Revit нет открытого документа.");
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Активен документ семейства. Откройте проект.");
            }
            return doc;
        }

        private static List<ProjectParameterInfo> GetProjectParameters(Document doc)
        {
            var result = new List<ProjectParameterInfo>();
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (!(it.Key is InternalDefinition definition) || !(it.Current is ElementBinding binding))
                {
                    continue;
                }

                Element parameterElement = doc.GetElement(definition.Id);
                var shared = parameterElement as SharedParameterElement;
                result.Add(new ProjectParameterInfo
                {
                    Id = IdOf(definition.Id),
                    Name = definition.Name,
                    IsShared = shared != null,
                    Guid = shared?.GuidValue.ToString(),
                    IsInstance = binding is InstanceBinding,
                    Categories = binding.Categories.Cast<Category>().Select(c => c.Name).OrderBy(n => n).ToList(),
                });
            }
            return result;
        }

        private sealed class ProjectParameterInfo
        {
            public long Id { get; set; }
            public string Name { get; set; }
            public bool IsShared { get; set; }
            public string Guid { get; set; }
            public bool IsInstance { get; set; }
            public List<string> Categories { get; set; }

            public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
            {
                ["id"] = Id,
                ["name"] = Name,
                ["kind"] = IsShared ? "shared" : "project",
                ["guid"] = Guid,
                ["binding"] = IsInstance ? "instance" : "type",
                ["categories"] = Categories,
            };
        }

        // ---- ElementId: Revit 2024 перешёл на 64-битные id ----

        private static long IdOf(ElementId id)
        {
#if REVIT2024
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        private static ElementId ToElementId(long id)
        {
#if REVIT2024
            return new ElementId(id);
#else
            return new ElementId(checked((int)id));
#endif
        }

        // ---- Разбор параметров запроса (JavaScriptSerializer отдаёт int/long/decimal/string/bool) ----

        private static string GetString(Dictionary<string, object> parameters, string key) =>
            parameters != null && parameters.TryGetValue(key, out object value) ? value?.ToString() : null;

        private static long? GetLong(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out object value) || value == null)
            {
                return null;
            }
            if (value is string s && string.IsNullOrWhiteSpace(s))
            {
                return null;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        private static bool? GetBool(Dictionary<string, object> parameters, string key)
        {
            if (parameters == null || !parameters.TryGetValue(key, out object value) || value == null)
            {
                return null;
            }
            return value is bool b ? b : bool.Parse(value.ToString());
        }
    }
}
