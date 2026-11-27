using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    internal sealed class ParameterIssue
    {
        public string Name;
        public string Guid;
        /// <summary>missing, categories, type_binding, other_guid.</summary>
        public string Kind;
        public List<BuiltInCategory> RequiredCategories = new List<BuiltInCategory>();
        public List<string> MissingCategoryNames = new List<string>();

        public bool CanFix => Kind == "missing" || Kind == "categories";

        public string Text()
        {
            switch (Kind)
            {
                case "missing": return $"• {Name}: нет в проекте";
                case "categories": return $"• {Name}: не привязан к категориям: {string.Join(", ", MissingCategoryNames)}";
                case "type_binding": return $"• {Name}: привязан к типу, нужен экземпляр (исправьте вручную)";
                default: return $"• {Name}: в проекте параметр с этим именем, но другим GUID (исправьте вручную)";
            }
        }
    }

    /// <summary>Проверка: параметры плагина загружены в проект и привязаны к нужным категориям.</summary>
    internal static class PluginParameterCheck
    {
        public const string SharedParameterFileName = "ARBM_ФОП_2026_v1.1_КЖ.txt";

        private const BuiltInCategory E = BuiltInCategory.OST_ElectricalEquipment;
        private const BuiltInCategory D = BuiltInCategory.OST_DataDevices;
        private const BuiltInCategory M = BuiltInCategory.OST_MechanicalEquipment;
        private const BuiltInCategory F = BuiltInCategory.OST_FireAlarmDevices;
        private const BuiltInCategory C = BuiltInCategory.OST_CommunicationDevices;
        private const BuiltInCategory N = BuiltInCategory.OST_NurseCallDevices;

        // Категории: из описания параметров в ФОП и те, что дополнительно читает кабельный журнал.
        private static readonly (string name, string guid, BuiltInCategory[] categories)[] Required =
        {
            ("ADSK_Этаж", "9eabf56c-a6cd-4b5c-a9d0-e9223e19ea3f", new[] { D, M, E, F, N }),
            ("ADSK_Номинальная мощность", "b4d13aad-0763-4481-b015-63137342d077", new[] { C, N }),
            ("ARBM_ПС_Имя изделия", "35eb571f-b69e-46f0-9e78-5486534c77d5", new[] { D, M, E }),
            ("ARBM_ПСБ_БЦО", "ef58a2c7-310c-4b78-87fe-d42252e94229", new[] { D, M, C, F, N }),
            ("ARBM_ПСБ_Адрес устройства", "e43bd062-2df7-4433-a2fc-0a06f97c14aa", new[] { D, M, C, N }),
            ("ARBM_Корпус", "9b93f917-ea43-402f-9c79-eda536611406", new[] { D, M, F, E, N }),
            ("ARBM_Номер прибора", "2f418ccc-71db-441a-ab67-321c0154782e", new[] { D, M, F, E, N }),
            ("ARBM_Номер линии", "a5f85aa2-4c71-432a-aa66-c2f846bd7bfc", new[] { D, M, F, N }),
            ("ARBM_ЗКПС", "4734826e-02fe-4762-a0e8-d4058fb36926", new[] { D, M, F }),
            ("ARBM_SP_ElementMark", "1984e5cc-9eac-455b-b4c4-51f2bb2f53a6", new[] { D, M, F, C, N }),
        };

        private static readonly Dictionary<string, Guid> SharedGuidsByName = BuildSharedGuidsByName();

        private static Dictionary<string, Guid> BuildSharedGuidsByName()
        {
            var known = Required.ToDictionary(parameter => parameter.name, parameter => new Guid(parameter.guid),
                StringComparer.OrdinalIgnoreCase);
            known.Add("О_Этаж", known[MarkingSettings.DefaultFloorParameter]);
            var groupingGuid = new Guid("3de5f1a4-d560-4fa8-a74f-25d250fb3401");
            known.Add("ADSK_Группирование", groupingGuid);
            known.Add("О_Группирование", groupingGuid);
            return known;
        }

        internal static bool TryGetSharedGuid(string name, out Guid guid) => SharedGuidsByName.TryGetValue(name, out guid);

        internal static bool IsKnownSharedGuid(Guid guid) => SharedGuidsByName.ContainsValue(guid);

        internal static bool IsStandardFloor(string name) =>
            TryGetSharedGuid(name, out Guid guid) && guid == SharedGuidsByName[MarkingSettings.DefaultFloorParameter];

        public static string SharedParameterFilePath =>
            Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), SharedParameterFileName);

        public static List<ParameterIssue> Check(Document doc, bool includePower = true, bool includeDefaultFloor = true,
            IReadOnlyDictionary<string, BuiltInCategory[]> scope = null)
        {
            List<ProjectParameterInfo> parameters = ProjectParameterOperations.List(doc);

            var issues = new List<ParameterIssue>();
            foreach (var (name, guid, categories) in Required)
            {
                if (!includePower && name == "ADSK_Номинальная мощность") continue;
                if (!includeDefaultFloor && name == MarkingSettings.DefaultFloorParameter) continue;
                BuiltInCategory[] required = categories;
                if (scope != null && !scope.TryGetValue(name, out required)) continue;
                Guid expectedGuid = SharedGuidsByName[name];
                ProjectParameterInfo existing = parameters.FirstOrDefault(parameter => parameter.Guid == expectedGuid);
                var issue = new ParameterIssue { Name = existing?.Name ?? name, Guid = guid, RequiredCategories = required.ToList() };
                if (existing == null)
                {
                    issue.Kind = parameters.Any(parameter => TryGetSharedGuid(parameter.Name, out Guid namedGuid) && namedGuid == expectedGuid)
                        ? "other_guid" : "missing";
                    issues.Add(issue);
                    continue;
                }

                ElementBinding binding = existing.Binding;
                if (!existing.IsInstance)
                {
                    issue.Kind = "type_binding";
                    issues.Add(issue);
                    continue;
                }
                issue.MissingCategoryNames = required
                    .Select(c => Category.GetCategory(doc, c))
                    .Where(c => c != null && !binding.Categories.Contains(c))
                    .Select(c => c.Name).ToList();
                if (issue.MissingCategoryNames.Count > 0)
                {
                    issue.Kind = "categories";
                    issues.Add(issue);
                }
            }
            return issues;
        }

        /// <summary>Загружает недостающие параметры из ФОП плагина и добавляет недостающие категории.</summary>
        public static List<string> Fix(Document doc, Application app, IEnumerable<ParameterIssue> issues, bool dryRun)
        {
            string file = SharedParameterFilePath;
            if (!File.Exists(file))
            {
                throw new FileNotFoundException("Рядом с надстройкой нет файла ФОП: " + file);
            }
            var log = new List<string>();
            foreach (ParameterIssue issue in issues.Where(i => i.CanFix))
            {
                AddSharedParametersResult result = SharedParameterOperations.Add(doc, app, new AddSharedParametersRequest
                {
                    FilePath = file,
                    Parameters = new List<string> { issue.Guid },
                    Categories = issue.RequiredCategories.Where(c => Category.GetCategory(doc, c) != null).Select(c => c.ToString()).ToList(),
                    IsInstance = true,
                    DryRun = dryRun,
                });
                log.AddRange(result.Items.Select(i => $"{issue.Name}: {i.Status}" + (string.IsNullOrEmpty(i.Message) ? "" : " — " + i.Message)));
            }
            return log;
        }

        public static string Report(List<ParameterIssue> issues)
        {
            var text = new StringBuilder();
            foreach (ParameterIssue issue in issues) text.AppendLine(issue.Text());
            return text.ToString();
        }

        public static Dictionary<string, object> Run(Document doc, Application app, bool dryRun)
        {
            if (doc.IsFamilyDocument) throw new InvalidOperationException("Проверка выполняется только в проекте.");
            List<ParameterIssue> issues = Check(doc);
            List<string> log = issues.Any(i => i.CanFix) ? Fix(doc, app, issues, dryRun) : new List<string>();
            List<ParameterIssue> left = dryRun ? issues : Check(doc);
            return new Dictionary<string, object>
            {
                ["dryRun"] = dryRun,
                ["ok"] = left.Count == 0,
                ["issues"] = left.Select(i => new Dictionary<string, object>
                {
                    ["parameter"] = i.Name,
                    ["kind"] = i.Kind,
                    ["missingCategories"] = i.MissingCategoryNames,
                    ["canFix"] = i.CanFix,
                }).ToList(),
                ["actions"] = log,
                ["summary"] = left.Count == 0
                    ? "Все параметры плагина загружены и привязаны."
                    : dryRun ? $"Замечаний: {left.Count}. Для исправления повторите с dryRun=false." : $"Осталось замечаний: {left.Count}.",
            };
        }
    }
}
