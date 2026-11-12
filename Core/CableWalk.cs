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
        private const string TransitionFamily = "ARBM_Межэтажный переход";
        private static readonly double Join = 100 / 304.8;      // стык отрезков
        private static readonly double Attach = 600 / 304.8;    // устройство сбоку от кабеля
        private static readonly double ControllerAttach = 3000 / 304.8;

        private sealed class Node
        {
            public XYZ Point;
            public long Level;
            public FamilyInstance Device;
            public readonly List<(Node to, double length)> Next = new List<(Node, double)>();
        }

        /// <summary>Устройства в порядке обхода; reached — сколько устройств кабель от прибора нашёл.</summary>
        public static List<FamilyInstance> Order(Document doc, List<FamilyInstance> cables, FamilyInstance controller, List<FamilyInstance> devices,
            out List<FamilyInstance> missed, out string problem)
        {
            missed = new List<FamilyInstance>();
            problem = null;
            _levels = null;

            // Отрезки кабеля по этажам
            var segments = new List<(XYZ a, XYZ b, long level)>();
            foreach (FamilyInstance cable in cables)
            {
                if (!(cable.Location is LocationCurve curve)) continue;
                IList<XYZ> points = curve.Curve.Tessellate();
                long level = LevelOf(cable);
                for (int i = 1; i < points.Count; i++) segments.Add((points[i - 1], points[i], level));
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

            int Nearest(XYZ p, long level, double limit)
            {
                int best = -1;
                double bestDistance = limit;
                for (int i = 0; i < segments.Count; i++)
                {
                    if (level != -1 && segments[i].level != -1 && segments[i].level != level) continue;
                    double d = DistanceToSegment(p, segments[i].a, segments[i].b);
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
                var node = new Node { Point = p, Level = LevelOf(device), Device = device };
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
                    int nearest = Nearest(p, node.Level, Attach);
                    if (nearest >= 0)
                    {
                        StopOnSegment(nearest, p, node);
                        placed = true;
                    }
                }
                if (placed) deviceNodes[RevitIds.IdOf(device.Id)] = node;
            }

            XYZ cp = (controller.Location as LocationPoint)?.Point;
            int controllerSegment = cp == null ? -1 : Nearest(cp, LevelOf(controller), ControllerAttach);
            if (controllerSegment < 0)
            {
                problem = "кабель линии не подходит к прибору ближе чем на 3 м";
                return null;
            }
            var start = new Node { Point = cp, Level = LevelOf(controller) };
            StopOnSegment(controllerSegment, cp, start);

            var transitions = new List<Node>();
            foreach (FamilyInstance transition in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                         .Where(f => f.Symbol.FamilyName == TransitionFamily && f.Location is LocationPoint))
            {
                XYZ p = ((LocationPoint)transition.Location).Point;
                long level = LevelOf(transition);
                int i = Nearest(p, level, Attach);
                if (i < 0)
                {
                    // Этаж трассы (ADSK_Этаж, вручную) может не совпасть с высотой перехода — переход встаёт на ближайшую трассу
                    i = Nearest(p, -1, Attach);
                    if (i < 0) continue;
                    level = segments[i].level;
                }
                var node = new Node { Point = p, Level = level };
                StopOnSegment(i, p, node);
                transitions.Add(node);
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
            for (int i = 0; i < transitions.Count; i++)
                for (int j = i + 1; j < transitions.Count; j++)
                    if (transitions[i].Level != transitions[j].Level && Plan(transitions[i].Point, transitions[j].Point) <= 300 / 304.8)
                        Link(transitions[i], transitions[j], 0);

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

            var inOrder = new HashSet<long>(order.Select(d => RevitIds.IdOf(d.Id)));
            missed = devices.Where(d => !inOrder.Contains(RevitIds.IdOf(d.Id))).ToList();
            return order;
        }

        /// <summary>
        /// Этаж по высоте точки: ближайший уровень не выше точки (+50 мм). Кабель под перекрытием — этаж ниже перекрытия. У трасс параметр «Уровень» часто пуст
        /// (основа — грань или связь), поэтому этаж трасс и устройств определяется одинаково — по Z.
        /// </summary>
        /// <summary>Уровень по высоте точки (тот же способ, что для трасс и устройств при обходе).</summary>
        internal static Level LevelByZ(Document doc, XYZ p)
        {
            if (_levels == null || _levelsDoc != doc)
            {
                _levelsDoc = doc;
                _levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            }
            return _levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 50 / 304.8) ?? _levels.FirstOrDefault();
        }

        private static long LevelOf(FamilyInstance element)
        {
            XYZ p = element.Location is LocationPoint lp ? lp.Point
                : element.Location is LocationCurve lc ? (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2 : null;
            if (p == null) return -1;
            if (_levels == null || _levelsDoc != element.Document)
            {
                _levelsDoc = element.Document;
                _levels = new FilteredElementCollector(element.Document).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
            }
            // ADSK_Этаж цифрой (у трасс заполняется вручную) — уровень с этим номером в имени
            if (int.TryParse(element.LookupParameter("ADSK_Этаж")?.AsString()?.Trim(), out int floorNumber))
            {
                Level byNumber = _levels.FirstOrDefault(l => { var m = System.Text.RegularExpressions.Regex.Match(l.Name ?? "", @"-?\d+"); return m.Success && int.Parse(m.Value) == floorNumber; });
                if (byNumber != null) return RevitIds.IdOf(byNumber.Id);
            }
            Level found = _levels.LastOrDefault(l => l.ProjectElevation <= p.Z + 50 / 304.8) ?? _levels.FirstOrDefault();
            return found == null ? -1 : RevitIds.IdOf(found.Id);
        }

        /// <summary>Сбросить кэш уровней (уровни могли добавить или удалить между вызовами).</summary>
        internal static void ResetLevels() => _levels = null;

        [ThreadStatic] private static List<Level> _levels;
        [ThreadStatic] private static Document _levelsDoc;

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
