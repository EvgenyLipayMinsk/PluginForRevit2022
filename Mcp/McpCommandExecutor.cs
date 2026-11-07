using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using PluginForRevit2022.Core;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Реестр MCP-команд. Логика — в Core/*Operations (те же операции вызывают кнопки на ленте).
    /// Новая команда: Register(...) здесь + запись в McpServer/tools.json.
    /// Выполняется только в UI-потоке Revit.
    /// </summary>
    internal static class McpCommandExecutor
    {
        private static readonly Dictionary<string, Func<UIApplication, McpArgs, object>> Commands =
            new Dictionary<string, Func<UIApplication, McpArgs, object>>(StringComparer.OrdinalIgnoreCase);

        static McpCommandExecutor()
        {
            Register("ping", Ping);

            // Параметры проекта
            Register("list_project_parameters", ListProjectParameters);
            Register("remove_project_parameter", RemoveProjectParameter);

            // ФОП: общие параметры
            Register("read_shared_parameter_file", ReadSharedParameterFile);
            Register("list_bindable_categories", ListBindableCategories);
            Register("list_parameter_groups", (app, args) => new Dictionary<string, object>
            {
                ["groups"] = SharedParameterOperations.ParameterGroups().Select(g => g.ToDictionary()).ToList(),
            });
            Register("add_shared_parameters", AddSharedParameters);
        }

        public static IEnumerable<string> CommandNames => Commands.Keys;

        private static void Register(string name, Func<UIApplication, McpArgs, object> handler) => Commands[name] = handler;

        public static McpResponse Execute(UIApplication uiApp, McpRequest request)
        {
            string name = request.CommandName?.Trim() ?? string.Empty;
            if (!Commands.TryGetValue(name, out Func<UIApplication, McpArgs, object> handler))
            {
                return McpResponse.Fail(request.RequestId,
                    $"Неизвестная команда: {name}. Доступны: {string.Join(", ", Commands.Keys)}");
            }

            try
            {
                return McpResponse.Ok(request.RequestId, handler(uiApp, new McpArgs(request.Parameters)));
            }
            catch (Exception ex)
            {
                McpLog.Error($"Команда {name} завершилась с ошибкой", ex);
                return McpResponse.Fail(request.RequestId, ex.Message);
            }
        }

        // ---------- Общие ----------

        private static object Ping(UIApplication app, McpArgs args)
        {
            Document doc = app.ActiveUIDocument?.Document;
            return new Dictionary<string, object>
            {
                ["addin"] = "AerBIM Manager (PluginForRevit2022)",
                ["revitVersion"] = app.Application.VersionNumber,
                ["activeDocument"] = doc?.Title,
                ["isFamilyDocument"] = doc?.IsFamilyDocument,
                ["sharedParameterFile"] = app.Application.SharedParametersFilename,
                ["commands"] = Commands.Keys.OrderBy(k => k).ToList(),
            };
        }

        private static Document ActiveDocument(UIApplication app) =>
            app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("В Revit нет открытого документа.");

        private static Document ActiveProject(UIApplication app)
        {
            Document doc = ActiveDocument(app);
            if (doc.IsFamilyDocument)
            {
                throw new InvalidOperationException("Активен документ семейства. Эта команда работает с проектом.");
            }
            return doc;
        }

        // ---------- Параметры проекта ----------

        private static object ListProjectParameters(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("nameContains");
            List<Dictionary<string, object>> items = ProjectParameterOperations.List(doc)
                .Where(p => filter == null || p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(p => p.ToDictionary())
                .ToList();
            return new Dictionary<string, object> { ["document"] = doc.Title, ["count"] = items.Count, ["parameters"] = items };
        }

        private static object RemoveProjectParameter(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            ProjectParameterInfo target = ProjectParameterOperations.Resolve(doc, args.Long("id"), args.String("name"));
            Dictionary<string, object> result = ProjectParameterOperations.Remove(doc, target, args.Bool("dryRun") ?? true);
            if (!(bool)result["dryRun"])
            {
                McpLog.Info($"Удалён параметр проекта «{target.Name}» (id {target.Id})");
            }
            return result;
        }

        // ---------- ФОП ----------

        private static object ReadSharedParameterFile(UIApplication app, McpArgs args)
        {
            var (path, parameters) = SharedParameterOperations.ReadFile(app.Application, args.String("filePath"));
            string filter = args.String("nameContains");
            string group = args.String("group");
            List<Dictionary<string, object>> items = parameters
                .Where(p => filter == null || p.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Where(p => group == null || string.Equals(p.Group, group, StringComparison.CurrentCultureIgnoreCase))
                .Select(p => p.ToDictionary())
                .ToList();
            return new Dictionary<string, object>
            {
                ["file"] = path,
                ["groups"] = parameters.Select(p => p.Group).Distinct().ToList(),
                ["count"] = items.Count,
                ["parameters"] = items,
            };
        }

        private static object ListBindableCategories(UIApplication app, McpArgs args)
        {
            Document doc = ActiveProject(app);
            string filter = args.String("nameContains");
            List<Dictionary<string, object>> items = SharedParameterOperations.BindableCategories(doc)
                .Where(c => filter == null || c.Name.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                .Select(c => new Dictionary<string, object>
                {
                    ["name"] = c.Name,
                    ["builtInCategory"] = SharedParameterOperations.BuiltInCategoryName(c),
                    ["type"] = c.CategoryType.ToString(),
                })
                .ToList();
            return new Dictionary<string, object> { ["count"] = items.Count, ["categories"] = items };
        }

        private static object AddSharedParameters(UIApplication app, McpArgs args)
        {
            Document doc = ActiveDocument(app);
            string binding = args.String("binding") ?? "instance";
            if (binding != "instance" && binding != "type")
            {
                throw new ArgumentException("binding: instance или type.");
            }

            var request = new AddSharedParametersRequest
            {
                FilePath = args.String("filePath"),
                Parameters = args.StringList("parameters"),
                Categories = args.StringList("categories"),
                IsInstance = binding == "instance",
                ParameterGroup = args.String("parameterGroup"),
                AllowKindChange = args.Bool("allowKindChange") ?? false,
                DryRun = args.Bool("dryRun") ?? true,
            };
            AddSharedParametersResult result = SharedParameterOperations.Add(doc, app.Application, request);
            if (!result.DryRun && result.ChangeCount > 0)
            {
                McpLog.Info($"Добавлены параметры из ФОП в «{doc.Title}»: {result.ChangeCount}");
            }
            return result.ToDictionary();
        }
    }
}
