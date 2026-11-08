using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace PluginForRevit2022.Core
{
    internal sealed class TagRoomsRequest
    {
        public string View { get; set; }
        /// <summary>Связи (часть имени); пусто — все загруженные.</summary>
        public List<string> Links { get; set; } = new List<string>();
        /// <summary>Также помещения самого проекта.</summary>
        public bool IncludeHost { get; set; } = true;
        /// <summary>Тип марки: «Семейство : Тип» или имя типа; пусто — тип по умолчанию.</summary>
        public string TagType { get; set; }
        /// <summary>Не ставить марку, если помещение уже замаркировано на этом виде.</summary>
        public bool SkipTagged { get; set; } = true;
        /// <summary>location — точка вставки помещения; center — центр габарита.</summary>
        public string Placement { get; set; } = "location";
        /// <summary>Допуск совпадения отметки уровня помещения и плана, мм.</summary>
        public double LevelToleranceMm { get; set; } = 100;
        /// <summary>Имя уровня помещений (в связи), если отметки не совпадают с уровнем плана.</summary>
        public string RoomLevel { get; set; }
        public bool DryRun { get; set; } = true;
    }

    /// <summary>Марки помещений на плане текущего проекта для помещений из связанных файлов (и из самого проекта).</summary>
    internal static class RoomTagOperations
    {
        public static Dictionary<string, object> TagRooms(Document doc, TagRoomsRequest request)
        {
            ViewPlan view = LinkScope.RequirePlan(ViewOperations.ResolveView(doc, request.View, ViewType.FloorPlan));
            var warnings = new List<string>();
            if (view.GetCategoryHidden(new ElementId(BuiltInCategory.OST_RoomTags)))
            {
                warnings.Add("на виде скрыта категория «Марки помещений»: марки будут созданы, но не видны");
            }
            FamilySymbol tagType = ResolveTagType(doc, request.TagType);
            double cutZ = LinkScope.ViewRangeZ(doc, view).cut;
            double tolerance = RevitText.ToFeet(request.LevelToleranceMm);
            string placement = (request.Placement ?? "location").Trim().ToLowerInvariant();

            var tagged = new HashSet<string>(ExistingTagKeys(doc, view));
            var sources = new List<(RevitLinkInstance link, Document source, Transform transform)>();
            if (request.IncludeHost)
            {
                sources.Add((null, doc, Transform.Identity));
            }
            foreach (RevitLinkInstance link in LinkScope.ResolveLinks(doc, request.Links, warnings))
            {
                sources.Add((link, link.GetLinkDocument(), link.GetTotalTransform()));
            }

            var perSource = new List<Dictionary<string, object>>();
            int created = 0;
            using (var transaction = new Transaction(doc, "AerBIM: марки помещений"))
            {
                transaction.Start();
                foreach (var (link, source, transform) in sources)
                {
                    int found = 0, already = 0, otherLevel = 0, outside = 0, unplaced = 0, failed = 0, made = 0;
                    var levelStats = new Dictionary<string, (int count, double bottom, double top)>();
                    // Список собирается заранее: создание марок меняет базу и ломает «ленивый» коллектор.
                    List<Room> rooms = new FilteredElementCollector(source).OfCategory(BuiltInCategory.OST_Rooms)
                        .WhereElementIsNotElementType().OfType<Room>().ToList();

                    foreach (Room room in rooms)
                    {
                        if (room.Area <= 0 || !(room.Location is LocationPoint location))
                        {
                            unplaced++;
                            continue;
                        }

                        XYZ point = transform.OfPoint(location.Point);
                        BoundingBoxXYZ volume = room.get_BoundingBox(null);
                        double bottom = volume != null ? transform.OfPoint(volume.Min).Z : point.Z;
                        double top = volume != null ? transform.OfPoint(volume.Max).Z : bottom;

                        string levelName = room.Level?.Name ?? "—";
                        levelStats[levelName] = levelStats.TryGetValue(levelName, out var s)
                            ? (s.count + 1, Math.Min(s.bottom, bottom), Math.Max(s.top, top))
                            : (1, bottom, top);

                        bool levelOk = !string.IsNullOrWhiteSpace(request.RoomLevel)
                            ? string.Equals(levelName, request.RoomLevel.Trim(), StringComparison.CurrentCultureIgnoreCase)
                            // Помещение на плане, если секущая плоскость плана проходит через его объём
                            // (уровень плана может не совпадать с уровнем АР: например, «над фальшполом»).
                            : cutZ >= bottom - tolerance && cutZ <= top + tolerance;
                        if (!levelOk)
                        {
                            otherLevel++;
                            continue;
                        }
                        found++;

                        if (placement == "center")
                        {
                            BoundingBoxXYZ box = room.get_BoundingBox(null);
                            if (box != null)
                            {
                                point = transform.OfPoint((box.Min + box.Max) / 2);
                            }
                        }
                        if (!LinkScope.InsideCrop(view, point))
                        {
                            outside++;
                            continue;
                        }

                        string key = Key(link?.Id, room.Id);
                        if (request.SkipTagged && tagged.Contains(key))
                        {
                            already++;
                            continue;
                        }

                        try
                        {
                            LinkElementId roomId = link == null ? new LinkElementId(room.Id) : new LinkElementId(link.Id, room.Id);
                            RoomTag tag = doc.Create.NewRoomTag(roomId, new UV(point.X, point.Y), view.Id);
                            if (tag == null)
                            {
                                failed++;
                                continue;
                            }
                            if (tagType != null && tag.GetTypeId() != tagType.Id)
                            {
                                tag.ChangeTypeId(tagType.Id);
                            }
                            tagged.Add(key);
                            made++;
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            warnings.Add($"«{room.Name}»: {ex.Message}");
                        }
                    }

                    created += made;
                    perSource.Add(new Dictionary<string, object>
                    {
                        ["source"] = link == null ? "проект" : link.Name,
                        ["roomsOnLevel"] = found,
                        ["tagged"] = made,
                        ["alreadyTagged"] = already,
                        ["outsideCrop"] = outside,
                        ["otherLevels"] = otherLevel,
                        ["notPlaced"] = unplaced,
                        ["failed"] = failed,
                        // Диагностика: уровни помещений источника и их высотный диапазон
                        // (абсолютные отметки во внутренних координатах проекта) — сравнить с cutPlaneMm.
                        ["roomLevels"] = levelStats.OrderBy(l => l.Value.bottom).Select(l => new Dictionary<string, object>
                        {
                            ["level"] = l.Key,
                            ["rooms"] = l.Value.count,
                            ["bottomMm"] = RevitText.ToMm(l.Value.bottom),
                            ["topMm"] = RevitText.ToMm(l.Value.top),
                        }).ToList(),
                    });
                }

                if (request.DryRun || created == 0)
                {
                    transaction.RollBack();
                }
                else if (transaction.Commit() != TransactionStatus.Committed)
                {
                    throw new InvalidOperationException("Revit не применил создание марок.");
                }
            }

            if (sources.Count == 0)
            {
                warnings.Add("нет источников помещений: связи не загружены, includeHost=false");
            }
            if (perSource.All(s => (int)s["roomsOnLevel"] == 0 && (int)s["otherLevels"] == 0 && (int)s["notPlaced"] == 0))
            {
                warnings.Add("в выбранных источниках нет помещений (проект/связи)");
            }
            else if (perSource.All(s => (int)s["roomsOnLevel"] == 0))
            {
                warnings.Add($"секущая плоскость плана «{view.Name}» не проходит ни через одно помещение: укажите roomLevel (уровень помещений в связи)");
            }

            return new Dictionary<string, object>
            {
                ["dryRun"] = request.DryRun,
                ["view"] = view.Name,
                ["level"] = view.GenLevel.Name,
                ["tagType"] = tagType == null ? "по умолчанию" : $"{tagType.FamilyName} : {tagType.Name}",
                ["created"] = created,
                ["cutPlaneMm"] = RevitText.ToMm(cutZ),
                ["sources"] = perSource,
                ["warnings"] = warnings.Distinct().Take(50).ToList(),
                ["summary"] = request.DryRun
                    ? $"Предпросмотр: будет поставлено марок: {created}. Для создания повторите с dryRun=false."
                    : $"Поставлено марок: {created}. Отменить можно в Revit через Ctrl+Z.",
            };
        }

        public static List<Dictionary<string, object>> ListRoomTagTypes(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).OfCategory(BuiltInCategory.OST_RoomTags)
                .Cast<FamilySymbol>()
                .OrderBy(s => s.FamilyName).ThenBy(s => s.Name)
                .Select(s => new Dictionary<string, object> { ["id"] = RevitIds.IdOf(s.Id), ["name"] = $"{s.FamilyName} : {s.Name}" })
                .ToList();

        private static IEnumerable<string> ExistingTagKeys(Document doc, View view)
        {
            foreach (RoomTag tag in new FilteredElementCollector(doc, view.Id).OfCategory(BuiltInCategory.OST_RoomTags)
                         .WhereElementIsNotElementType().OfType<RoomTag>())
            {
                LinkElementId id = tag.TaggedRoomId;
                if (id == null) continue;
                yield return id.LinkInstanceId == ElementId.InvalidElementId
                    ? Key(null, id.HostElementId)
                    : Key(id.LinkInstanceId, id.LinkedElementId);
            }
        }

        private static string Key(ElementId linkId, ElementId roomId) =>
            (linkId == null || linkId == ElementId.InvalidElementId ? "host" : RevitIds.IdOf(linkId).ToString()) + ":" + RevitIds.IdOf(roomId);

        private static FamilySymbol ResolveTagType(Document doc, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string wanted = value.Trim();
            List<FamilySymbol> all = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol))
                .OfCategory(BuiltInCategory.OST_RoomTags).Cast<FamilySymbol>().ToList();
            List<FamilySymbol> matches = all.Where(s =>
                string.Equals($"{s.FamilyName} : {s.Name}", wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                matches = all.Where(s => string.Equals(s.Name, wanted, StringComparison.CurrentCultureIgnoreCase)).ToList();
            }
            if (matches.Count == 1) return matches[0];
            throw new ArgumentException(matches.Count == 0
                ? $"Тип марки помещения «{wanted}» не найден. Список: list_room_tag_types."
                : $"«{wanted}» неоднозначно — укажите «Семейство : Тип».");
        }
    }
}
