using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace PluginForRevit2022.Core
{
    internal sealed class SouyeRouteLayoutException : InvalidOperationException
    {
        public const string DialogTitle = "Ошибка в раскладке трасс СОУЭ";

        public SouyeRouteLayoutException(string message) : base(message) { }
    }

    internal static class SouyeLineIdentity
    {
        public const int MaxLine = 20;
        private static readonly Guid RackLinkSchemaId = new Guid("e1901d0e-2bac-483d-8a4c-e4adf9f48fe4");
        private const string RackLinkField = "RackUniqueId";

        public static string LinkedRack(Element device)
        {
            Schema schema = Schema.Lookup(RackLinkSchemaId);
            if (schema == null) return null;
            Entity entity = device.GetEntity(schema);
            return entity.IsValid() ? entity.Get<string>(RackLinkField) : null;
        }

        public static void LinkRack(Element device, string rackUniqueId)
        {
            Schema schema = Schema.Lookup(RackLinkSchemaId);
            if (string.IsNullOrEmpty(rackUniqueId))
            {
                if (schema != null && device.GetEntity(schema).IsValid())
                    device.DeleteEntity(schema);
                return;
            }
            if (schema == null)
            {
                var builder = new SchemaBuilder(RackLinkSchemaId);
                builder.SetSchemaName("AerBIMSouyeRackLink");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField(RackLinkField, typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set(RackLinkField, rackUniqueId);
            device.SetEntity(entity);
        }

        public static bool IsDevice(Element element) =>
            element.Category != null && RevitIds.IdOf(element.Category.Id) == (long)BuiltInCategory.OST_NurseCallDevices;

        public static bool IsRack(Element element) =>
            element is FamilyInstance instance && instance.SuperComponent == null &&
            instance.Category != null && RevitIds.IdOf(instance.Category.Id) == (long)BuiltInCategory.OST_ElectricalEquipment;

        public static string Read(Element element, string parameter) => RevitText.FindParameter(element, parameter)?.AsString()?.Trim();

        public static bool Number(string value, out int number) => int.TryParse(value, out number) && number > 0;

        public static bool TryDeviceNumbers(string value, out List<int> numbers)
        {
            numbers = new List<int>();
            if (string.IsNullOrWhiteSpace(value)) return true;
            var seen = new HashSet<int>();
            foreach (string token in value.Split(','))
            {
                if (!Number(token, out int number) || !seen.Add(number)) return false;
                numbers.Add(number);
            }
            return true;
        }

        public static bool RackContains(Element element, int rack) =>
            TryDeviceNumbers(Read(element, DeviceMarkingOperations.ParamController), out var numbers) && numbers.Contains(rack);

        public static string NormalizeBuilding(string building)
        {
            string code = building?.Trim();
            return code != null && Regex.IsMatch(code, @"^[Мм][0-9]+$")
                ? "M" + code.Substring(1)
                : code;
        }

        public static bool SameBuilding(string first, string second) =>
            string.Equals(NormalizeBuilding(first), NormalizeBuilding(second), StringComparison.OrdinalIgnoreCase);

        public static bool MatchesRack(Element element, string building, int rack) =>
            SameBuilding(Read(element, DeviceMarkingOperations.ParamBuilding), building) &&
            RackContains(element, rack);

        public static IEnumerable<FamilyInstance> Racks(Document doc, string comments = null) =>
            new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(rack => IsRack(rack) &&
                    (string.IsNullOrWhiteSpace(comments) || DeviceMarkingOperations.HasComment(rack, comments)));

        public static bool TryLineNumbers(Element element, out int rack, out int line)
        {
            rack = line = 0;
            return IsDevice(element) && Number(Read(element, DeviceMarkingOperations.ParamController), out rack) &&
                Number(Read(element, DeviceMarkingOperations.ParamLine), out line);
        }

        public static bool TryKey(Element element, out string key)
        {
            key = null;
            if (!TryLineNumbers(element, out int rack, out int line)) return false;
            string linked = LinkedRack(element);
            if (!string.IsNullOrEmpty(linked))
            {
                key = $"souye|@{linked}|{rack}|{line}";
                return true;
            }

            string building = Read(element, DeviceMarkingOperations.ParamBuilding);
            if (string.IsNullOrWhiteSpace(building)) return false;
            key = Key(building, rack, line, DeviceMarkingOperations.CommentOf(element));
            return true;
        }

        public static string Key(string building, int rack, int line, string comments) =>
            $"souye|{NormalizeBuilding(building).ToUpperInvariant()}|{rack}|{line}|{(comments ?? "").Trim().ToUpperInvariant()}";

        public static Func<Element, bool> RouteScope(Document doc, string building, int rack, string comments, bool functionalZones = false)
        {
            bool requireBuilding = !functionalZones && Racks(doc, comments).Any(other =>
                RackContains(other, rack) &&
                !string.IsNullOrEmpty(Read(other, DeviceMarkingOperations.ParamBuilding)) &&
                !SameBuilding(Read(other, DeviceMarkingOperations.ParamBuilding), building));
            return element =>
            {
                if (CableWalk.IsTransition(element) || (!string.IsNullOrWhiteSpace(comments) && !DeviceMarkingOperations.HasComment(element, comments))) return false;
                string routeBuilding = Read(element, DeviceMarkingOperations.ParamBuilding);
                if (functionalZones && string.IsNullOrWhiteSpace(building)) return true;
                return string.IsNullOrEmpty(routeBuilding) ? !requireBuilding : SameBuilding(routeBuilding, building);
            };
        }

        public static Func<Element, bool> MarkingRouteFilter(Document doc, string building, int rack, int line, string comments, bool functionalZones = false)
        {
            var inScope = RouteScope(doc, building, rack, comments, functionalZones);
            var lroMatches = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            var routes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType().Where(element => element.Location is LocationCurve && !CableWalk.IsTransition(element));
            foreach (Element element in routes.Where(inScope))
            {
                string grouping = DeviceMarkingOperations.ReadGrouping(element);
                if (string.IsNullOrEmpty(grouping) || lroMatches.ContainsKey(grouping) ||
                    !Regex.IsMatch(grouping, @"(?:^|[,;])\s*ЛРО(?=\s|[.,;]|$)", RegexOptions.IgnoreCase)) continue;

                if (TryMatchRouteGrouping(grouping, rack, line, out bool matches, out bool unrelated))
                    lroMatches.Add(grouping, matches);
                else if (unrelated)
                    lroMatches.Add(grouping, false);
                else
                    errors.Add($"ID {RevitIds.IdOf(element.Id)} — «{grouping}»");
            }

            if (errors.Count > 0)
                throw new SouyeRouteLayoutException(
                    $"Корпус «{building}», прибор {rack}, линия {line}. Ошибок в «{DeviceMarkingOperations.ParamGrouping}»: {errors.Count}.\n\n" +
                    string.Join("\n", errors.Take(15)) +
                    $"\n\nИспользуйте «ЛРО.2» для участка прибора или «ЛРО.2.4» для его линии. Несколько значений указывайте через запятую. Номер линии — от 1 до {MaxLine}.");

            return element =>
            {
                if (!inScope(element)) return false;
                string grouping = DeviceMarkingOperations.ReadGrouping(element);
                if (string.IsNullOrEmpty(grouping)) return false;
                return lroMatches.TryGetValue(grouping, out bool matches) ? matches : MatchesLine(element, rack, line);
            };
        }


        private static bool TryMatchRouteGrouping(string grouping, int rack, int line, out bool matches, out bool unrelated)
        {
            bool valid = !grouping.Contains(";"), identifiable = false, mayBelong = false;
            matches = false;
            foreach (string item in grouping.Split(',', ';'))
            {
                string[] parts = item.Split('.');
                bool hasRack = parts.Length >= 2 &&
                    string.Equals(parts[0].Trim(), "ЛРО", StringComparison.OrdinalIgnoreCase);
                int itemRack = 0;
                hasRack = hasRack && Number(parts[1], out itemRack);
                int itemLine = 0;
                bool hasLine = parts.Length >= 3 && Number(parts[2], out itemLine) && itemLine <= MaxLine;
                bool itemValid = hasRack && (parts.Length == 2 || (parts.Length == 3 && hasLine));
                valid &= itemValid;
                identifiable |= hasRack;
                // Для правильной части принадлежность известна. Ошибка с неразличимым прибором проверяется,
                // за исключением пустого разделителя после явно чужих частей.
                if (hasRack)
                {
                    bool related = itemRack == rack && (!hasLine || itemLine == line);
                    mayBelong |= related;
                    matches |= itemValid && related;
                }
                else if (!string.IsNullOrWhiteSpace(item))
                    mayBelong = true;
            }
            unrelated = identifiable && !mayBelong;
            return valid;
        }

        public static bool MatchesLine(Element element, int rack, int line) =>
            DeviceMarkingOperations.IsLineRoute(element, rack.ToString(), line.ToString());
    }
}
