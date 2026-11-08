using System;
using System.Collections.Generic;
using System.Globalization;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>Единицы и запись значений параметров по тексту — общие помощники для операций.</summary>
    internal static class RevitText
    {
        private const double MmPerFoot = 304.8;

        public static double ToMm(double feet) => Math.Round(feet * MmPerFoot, 1);

        public static double ToFeet(double mm) => mm / MmPerFoot;

        public static string ValueText(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
            {
                return null;
            }
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.AsString();
                default:
                    return parameter.AsValueString() ?? parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
            }
        }

        /// <summary>
        /// Записывает значение параметра из текста. Возвращает null при успехе, иначе текст предупреждения.
        /// Числа с единицами («5000», «5 м») передаются Revit через SetValueString.
        /// </summary>
        public static string TrySet(Element element, string parameterName, string value) =>
            TrySet(element.LookupParameter(parameterName), parameterName, value);

        public static string TrySet(Parameter parameter, string parameterName, string value)
        {
            if (parameter == null)
            {
                return $"параметр «{parameterName}» не найден";
            }
            if (parameter.IsReadOnly)
            {
                return $"параметр «{parameterName}» только для чтения";
            }

            try
            {
                bool ok;
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        ok = parameter.Set(value ?? string.Empty);
                        break;
                    case StorageType.Integer:
                        ok = TryParseInt(value, out int number)
                            ? parameter.Set(number)
                            : parameter.SetValueString(value);
                        break;
                    case StorageType.Double:
                        ok = parameter.SetValueString(value);
                        break;
                    default:
                        return $"параметр «{parameterName}»: тип {parameter.StorageType} не поддерживается";
                }
                return ok ? null : $"параметр «{parameterName}»: Revit не принял значение «{value}»";
            }
            catch (Exception ex)
            {
                return $"параметр «{parameterName}»: {ex.Message}";
            }
        }

        private static bool TryParseInt(string value, out int number)
        {
            string text = (value ?? string.Empty).Trim().ToLowerInvariant();
            switch (text)
            {
                case "да":
                case "yes":
                case "true":
                    number = 1;
                    return true;
                case "нет":
                case "no":
                case "false":
                    number = 0;
                    return true;
                default:
                    return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number);
            }
        }

        public static Dictionary<string, object> Size(double widthMm, double heightMm) => new Dictionary<string, object>
        {
            ["widthMm"] = Math.Round(widthMm, 1),
            ["heightMm"] = Math.Round(heightMm, 1),
        };
    }
}
