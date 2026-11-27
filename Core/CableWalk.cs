using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace PluginForRevit2022.Core
{
    /// <summary>
    /// Порядок устройств по ходу кабеля: кабель линии собирается из отрезков (семейств кабеля) только там,
    /// где они реально стыкуются (конец к концу или конец в середину, допуск 100 мм), устройства и межэтажные
    /// переходы встают на ближайший отрезок своего этажа, затем кабель обходится от прибора.
    /// Устройство получает номер, когда кабель впервые к нему подходит: на цепочке — подряд, на ответвлении —
    /// ветка до конца и назад. Без допусков графа журнала, поэтому соседние кабели через стену не путаются.
    /// </summary>
    internal static class CableWalk
    {
        internal const string TransitionFamily = "ARBM_Межэтажный переход";
        internal const string TunnelErrorTitle = "Ошибка структуры уровней";
        internal sealed class TunnelException : InvalidOperationException
        {
            public TunnelException(string message) : base(message) { }
        }
        private static readonly double Join = 100 / 304.8;      // стык отрезков
        private static readonly double Attach = 600 / 304.8;    // устройство сбоку от кабеля
        private static readonly double ControllerAttach = 3000 / 304.8;

        private const double FeetPerMillimeter = 1.0 / 304.8;
        private const double ShaftAlignment = 300 * FeetPerMillimeter;
        private const double PortAmbiguity = 25 * FeetPerMillimeter;
        private const double SamePort = 10 * FeetPerMillimeter;
        private const double LaneAlignment = 100 * FeetPerMillimeter;


        internal sealed class RouteSpan
        {
            public XYZ A;
            public XYZ B;
            public long Level;
            public long ElementId;
            public string Comment;
        }

        internal sealed class ShaftPort
        {
            public long Id;
            public long Level;
            public XYZ Point;
            public string Comment;
            public int Span = -1;
            public XYZ Projection;
        }

        internal sealed class TunnelPlan
        {
            public readonly List<(ShaftPort Lower, ShaftPort Upper)> Links = new List<(ShaftPort, ShaftPort)>();
            public readonly List<(ShaftPort Lower, ShaftPort Upper)> Shafts = new List<(ShaftPort, ShaftPort)>();
            public readonly List<string> Issues = new List<string>();
            public readonly List<long> MarkerIds = new List<long>();

            public string Details => (Issues.Count == 0 ? "" : string.Join(" ", Issues.Take(8)) + " ") +
                (MarkerIds.Count == 0 ? "Подходящие экземпляры переходов по «Комментарии» отсутствуют." :
                    $"ID переходов: {string.Join(", ", MarkerIds.Take(20))}.");
        }

        internal static bool IsTransition(Element element) =>
            element is FamilyInstance instance && instance.Symbol.FamilyName == TransitionFamily;

        private static string Comments(Element element) => DeviceMarkingOperations.CommentOf(element);

        internal static TunnelPlan PlanTunnels(Document doc, IReadOnlyList<RouteSpan> spans, string comment = null)
        {
            var plan = new TunnelPlan();
            var portGroups = new Dictionary<string, List<ShaftPort>>(StringComparer.OrdinalIgnoreCase);
            string floorParameter = MarkingSettings.CurrentFloorParameter();
            var levels = ProjectLevels(doc);
            foreach (FamilyInstance transition in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().Where(IsTransition))
            {
                string system = Comments(transition);
                if (!string.IsNullOrWhiteSpace(comment) && !string.Equals(system, comment.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                long id = RevitIds.IdOf(transition.Id);
                plan.MarkerIds.Add(id);
                var position = transition.Location as LocationPoint;
                Level level = ConfiguredLevel(transition, floorParameter, levels, requireUnique: true);
                if (position == null || level == null)
                {
                    plan.Issues.Add($"Переход ID {id}: укажите точку размещения и однозначный «{floorParameter}».");
                    continue;
                }

                var port = new ShaftPort { Id = id, Level = RevitIds.IdOf(level.Id), Point = position.Point, Comment = system };
                if (!portGroups.TryGetValue(system, out var ports))
                    portGroups.Add(system, ports = new List<ShaftPort>());
                ports.Add(port);
            }

            // Сначала проверяем геометрию шахты, включая точки без кабеля. Такой конец не заменяется соседней шахтой.
            var levelsById = levels.ToDictionary(level => RevitIds.IdOf(level.Id));
            var floorNumbers = levelsById.ToDictionary(pair => pair.Key, pair => FloorNumber(doc, pair.Value));
            bool Consecutive(long lower, long upper)
            {
                Level a = levelsById[lower], b = levelsById[upper];
                double z1 = a.ProjectElevation, z2 = b.ProjectElevation;
                if (z2 - z1 <= SamePort) return false;
                bool hasBetween = levelsById.Any(pair => pair.Value.ProjectElevation > z1 + SamePort &&
                    pair.Value.ProjectElevation < z2 - SamePort &&
                    (!floorNumbers[lower].HasValue || !floorNumbers[upper].HasValue || floorNumbers[pair.Key].HasValue));
                if (hasBetween) return false;

                int? n1 = floorNumbers[lower], n2 = floorNumbers[upper];
                return !n1.HasValue || !n2.HasValue || Math.Abs((long)n2.Value - n1.Value) == 1 ||
                    (n1 == -1 && n2 == 1 && !floorNumbers.ContainsValue(0));
            }

            var spansByLevel = spans.Select((span, index) => (Span: span, Index: index))
                .GroupBy(item => item.Span.Level).ToDictionary(group => group.Key, group => group.ToList());
            bool SameAxis(RouteSpan a, RouteSpan b)
            {
                XYZ u = a.B - a.A, v = b.B - b.A;
                double length = Math.Sqrt((u.X * u.X + u.Y * u.Y) * (v.X * v.X + v.Y * v.Y));
                if (length < 1e-9 || Math.Abs(u.X * v.Y - u.Y * v.X) > length * 0.01) return false;
                return Math.Min(Math.Min(Plan(a.A, b.A), Plan(a.A, b.B)),
                    Math.Min(Plan(a.B, b.A), Plan(a.B, b.B))) <= SamePort;
            }

            List<ShaftPort> SpansAt(ShaftPort marker, IReadOnlyList<ShaftPort> floorMarkers)
            {
                var ports = new List<ShaftPort>();
                if (!spansByLevel.TryGetValue(marker.Level, out var candidates)) return ports;
                var nearby = candidates.Where(candidate => string.Equals(candidate.Span.Comment, marker.Comment, StringComparison.OrdinalIgnoreCase))
                    .Select(candidate =>
                    {
                        var span = candidate.Span;
                        XYZ projection = span.A + Projection(marker.Point, span.A, span.B) * (span.B - span.A);
                        return (candidate.Index, Projection: projection, Distance: Plan(marker.Point, projection));
                    }).Where(candidate => candidate.Distance <= Join)
                    .OrderBy(candidate => candidate.Distance).ThenBy(candidate => spans[candidate.Index].ElementId);

                foreach (var candidate in nearby)
                {
                    if (ports.Any(port => Plan(port.Projection, candidate.Projection) <= SamePort ||
                        SameAxis(spans[port.Span], spans[candidate.Index]))) continue;
                    var closer = floorMarkers.FirstOrDefault(other => other != marker &&
                        Plan(other.Point, candidate.Projection) <= candidate.Distance + PortAmbiguity &&
                        Plan(other.Point, marker.Point) > SamePort);
                    if (closer != null)
                    {
                        if (Math.Abs(Plan(closer.Point, candidate.Projection) - candidate.Distance) <= PortAmbiguity)
                            plan.Issues.Add($"Трасса ID {spans[candidate.Index].ElementId}: неоднозначное примыкание к шахтам ID {marker.Id}, {closer.Id}.");
                        continue;
                    }
                    ports.Add(new ShaftPort
                    {
                        Id = marker.Id, Level = marker.Level, Point = marker.Point, Comment = marker.Comment,
                        Span = candidate.Index, Projection = candidate.Projection,
                    });
                }
                return ports;
            }

            void LinkLanes(ShaftPort a, ShaftPort b, List<ShaftPort> lower, List<ShaftPort> upper)
            {
                if (lower.Count == 0 || upper.Count == 0)
                {
                    if (lower.Count != 0 || upper.Count != 0)
                        plan.Issues.Add($"Переходы ID {a.Id}, {b.Id}: к одному концу не подходит подходящий участок трассы.");
                    return;
                }

                ShaftPort ClosestLane(ShaftPort from, List<ShaftPort> to)
                {
                    XYZ offset = from.Projection - from.Point;
                    var options = to.Select(option => (Port: option, Distance: Plan(offset, option.Projection - option.Point)))
                        .Where(option => option.Distance <= LaneAlignment).OrderBy(option => option.Distance).Take(2).ToList();
                    if (options.Count == 0 || (options.Count == 2 && options[1].Distance <= options[0].Distance + PortAmbiguity)) return null;
                    return options[0].Port;
                }

                var matched = new HashSet<ShaftPort>();
                foreach (ShaftPort start in lower)
                {
                    ShaftPort end = ClosestLane(start, upper);
                    if (end == null || ClosestLane(end, lower) != start) continue;
                    plan.Links.Add((start, end));
                    matched.Add(start);
                    matched.Add(end);
                }
                var unmatched = lower.Concat(upper).Where(port => !matched.Contains(port)).Select(port => spans[port.Span].ElementId).Distinct().ToList();
                if (unmatched.Count > 0)
                    plan.Issues.Add($"Переходы ID {a.Id}, {b.Id}: нет однозначного соответствия участков шахты для трасс ID {string.Join(", ", unmatched)}.");
            }

            foreach (var ports in portGroups.Values)
            {
                var byFloor = ports.GroupBy(port => port.Level).OrderBy(group => levelsById[group.Key].ProjectElevation).ToList();
                var lanes = byFloor.SelectMany(floor => floor.Select(marker => (Marker: marker, Lanes: SpansAt(marker, floor.ToList()))))
                    .ToDictionary(item => item.Marker, item => item.Lanes);
                for (int i = 1; i < byFloor.Count; i++)
                {
                    var lower = byFloor[i - 1];
                    var upper = byFloor[i];
                    var lowerPorts = lower.ToList();
                    var upperPorts = upper.ToList();
                    if (!Consecutive(lower.Key, upper.Key))
                    {
                        foreach (var a in lowerPorts)
                            foreach (var b in upperPorts.Where(b => Plan(a.Point, b.Point) <= ShaftAlignment && (lanes[a].Count != 0 || lanes[b].Count != 0)))
                                plan.Issues.Add($"Между переходами ID {a.Id}, {b.Id} пропущен этаж.");
                        continue;
                    }

                    ShaftPort Closest(ShaftPort port, List<ShaftPort> others)
                    {
                        var near = others.Select(other => (Port: other, Distance: Plan(other.Point, port.Point)))
                            .Where(option => option.Distance <= ShaftAlignment).OrderBy(option => option.Distance).Take(2).ToList();
                        if (near.Count == 2 && near[1].Distance <= near[0].Distance + PortAmbiguity)
                        {
                            if (lanes[port].Count != 0 || near.Any(option => lanes[option.Port].Count != 0))
                                plan.Issues.Add($"Переход ID {port.Id}: нельзя различить соседние шахты, ID {near[0].Port.Id}, {near[1].Port.Id}.");
                            return null;
                        }
                        return near.Count == 0 ? null : near[0].Port;
                    }

                    var lowerByUpper = upperPorts.ToDictionary(port => port, port => Closest(port, lowerPorts));
                    foreach (ShaftPort a in lowerPorts)
                    {
                        ShaftPort b = Closest(a, upperPorts);
                        if (b == null || lowerByUpper[b] != a) continue;


                        if (Math.Abs(a.Point.Z - b.Point.Z) <= SamePort)
                        {
                            plan.Issues.Add($"Переходы ID {a.Id}, {b.Id}: у точек размещения совпадают высоты.");
                            continue;
                        }
                        plan.Shafts.Add((a, b));
                        LinkLanes(a, b, lanes[a], lanes[b]);
                    }
                }
            }
            return plan;
        }

        private static int? FloorNumber(Document doc, Level level)
        {
            if (_levelNumbers == null) _levelNumbers = new Dictionary<ElementId, int?>();
            if (_levelNumbers.TryGetValue(level.Id, out int? known)) return known;
            string mapped = MarkingSettings.LevelFloor(doc, level.Name);
            if (int.TryParse(mapped, out int number)) return _levelNumbers[level.Id] = number;
            var match = System.Text.RegularExpressions.Regex.Match(level.Name ?? "", @"[+-]?\d+");
            return _levelNumbers[level.Id] = match.Success && int.TryParse(match.Value, out number) ? (int?)number : null;
        }

        internal static Level ConfiguredLevel(Element element, string floorParameter, IReadOnlyList<Level> levels, bool requireUnique = false)
        {
            string floor = RevitText.ValueText(RevitText.FindParameter(element, floorParameter))?.Trim();
            if (string.IsNullOrEmpty(floor)) return null;
            var exact = levels.Where(level => string.Equals(level.Name, floor, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count == 1) return exact[0];
            if (!int.TryParse(floor, out int number)) return null;
            var matching = levels.Where(level => FloorNumber(element.Document, level) == number).ToList();
            if (matching.Count == 1) return matching[0];
            if (element is FamilyInstance instance)
            {
                Level placed = DeviceMarkingOperations.InstanceLevel(element.Document, instance);
                Level hosted = matching.FirstOrDefault(level => level.Id == placed?.Id);
                if (hosted != null) return hosted;
            }
            return requireUnique ? null : matching.FirstOrDefault();
        }

        private sealed class Node
        {
            public XYZ Point;
            public long Level;
            public FamilyInstance Device;
            public readonly List<(Node to, double length)> Next = new List<(Node, double)>();
        }

        /// <summary>Устройства в порядке обхода; reached — сколько устройств кабель от прибора нашёл.</summary>
        /// <param name="comment">Если задан — межэтажные переходы только с этими «Комментарии» (как трассы линии).</param>
        public static List<FamilyInstance> Order(Document doc, List<FamilyInstance> cables, FamilyInstance controller, List<FamilyInstance> devices,
            out List<FamilyInstance> missed, out string problem, string comment = null, Dictionary<string, object> diagnostics = null)
        {
            missed = new List<FamilyInstance>();
            problem = null;
            string floorParameter = MarkingSettings.CurrentFloorParameter();
            ResetLevels();

            // Отрезки кабеля по этажам
            var segments = new List<(XYZ a, XYZ b, long level)>();
            var tunnelSpans = new List<RouteSpan>();
            foreach (FamilyInstance cable in cables)
            {
                if (IsTransition(cable) || !(cable.Location is LocationCurve curve)) continue;
                IList<XYZ> points = curve.Curve.Tessellate();
                long level = LevelOf(cable, floorParameter);
                long id = RevitIds.IdOf(cable.Id);
                string cableComment = Comments(cable);
                for (int i = 1; i < points.Count; i++)
                {
                    segments.Add((points[i - 1], points[i], level));
                    tunnelSpans.Add(new RouteSpan { A = points[i - 1], B = points[i], Level = level, ElementId = id, Comment = cableComment });
                }
            }
            if (segments.Count == 0)
            {
                problem = "у трасс линии нет линейной геометрии";
                return null;
            }

            // Узлы-стыки: концы отрезков, сведённые в одну точку с допуском
            var nodes = new List<Node>();
            Node NodeAt(XYZ p, long level)
            {
                foreach (Node n in nodes)
                {
                    if (n.Device == null && n.Level == level && Plan(n.Point, p) <= Join) return n;
                }
                var created = new Node { Point = p, Level = level };
                nodes.Add(created);
                return created;
            }
            var ends = segments.Select(s => (a: NodeAt(s.a, s.level), b: NodeAt(s.b, s.level))).ToList();

            // Остановки на каждом отрезке: его концы, концы других отрезков внутри (Т-стык), устройства, прибор, переходы
            var stops = segments.Select((s, i) => new List<(double t, Node node)> { (0, ends[i].a), (1, ends[i].b) }).ToList();
            void StopOnSegment(int i, XYZ p, Node node)
            {
                var (a, b, _) = segments[i];
                stops[i].Add((Projection(p, a, b), node));
            }
            for (int i = 0; i < segments.Count; i++)
            {
                foreach (Node end in nodes.ToList())
                {
                    if (end.Level != segments[i].level || end == ends[i].a || end == ends[i].b) continue;
                    double t = Projection(end.Point, segments[i].a, segments[i].b);
                    if (t > 0 && t < 1 && DistanceToSegment(end.Point, segments[i].a, segments[i].b) <= Join) StopOnSegment(i, end.Point, end);
                }
            }

            int Nearest(XYZ p, long level, double limit, XYZ preferredPoint = null)
            {
                int best = -1;
                double bestDistance = double.PositiveInfinity;
                for (int i = 0; i < segments.Count; i++)
                {
                    if (level != -1 && segments[i].level != -1 && segments[i].level != level) continue;
                    var (a, b, _) = segments[i];
                    double distanceFromInsertion = DistanceToSegment(p, a, b);
                    if (distanceFromInsertion > limit) continue;
                    double d = preferredPoint == null ? distanceFromInsertion : DistanceToSegment(preferredPoint, a, b);
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = i;
                    }
                }
                return best;
            }

            var deviceNodes = new Dictionary<long, Node>();
            foreach (FamilyInstance device in devices)
            {
                XYZ p = (device.Location as LocationPoint)?.Point;
                if (p == null) continue;
                var node = new Node { Point = p, Level = LevelOf(device, floorParameter), Device = device };
                // На совпадающих кабелях («туда» и «обратно») устройство встаёт на каждый из них — это одна точка.
                bool placed = false;
                for (int i = 0; i < segments.Count; i++)
                {
                    if (node.Level != -1 && segments[i].level != -1 && segments[i].level != node.Level) continue;
                    if (DistanceToSegment(p, segments[i].a, segments[i].b) <= Attach * 0.5)
                    {
                        StopOnSegment(i, p, node);
                        placed = true;
                    }
                }
                if (!placed)
                {
                    int nearest = Nearest(p, node.Level, Attach, WallSpeakerCenter(device));
                    if (nearest >= 0)
                    {
                        StopOnSegment(nearest, p, node);
                        placed = true;
                    }
                }
                if (placed) deviceNodes[RevitIds.IdOf(device.Id)] = node;
            }

            XYZ cp = (controller.Location as LocationPoint)?.Point;
            int controllerSegment = cp == null ? -1 : Nearest(cp, LevelOf(controller, floorParameter), ControllerAttach);
            if (controllerSegment < 0)
            {
                problem = "кабель линии не подходит к прибору ближе чем на 3 м";
                return null;
            }
            var start = new Node { Point = cp, Level = LevelOf(controller, floorParameter) };
            StopOnSegment(controllerSegment, cp, start);

            var tunnels = PlanTunnels(doc, tunnelSpans, comment);
            var transitions = new Dictionary<ShaftPort, Node>();
            Node TransitionNode(ShaftPort port)
            {
                if (transitions.TryGetValue(port, out var existing)) return existing;
                var node = new Node { Point = port.Projection, Level = port.Level };
                StopOnSegment(port.Span, port.Projection, node);
                transitions.Add(port, node);
                return node;
            }
            foreach (var (lower, upper) in tunnels.Links)
            {
                TransitionNode(lower);
                TransitionNode(upper);
            }

            // Рёбра: соседние остановки вдоль каждого отрезка
            void Link(Node a, Node b, double length)
            {
                if (a == b) return;
                a.Next.Add((b, length));
                b.Next.Add((a, length));
            }
            for (int i = 0; i < segments.Count; i++)
            {
                double length = Plan(segments[i].a, segments[i].b);
                List<(double t, Node node)> ordered = stops[i].OrderBy(s => s.t).ToList();
                for (int k = 1; k < ordered.Count; k++) Link(ordered[k - 1].node, ordered[k].node, (ordered[k].t - ordered[k - 1].t) * length);
            }
            foreach (var (lower, upper) in tunnels.Links)
                Link(transitions[lower], transitions[upper], lower.Point.DistanceTo(upper.Point) +
                    Plan(lower.Point, lower.Projection) + Plan(upper.Point, upper.Projection));

            // Обход в глубину от прибора, без рекурсии; из узла сначала по более короткому ребру
            var order = new List<FamilyInstance>();
            var visited = new HashSet<Node> { start };
            var stack = new Stack<IEnumerator<Node>>();
            IEnumerator<Node> NextOf(Node n) => n.Next.OrderBy(x => x.length).Select(x => x.to).ToList().GetEnumerator();
            stack.Push(NextOf(start));
            while (stack.Count > 0)
            {
                IEnumerator<Node> step = stack.Peek();
                if (!step.MoveNext())
                {
                    stack.Pop();
                    continue;
                }
                Node node = step.Current;
                if (!visited.Add(node)) continue;
                if (node.Device != null) order.Add(node.Device);
                stack.Push(NextOf(node));
            }

            if (diagnostics != null)
            {
                var graphNodes = ends.SelectMany(e => new[] { e.a, e.b }).Concat(transitions.Values).Concat(deviceNodes.Values).Concat(new[] { start }).Distinct().ToList();
                var seen = new HashSet<Node>(); int components = 0;
                foreach (var seed in graphNodes)
                {
                    if (!seen.Add(seed)) continue;
                    components++; var queue = new Queue<Node>(); queue.Enqueue(seed);
                    while (queue.Count > 0) foreach (var edge in queue.Dequeue().Next) if (seen.Add(edge.to)) queue.Enqueue(edge.to);
                }
                int Degree(Node n) => n.Next.Select(e => e.to).Distinct().Count();
                object Point(Node n) => new { levelId = n.Level, xMm = n.Point.X * 304.8, yMm = n.Point.Y * 304.8, zMm = n.Point.Z * 304.8 };
                diagnostics["components"] = components;
                diagnostics["branchPoints"] = graphNodes.Where(n => Degree(n) > 2).Take(300).Select(Point).ToList();
                diagnostics["openEnds"] = graphNodes.Where(n => n.Device == null && n != start && Degree(n) == 1).Take(300).Select(Point).ToList();
                diagnostics["unreachableRouteIds"] = ends.Select((e, i) => new { e, i }).Where(x => !visited.Contains(x.e.a) || !visited.Contains(x.e.b)).Select(x => tunnelSpans[x.i].ElementId).Distinct().ToList();
            }
            var inOrder = new HashSet<long>(order.Select(d => RevitIds.IdOf(d.Id)));
            missed = devices.Where(d => !inOrder.Contains(RevitIds.IdOf(d.Id))).ToList();
            if (diagnostics != null)
            {
                diagnostics["transitionIds"] = tunnels.MarkerIds;
                diagnostics["transitionIssues"] = tunnels.Issues;
            }
            if (missed.Any(device => LevelOf(device, floorParameter) != start.Level))
                problem = "нет подтверждённого межэтажного хода трассы. " + tunnels.Details;
            return order;
        }

        /// <summary>
        /// Этаж по высоте точки: ближайший уровень не выше точки (+50 мм). Кабель под перекрытием — этаж ниже перекрытия. У трасс параметр «Уровень» часто пуст
        /// (основа — грань или связь), поэтому этаж трасс и устройств определяется одинаково — по Z.
        /// </summary>
        /// <summary>Уровень по высоте точки (тот же способ, что для трасс и устройств при обходе).</summary>
        internal static Level LevelByZ(Document doc, XYZ p)
        {
            var levels = ProjectLevels(doc);
            return levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 50 / 304.8) ?? levels.FirstOrDefault();
        }

        internal static IReadOnlyList<Level> ProjectLevels(Document doc)
        {
            if (_levels == null || _levelsDoc != doc)
            {
                _levelNumbers = null;
                _levelsDoc = doc;
                _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            }
            return _levels;
        }

        internal static long LevelOf(FamilyInstance element, string floorParameter)
        {
            XYZ p = element.Location is LocationPoint lp ? lp.Point
                : element.Location is LocationCurve lc ? (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2 : null;
            if (p == null) return -1;
            Level found = ConfiguredLevel(element, floorParameter, ProjectLevels(element.Document));
            if (found == null && element.Location is LocationPoint)
                found = DeviceMarkingOperations.InstanceLevel(element.Document, element);
            found = found ?? LevelByZ(element.Document, p);
            return found == null ? -1 : RevitIds.IdOf(found.Id);
        }

        /// <summary>Сбросить кэш уровней (уровни могли добавить или удалить между вызовами).</summary>
        internal static void ResetLevels()
        {
            _levels = null;
            _levelNumbers = null;
        }

        [ThreadStatic] private static Dictionary<ElementId, int?> _levelNumbers;

        [ThreadStatic] private static List<Level> _levels;
        [ThreadStatic] private static Document _levelsDoc;

        internal static XYZ WallSpeakerCenter(FamilyInstance device)
        {
            if (device.Symbol.FamilyName.IndexOf("Громкоговоритель_Настенный", StringComparison.OrdinalIgnoreCase) < 0)
                return null;

            // Точка вставки находится у стены, а габарит оповещателя смещён внутрь помещения.
            // При выборе близкого кабеля это помогает отличить трассы по разные стороны стены.
            BoundingBoxXYZ box = device.get_BoundingBox(null);
            return box == null ? null : box.Transform.OfPoint((box.Min + box.Max) / 2);
        }

        private static double Plan(XYZ a, XYZ b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

        private static double Projection(XYZ p, XYZ a, XYZ b)
        {
            double ex = b.X - a.X, ey = b.Y - a.Y, l2 = ex * ex + ey * ey;
            return l2 < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((p.X - a.X) * ex + (p.Y - a.Y) * ey) / l2));
        }

        private static double DistanceToSegment(XYZ p, XYZ a, XYZ b)
        {
            double t = Projection(p, a, b);
            double x = a.X + (b.X - a.X) * t, y = a.Y + (b.Y - a.Y) * t;
            return Math.Sqrt((p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y));
        }
    }
}
