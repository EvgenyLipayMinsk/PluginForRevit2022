using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Различия API между версиями Revit в одном месте.
    /// Группы параметров (палитра свойств): в 2021 — перечисление BuiltInParameterGroup,
    /// с 2022 — ForgeTypeId (GroupTypeId).
    /// </summary>
    internal static class RevitCompat
    {
        /// <summary>Группа параметров: ключ (data), подпись, строковый id и «родное» значение API.</summary>
        internal sealed class Group
        {
            public string Key { get; set; }
            public string Label { get; set; }
            public string Id { get; set; }
#if REVIT2021
            public BuiltInParameterGroup Native { get; set; }
#else
            public ForgeTypeId Native { get; set; }
#endif
        }

        public static List<Group> AllGroups()
        {
            var result = new List<Group>();
#if REVIT2021
            foreach (BuiltInParameterGroup g in Enum.GetValues(typeof(BuiltInParameterGroup)).Cast<BuiltInParameterGroup>().Distinct())
            {
                if (g == BuiltInParameterGroup.INVALID)
                {
                    result.Add(new Group { Key = "other", Label = "Прочее", Id = g.ToString(), Native = g });
                    continue;
                }
                string label;
                try { label = LabelUtils.GetLabelFor(g); } catch { continue; }
                if (string.IsNullOrEmpty(label)) continue;
                string name = g.ToString();
                result.Add(new Group
                {
                    Key = (name.StartsWith("PG_") ? name.Substring(3) : name).ToLowerInvariant(),
                    Label = label,
                    Id = name,
                    Native = g,
                });
            }
#else
            result.Add(new Group { Key = "other", Label = "Прочее", Id = string.Empty, Native = new ForgeTypeId(string.Empty) });
            foreach (ForgeTypeId id in ParameterUtils.GetAllBuiltInGroups())
            {
                string label;
                try { label = LabelUtils.GetLabelForGroup(id); } catch { continue; }
                result.Add(new Group { Key = GroupKey(id), Label = label, Id = id.TypeId, Native = id });
            }
#endif
            return result.GroupBy(g => g.Id).Select(g => g.First()).ToList();
        }

#if !REVIT2021
        /// <summary>«autodesk.parameter.group:data-1.0.0» → «data».</summary>
        private static string GroupKey(ForgeTypeId id)
        {
            string typeId = id.TypeId ?? string.Empty;
            int colon = typeId.LastIndexOf(':');
            string key = colon >= 0 ? typeId.Substring(colon + 1) : typeId;
            int dash = key.LastIndexOf('-');
            return dash > 0 ? key.Substring(0, dash) : key;
        }
#endif

        public static string GroupLabel(Definition definition)
        {
            try
            {
#if REVIT2021
                BuiltInParameterGroup g = definition.ParameterGroup;
                return g == BuiltInParameterGroup.INVALID ? "Прочее" : LabelUtils.GetLabelFor(g);
#else
                ForgeTypeId group = definition.GetGroupTypeId();
                return group == null || string.IsNullOrEmpty(group.TypeId) ? "Прочее" : LabelUtils.GetLabelForGroup(group);
#endif
            }
            catch
            {
                return null;
            }
        }

        public static bool Insert(BindingMap map, Definition definition, Binding binding, Group group) =>
            map.Insert(definition, binding, group.Native);

        /// <summary>Обновляет привязку, сохраняя текущую группу параметра.</summary>
        public static bool ReInsertKeepGroup(BindingMap map, InternalDefinition definition, Binding binding) =>
#if REVIT2021
            map.ReInsert(definition, binding, definition.ParameterGroup);
#else
            map.ReInsert(definition, binding, definition.GetGroupTypeId());
#endif

        public static FamilyParameter AddFamilyParameter(FamilyManager manager, ExternalDefinition definition, Group group, bool isInstance) =>
            manager.AddParameter(definition, group.Native, isInstance);

        /// <summary>Тип данных определения параметра (spec).</summary>
        public static ForgeTypeId DataType(Definition definition)
        {
#if REVIT2021
            return definition.GetSpecTypeId();
#else
            return definition.GetDataType();
#endif
        }

        /// <summary>Тип хранения значения по определению параметра (когда нет элемента-образца).</summary>
        public static StorageType StorageOf(Definition definition)
        {
#if REVIT2021
#pragma warning disable CS0618 // ParameterType устарел в 2022+, но в 2021 — основной способ
            switch (definition.ParameterType)
            {
                case ParameterType.Text:
                case ParameterType.URL:
                case ParameterType.MultilineText:
                    return StorageType.String;
                case ParameterType.YesNo:
                case ParameterType.Integer:
                    return StorageType.Integer;
                case ParameterType.Invalid:
                    return StorageType.String;
                case ParameterType.FamilyType:
                case ParameterType.Material:
                    return StorageType.ElementId;
                default:
                    return StorageType.Double;
            }
#pragma warning restore CS0618
#else
            ForgeTypeId spec = definition.GetDataType();
            if (spec == null || string.IsNullOrEmpty(spec.TypeId)) return StorageType.String;
            if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.MultilineText || spec == SpecTypeId.String.Url) return StorageType.String;
            if (spec == SpecTypeId.Boolean.YesNo || spec == SpecTypeId.Int.Integer) return StorageType.Integer;
            if (Category.IsBuiltInCategory(spec) || spec == SpecTypeId.Reference.Material) return StorageType.ElementId;
            if (UnitUtils.IsMeasurableSpec(spec) || spec == SpecTypeId.Number) return StorageType.Double;
            return StorageType.String;
#endif
        }

        /// <summary>Подпись типа данных параметра ФОП (Текст, Длина, …).</summary>
        public static string SpecLabel(ExternalDefinition definition)
        {
            ForgeTypeId spec = null;
            try
            {
#if REVIT2021
                spec = definition.GetSpecTypeId();
#else
                spec = definition.GetDataType();
#endif
                return spec == null ? null : LabelUtils.GetLabelForSpec(spec);
            }
            catch
            {
                return spec?.TypeId;
            }
        }
    }
}
