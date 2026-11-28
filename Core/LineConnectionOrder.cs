using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace PluginForRevit2022.Core
{
    internal static class LineConnectionOrder
    {
        private static readonly Guid SchemaId = new Guid("552a6388-e769-42d4-bba2-ef5b600defcf");

        internal sealed class Entry
        {
            public DataStorage Storage;
            public List<string> Order;
        }

        internal static Dictionary<string, Entry> Read(Document doc)
        {
            var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            var schema = Schema.Lookup(SchemaId);
            if (schema == null) return result;
            foreach (var storage in new FilteredElementCollector(doc).OfClass(typeof(DataStorage)).Cast<DataStorage>())
            {
                var entity = storage.GetEntity(schema);
                if (!entity.IsValid()) continue;
                string key = entity.Get<string>("LineKey");
                if (result.ContainsKey(key)) throw new InvalidOperationException("В модели несколько записей порядка для линии " + key + ".");
                result.Add(key, new Entry { Storage = storage, Order = entity.Get<IList<string>>("Devices").ToList() });
            }
            return result;
        }

        internal static void Save(Document doc, string key, IReadOnlyList<string> order)
        {
            var entries = Read(doc);
            entries.TryGetValue(key, out Entry existing);
            if (order == null)
            {
                if (existing != null) doc.Delete(existing.Storage.Id);
                return;
            }
            var schema = Schema.Lookup(SchemaId);
            if (schema == null)
            {
                var builder = new SchemaBuilder(SchemaId);
                builder.SetSchemaName("AerBIMApsConnectionOrder");
                builder.SetReadAccessLevel(AccessLevel.Public);
                builder.SetWriteAccessLevel(AccessLevel.Public);
                builder.AddSimpleField("LineKey", typeof(string));
                builder.AddArrayField("Devices", typeof(string));
                schema = builder.Finish();
            }
            var entity = new Entity(schema);
            entity.Set("LineKey", key);
            entity.Set<IList<string>>("Devices", order.ToList());
            (existing?.Storage ?? DataStorage.Create(doc)).SetEntity(entity);
        }

        internal static int Address(FamilyInstance device) =>
            int.TryParse(RevitText.FindParameter(device, DeviceMarkingOperations.ParamMark)?.AsString(), out int address) ? address : 0;

        internal static List<FamilyInstance> Ordered(IEnumerable<FamilyInstance> devices, IReadOnlyList<string> order, bool checkAddresses = true)
        {
            var byId = devices.ToDictionary(d => d.UniqueId);
            if (order.Count != byId.Count || order.Distinct().Count() != order.Count || order.Any(id => !byId.ContainsKey(id)))
                throw new InvalidOperationException("Состав линии изменился. Проверьте и подтвердите порядок подключения заново.");
            if (checkAddresses) ValidateAddresses(byId.Values);
            return order.Select(id => byId[id]).ToList();
        }

        internal static void ValidateAddresses(IEnumerable<FamilyInstance> devices) =>
            Parse(string.Join(",", devices.Select(Address)), devices);

        internal static List<string> Parse(string text, IEnumerable<FamilyInstance> devices)
        {
            var groups = devices.GroupBy(Address).ToDictionary(g => g.Key, g => g.ToList());
            if (groups.ContainsKey(0) || groups.Keys.Any(n => n < 0))
                throw new ArgumentException("В шлейфе есть устройства без положительного числового адреса.");
            var duplicate = groups.Where(g => g.Value.Count > 1).Select(g => g.Key).OrderBy(x => x).ToList();
            if (duplicate.Count > 0) throw new ArgumentException("Повторяющиеся адреса в модели: " + string.Join(", ", duplicate));
            var seen = new HashSet<int>();
            var result = new List<string>();
            foreach (string token in Regex.Split(text.Trim(), @"[\s,;→]+").Where(s => s.Length > 0))
            {
                if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out int address) || !groups.TryGetValue(address, out var group))
                    throw new ArgumentException("В выбранном шлейфе нет адреса «" + token + "».");
                if (!seen.Add(address)) throw new ArgumentException("Адрес " + address + " указан несколько раз.");
                result.Add(group[0].UniqueId);
            }
            var missing = groups.Keys.Except(seen).OrderBy(x => x).ToList();
            if (missing.Count > 0) throw new ArgumentException("Добавьте адреса: " + string.Join(", ", missing));
            return result;
        }

        internal static List<string> ParseIds(string text, IEnumerable<FamilyInstance> devices)
        {
            var byId = devices.ToDictionary(d => RevitIds.IdOf(d.Id));
            var seen = new HashSet<long>();
            var result = new List<string>();
            foreach (string token in Regex.Split(text.Trim(), @"[\s,;→]+").Where(s => s.Length > 0))
            {
                if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || !byId.TryGetValue(id, out var device))
                    throw new ArgumentException("В выбранном шлейфе нет устройства с ID «" + token + "».");
                if (!seen.Add(id)) throw new ArgumentException("ID " + id + " указан несколько раз.");
                result.Add(device.UniqueId);
            }
            var missing = byId.Keys.Except(seen).OrderBy(x => x).ToList();
            if (missing.Count > 0) throw new ArgumentException("Добавьте устройства с ID: " + string.Join(", ", missing));
            return result;
        }

        internal sealed class Suggestion
        {
            public List<FamilyInstance> Devices = new List<FamilyInstance>();
            public HashSet<string> Estimated = new HashSet<string>();
            public HashSet<string> Review = new HashSet<string>();
            public string Hint;
        }

        internal static Suggestion Suggest(Document doc, FamilyInstance controller, List<FamilyInstance> devices,
            string key, List<FamilyInstance> retained, bool estimateFromPlan, bool useRouteGrouping)
        {
            var suggestion = new Suggestion();
            if (devices.Count == 0)
            {
                suggestion.Hint = "В шлейфе нет устройств.";
                return suggestion;
            }

            if (!(controller?.Location is LocationPoint))
            {
                suggestion.Devices = retained ?? new List<FamilyInstance>();
                suggestion.Hint = "У прибора нет однозначной точки начала. Укажите недостающий порядок вручную.";
                return suggestion;
            }
            string comment = DeviceMarkingOperations.CommentOf(devices[0]);
            var routes = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(c => c.Location is LocationCurve && c.Symbol.FamilyName != CableWalk.TransitionFamily &&
                    DeviceMarkingOperations.HasComment(c, comment) && (!useRouteGrouping || IsRoute(c, key))).ToList();
            var walk = CableWalk.Order(doc, routes, controller, devices, out var missed, out string problem, comment);
            if (!estimateFromPlan)
            {
                suggestion.Devices = walk ?? new List<FamilyInstance>();
                suggestion.Hint = problem != null ? "Обход трассы: " + problem + ". Укажите порядок вручную." :
                    missed.Count > 0 ? "Обход не нашёл адреса: " + string.Join(", ", missed.Select(Address)) + ". Добавьте их в нужные места." :
                    "Порядок предложен по трассе. Сверьте направление обхода и развилки.";
                return suggestion;
            }
            if (retained == null && walk != null && missed.Count == 0)
            {
                suggestion.Devices = walk;
                suggestion.Hint = "Порядок предложен по трассе. Сверьте направление обхода и развилки.";
                return suggestion;
            }

            suggestion.Devices = retained ?? walk ?? new List<FamilyInstance>();
            var known = new HashSet<string>(suggestion.Devices.Select(device => device.UniqueId));
            List<FamilyInstance> remaining = devices.Where(device => !known.Contains(device.UniqueId))
                .OrderBy(Address).ThenBy(device => RevitIds.IdOf(device.Id)).ToList();
            int retainedCount = suggestion.Devices.Count;
            if (remaining.Count == 0)
            {
                suggestion.Hint = "Сохранён относительный порядок оставшихся устройств. Проверьте соединения.";
                return suggestion;
            }

            string floorParameter = MarkingSettings.CurrentFloorParameter();
            PlanConnectionOrder.Point PointOf(FamilyInstance device, int index)
            {
                if (!(device.Location is LocationPoint location)) return null;
                XYZ point = location.Point;
                return new PlanConnectionOrder.Point
                {
                    Index = index, Level = CableWalk.LevelOf(device, floorParameter),
                    X = point.X, Y = point.Y, Z = point.Z,
                };
            }

            var start = suggestion.Devices.Select(device => PointOf(device, -1)).LastOrDefault(point => point != null)
                ?? PointOf(controller, -1);
            var points = remaining.Select(PointOf).ToList();
            PlanConnectionOrder.Point Shaft(CableWalk.ShaftPort port) => new PlanConnectionOrder.Point
            {
                Index = -1, Level = port.Level, X = port.Point.X, Y = port.Point.Y, Z = port.Point.Z,
            };
            var tunnelPlan = CableWalk.PlanTunnels(doc, Array.Empty<CableWalk.RouteSpan>(), comment);
            var transitions = tunnelPlan.Shafts.Select(pair => (Shaft(pair.Lower), Shaft(pair.Upper)));
            var estimated = PlanConnectionOrder.Build(start, points.Where(point => point != null), transitions, out var review);
            for (int i = 0; i < points.Count; i++)
            {
                if (points[i] != null) continue;
                estimated.Add(i);
                review.Add(i);
            }

            suggestion.Devices.AddRange(estimated.Select(i => remaining[i]));
            suggestion.Estimated.UnionWith(remaining.Select(device => device.UniqueId));
            suggestion.Review.UnionWith(review.Select(i => remaining[i].UniqueId));
            if (retained != null)
                suggestion.Hint = $"Сохранён относительный порядок {retainedCount} устройств. Новые адреса добавлены в конец по плану. Проверьте их место в шлейфе.";
            else if (retainedCount > 0)
                suggestion.Hint = $"По трассе найдено {retainedCount} из {devices.Count} устройств. Остальные добавлены в конец по плану. Проверьте их место в шлейфе.";
            else
                suggestion.Hint = (problem == null ? "" : "Обход трассы: " + problem + ". ") +
                    "Подставлен черновик по расположению устройств на плане. Проверьте фактический порядок подключения.";
            if (points.Any(point => point == null))
                suggestion.Hint += " Устройства без точки на плане добавлены в конец по адресам.";
            return suggestion;
        }

        internal static bool IsRoute(Element route, string key)
        {
            string[] parts = key.Split('|');
            string building = RevitText.FindParameter(route, DeviceMarkingOperations.ParamBuilding)?.AsString()?.Trim();
            return DeviceMarkingOperations.IsLineRoute(route, parts[2], parts[3]) &&
                (string.IsNullOrEmpty(building) || string.Equals(building, parts[1], StringComparison.OrdinalIgnoreCase));
        }

        internal static FamilyInstance Controller(Document doc, string key, string comment)
        {
            string[] parts = key.Split('|');
            if (!int.TryParse(parts[2], out int number)) return null;
            var parser = new ARBM.CableJournalExporter.Services.ApsElementParser();
            var matches = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_ElectricalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>()
                .Where(d => d.SuperComponent == null && d.Location is LocationPoint && DeviceMarkingOperations.HasComment(d, comment))
                .Where(d =>
                {
                    var id = parser.ParseApsControllerId(d);
                    return id != null && id.DeviceNumber == number && string.Equals(id.BuildingNumber, parts[1], StringComparison.OrdinalIgnoreCase);
                }).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }
    }
}
