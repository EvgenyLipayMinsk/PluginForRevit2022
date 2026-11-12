using TaskDialog = PluginForRevit2022.Core.CableJournal.JournalDialog;
using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Systems.Security.Models;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Linq;
using static ARBM.CableJournalExporter.Services.RouteLengthCalculator.Graph;

namespace ARBM.CableJournalExporter.Services
{
   
    public partial class RouteLengthCalculator
    {
        // ============================================================
        // Константы
        // ============================================================
        /// <summary>
        /// The Revit family name used to identify inter‑floor transition elements
        /// ("ARBM_Межэтажный переход") when building routing graphs.
        /// 
        /// This constant is used to:
        /// <para>
        /// • detect transition families during graph construction,  
        /// • exclude them from regular mechanical equipment processing,  
        /// • add them as dedicated transition nodes between levels.  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Имя семейства Revit, используемое для идентификации межэтажных переходов
        /// («ARBM_Межэтажный переход») при построении графов трасс.
        /// 
        /// Константа применяется для:
        /// <para>
        /// • определения семейств переходов при построении графа,  
        /// • исключения их из обычной обработки оборудования,  
        /// • добавления специальных узлов переходов между уровнями.  
        /// </para>
        /// </summary>
        private const string TRANSITION_FAMILY_NAME = "ARBM_Межэтажный переход";
        // ============================================================
        // Граф
        // ============================================================
        /// <summary>
/// Represents a graph structure used to model cable routing paths on each Revit level.
/// The graph consists of nodes (points in 3D space) and edges (segments connecting nodes).
/// Provides methods for adding nodes, connecting them with edges, removing edges,
/// and computing shortest paths using Dijkstra's algorithm.
/// </summary>
        public class Graph
        {
            /// <summary>
            /// Represents a graph node corresponding to a point in 3D space.
            /// Each node stores its geometric position and a list of connected edges.
            /// </summary>
            public class Node
            {
                /// <summary>
                /// Gets or sets the 3D coordinate of this node.
                /// Z‑coordinate is typically aligned with the level elevation.
                /// </summary>
                public XYZ Point;

                /// <summary>
                /// Gets the list of edges connected to this node.
                /// </summary>
                public List<Edge> Edges = new List<Edge>();
            }

            /// <summary>
            /// Represents a graph edge connecting two nodes.
            /// Stores references to both endpoints and the precomputed length of the segment.
            /// </summary>
            public class Edge
            {
                /// <summary>
                /// Gets or sets the first endpoint of the edge.
                /// </summary>
                public Node A;

                /// <summary>
                /// Gets or sets the second endpoint of the edge.
                /// </summary>
                public Node B;

                /// <summary>
                /// Gets or sets the geometric length of the edge.
                /// </summary>
                public double Length;
            }

            private readonly List<Node> _nodes = new List<Node>();
            private readonly List<Edge> _edges = new List<Edge>();

            /// <summary>
            /// Gets the collection of all nodes in the graph.
            /// </summary>
            public IEnumerable<Node> Nodes => _nodes;
            /// <summary>
            /// Gets the collection of all edges in the graph.
            /// </summary>
            public IEnumerable<Edge> Edges => _edges;

            /// <summary>
            /// Creates a new graph node at the specified 3D point and adds it to the graph.
            /// </summary>
            /// <param name="p">The XYZ coordinate of the new node.</param>
            /// <returns>The created <see cref="Node"/> instance.</returns>
            public Node AddNode(XYZ p)
            {
                var n = new Node { Point = p };
                _nodes.Add(n);
                return n;
            }

            /// <summary>
            /// Creates an edge between two nodes and adds it to the graph.
            /// The edge length is computed automatically based on the distance between nodes.
            /// </summary>
            /// <param name="a">The first endpoint node.</param>
            /// <param name="b">The second endpoint node.</param>
            public Edge AddEdge(Node a, Node b)
            {
                double len = a.Point.DistanceTo(b.Point);
                var e = new Edge { A = a, B = b, Length = len };
                a.Edges.Add(e);
                b.Edges.Add(e);
                _edges.Add(e);
                return e;
            }
            /// <summary>
            /// Removes the specified edge from the graph, detaching it from both endpoint nodes.
            /// </summary>
            /// <param name="e">The edge to remove.</param>
            public void RemoveEdge(Edge e)
            {
                e.A.Edges.Remove(e);
                e.B.Edges.Remove(e);
                _edges.Remove(e);
            }

            /// <summary>
            /// Finds the shortest path distance between two nodes in the graph using
            /// Dijkstra’s algorithm, where edge weights are given by <c>e.Length</c>.
            /// 
            /// The method:
            /// <para>
            /// • initializes all node distances to <c>PositiveInfinity</c>, except the start node (0),  
            /// • uses a priority queue (SortedSet) ordered by current distance,  
            /// • iteratively extracts the node with the smallest distance,  
            /// • relaxes all outgoing edges and updates neighbor distances,  
            /// • stops early when the target node is reached, returning the shortest distance.  
            /// </para>
            /// If the target node is unreachable, the method returns <c>double.PositiveInfinity</c>.
            /// 
            /// <para><b>RU:</b></para>
            /// Находит длину кратчайшего пути между двумя узлами графа с помощью
            /// алгоритма Дейкстры, где весами рёбер являются значения <c>e.Length</c>.
            /// 
            /// Метод:
            /// <para>
            /// • инициализирует расстояния до всех узлов значением <c>PositiveInfinity</c>,
            ///   кроме стартового узла (0),  
            /// • использует очередь с приоритетом (SortedSet), упорядоченную по текущему расстоянию,  
            /// • итеративно извлекает узел с минимальным расстоянием,  
            /// • выполняет релаксацию всех исходящих рёбер и обновляет расстояния до соседей,  
            /// • останавливается, как только достигнут целевой узел, возвращая длину кратчайшего пути.  
            /// </para>
            /// Если целевой узел недостижим, метод возвращает <c>double.PositiveInfinity</c>.
            /// </summary>
            /// <param name="start">
            /// Start node of the route.
            /// <para>RU: Стартовый узел маршрута.</para>
            /// </param>
            /// <param name="end">
            /// Target node of the route.
            /// <para>RU: Конечный узел маршрута.</para>
            /// </param>
            /// <returns>
            /// The shortest path length between <paramref name="start"/> and <paramref name="end"/>,
            /// or <c>double.PositiveInfinity</c> if no path exists.
            /// <para>RU: Длина кратчайшего пути между <paramref name="start"/> и <paramref name="end"/>,
            /// либо <c>double.PositiveInfinity</c>, если путь отсутствует.</para>
            /// </returns>
            public double FindShortestPath(Node start, Node end)
            {
                // Создаём словарь расстояний от стартового узла до всех остальных.
                // EN: Distance map from start node to all other nodes.
                var dist = new Dictionary<Node, double>();

                // Приоритетная очередь (минимальная дистанция — первый элемент).
                // EN: Priority queue implemented via SortedSet.
                var pq = new SortedSet<(double, Node)>(
                    Comparer<(double, Node)>.Create((x, y) =>
                    {
                        // Сравниваем по расстоянию.
                        // EN: Compare by distance.
                        int cmp = x.Item1.CompareTo(y.Item1);

                        // Если расстояния разные — возвращаем результат сравнения.
                        // EN: If distances differ — return comparison.
                        if (cmp != 0)
                            return cmp;

                        // Если расстояния равны — сравниваем хэши узлов,
                        // чтобы SortedSet мог различать элементы.
                        // EN: Tie‑breaker: compare node hash codes.
                        return x.Item2.GetHashCode().CompareTo(y.Item2.GetHashCode());
                    }));

                // Инициализируем расстояния до всех узлов как бесконечность.
                // EN: Initialize all distances to infinity.
                foreach (var n in _nodes)
                    dist[n] = double.PositiveInfinity;

                // Стартовый узел имеет расстояние 0.
                // EN: Start node distance = 0.
                dist[start] = 0;

                // Добавляем стартовый узел в очередь.
                // EN: Push start node into priority queue.
                pq.Add((0, start));

                // Основной цикл алгоритма Дейкстры.
                // EN: Main Dijkstra loop.
                while (pq.Count > 0)
                {
                    // Берём элемент с минимальной дистанцией.
                    // EN: Extract node with minimal distance.
                    var (d, node) = pq.Min;

                    // Удаляем его из очереди.
                    // EN: Remove it from priority queue.
                    pq.Remove(pq.Min);

                    // Если достигли конечного узла — возвращаем длину пути.
                    // EN: If target reached — return shortest distance.
                    if (node == end)
                        return d;

                    // Перебираем все рёбра текущего узла.
                    // EN: Iterate through all edges of current node.
                    foreach (var e in node.Edges)
                    {
                        // Определяем соседний узел по ребру.
                        // EN: Determine neighbor node.
                        var next = e.A == node ? e.B : e.A;

                        // FIX: Если узел ещё не в словаре — добавляем.
                        // EN: Ensure neighbor exists in distance map.
                        if (!dist.ContainsKey(next))
                            dist[next] = double.PositiveInfinity;

                        // Вычисляем потенциальную новую дистанцию.
                        // EN: Compute new tentative distance.
                        double nd = d + e.Length;

                        // Если нашли более короткий путь — обновляем.
                        // EN: Relax edge if new distance is shorter.
                        if (nd < dist[next])
                        {
                            // Удаляем старую запись из очереди (если была).
                            // EN: Remove outdated entry from priority queue.
                            pq.Remove((dist[next], next));

                            // Обновляем расстояние.
                            // EN: Update distance.
                            dist[next] = nd;

                            // Добавляем новую запись в очередь.
                            // EN: Push updated distance into queue.
                            pq.Add((nd, next));
                        }
                    }
                }

                // Если очередь опустела и целевой узел не найден —
                // пути не существует.
                // EN: If queue is empty and end not reached — no path exists.
                return double.PositiveInfinity;
            }

            /// <summary>
            /// Finds the boundary node of the connected component that contains <paramref name="start"/>,
            /// i.e., the node closest to <paramref name="end"/> when <paramref name="end"/> is unreachable.
            /// <para>RU: Находит граничный узел компоненты связности, содержащей <paramref name="start"/>,
            /// то есть узел, ближайший к <paramref name="end"/>, если <paramref name="end"/> недостижим.</para>
            /// </summary>
            /// <param name="start">
            /// Start node of the route.
            /// <para>RU: Стартовый узел маршрута.</para>
            /// </param>
            /// <param name="end">
            /// Target node of the route.
            /// <para>RU: Конечный узел маршрута.</para>
            /// </param>
            /// <param name="visited">
            /// Output set of nodes reachable from <paramref name="start"/>.
            /// <para>RU: Множество узлов, достижимых от <paramref name="start"/>.</para>
            /// </param>
            /// <returns>
            /// If <paramref name="end"/> is reachable — returns <c>null</c>.
            /// Otherwise returns the node in the start‑component that is geometrically closest to <paramref name="end"/>.
            /// <para>RU: Если <paramref name="end"/> достижим — возвращает <c>null</c>.
            /// Иначе возвращает узел из компоненты старта, который геометрически ближе всего к <paramref name="end"/>.</para>
            /// </returns>
            public Node FindBreakNode(Node start, Node end, out HashSet<Node> visited)
            {
                visited = new HashSet<Node>();
                var queue = new Queue<Node>();

                // Add start node to BFS queue.
                // RU: Добавляем стартовый узел в очередь BFS.
                queue.Enqueue(start);
                visited.Add(start);

                // BFS traversal from start.
                // RU: Обход BFS от стартового узла.
                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();

                    // If end is reached — no break exists.
                    // RU: Если достигли end — разрыва нет.
                    if (node == end)
                        return null;

                    foreach (var e in node.Edges)
                    {
                        var next = e.A == node ? e.B : e.A;

                        if (!visited.Contains(next))
                        {
                            visited.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }

                // If we are here — end is unreachable.
                // RU: Если мы здесь — end недостижим.

                Node best = null;
                double bestDist = double.MaxValue;

                // Find the visited node closest to end.
                // RU: Ищем узел из visited, который ближе всего к end.
                foreach (var n in visited)
                {
                    double d = n.Point.DistanceTo(end.Point);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = n;
                    }
                }

                return best;
            }
            /// <summary>
            /// Finds the node in the connected component of <paramref name="end"/> that is
            /// geometrically closest to <paramref name="breakNode"/>.
            /// <para>RU: Находит узел из компоненты связности <paramref name="end"/>,
            /// который геометрически ближе всего к <paramref name="breakNode"/>.</para>
            /// </summary>
            /// <param name="breakNode">
            /// Boundary node of the start‑component.
            /// <para>RU: Граничный узел компоненты старта.</para>
            /// </param>
            /// <param name="end">
            /// Target node of the route.
            /// <para>RU: Конечный узел маршрута.</para>
            /// </param>
            /// <returns>
            /// Node in the end‑component closest to <paramref name="breakNode"/>.
            /// <para>RU: Узел из компоненты end, ближайший к <paramref name="breakNode"/>.</para>
            /// </returns>
            public Node FindNearestToBreakOnEndSide(Node breakNode, Node end)
            {
                var visited = new HashSet<Node>();
                var queue = new Queue<Node>();

                // BFS from end to get its connected component.
                // RU: BFS от end для получения его компоненты связности.
                queue.Enqueue(end);
                visited.Add(end);

                while (queue.Count > 0)
                {
                    var node = queue.Dequeue();

                    foreach (var e in node.Edges)
                    {
                        var next = e.A == node ? e.B : e.A;

                        if (!visited.Contains(next))
                        {
                            visited.Add(next);
                            queue.Enqueue(next);
                        }
                    }
                }

                // Now visited = end‑component.
                // RU: visited = компонента end.

                Node best = null;
                double bestDist = double.MaxValue;

                // Find node closest to breakNode.
                // RU: Ищем узел, ближайший к breakNode.
                foreach (var n in visited)
                {
                    double d = n.Point.DistanceTo(breakNode.Point);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = n;
                    }
                }

                return best;
            }
            /// <summary>
            /// Creates a new edge between two nodes, effectively connecting two previously
            /// disconnected components.
            /// <para>RU: Создаёт новое ребро между двумя узлами, соединяя две ранее
            /// несвязанные компоненты графа.</para>
            /// </summary>
            /// <param name="a">
            /// First endpoint node.
            /// <para>RU: Первый узел.</para>
            /// </param>
            /// <param name="b">
            /// Second endpoint node.
            /// <para>RU: Второй узел.</para>
            /// </param>
            /// <returns>
            /// The created edge.
            /// <para>RU: Созданное ребро.</para>
            /// </returns>
            public Edge ConnectComponents(Node a, Node b)
            {
                // Simply reuse AddEdge.
                // RU: Используем стандартный AddEdge.
                return AddEdge(a, b);
            }


        }

        // ============================================================
        // Поля
        // ============================================================
        /// <summary>
        /// Stores routing graphs grouped by Revit level ID.
        /// Each level has its own independent <see cref="Graph"/> instance
        /// representing all mechanical routing segments located on that level.
        /// </summary>
        public Dictionary<long, Graph> _graphsByLevel = new Dictionary<long, Graph>();

        /// <summary>
        /// Internal cache of all <see cref="Level"/> elements in the document,
        /// indexed by their numeric ID for fast lookup during device‑to‑level resolution.
        /// </summary>
        private Dictionary<long, Level> _levelsById = new Dictionary<long, Level>();

        /// <summary>
        /// Stores graph nodes representing devices, controllers, and expanders.
        /// The key is a unique string identifier (typically the Revit ElementId of the device).
        /// This dictionary allows quick access to the graph node corresponding to a device.
        /// </summary>
        public Dictionary<string, Graph.Node> _deviceNodes = new Dictionary<string, Graph.Node>();

        /// <summary>
        /// Stores transition nodes for each level.
        /// Transition nodes represent vertical connection points between levels
        /// (e.g., virtual risers used to connect graphs of different floors).
        /// </summary>
        private Dictionary<long, List<Graph.Node>> _transitionNodesByLevel = new Dictionary<long, List<Graph.Node>>();

        /// <summary>
        /// Provides read‑only access to the cached Revit levels.
        /// Used by routing logic to determine device elevation and level relationships.
        /// </summary>
        public Dictionary<long, Level> LevelsById => _levelsById;


        // ============================================================
        // Уровни
        // ============================================================
        /// <summary>
        /// Collects all <see cref="Level"/> elements in the document and caches them
        /// in a dictionary for fast lookup by their numeric ID.
        /// 
        /// The ID extraction differs between Revit 2021/2022 and Revit 2024,
        /// so conditional compilation is used.
        /// </summary>
        /// <param name="doc">The active Revit document.</param>
        private void CacheLevels(Document doc)
        {
#if REVIT2021 || REVIT2022 || REVIT2023
    _levelsById = new FilteredElementCollector(doc)
        .OfClass(typeof(Level))
        .Cast<Level>()
        .ToDictionary(l => (long)l.Id.IntegerValue, l => l);
#elif REVIT2024
    _levelsById = new FilteredElementCollector(doc)
        .OfClass(typeof(Level))
        .Cast<Level>()
        .ToDictionary(l => (long)l.Id.Value, l => l);
#endif
        }


        /// <summary>
        /// Attempts to determine the level ID of the given element by inspecting its host.
        /// 
        /// Works for family instances hosted on levels or other elements.
        /// If no valid level is found, returns -1.
        /// </summary>
        /// <param name="el">The element whose host level should be determined.</param>
        /// <returns>
        /// The numeric level ID, or -1 if the level cannot be resolved.
        /// </returns>
        /// <summary>
        /// Уровень по ADSK_Этаж (только цифра): «3» → уровень с номером 3 в имени («Этаж 03»).
        /// Номер, которого нет в именах уровней (техпространство, этаж 6), — уровень по высоте элемента
        /// (ближайший не выше точки, +50 мм). Пусто или не число — −1, дальше прежние правила.
        /// Так трасса под перекрытием и устройства её этажа попадают в один граф, без «мостов».
        /// </summary>
        private long GetLevelIdFromFloorNumber(Element el)
        {
            string floor = el.LookupParameter("ADSK_Этаж")?.AsString()?.Trim();
            if (string.IsNullOrEmpty(floor) || !int.TryParse(floor, out int number)) return -1;
            foreach (var pair in _levelsById)
            {
                var match = System.Text.RegularExpressions.Regex.Match(pair.Value.Name ?? "", @"-?\d+");
                if (match.Success && int.Parse(match.Value) == number) return pair.Key;
            }
            XYZ point = el.Location is LocationPoint lp ? lp.Point
                : el.Location is LocationCurve lc ? (lc.Curve.GetEndPoint(0) + lc.Curve.GetEndPoint(1)) / 2 : null;
            if (point == null) return -1;
            var byHeight = _levelsById.Where(p => p.Value.ProjectElevation <= point.Z + 50 / 304.8)
                .OrderByDescending(p => p.Value.ProjectElevation).Select(p => (long?)p.Key).FirstOrDefault();
            return byHeight ?? -1;
        }

        private long GetLevelIdFromHost(Element el)
        {
            long byFloor = GetLevelIdFromFloorNumber(el);
            if (byFloor != -1) return byFloor;

            var host = ((FamilyInstance)el).Host;
            if (host != null)
            {
                // Host is a Level element
                if (host is Level lvl)
                {
#if REVIT2021 || REVIT2022 || REVIT2023
            return lvl.Id.IntegerValue;
#elif REVIT2024
                    return lvl.Id.Value;
#endif
                }

                // Host has a LevelId property
                if (host.LevelId != ElementId.InvalidElementId)
                {
#if REVIT2021 || REVIT2022 || REVIT2023
            return (int)host.LevelId.IntegerValue;
#elif REVIT2024
                    return (int)host.LevelId.Value;
#endif
                }
            }

            // Fallback: use the element's own LevelId
            if (el.LevelId != ElementId.InvalidElementId)
            {
#if REVIT2021 || REVIT2022 || REVIT2023
        return (int)el.LevelId.IntegerValue;
#elif REVIT2024
                return (int)el.LevelId.Value;
#endif
            }

            return -1;
        }

        /// <summary>
        /// Determines the level ID for a device (family instance).
        /// 
        /// The method attempts several strategies:
        /// 1. If the device has parameter "ADSK_Этаж", its value is matched to a level name.
        /// 2. If the device has a host, the host's level is used.
        /// 3. If the device has a <see cref="LocationPoint"/>, the nearest level by elevation is selected.
        /// 
        /// Returns -1 if no level can be resolved.
        /// </summary>
        /// <param name="doc">The active Revit document.</param>
        /// <param name="fi">The device instance.</param>
        /// <returns>The numeric level ID, or -1 if unresolved.</returns>
        public long GetLevelIdForDevice(Document doc, FamilyInstance fi)
        {
            // 1) Попытка получить уровень из параметра ADSK_Этаж
            var p = fi.LookupParameter("ADSK_Этаж");
            if (p != null && p.StorageType == StorageType.String)
            {
                string floorName = p.AsString();
                if (!string.IsNullOrEmpty(floorName))
                {
                    var lvl = _levelsById.Values
                        .FirstOrDefault(l => l.Name.Equals(floorName, StringComparison.OrdinalIgnoreCase));

#if REVIT2021 || REVIT2022 || REVIT2023
            if (lvl != null)
                return lvl.Id.IntegerValue;
#elif REVIT2024
                    if (lvl != null)
                        return lvl.Id.Value;
#endif
                }
            }

            // 2) Попытка получить уровень через Host
            long hostLevelId = GetLevelIdFromHost(fi);
            if (hostLevelId != -1)
                return hostLevelId;

            // 3) Попытка получить уровень через INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM
            var scheduleLevelParam = fi.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
            if (scheduleLevelParam != null && scheduleLevelParam.StorageType == StorageType.ElementId)
            {
                ElementId lvlId = scheduleLevelParam.AsElementId();

#if REVIT2021 || REVIT2022 || REVIT2023
        if (lvlId != null && lvlId.IntegerValue != -1)
            return lvlId.IntegerValue;
#elif REVIT2024
                if (lvlId != null && lvlId.Value != -1)
                    return lvlId.Value;
#endif
            }

            // 4) Определяем уровень по Z координате (fallback)
            var lp = fi.Location as LocationPoint;
            if (lp != null)
            {
                double z = lp.Point.Z;

                var best = _levelsById.Values
                    .OrderBy(l => Math.Abs(l.Elevation - z))
                    .FirstOrDefault();

#if REVIT2021 || REVIT2022 || REVIT2023
                if (best != null)
            return best.Id.IntegerValue;
#endif
#if REVIT2024
                if (best != null)
                    return best.Id.Value;
#endif
            }

            // 5) Ничего не нашли
            return -1;
        }



        // ============================================================
        // Обнаружение и добавление переходов
        // ============================================================

        /// <summary>
        /// Собрать все семейства "ARBM_Межэтажный переход" и добавить их как узлы в соответствующие графы.
        /// Проверяет дублирование узлов по координатам и ID элемента.
        /// </summary>
        /// /// <summary>
        /// Collects all "ARBM_Межэтажный переход" (inter-floor transition) family instances
        /// and adds them as nodes into the corresponding level graphs.
        /// Also checks for duplicate transition nodes by element ID and by coordinates
        /// within the document short curve tolerance.
        /// </summary>
        /// <param name="doc">The active Revit document.</param>
        private void AddTransitionFamilies(Document doc)
        {
            // Очищаем словарь переходных узлов по уровням.
            // EN: Clear the dictionary that stores transition nodes grouped by level.
            _transitionNodesByLevel.Clear();

            // Создаём коллектор, который ищет все экземпляры оборудования (MechanicalEquipment),
            // исключая типы элементов, и приводим их к FamilyInstance.
            // EN: Collect all mechanical equipment instances (not types) as FamilyInstance.
            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>();

            // Получаем допуск документа для сравнения координат.
            // EN: Get Revit's short curve tolerance for coordinate comparison.
            double tol = doc.Application.ShortCurveTolerance;

            // Перебираем все найденные семейства оборудования.
            // EN: Iterate through all collected mechanical equipment instances.
            foreach (var fi in collector)
            {
                // Пропускаем всё, что не является семейством межэтажного перехода.
                // EN: Skip all families except the transition family.
                if (fi.Symbol.FamilyName != "ARBM_Межэтажный переход")
                    continue;

                // Получаем точку вставки семейства.
                // EN: Get the insertion point (LocationPoint).
                var lp = fi.Location as LocationPoint;

                // Если у семейства нет точки вставки — пропускаем.
                // EN: If no location point exists — skip.
                if (lp == null)
                    continue;

                // Определяем ID уровня, на котором размещено устройство.
                // EN: Determine the level ID for this device.
                long levelId = GetLevelIdForDevice(doc, fi);

                // Если уровень определить не удалось — пропускаем.
                // EN: If level cannot be determined — skip.
                if (levelId == -1)
                    continue;

                // Гарантируем, что граф для уровня существует.
                // EN: Ensure that a graph exists for this level.
                if (!_graphsByLevel.ContainsKey(levelId))
                    _graphsByLevel[levelId] = new Graph();

                // Получаем граф уровня.
                // EN: Retrieve the graph for this level.
                var graph = _graphsByLevel[levelId];

                // Получаем строковый ID элемента.
                // EN: Get element ID as string.
                string elementId = fi.Id.ToString();

                // Если узел уже добавлен — пропускаем.
                // EN: Skip if this device node already exists.
                if (_deviceNodes.ContainsKey(elementId))
                    continue;

                // Определяем Z-координату: либо по уровню, либо по точке вставки.
                // EN: Determine Z coordinate from level elevation or instance point.
                double z = _levelsById.ContainsKey(levelId)
                    ? _levelsById[levelId].Elevation
                    : lp.Point.Z;

                // Создаём XYZ точку устройства с корректной Z.
                // EN: Create device point with corrected Z.
                XYZ devicePoint = new XYZ(lp.Point.X, lp.Point.Y, z);

                // Флаг дублирования.
                // EN: Duplicate flag.
                bool isDuplicate = false;

                // Перебираем уже добавленные переходные узлы на этом уровне.
                // EN: Iterate through existing transition nodes on this level.
                foreach (var existingNode in _transitionNodesByLevel.ContainsKey(levelId)
                    ? _transitionNodesByLevel[levelId]
                    : new List<Graph.Node>())
                {
                    // Если расстояние меньше допуска — это дубликат.
                    // EN: If distance < tolerance — this is a duplicate.
                    if (existingNode.Point.DistanceTo(devicePoint) < tol)
                    {
                        isDuplicate = true;

                        // Показываем предупреждение пользователю.
                        // EN: Show warning dialog about duplicate.
                        TaskDialog.Show("Предупреждение",
                            $"⚠️  Дублирование семейства '{TRANSITION_FAMILY_NAME}':\n\n" +
                            $"Семейство с ID {elementId} размещено в точке, " +
                            $"совпадающей с уже добавленным семейством на уровне {levelId}.\n\n" +
                            $"Координаты: X={devicePoint.X:F2}, Y={devicePoint.Y:F2}, Z={devicePoint.Z:F2}.\n\n" +
                            $"Это семейство будет пропущено.");
                        break;
                    }
                }

                // Если дубликат найден — пропускаем добавление.
                // EN: Skip adding if duplicate detected.
                if (isDuplicate)
                    continue;

                // Добавляем узел перехода в граф уровня через общий метод AttachDeviceToGraphFixed.
                // EN: Add transition node to the graph using AttachDeviceToGraphFixed.
                var transitionNode = AttachDeviceToGraphFixed(
                    graph, elementId, lp.Point, doc, levelId);

                // Гарантируем, что список переходов для уровня существует.
                // EN: Ensure transition list for this level exists.
                if (!_transitionNodesByLevel.ContainsKey(levelId))
                    _transitionNodesByLevel[levelId] = new List<Graph.Node>();

                // Добавляем переходный узел в список уровня.
                // EN: Add transition node to the level's list.
                _transitionNodesByLevel[levelId].Add(transitionNode);
            }
        }


        // ============================================================
        // Анализ цепей и подключение уровней
        // ============================================================
        /// <summary>
        /// Analyzes a device chain and determines which Revit levels are involved.
        /// Returns a sorted list of unique level IDs referenced by the devices in the chain.
        ///
        /// <para><b>RU:</b></para>
        /// Анализирует цепочку устройств и определяет, какие уровни Revit в неё входят.
        /// Метод проходит по каждому устройству в цепи, вычисляет уровень, на котором оно
        /// расположено (с учётом параметров семейства, хоста и координаты Z), и собирает
        /// уникальные идентификаторы уровней.  
        /// Возвращаемый список отсортирован по возрастанию и содержит только те уровни,
        /// на которых реально присутствуют устройства из цепочки.
        /// </summary>
        ///
        /// <param name="doc">
        /// The active Revit document.
        /// <para><b>RU:</b> Текущий документ Revit.</para>
        /// </param>
        ///
        /// <param name="devices">
        /// The collection of devices (family instances) that form the chain.
        /// <para><b>RU:</b> Коллекция устройств (экземпляров семейств), которые образуют цепочку.</para>
        /// </param>
        ///
        /// <returns>
        /// A list of unique level IDs, sorted in ascending order, representing all levels
        /// on which the devices in the chain are located.
        /// 
        /// <para><b>RU:</b></para>
        /// Список уникальных идентификаторов уровней, отсортированный по возрастанию,
        /// представляющий все уровни, на которых расположены устройства цепочки.
        /// </returns>

        private List<long> GetLevelsInChain(Document doc, IEnumerable<FamilyInstance> devices)
        {
            var levels = new SortedSet<long>();

            foreach (var device in devices.ToList())
            {
                long levelId = GetLevelIdForDevice(doc, device);
                if (levelId != -1)
                    levels.Add(levelId);
            }

            return levels.ToList();
        }

        /// <summary>
        /// Validates that inter-floor transition families are available on the specified levels.
        /// Returns a localized error message if transitions are missing on one or both levels,
        /// or <c>null</c> if the configuration is valid.
        ///
        /// <para><b>RU:</b></para>
        /// Проверяет наличие семейств межэтажных переходов на указанных уровнях.
        /// Метод используется перед созданием вертикальных связей между уровнями, чтобы убедиться,
        /// что на каждом уровне присутствует хотя бы один экземпляр семейства
        /// <c>ARBM_Межэтажный переход</c>.  
        /// Если на одном или обоих уровнях отсутствуют такие семейства, возвращается
        /// текст ошибки с подробным описанием проблемы.  
        /// Если переходы присутствуют на обоих уровнях — возвращается <c>null</c>, что означает,
        /// что уровни корректно подготовлены для построения межуровневых связей.
        /// </summary>
        ///
        /// <param name="fromLevel">
        /// The source level ID.
        /// <para><b>RU:</b> Идентификатор исходного уровня.</para>
        /// </param>
        ///
        /// <param name="toLevel">
        /// The target level ID.
        /// <para><b>RU:</b> Идентификатор целевого уровня.</para>
        /// </param>
        ///
        /// <returns>
        /// A string containing an error description when required transition families
        /// are missing, or <c>null</c> if transitions are available on both levels.
        /// 
        /// <para><b>RU:</b></para>
        /// Строка с описанием ошибки, если на одном или обоих уровнях отсутствуют
        /// необходимые семейства межэтажных переходов.  
        /// Возвращает <c>null</c>, если переходы присутствуют на обоих уровнях.
        /// </returns>

        private string ValidateTransitionAvailability(long fromLevel, long toLevel)
        {
            bool hasFromTransition = _transitionNodesByLevel.ContainsKey(fromLevel) &&
                                     _transitionNodesByLevel[fromLevel].Count > 0;
            bool hasToTransition = _transitionNodesByLevel.ContainsKey(toLevel) &&
                                   _transitionNodesByLevel[toLevel].Count > 0;

            if (!hasFromTransition && !hasToTransition)
                return $"❌ Ошибка: На обоих уровнях ({fromLevel} и {toLevel}) отсутствуют семейства '{TRANSITION_FAMILY_NAME}'.";

            if (!hasFromTransition)
                return $"❌ Ошибка: На уровне {fromLevel} отсутствуют семейства '{TRANSITION_FAMILY_NAME}'.";

            if (!hasToTransition)
                return $"❌ Ошибка: На уровне {toLevel} отсутствуют семейства '{TRANSITION_FAMILY_NAME}'.";

            return null; // OK
        }
        // Предполагаем, что у тебя есть:
        // Dictionary<long, List<Graph.Node>> _transitionNodesByLevel;
        // Dictionary<string, Graph.Node> _deviceNodes; // ключ = ElementId.ToString()
        /// <summary>
        /// Finds the transition node on the specified level that corresponds to the given device node.
        /// The method attempts two strategies:
        /// <para>
        /// 1) If the device node is already connected to a transition node by an edge,
        ///    that transition node is returned.
        /// </para>
        /// <para>
        /// 2) If no direct connection exists, the nearest transition node (by geometric distance)
        ///    is selected as a fallback.
        /// </para>
        ///
        /// <para><b>RU:</b></para>
        /// Находит узел межэтажного перехода на указанном уровне, который соответствует
        /// переданному узлу устройства.  
        /// Метод использует два последовательных подхода:
        /// <para>
        /// 1) Если устройство уже связано ребром с переходным узлом, возвращается именно этот узел.  
        ///    Это гарантирует корректное использование заранее созданных связей графа.
        /// </para>
        /// <para>
        /// 2) Если прямой связи нет, выбирается ближайший переходный узел по геометрическому расстоянию.  
        ///    Такой механизм служит резервным вариантом и позволяет корректно работать даже при
        ///    отсутствии явных связей или при частично построенном графе.
        /// </para>
        /// Если на уровне нет ни одного переходного узла, возвращается <c>null</c>.
        /// </summary>
        ///
        /// <param name="levelId">
        /// The level ID on which the search should be performed.
        /// <para><b>RU:</b> Идентификатор уровня, на котором выполняется поиск.</para>
        /// </param>
        ///
        /// <param name="deviceNode">
        /// The graph node representing the device.
        /// <para><b>RU:</b> Узел графа, соответствующий устройству.</para>
        /// </param>
        ///
        /// <returns>
        /// A <see cref="Graph.Node"/> representing the transition node, or <c>null</c>
        /// if no transition nodes exist on the specified level.
        /// 
        /// <para><b>RU:</b></para>
        /// Узел перехода (<see cref="Graph.Node"/>), если он найден, либо <c>null</c>,
        /// если на уровне отсутствуют переходные узлы.
        /// </returns>

        private Graph.Node FindTransitionNodeForDevice(long levelId, Graph.Node deviceNode)
        {
            // Пытаемся получить список переходных узлов для указанного уровня.
            // EN: Try to get the list of transition nodes for the given level.
            if (!_transitionNodesByLevel.TryGetValue(levelId, out var transitions) ||
                transitions == null || transitions.Count == 0)
                // Если списка нет или он пуст — переходов на уровне нет → возвращаем null.
                // EN: If no transitions exist on this level — return null.
                return null;

            // --- 1) Попытка №1: найти переходный узел, который уже связан ребром с устройством ---

            // Берём все рёбра устройства, и для каждого определяем соседний узел:
            // если A == deviceNode → сосед B, иначе A.
            // EN: For each edge of the device node, determine the adjacent node.
            var connected = deviceNode.Edges
                .Select(e => e.A == deviceNode ? e.B : e.A)
                // Ищем среди соседей тот, который является переходным узлом.
                // EN: Find the first adjacent node that is a transition node.
                .FirstOrDefault(n => transitions.Contains(n));

            // Если нашли переходный узел, связанный ребром — возвращаем его.
            // EN: If a directly connected transition node exists — return it.
            if (connected != null)
                return connected;

            // --- 2) Попытка №2: если прямой связи нет — ищем ближайший переходный узел ---

            // Сортируем переходные узлы по расстоянию до устройства
            // и берём первый (минимальное расстояние).
            // EN: Sort transition nodes by distance to the device and return the nearest one.
            return transitions
                .OrderBy(t => t.Point.DistanceTo(deviceNode.Point))
                .FirstOrDefault();
        }


        /// <summary>
        /// Соединяет два уровня только через transitionNodes:
        /// - transitionNode текущего уровня, связанный с fromDevice;
        /// - transitionNode следующего уровня, связанный с firstDeviceOnNextLevel.
        /// </summary>
        /// 
        /// <summary>
        /// Connects two levels using only their corresponding transition nodes.
        /// This method establishes a vertical routing link between floors by:
        /// <para>
        /// • locating the transition node on the source level that is associated with <paramref name="fromDevice"/>  
        /// • locating the transition node on the target level that is associated with <paramref name="firstDeviceOnNextLevel"/>
        /// </para>
        /// If both transition nodes exist and are not already connected, a bidirectional
        /// edge is added between them, representing a virtual vertical riser.
        ///
        /// <para><b>RU:</b></para>
        /// Соединяет два уровня, используя исключительно их переходные узлы (transition nodes).  
        /// Метод формирует вертикальную связь между этажами, выполняя следующие шаги:
        /// <para>
        /// • определяет переходный узел на исходном уровне, связанный с устройством <paramref name="fromDevice"/>;  
        /// • определяет переходный узел на целевом уровне, связанный с устройством <paramref name="firstDeviceOnNextLevel"/>.  
        /// </para>
        /// Если оба переходных узла найдены и между ними ещё нет связи, метод создаёт
        /// двунаправленное ребро, представляющее виртуальный вертикальный стояк.  
        /// Такая связь используется при построении маршрутов между устройствами,
        /// расположенными на разных уровнях.
        /// </summary>
        ///
        /// <param name="fromLevel">
        /// The level ID of the device initiating the transition.
        /// <para><b>RU:</b> Идентификатор уровня, на котором находится устройство, инициирующее переход.</para>
        /// </param>
        ///
        /// <param name="toLevel">
        /// The level ID of the next device in the chain.
        /// <para><b>RU:</b> Идентификатор уровня, на котором находится следующее устройство в цепочке.</para>
        /// </param>
        ///
        /// <param name="fromDevice">
        /// The device on the source level whose transition node should be used
        /// to establish the vertical connection.
        /// <para><b>RU:</b> Устройство на исходном уровне, переходный узел которого используется для создания вертикальной связи.</para>
        /// </param>
        ///
        /// <param name="firstDeviceOnNextLevel">
        /// The first device on the target level whose transition node should be used
        /// to complete the vertical connection.
        /// <para><b>RU:</b> Первое устройство на целевом уровне, переходный узел которого используется для завершения вертикальной связи.</para>
        /// </param>

    private void ConnectLevelsByTransitions(
     long fromLevel,
     long toLevel,
     FamilyInstance fromDevice,
     FamilyInstance firstDeviceOnNextLevel)
        {
            // Проверяем, что графы для обоих уровней существуют.
            // EN: Ensure that graphs for both levels exist.
            if (!_graphsByLevel.ContainsKey(fromLevel) ||
                !_graphsByLevel.ContainsKey(toLevel))
                // Если хотя бы одного графа нет — связь построить невозможно.
                // EN: If either graph is missing — cannot connect levels.
                return;

            // Получаем граф исходного уровня.
            // EN: Retrieve graph for the source level.
            var graphFrom = _graphsByLevel[fromLevel];

            // Получаем граф целевого уровня.
            // EN: Retrieve graph for the target level.
            var graphTo = _graphsByLevel[toLevel];

            // Пытаемся получить узел устройства на исходном уровне.
            // EN: Try to get the graph node corresponding to the source device.
            if (!_deviceNodes.TryGetValue(fromDevice.Id.ToString(), out var fromDevNode))
                // Если узла нет — связь невозможна.
                // EN: If device node not found — cannot proceed.
                return;

            // Пытаемся получить узел устройства на целевом уровне.
            // EN: Try to get the graph node corresponding to the target-level device.
            if (!_deviceNodes.TryGetValue(firstDeviceOnNextLevel.Id.ToString(), out var toDevNode))
                // Если узла нет — связь невозможна.
                // EN: If device node not found — cannot proceed.
                return;

            // --- Находим transitionNode на каждом уровне ---

            // Ищем переходный узел, связанный с устройством на исходном уровне.
            // EN: Find transition node associated with the source-level device.
            var fromTrans = FindTransitionNodeForDevice(fromLevel, fromDevNode);

            // Ищем переходный узел, связанный с устройством на целевом уровне.
            // EN: Find transition node associated with the target-level device.
            var toTrans = FindTransitionNodeForDevice(toLevel, toDevNode);

            // Если хотя бы один переходный узел не найден — связь невозможна.
            // EN: If either transition node is missing — cannot connect levels.
            if (fromTrans == null || toTrans == null)
                return;

            // --- Проверяем, нет ли уже существующей связи между переходами ---

            // Проверяем, есть ли ребро между fromTrans и toTrans.
            // EN: Check if an edge already exists between the two transition nodes.
            bool alreadyConnected =
                fromTrans.Edges.Any(e => e.A == toTrans || e.B == toTrans) ||
                toTrans.Edges.Any(e => e.A == fromTrans || e.B == fromTrans);

            // Если связь уже есть — повторно её не создаём.
            // EN: If already connected — do nothing.
            if (alreadyConnected)
                return;

            // --- Создаём вертикальную связь между уровнями ---

            // Добавляем ребро в граф исходного уровня.
            // EN: Add edge in the source-level graph.
            graphFrom.AddEdge(fromTrans, toTrans);

            // Добавляем обратное ребро в граф целевого уровня.
            // EN: Add reverse edge in the target-level graph.
            graphTo.AddEdge(toTrans, fromTrans);
        }

        // ============================================================
        // Строим графы по уровням
        // ============================================================

        /// <summary>
        /// Построить графы трасс по уровням и привязать все устройства.
        /// Параметр filterParam позволяет фильтровать трассы по значению параметра "Comments".
        /// </summary>
        /// <summary>
        /// Builds routing graphs per Revit level and attaches all controllers, expanders,
        /// and devices to these graphs.
        ///
        /// The method performs several stages:
        /// <para>
        /// 1) Collects all mechanical equipment (OST_MechanicalEquipment) elements,
        ///    optionally filtered by the system parameter "Comments", and creates
        ///    level-based routing graphs from their curves (excluding inter-floor
        ///    transition families).
        /// </para>
        /// <para>
        /// 2) Adds all "ARBM_Межэтажный переход" (inter-floor transition) families
        ///    as dedicated transition nodes into the corresponding level graphs,
        ///    with duplicate detection by ID and coordinates.
        /// </para>
        /// <para>
        /// 3) Automatically connects and splits edges within each level graph
        ///    to ensure proper topology for routing.
        /// </para>
        /// <para>
        /// 4) Attaches controllers, expanders, and all devices in their loops
        ///    to the appropriate level graphs, and connects different levels
        ///    using transition nodes according to the actual device sequence
        ///    in each loop, with validation of transition availability between levels.
        /// </para>
        ///
        /// <para><b>RU:</b></para>
        /// <para>
        /// Метод строит графы трасс для каждого уровня Revit и привязывает к ним
        /// контроллеры, расширители и устройства.
        /// </para>
        /// <para>
        /// Этапы работы:
        /// </para>
        /// <para>
        /// 1) Собирает все элементы категории OST_MechanicalEquipment, при необходимости
        ///    фильтруя их по параметру "Комментарии", и формирует графы трасс по уровням
        ///    на основе их геометрии (кроме семейств межэтажных переходов).
        /// </para>
        /// <para>
        /// 2) Добавляет все семейства "ARBM_Межэтажный переход" как специальные узлы
        ///    переходов между уровнями, с проверкой дублирования по ID и координатам.
        /// </para>
        /// <para>
        /// 3) Выполняет автосшивку и разрезание рёбер внутри каждого графа,
        ///    обеспечивая корректную топологию для поиска маршрутов.
        /// </para>
        /// <para>
        /// 4) Привязывает контроллеры, расширители и устройства в их петлях
        ///    к соответствующим графам уровней и соединяет уровни между собой
        ///    через узлы переходов в соответствии с фактической последовательностью
        ///    устройств, включая проверку наличия переходов между уровнями.
        /// </para>
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="controllers">
        /// The collection of controllers with their associated loops and devices
        /// that should be attached to the routing graphs.
        /// <para>RU: Коллекция контроллеров с их петлями и устройствами,
        /// которые должны быть привязаны к графам.</para>
        /// </param>
        /// <param name="filterParam">
        /// Optional filter value for the system parameter "Comments".
        /// If empty or <c>null</c>, all mechanical equipment elements are used;
        /// otherwise, only elements whose "Comments" value equals this string
        /// (case-insensitive) are included in the routing graphs.
        /// <para>
        /// RU: Необязательный фильтр по параметру «Комментарии».  
        /// Если пустой или <c>null</c> — используются все элементы.  
        /// Если задан — используются только элементы, у которых значение параметра
        /// «Комментарии» совпадает с указанным (без учёта регистра).
        /// </para>
        /// </param>

        public void BuildGraphsByLevel(Document doc, List<ControllerWithLoops> controllers, string filterParam = "", Func<Element, bool> routeFilter = null)
        {
            // Кэшируем уровни Revit в локальную структуру.
            // EN: Cache all Revit levels into internal dictionary.
            CacheLevels(doc);

            // Очищаем все графы уровней — начинаем построение заново.
            // EN: Reset all level graphs.
            _graphsByLevel.Clear();

            // Очищаем словарь узлов устройств.
            // EN: Reset device node registry.
            _deviceNodes.Clear();

            // Очищаем словарь переходных узлов.
            // EN: Reset transition node registry.
            _transitionNodesByLevel.Clear();

            // Собираем все экземпляры MechanicalEquipment.
            // EN: Collect all mechanical equipment instances.
            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType();

            // По умолчанию — без фильтра.
            // EN: Default: no filtering.
            IEnumerable<Element> filteredElements = collector;

            // Маркировка AerBIM передаёт свой отбор трасс (по ADSK_Группирование); журнал его не задаёт.
            if (routeFilter != null)
            {
                filteredElements = collector.Where(routeFilter);
            }
            // Если задан фильтр по параметру "Комментарии" — применяем его.
            // EN: Apply comment-based filter if provided.
            else if (!string.IsNullOrEmpty(filterParam))
            {
                filteredElements = collector.Where(el =>
                {
                    // Получаем значение параметра "Комментарии".
                    // EN: Read "Comments" parameter.
                    string comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();

                    // Сравниваем с фильтром без учёта регистра.
                    // EN: Case-insensitive comparison.
                    return string.Equals(comments, filterParam, StringComparison.OrdinalIgnoreCase);
                });
            }

            // --- Шаг 1: Собрать все трассы (кроме переходов)
            var allRoutes = new List<Element>();
            foreach (var el in filteredElements)
            {
                // Пробуем привести элемент к FamilyInstance.
                // EN: Try cast to FamilyInstance.
                var fi = el as FamilyInstance;

                // Если это переход — пропускаем (обработаем позже).
                // EN: Skip transition families.
                if (fi != null && fi.Symbol.FamilyName == TRANSITION_FAMILY_NAME)
                    continue;
                allRoutes.Add(el);
                // Если элемент имеет геометрию в виде линии — это трасса.
                // EN: If element has curve geometry — treat as routing segment.
                if (el.Location is LocationCurve lc)
                {
                    // Получаем кривую.
                    // EN: Extract curve.
                    var c = lc.Curve;

                    // Получаем точки начала и конца.
                    // EN: Curve endpoints.
                    var p1 = c.GetEndPoint(0);
                    var p2 = c.GetEndPoint(1);

                    // Определяем уровень по хосту.
                    // EN: Determine level ID from host.
                    long levelId = GetLevelIdFromHost(el);

                    // Создаём граф уровня, если его ещё нет.
                    // EN: Ensure graph exists.
                    if (!_graphsByLevel.ContainsKey(levelId))
                        _graphsByLevel[levelId] = new Graph();

                    // Получаем граф уровня.
                    // EN: Retrieve graph.
                    var graph = _graphsByLevel[levelId];

                    // Z берём строго по уровню, иначе — по геометрии.
                    // EN: Use level elevation for Z.
                    double z = _levelsById.ContainsKey(levelId)
                        ? _levelsById[levelId].Elevation
                        : p1.Z;

                    // Добавляем узлы трассы.
                    // EN: Add segment endpoints as nodes.
                    var n1 = graph.AddNode(new XYZ(p1.X, p1.Y, z));
                    var n2 = graph.AddNode(new XYZ(p2.X, p2.Y, z));

                    // Добавляем ребро между узлами.
                    // EN: Add edge between nodes.
                    graph.AddEdge(n1, n2);
                }
            }
            // ✅ ВАЛИДАЦИЯ: трассы
            if (!SecurityDataValidator.ValidateRoutes(allRoutes))
                return;
            // --- Шаг 1.5: Добавить семейства переходов
            // EN: Add transition families as nodes.
            AddTransitionFamilies(doc);

            // --- Шаг 2: Автосшивка и разрезание рёбер
            // Радиус поиска для автосшивки (в футах).
            // EN: Autoconnect search radius.
            double radiusFeet = 500.0 / 304.8;

            // Обрабатываем каждый граф уровня.
            // EN: Process each level graph.
            foreach (var graph in _graphsByLevel.Values)
            {
                // Автоматически соединяем близкие узлы.
                // EN: Autoconnect nearby nodes.
                AutoConnectMechanicalEquipment(graph, radiusFeet);

                // Разрезаем рёбра в местах пересечений.
                // EN: Split edges at intersections.
                SplitEdgesByNodes(graph);
            }

            // --- Шаг 3: Привязка всех устройств всех контроллеров
            foreach (var ctrl in controllers)
            {
                // Получаем точку вставки контроллера.
                // EN: Get controller location point.
                var lpCtrl = ctrl.Controller.Location as LocationPoint;
                if (lpCtrl == null) continue;

                // Определяем уровень контроллера.
                // EN: Determine controller level.
                long ctrlLevel = GetLevelIdForDevice(doc, ctrl.Controller);

                // Создаём граф уровня при необходимости.
                // EN: Ensure graph exists.
                if (!_graphsByLevel.ContainsKey(ctrlLevel))
                    _graphsByLevel[ctrlLevel] = new Graph();

                // Получаем граф уровня контроллера.
                // EN: Retrieve graph.
                var graphCtrl = _graphsByLevel[ctrlLevel];

                // Добавляем контроллер как узел графа.
                // EN: Attach controller node.
                AttachDeviceToGraphFixed(
                    graphCtrl, ctrl.Controller.Id.ToString(), lpCtrl.Point, doc, ctrlLevel);

                // --- Обработка расширителей контроллера
                foreach (var exp in ctrl.Expanders)
                {
                    // Расширитель находится на том же уровне, что и контроллер.
                    // EN: Expander inherits controller level.
                    XYZ expPoint = lpCtrl.Point;
                    long expLevel = ctrlLevel;
                    var graphExp = graphCtrl;

                    // Добавляем расширитель в граф.
                    // EN: Attach expander node.
                    AttachDeviceToGraphFixed(graphExp, exp.ExpanderId.ToString(), expPoint, doc, expLevel);

                    // Получаем FamilyInstance контроллера (для переходов).
                    // EN: Retrieve controller FI.
                    var expFi = ctrl.Controller;

                    if (expFi == null)
                        continue;

                    // --- Обработка петель расширителя
                    foreach (var loop in exp.Loops)
                    {
                        // 1) Определяем уровни устройств в петле.
                        // EN: Determine levels in chain.
                        var levelsInChain = GetLevelsInChain(doc, loop.Devices);

                        // Гарантируем, что уровень расширителя — первый.
                        // EN: Ensure expander level is first.
                        if (levelsInChain.Count > 0 && levelsInChain[0] != expLevel)
                            levelsInChain.Insert(0, expLevel);
                        else if (levelsInChain.Count == 0)
                            levelsInChain.Add(expLevel);

                        // Проверяем наличие переходов между уровнями.
                        // EN: Validate transitions.
                        if (levelsInChain.Count > 1)
                        {
                            for (int i = 0; i < levelsInChain.Count - 1; i++)
                            {
                                long fromLevel = levelsInChain[i];
                                long toLevel = levelsInChain[i + 1];

                                string validationError = ValidateTransitionAvailability(fromLevel, toLevel);
                                if (validationError != null)
                                {
                                    // Показываем ошибку структуры уровней.
                                    // EN: Show validation error.
                                    TaskDialog.Show("Ошибка структуры уровней", validationError);
                                    return;
                                }
                            }
                        }

                        // 2) Фактическое построение переходов
                        // Начинаем с расширителя.
                        // EN: Start from expander.
                        FamilyInstance prevDev = expFi;
                        long prevLevel = expLevel;

                        foreach (var dev in loop.Devices)
                        {
                            // Получаем точку вставки устройства.
                            // EN: Get device location.
                            var lpDev = dev.Location as LocationPoint;
                            if (lpDev == null) continue;

                            // Определяем уровень устройства.
                            // EN: Determine device level.
                            long devLevel = GetLevelIdForDevice(doc, dev);

                            // Создаём граф уровня при необходимости.
                            // EN: Ensure graph exists.
                            if (!_graphsByLevel.ContainsKey(devLevel))
                                _graphsByLevel[devLevel] = new Graph();

                            // Получаем граф уровня устройства.
                            // EN: Retrieve graph.
                            var graphDev = _graphsByLevel[devLevel];

                            // Добавляем устройство в граф.
                            // EN: Attach device node.
                            AttachDeviceToGraphFixed(graphDev, dev.Id.ToString(), lpDev.Point, doc, devLevel);

                            // Если устройство на другом уровне — создаём вертикальную связь.
                            // EN: Connect levels if changed.
                            if (prevLevel != devLevel)
                            {
                                ConnectLevelsByTransitions(
                                    prevLevel,
                                    devLevel,
                                    prevDev,   // расширитель или предыдущее устройство
                                    dev        // первое устройство на новом уровне
                                );
                            }

                            // Обновляем предыдущие значения.
                            // EN: Update previous device and level.
                            prevDev = dev;
                            prevLevel = devLevel;
                        }
                    }
                }

                // --- Обработка петель контроллера (аналогично расширителю)
                foreach (var loop in ctrl.Loops)
                {
                    // Определяем уровни устройств в петле.
                    // EN: Determine levels in chain.
                    var levelsInChain = GetLevelsInChain(doc, loop.Devices);

                    // Гарантируем, что уровень контроллера — первый.
                    // EN: Ensure controller level is first.
                    if (levelsInChain.Count > 0 && levelsInChain[0] != ctrlLevel)
                        levelsInChain.Insert(0, ctrlLevel);
                    else if (levelsInChain.Count == 0)
                        levelsInChain.Add(ctrlLevel);

                    // Проверяем наличие переходов между уровнями.
                    // EN: Validate transitions.
                    if (levelsInChain.Count > 1)
                    {
                        for (int i = 0; i < levelsInChain.Count - 1; i++)
                        {
                            long fromLevel = levelsInChain[i];
                            long toLevel = levelsInChain[i + 1];

                            string validationError = ValidateTransitionAvailability(fromLevel, toLevel);
                            if (validationError != null)
                            {
                                TaskDialog.Show("Ошибка структуры уровней", validationError);
                                return;
                            }
                        }
                    }

                    // 2) Построение графа по фактической последовательности устройств
                    // Начинаем с контроллера.
                    // EN: Start from controller.
                    FamilyInstance prevDev = ctrl.Controller;
                    long prevLevel = ctrlLevel;

                    foreach (var dev in loop.Devices)
                    {
                        // Получаем точку вставки устройства.
                        // EN: Get device location.
                        var lpDev = dev.Location as LocationPoint;
                        if (lpDev == null) continue;

                        // Определяем уровень устройства.
                        // EN: Determine device level.
                        long devLevel = GetLevelIdForDevice(doc, dev);

                        // Создаём граф уровня при необходимости.
                        // EN: Ensure graph exists.
                        if (!_graphsByLevel.ContainsKey(devLevel))
                            _graphsByLevel[devLevel] = new Graph();

                        // Получаем граф уровня устройства.
                        // EN: Retrieve graph.
                        var graphDev = _graphsByLevel[devLevel];

                        // Добавляем устройство в граф.
                        // EN: Attach device node.
                        AttachDeviceToGraphFixed(graphDev, dev.Id.ToString(), lpDev.Point, doc, devLevel);

                        // Если уровень изменился — создаём вертикальную связь.
                        // EN: Connect levels if changed.
                        if (prevLevel != devLevel)
                        {
                            ConnectLevelsByTransitions(
                                prevLevel,
                                devLevel,
                                prevDev,   // контроллер или предыдущее устройство
                                dev        // первое устройство на новом уровне
                            );
                        }

                        // Обновляем предыдущие значения.
                        // EN: Update previous device and level.
                        prevDev = dev;
                        prevLevel = devLevel;
                    }
                }
            }
        }

        /// <summary>
        /// Builds routing graphs per Revit level and attaches all APS controllers
        /// and APS devices to these graphs.
        /// 
        /// The method performs several stages:
        /// <para>
        /// 1) Collects all mechanical equipment (OST_MechanicalEquipment) elements,
        ///    optionally filtered by the system parameter "Comments", and creates
        ///    level-based routing graphs from their curves (excluding inter-floor
        ///    transition families).
        /// </para>
        /// <para>
        /// 2) Adds all "ARBM_Межэтажный переход" (inter-floor transition) families
        ///    as dedicated transition nodes into the corresponding level graphs,
        ///    with duplicate detection by ID and coordinates.
        /// </para>
        /// <para>
        /// 3) Automatically connects and splits edges within each level graph
        ///    to ensure proper topology for routing.
        /// </para>
        /// <para>
        /// 4) Attaches APS controllers and all APS devices in their loops
        ///    to the appropriate level graphs, and connects different levels
        ///    using transition nodes according to the actual device sequence
        ///    in each loop, with validation of transition availability between levels.
        /// </para>
        ///
        /// <para><b>RU:</b></para>
        /// <para>
        /// Метод строит графы трасс для каждого уровня Revit и привязывает к ним
        /// контроллеры АПС и устройства АПС.
        /// </para>
        /// <para>
        /// Этапы работы:
        /// </para>
        /// <para>
        /// 1) Собирает все элементы категории OST_MechanicalEquipment, при необходимости
        ///    фильтруя их по параметру "Комментарии", и формирует графы трасс по уровням
        ///    на основе их геометрии (кроме семейств межэтажных переходов).
        /// </para>
        /// <para>
        /// 2) Добавляет все семейства "ARBM_Межэтажный переход" как специальные узлы
        ///    переходов между уровнями, с проверкой дублирования по ID и координатам.
        /// </para>
        /// <para>
        /// 3) Выполняет автосшивку и разрезание рёбер внутри каждого графа,
        ///    обеспечивая корректную топологию для поиска маршрутов.
        /// </para>
        /// <para>
        /// 4) Привязывает контроллеры АПС и устройства в их петлях
        ///    к соответствующим графам уровней и соединяет уровни между собой
        ///    через узлы переходов в соответствии с фактической последовательностью
        ///    устройств, включая проверку наличия переходов между уровнями.
        /// </para>
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="controllers">
        /// The collection of APS controllers with their associated loops and devices
        /// that should be attached to the routing graphs.
        /// <para>RU: Коллекция контроллеров АПС с их петлями и устройствами,
        /// которые должны быть привязаны к графам.</para>
        /// </param>
        /// <param name="filterParam">
        /// Optional filter value for the system parameter "Comments".
        /// If empty or <c>null</c>, all mechanical equipment elements are used;
        /// otherwise, only elements whose "Comments" value equals this string
        /// (case-insensitive) are included in the routing graphs.
        /// <para>
        /// RU: Необязательный фильтр по параметру «Комментарии».  
        /// Если пустой или <c>null</c> — используются все элементы.  
        /// Если задан — используются только элементы, у которых значение параметра
        /// «Комментарии» совпадает с указанным (без учёта регистра).
        /// </para>
        /// </param>
        public void BuildGraphsByLevel(Document doc, List<ApsControllerWithLoops> controllers, string filterParam = "")
        {
            // ============================================================
            // 0. Cache levels and reset internal structures
            // ============================================================

            CacheLevels(doc);
            _graphsByLevel.Clear();
            _deviceNodes.Clear();
            _transitionNodesByLevel.Clear();

            // ============================================================
            // 1. Collect mechanical equipment (routing segments)
            // ============================================================

            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType();

            IEnumerable<Element> filteredElements = collector;

            if (!string.IsNullOrEmpty(filterParam))
            {
                filteredElements = collector.Where(el =>
                {
                    string comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    return string.Equals(comments, filterParam, StringComparison.OrdinalIgnoreCase);
                });
            }

            var allRoutes = new List<Element>();

            foreach (var el in filteredElements)
            {
                var fi = el as FamilyInstance;

                if (fi != null && fi.Symbol.FamilyName == TRANSITION_FAMILY_NAME)
                    continue;

                allRoutes.Add(el);

                if (el.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var p1 = c.GetEndPoint(0);
                    var p2 = c.GetEndPoint(1);

                    long levelId = GetLevelIdFromHost(el);

                    if (!_graphsByLevel.ContainsKey(levelId))
                        _graphsByLevel[levelId] = new Graph();

                    var graph = _graphsByLevel[levelId];

                    double z = _levelsById.ContainsKey(levelId)
                        ? _levelsById[levelId].Elevation
                        : p1.Z;

                    var n1 = graph.AddNode(new XYZ(p1.X, p1.Y, z));
                    var n2 = graph.AddNode(new XYZ(p2.X, p2.Y, z));

                    graph.AddEdge(n1, n2);
                }
            }

            if (!SecurityDataValidator.ValidateRoutes(allRoutes))
                return;

            // ============================================================
            // 2. Add transition families
            // ============================================================

            AddTransitionFamilies(doc);

            // ============================================================
            // 3. Autoconnect and split edges
            // ============================================================

            double radiusFeet = 500.0 / 304.8;

            foreach (var graph in _graphsByLevel.Values)
            {
                AutoConnectMechanicalEquipment(graph, radiusFeet);
                SplitEdgesByNodes(graph);
            }

            // ============================================================
            // 4. Attach APS controllers and APS devices
            // ============================================================

            foreach (var ctrl in controllers)
            {
                var ctrlFi = ctrl.Controller.Element;
                var lpCtrl = ctrlFi.Location as LocationPoint;
                if (lpCtrl == null)
                    continue;

                long ctrlLevel = GetLevelIdForDevice(doc, ctrlFi);

                if (!_graphsByLevel.ContainsKey(ctrlLevel))
                    _graphsByLevel[ctrlLevel] = new Graph();

                var graphCtrl = _graphsByLevel[ctrlLevel];

                AttachDeviceToGraphFixed(
                    graphCtrl,
                    ctrl.Controller.Element.Id.ToString(),
                    lpCtrl.Point,
                    doc,
                    ctrlLevel);

                // ============================================================
                // Process APS loops (no expanders in APS)
                // ============================================================

                foreach (var loop in ctrl.Loops)
                {
                    var levelsInChain = GetLevelsInChain(doc, loop.Devices.Select(d => d.BaseInfo.Element as FamilyInstance).ToList());

                    if (levelsInChain.Count > 0 && levelsInChain[0] != ctrlLevel)
                        levelsInChain.Insert(0, ctrlLevel);
                    else if (levelsInChain.Count == 0)
                        levelsInChain.Add(ctrlLevel);

                    if (levelsInChain.Count > 1)
                    {
                        for (int i = 0; i < levelsInChain.Count - 1; i++)
                        {
                            long fromLevel = levelsInChain[i];
                            long toLevel = levelsInChain[i + 1];

                            string validationError = ValidateTransitionAvailability(fromLevel, toLevel);
                            if (validationError != null)
                            {
                                TaskDialog.Show("Ошибка структуры уровней", validationError);
                                return;
                            }
                        }
                    }

                    FamilyInstance prevDev = ctrlFi;
                    long prevLevel = ctrlLevel;

                    foreach (var devInfo in loop.Devices)
                    {
                        var dev = devInfo.BaseInfo.Element;
                        var lpDev = dev.Location as LocationPoint;
                        if (lpDev == null)
                            continue;

                        long devLevel = GetLevelIdForDevice(doc, dev as FamilyInstance);

                        if (!_graphsByLevel.ContainsKey(devLevel))
                            _graphsByLevel[devLevel] = new Graph();

                        var graphDev = _graphsByLevel[devLevel];

                        AttachDeviceToGraphFixed(
                            graphDev,
                            dev.Id.ToString(),
                            lpDev.Point,
                            doc,
                            devLevel);

                        if (prevLevel != devLevel)
                        {
                            ConnectLevelsByTransitions(
                                prevLevel,
                                devLevel,
                                prevDev,
                                dev as FamilyInstance);
                        }

                        prevDev = dev as FamilyInstance;
                        prevLevel = devLevel;
                    }
                }
            }
        }

        /// <summary>
        /// Builds routing graphs per Revit level and attaches Souye controller
        /// and Souye devices to these graphs.
        /// 
        /// The method performs the same stages as BuildGraphsByLevel for APS:
        /// <para>
        /// 1) Collects all mechanical equipment (OST_MechanicalEquipment) elements,
        ///    optionally filtered by the system parameter "Comments", and creates
        ///    level-based routing graphs from their curves (excluding inter-floor
        ///    transition families).
        /// </para>
        /// <para>
        /// 2) Adds all "ARBM_Межэтажный переход" (inter-floor transition) families
        ///    as dedicated transition nodes into the corresponding level graphs.
        /// </para>
        /// <para>
        /// 3) Automatically connects and splits edges within each level graph
        ///    to ensure proper topology for routing.
        /// </para>
        /// <para>
        /// 4) Attaches Souye controller and all Souye devices in their loop groups
        ///    to the appropriate level graphs, and connects different levels
        ///    using transition nodes.
        /// </para>
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="data">
        /// The Souye controller with loop groups and devices
        /// that should be attached to the routing graphs.
        /// <para>RU: Контроллер СОУЭ с группами петель и устройствами,
        /// которые должны быть привязаны к графам.</para>
        /// </param>
        /// <param name="filterParam">
        /// Optional filter value for the system parameter "Comments".
        /// If empty or <c>null</c>, all mechanical equipment elements are used;
        /// otherwise, only elements whose "Comments" value equals this string
        /// (case-insensitive) are included in the routing graphs.
        /// </param>
        public void BuildGraphsByLevel(Document doc, SouyeControllerWithLoops data, string filterParam = "")
        {
            // ============================================================
            // 0. Cache levels and reset internal structures
            // ============================================================

            CacheLevels(doc);
            _graphsByLevel.Clear();
            _deviceNodes.Clear();
            _transitionNodesByLevel.Clear();

            // ============================================================
            // 1. Collect mechanical equipment (routing segments)
            // ============================================================

            var collector = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_MechanicalEquipment)
                .WhereElementIsNotElementType();

            IEnumerable<Element> filteredElements = collector;

            if (!string.IsNullOrEmpty(filterParam))
            {
                filteredElements = collector.Where(el =>
                {
                    string comments = el.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS)?.AsString();
                    return string.Equals(comments, filterParam, StringComparison.OrdinalIgnoreCase);
                });
            }

            var allRoutes = new List<Element>();

            foreach (var el in filteredElements)
            {
                var fi = el as FamilyInstance;

                if (fi != null && fi.Symbol.FamilyName == TRANSITION_FAMILY_NAME)
                    continue;

                allRoutes.Add(el);

                if (el.Location is LocationCurve lc)
                {
                    var c = lc.Curve;
                    var p1 = c.GetEndPoint(0);
                    var p2 = c.GetEndPoint(1);

                    long levelId = GetLevelIdFromHost(el);

                    if (!_graphsByLevel.ContainsKey(levelId))
                        _graphsByLevel[levelId] = new Graph();

                    var graph = _graphsByLevel[levelId];

                    double z = _levelsById.ContainsKey(levelId)
                        ? _levelsById[levelId].Elevation
                        : p1.Z;

                    var n1 = graph.AddNode(new XYZ(p1.X, p1.Y, z));
                    var n2 = graph.AddNode(new XYZ(p2.X, p2.Y, z));

                    graph.AddEdge(n1, n2);
                }
            }

            // Validation - only if needed
            // if (!SouyeDataValidator.ValidateRoutes(allRoutes))
            //     return;

            // ============================================================
            // 2. Add transition families
            // ============================================================

            AddTransitionFamilies(doc);

            // ============================================================
            // 3. Autoconnect and split edges
            // ============================================================

            double radiusFeet = 500.0 / 304.8;

            foreach (var graph in _graphsByLevel.Values)
            {
                AutoConnectMechanicalEquipment(graph, radiusFeet);
                SplitEdgesByNodes(graph);
            }

            // ============================================================
            // 4. Attach Souye controller and devices
            // ============================================================

            if (data == null || data.Controller == null)
                return;

            var ctrlFi = data.Controller.Element;
            var lpCtrl = ctrlFi.Location as LocationPoint;
            if (lpCtrl == null)
                return;

            long ctrlLevel = GetLevelIdForDevice(doc, ctrlFi as FamilyInstance);

            if (!_graphsByLevel.ContainsKey(ctrlLevel))
                _graphsByLevel[ctrlLevel] = new Graph();

            var graphCtrl = _graphsByLevel[ctrlLevel];

            AttachDeviceToGraphFixed(
                graphCtrl,
                ctrlFi.Id.ToString(),
                lpCtrl.Point,
                doc,
                ctrlLevel);

            // ============================================================
            // Process Souye loop groups
            // ============================================================

            foreach (var loopGroup in data.LoopGroups)
            {
                if (loopGroup.Devices == null || loopGroup.Devices.Count == 0)
                    continue;

    var familyInstances = loopGroup.Devices.Select(d => d.Element as FamilyInstance)
    .Where(fi => fi != null)
    .ToList();
var levelsInChain = GetLevelsInChain(doc, familyInstances);

                if (levelsInChain.Count > 0 && levelsInChain[0] != ctrlLevel)
                    levelsInChain.Insert(0, ctrlLevel);
                else if (levelsInChain.Count == 0)
                    levelsInChain.Add(ctrlLevel);

                if (levelsInChain.Count > 1)
                {
                    for (int i = 0; i < levelsInChain.Count - 1; i++)
                    {
                        long fromLevel = levelsInChain[i];
                        long toLevel = levelsInChain[i + 1];

                        string validationError = ValidateTransitionAvailability(fromLevel, toLevel);
                        if (validationError != null)
                        {
                            TaskDialog.Show("Ошибка структуры уровней", validationError);
                            return;
                        }
                    }
                }

                FamilyInstance prevDev = ctrlFi as FamilyInstance;
                long prevLevel = ctrlLevel;

                foreach (var deviceInfo in loopGroup.Devices)
                {
                    var dev = deviceInfo.Element;
                    var lpDev = dev.Location as LocationPoint;
                    if (lpDev == null)
                        continue;

                    long devLevel = GetLevelIdForDevice(doc, dev as FamilyInstance);

                    if (!_graphsByLevel.ContainsKey(devLevel))
                        _graphsByLevel[devLevel] = new Graph();

                    var graphDev = _graphsByLevel[devLevel];

                    AttachDeviceToGraphFixed(
                        graphDev,
                        dev.Id.ToString(),
                        lpDev.Point,
                        doc,
                        devLevel);

                    if (prevLevel != devLevel)
                    {
                        ConnectLevelsByTransitions(
                            prevLevel,
                            devLevel,
                            prevDev,
                            dev as FamilyInstance);
                    }

                    prevDev = dev as FamilyInstance;
                    prevLevel = devLevel;
                }
            }
        }

        /// <summary>
        /// Visualizes a gap between two graph nodes by drawing a red DirectShape line
        /// in the active Revit view.  
        /// The method performs several validation steps before creating the geometry:
        /// <para>
        /// • Ensures the gap length exceeds the minimum threshold (50 mm).  
        /// • Ensures both nodes are endpoints (not intermediate nodes).  
        /// • Ensures the nodes are collinear with the routing geometry.  
        /// </para>
        /// If all conditions are met, a red line is created between the two points
        /// using a temporary DirectShape element.
        /// 
        /// <para><b>RU:</b></para>
        /// Визуализирует разрыв между двумя узлами графа, создавая красную линию
        /// DirectShape в активном виде Revit.  
        /// Метод выполняет несколько проверок перед созданием геометрии:
        /// <para>
        /// • Проверяет, что длина разрыва превышает минимальный порог (50 мм).  
        /// • Проверяет, что оба узла являются конечными точками (endpoints).  
        /// • Проверяет, что точки коллинеарны с трассой.  
        /// </para>
        /// Если все условия выполнены, создаётся красная линия между двумя точками
        /// с помощью временного элемента DirectShape.
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.  
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="a">
        /// The first graph node representing one endpoint of the gap.  
        /// <para>RU: Первый узел графа — одна из точек разрыва.</para>
        /// </param>
        /// <param name="b">
        /// The second graph node representing the opposite endpoint of the gap.  
        /// <para>RU: Второй узел графа — противоположная точка разрыва.</para>
        /// </param>
        public void VisualizeGap(Document doc, Graph.Node a, Graph.Node b)
        {
            double minLengthFeet = 50.0 / 304.8;
            double dist = a.Point.DistanceTo(b.Point);

            if (dist < minLengthFeet)
                return;

            if (!IsEndpoint(a) || !IsEndpoint(b))
                return;

            if (!AreCollinear(a, b))
                return;

            using (Transaction t = new Transaction(doc, "Visualize Gap"))
            {
                t.Start();

                Category cat = Category.GetCategory(doc, BuiltInCategory.OST_Lines);

                var ds = DirectShape.CreateElement(doc, cat.Id);

                OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                ogs.SetProjectionLineColor(new Color(255, 0, 0));

                Line gapLine = Line.CreateBound(a.Point, b.Point);

                ds.SetShape(new List<GeometryObject> { gapLine });

                doc.ActiveView.SetElementOverrides(ds.Id, ogs);

                t.Commit();
            }
        }

        /// <summary>
        /// Determines whether the specified graph node is an endpoint,
        /// meaning it has exactly one connected edge.
        /// 
        /// <para><b>RU:</b></para>
        /// Определяет, является ли указанный узел конечной точкой графа,
        /// то есть имеет ли он ровно одно подключённое ребро.
        /// </summary>
        /// <param name="n">
        /// The graph node to evaluate.
        /// <para>RU: Узел графа, который необходимо проверить.</para>
        /// </param>
        /// <returns>
        /// <c>true</c> if the node has exactly one edge; otherwise <c>false</c>.  
        /// <para>RU: <c>true</c>, если у узла ровно одно ребро; иначе <c>false</c>.</para>
        /// </returns>
        private bool IsEndpoint(Graph.Node n)
        {
            return n.Edges.Count == 1;
        }


        /// <summary>
        /// Determines whether two graph nodes are collinear with their adjacent routing
        /// directions.  
        /// 
        /// The method checks the angle between:
        /// <para>
        /// • the vector from node A to node B,  
        /// • the direction of the first edge connected to node A,  
        /// • the direction of the first edge connected to node B.  
        /// </para>
        /// If both angles are less than 10 degrees, the nodes are considered collinear.
        /// 
        /// <para><b>RU:</b></para>
        /// Определяет, являются ли два узла графа коллинеарными относительно направлений
        /// их подключённых рёбер.  
        /// 
        /// Метод вычисляет угол между:
        /// <para>
        /// • вектором от узла A к узлу B,  
        /// • направлением первого ребра, подключённого к узлу A,  
        /// • направлением первого ребра, подключённого к узлу B.  
        /// </para>
        /// Если оба угла меньше 10 градусов, узлы считаются коллинеарными.
        /// </summary>
        /// <param name="a">
        /// The first graph node.  
        /// <para>RU: Первый узел графа.</para>
        /// </param>
        /// <param name="b">
        /// The second graph node.  
        /// <para>RU: Второй узел графа.</para>
        /// </param>
        /// <returns>
        /// <c>true</c> if both nodes lie on the same straight routing line; otherwise <c>false</c>.  
        /// <para>RU: <c>true</c>, если оба узла лежат на одной линии трассы; иначе <c>false</c>.</para>
        /// </returns>
        private bool AreCollinear(Graph.Node a, Graph.Node b)
        {
            XYZ ab = (b.Point - a.Point).Normalize();

            var eA = a.Edges.FirstOrDefault();
            if (eA == null) return false;
            XYZ aDir = ((eA.A == a ? eA.B.Point : eA.A.Point) - a.Point).Normalize();

            var eB = b.Edges.FirstOrDefault();
            if (eB == null) return false;
            XYZ bDir = ((eB.A == b ? eB.B.Point : eB.A.Point) - b.Point).Normalize();

            double angleA = ab.AngleTo(aDir);
            double angleB = ab.AngleTo(bDir);

            return angleA < (10 * Math.PI / 180.0) && angleB < (10 * Math.PI / 180.0);
        }

        // ============================================================
        // Привязка устройства
        // ============================================================
        /// <summary>
        /// Attaches a device to the routing graph by creating a graph node at the device
        /// location and connecting it to the nearest routing segment.
        /// 
        /// The method performs several steps:
        /// <para>
        /// 1) Ensures the device is not already registered in <c>_deviceNodes</c>.  
        /// 2) Creates a new graph node at the device's XY position with Z taken from the level elevation.  
        /// 3) Searches all graph edges and finds the closest projection point on any segment.  
        /// 4) If the device lies directly on a segment (within tolerance), it connects to the nearest node.  
        /// 5) Otherwise, inserts a projection node into the segment and connects the device to it.  
        /// 6) Additionally connects the device to all nearby nodes within a radius (700 mm).  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Привязывает устройство к графу трасс, создавая узел графа в точке устройства
        /// и соединяя его с ближайшим сегментом трассы.
        /// 
        /// Метод выполняет несколько этапов:
        /// <para>
        /// 1) Проверяет, не было ли устройство уже добавлено в <c>_deviceNodes</c>.  
        /// 2) Создаёт новый узел графа в координатах устройства, используя Z уровня.  
        /// 3) Ищет ближайшую проекцию точки устройства на любое ребро графа.  
        /// 4) Если устройство лежит на ребре (в пределах допуска), соединяет его с ближайшим узлом.  
        /// 5) Иначе вставляет промежуточный узел в точке проекции и соединяет устройство через него.  
        /// 6) Дополнительно соединяет устройство со всеми узлами в радиусе 700 мм.  
        /// </para>
        /// </summary>
        /// <param name="graph">
        /// The graph representing the routing network on the current level.
        /// <para>RU: Граф трасс текущего уровня.</para>
        /// </param>
        /// <param name="key">
        /// Unique identifier for the device (typically <c>ElementId.ToString()</c>).
        /// <para>RU: Уникальный ключ устройства (обычно <c>ElementId.ToString()</c>).</para>
        /// </param>
        /// <param name="devicePoint">
        /// The original XYZ insertion point of the device.
        /// <para>RU: Исходная точка вставки устройства.</para>
        /// </param>
        /// <param name="doc">
        /// The active Revit document.
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="levelId">
        /// The level ID to which the device belongs.
        /// <para>RU: Идентификатор уровня, к которому относится устройство.</para>
        /// </param>
        /// <returns>
        /// The created or previously registered <see cref="Graph.Node"/> representing the device.
        /// <para>RU: Созданный или ранее зарегистрированный узел графа, соответствующий устройству.</para>
        /// </returns>
        public Graph.Node AttachDeviceToGraphFixed(Graph graph, string key, XYZ devicePoint, Document doc, long levelId)
        {
            // Получаем допуск документа для сравнения расстояний.
            // EN: Revit short-curve tolerance for distance comparisons.
            double tol = doc.Application.ShortCurveTolerance;

            // Если устройство уже добавлено ранее — возвращаем существующий узел.
            // EN: If this device was already registered, return its existing node.
            if (_deviceNodes.TryGetValue(key, out var existingNode))
                return existingNode;

            // Определяем корректную Z-координату: по уровню, если он известен.
            // EN: Determine Z coordinate from level elevation if available.
            double z = _levelsById.ContainsKey(levelId)
                ? _levelsById[levelId].Elevation
                : devicePoint.Z;

            // Создаём новый узел устройства в графе.
            // EN: Add new device node to the graph.
            var deviceNode = graph.AddNode(new XYZ(devicePoint.X, devicePoint.Y, z));

            // Регистрируем узел устройства по ключу.
            // EN: Store device node in global registry.
            _deviceNodes[key] = deviceNode;

            // Переменные для поиска ближайшей проекции на рёбра графа.
            // EN: Variables for nearest projection search.
            double bestDist = double.MaxValue;
            XYZ bestProjection = null;
            Graph.Node bestA = null;
            Graph.Node bestB = null;

            // Перебираем все узлы графа.
            // EN: Iterate through all nodes in the graph.
            foreach (var node in graph.Nodes.ToList())
            {
                // Перебираем все рёбра, исходящие из узла.
                // EN: Iterate through all edges of this node.
                foreach (var edge in node.Edges.ToList())
                {
                    // Получаем точки A и B ребра.
                    // EN: Extract endpoints of the edge.
                    XYZ a = edge.A.Point;
                    XYZ b = edge.B.Point;

                    // Если ребро слишком короткое — пропускаем.
                    // EN: Skip degenerate edges.
                    if (a.DistanceTo(b) < tol)
                        continue;

                    // Вектор AB.
                    // EN: Vector AB.
                    XYZ ab = b - a;

                    // Вектор AP (от A до устройства).
                    // EN: Vector AP (A → device).
                    XYZ ap = deviceNode.Point - a;

                    // Квадрат длины AB.
                    // EN: Squared length of AB.
                    double abLen2 = ab.DotProduct(ab);

                    // Если длина почти нулевая — отрезок вырожденный (A и B совпадают), такое пропускаем.
                    // EN: If length of AB is almost zero, skip this edge.
                    if (abLen2 < 1e-9)
                        continue;

                    // Параметр t проекции точки на отрезок.
                    // EN: Projection parameter t.
                    double t = ap.DotProduct(ab) / abLen2;

                    // Ограничиваем t диапазоном [0,1], чтобы проекция лежала на отрезке.
                    // EN: Clamp t to [0,1].
                    t = Math.Max(0, Math.Min(1, t));

                    // Вычисляем точку проекции.
                    // EN: Compute projection point.
                    XYZ proj = a + t * ab;

                    // Расстояние от устройства до проекции.
                    // EN: Distance from device to projection.
                    double d = proj.DistanceTo(deviceNode.Point);

                    // Если это лучшая (минимальная) проекция — запоминаем.
                    // EN: Update best projection if closer.
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestProjection = proj;
                        bestA = edge.A;
                        bestB = edge.B;
                    }
                }
            }

            // Если проекция найдена — подключаем устройство к графу.
            // EN: If projection found — connect device to graph.
            if (bestProjection != null)
            {
                // Если устройство почти лежит на ребре — соединяем напрямую.
                // EN: If device is very close to edge — connect directly.
                if (bestDist < tol)
                {
                    graph.AddEdge(deviceNode, bestA);
                }
                else
                {
                    // Иначе создаём новый узел в точке проекции.
                    // EN: Otherwise create projection node.
                    var projNode = graph.AddNode(bestProjection);

                    // Соединяем устройство с проекцией.
                    // EN: Connect device → projection.
                    graph.AddEdge(deviceNode, projNode);

                    // Соединяем проекцию с концами исходного ребра.
                    // EN: Connect projection → original edge endpoints.
                    graph.AddEdge(projNode, bestA);
                    graph.AddEdge(projNode, bestB);
                }
            }

            // Радиус автосшивки (в футах).
            // EN: Autoconnect radius.
            double radius = 700.0 / 304.8;

            // Перебираем все узлы графа для автосшивки.
            // EN: Iterate all nodes for autoconnect.
            foreach (var node in graph.Nodes)
            {
                // Пропускаем сам узел устройства.
                // EN: Skip the device node itself.
                if (node == deviceNode)
                    continue;

                // Расстояние до другого узла.
                // EN: Distance to another node.
                double d = node.Point.DistanceTo(deviceNode.Point);

                // Если узел попадает в радиус автосшивки…
                // EN: If within autoconnect radius…
                if (d < radius)
                {
                    // Проверяем, нет ли уже ребра между ними.
                    // EN: Check if edge already exists.
                    bool exists = deviceNode.Edges.Any(e => e.A == node || e.B == node);

                    // Если ребра нет — создаём.
                    // EN: Add edge if missing.
                    if (!exists)
                        graph.AddEdge(deviceNode, node);
                }
            }

            // Возвращаем созданный узел устройства.
            // EN: Return created device node.
            return deviceNode;
        }


        // ============================================================
        // ВИЗУАЛИЗАЦИЯ
        // ============================================================
        /// <summary>
        /// Visualizes the entire routing graph in the active Revit view by drawing:
        /// <para>
        /// • a DirectShape line for every graph edge,  
        /// • a cross-shaped marker for every graph node.  
        /// </para>
        /// 
        /// The method performs several safety checks:
        /// <para>
        /// • validates all XYZ points to avoid invalid geometry,  
        /// • skips degenerate edges shorter than the document tolerance,  
        /// • wraps each DirectShape creation in a try/catch to prevent transaction failure.  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Визуализирует весь граф трасс в активном виде Revit, создавая:
        /// <para>
        /// • линию DirectShape для каждого ребра графа,  
        /// • крест‑маркер для каждого узла графа.  
        /// </para>
        /// 
        /// Метод выполняет несколько проверок:
        /// <para>
        /// • проверяет корректность всех XYZ‑точек,  
        /// • пропускает вырожденные рёбра короче допуска документа,  
        /// • оборачивает создание DirectShape в try/catch, чтобы избежать ошибок транзакции.  
        /// </para>
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.  
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="graph">
        /// The routing graph to visualize.  
        /// <para>RU: Граф трасс, который требуется визуализировать.</para>
        /// </param>
        public void VisualizeGraph(Document doc, Graph graph)
        {
            using (Transaction t = new Transaction(doc, "Visualize Graph"))
            {
                t.Start();

                Category cat = Category.GetCategory(doc, BuiltInCategory.OST_Lines);

                double arm = 100.0 / 304.8;
                double tol = doc.Application.ShortCurveTolerance;

                bool ValidPoint(XYZ p)
                {
                    if (p == null) return false;
                    if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsNaN(p.Z)) return false;
                    if (double.IsInfinity(p.X) || double.IsInfinity(p.Y) || double.IsInfinity(p.Z)) return false;
                    return true;
                }

                foreach (var node in graph.Nodes)
                {
                    foreach (var edge in node.Edges)
                    {
                        XYZ p1 = edge.A.Point;
                        XYZ p2 = edge.B.Point;

                        if (!ValidPoint(p1) || !ValidPoint(p2))
                            continue;

                        if (p1.DistanceTo(p2) < tol)
                            continue;

                        try
                        {
                            var dsEdge = DirectShape.CreateElement(doc, cat.Id);
                            var line = Line.CreateBound(p1, p2);
                            dsEdge.SetShape(new List<GeometryObject> { line });
                        }
                        catch
                        {
                        }
                    }
                }

                foreach (var node in graph.Nodes)
                {
                    XYZ c = node.Point;

                    if (!ValidPoint(c))
                        continue;

                    XYZ h1 = new XYZ(c.X - arm, c.Y, c.Z);
                    XYZ h2 = new XYZ(c.X + arm, c.Y, c.Z);

                    XYZ v1 = new XYZ(c.X, c.Y - arm, c.Z);
                    XYZ v2 = new XYZ(c.X, c.Y + arm, c.Z);

                    try
                    {
                        var dsNode = DirectShape.CreateElement(doc, cat.Id);
                        dsNode.SetShape(new List<GeometryObject>
                        {
                            Line.CreateBound(h1, h2),
                            Line.CreateBound(v1, v2)
                        });
                    }
                    catch
                    {
                    }
                }

                t.Commit();
            }
        }
                /// <summary>
                /// Iteratively splits graph edges at intermediate nodes that lie exactly on
                /// the geometric segment between two edge endpoints.
                /// 
                /// The method performs repeated passes until no more edges can be split:
                /// <para>
                /// • For each edge, checks every node in the graph.  
                /// • Projects the node onto the edge segment.  
                /// • If the projection lies on the segment (within tolerance) and is not
                ///   coincident with either endpoint, the edge is removed and replaced with
                ///   two new edges passing through the node.  
                /// </para>
                /// This ensures that all routing intersections and device attachments
                /// correctly subdivide the graph topology.
                /// 
                /// <para><b>RU:</b></para>
                /// Итеративно разрезает рёбра графа в тех местах, где промежуточные узлы
                /// лежат точно на геометрическом отрезке между двумя концами ребра.
                /// 
                /// Метод выполняет несколько проходов, пока разрезания возможны:
                /// <para>
                /// • Для каждого ребра проверяет каждый узел графа.  
                /// • Проецирует узел на отрезок ребра.  
                /// • Если проекция лежит на отрезке (в пределах допуска) и не совпадает
                ///   с концами ребра, исходное ребро удаляется и заменяется двумя новыми,
                ///   проходящими через этот узел.  
                /// </para>
                /// Это гарантирует корректную топологию графа, когда устройства или
                /// пересечения трасс должны разрезать существующие сегменты.
                /// </summary>
                /// <param name="graph">
                /// The routing graph whose edges should be split.
                /// <para>RU: Граф трасс, рёбра которого необходимо разрезать.</para>
                /// </param>
                /// <param name="tol">
                /// Numerical tolerance for projection comparison.
                /// <para>RU: Числовой допуск для сравнения проекций.</para>
                /// </param>
                //ПОЯСНЯЮЩИЙ РИСУНОК
                // B(3, 5)
               //    /
               //   /          ← ребро AB
              //   /
              // A(1, 2)

             // Bbox:
//┌─────────────┐  maxY = 5 + tol
//│             │
//│      B      │
//│     /       │
//│    /        │
//│   A         │
//│             │
//└─────────────┘  minY = 2 - tol
//minX = 1 - tol   maxX=3+tol
                public void SplitEdgesByNodes(Graph graph, double tol = 1e-6)
                {
                    // Защита на случай пустого графа
                    if (graph == null) return;

                    // Локальная функция: проверяет существование ребра между двумя узлами
                    bool EdgeExists(Graph.Node a, Graph.Node b)
                    {
                        return a.Edges.Any(e => e.A == b || e.B == b);
                    }

                    int maxIterations = 50; // защитный предел
                    int iter = 0;
                    bool changed;

                    do
                    {
                        changed = false;
                        iter++;
                        // Берём снепшот рёбер на входе итерации
                        var edgesSnapshot = graph.Edges.ToList();

                        // Кешируем список узлов для быстрого перебора и обновляем при каждой итерации
                        var allNodes = graph.Nodes.ToList();

                        foreach (var edge in edgesSnapshot)
                        {
                            // Снова убедимся, что ребро все ещё в графе (могло быть удалено в предыдущих итерациях)
                            if (!graph.Edges.Contains(edge))
                                continue;

                            XYZ A = edge.A.Point;
                            XYZ B = edge.B.Point;

                            // Быстрая проверка вырожденного ребра
                            if (A.IsAlmostEqualTo(B) || A.DistanceTo(B) < tol)
                                continue;

                    // bounding box по XY с запасом tol — фильтр кандидатов
                    // Берём минимальную и максимальную координату из двух точек — это и есть границы прямоугольника.
                    // tol добавляется как зазор, чтобы не потерять точки, которые лежат ровно на границе из-за погрешности float.
                    // Пример с числами: A(1, 2), B(3, 5), tol = 0.001
                    //  minX = Min(1, 3) - 0.001 = 0.999
                    //  maxX = Max(1, 3) + 0.001 = 3.001
                    //  minY = Min(2, 5) - 0.001 = 1.999
                    //  maxY = Max(2, 5) + 0.001 = 5.001
                            double minX = Math.Min(A.X, B.X) - tol;
                            double maxX = Math.Max(A.X, B.X) + tol;
                            double minY = Math.Min(A.Y, B.Y) - tol;
                            double maxY = Math.Max(A.Y, B.Y) + tol;

                            var candidates = new List<(double t, Graph.Node node)>();

                            // Предварительный отбор узлов по bbox и исключение концов ребра
                            foreach (var node in allNodes)
                            {
                                if (node == edge.A || node == edge.B) continue;

                                var p = node.Point;
                        // Это 4 простых сравнения чисел.Если узел за пределами прямоугольника — он точно не лежит на отрезке,
                        // дальше не считаем ничего.
                                if (p.X < minX || p.X > maxX || p.Y < minY || p.Y > maxY) continue;

                                // Проекция и параметр t (без вызова отдельной функции — экономим вызовы)
                                XYZ ab = B - A; // стрелка от A к B (направление и длина ребра)
                                XYZ ap = p - A;  // стрелка от A к узлу P
    //                    A •────────────────────• B
                           //\
                           // \  (ap)
                           //  \
                           //   • P

                        
                           //  abLen2 — это длина отрезка в квадрате
                                double abLen2 = ab.DotProduct(ab);
                                if (abLen2 < 1e-9) continue;

                        // DotProduct вектора самого на себя даёт квадрат его длины
                        // (теорема Пифагора без квадратного корня — корень считать дольше).
                        // Если длина почти нулевая — отрезок вырожденный (A и B совпадают), такое пропускаем.
                        // t — это "сколько процентов пути от A до B"
                        // Это самое важное число. t показывает, куда проецируется точка P, если идти по линии от A к B:
  //                      t = -0.3      t = 0(A)    t = 0.5    t = 1(B)    t = 1.5
                           // ✗           •──────────•──────────•          ✗
                    //        вне                  внутри                 вне
                                double t = ap.DotProduct(ab) / abLen2;
                                if (t <= 0.0 + 1e-12 || t >= 1.0 - 1e-12) continue; // строго внутри
                           // Если t меньше 0 или больше 1 — проекция падает за пределы отрезка,
                           // узел точно не лежит на ребре. Сразу continue, не считаем ничего дальше — это и есть экономия.
                           // Зная t, легко найти точку: "встань в A и пройди t процентов пути в направлении B"
                           // То есть каждая координата ab вектора домножается на t отдельно.
                           // t * ab = (t * ab.X, t * ab.Y, t * ab.Z)
                                XYZ proj = A + t * ab;
                                if (proj.DistanceTo(p) < tol)
                                    candidates.Add((t, node));
                            }

                            if (candidates.Count == 0)
                                continue;

                            // Сортируем по параметру t вдоль ребра и удаляем возможные дубликаты (один узел — один раз)
                            var ordered = candidates
                                .OrderBy(x => x.t)
                                .Select(x => x.node)
                                .Distinct()
                                .ToList();

                            // Если ни одного валидного внутреннего узла — пропускаем
                            if (ordered.Count == 0)
                                continue;

                            // Удаляем исходное ребро и заменяем его цепочкой рёбер:
                            // A -> n1 -> n2 -> ... -> B
                            graph.RemoveEdge(edge);

                            var prev = edge.A;
                            foreach (var n in ordered)
                            {
                                if (!EdgeExists(prev, n))
                                    graph.AddEdge(prev, n);
                                prev = n;
                            }

                            if (!EdgeExists(prev, edge.B))
                                graph.AddEdge(prev, edge.B);

                            changed = true;
                        }

                        // Если итераций слишком много — прерываем чтобы избежать зависания
                        if (iter >= maxIterations)
                            break;

                    } while (changed);
                }

        /// <summary>
        /// Computes the shortest routing distance between two Revit devices using the
        /// pre‑built level‑based routing graphs.  
        /// The method resolves the graph nodes corresponding to the start and end devices,
        /// determines the correct level graph, and invokes Dijkstra‑based pathfinding.
        /// 
        /// <para><b>RU:</b></para>
        /// Вычисляет кратчайшее расстояние маршрута между двумя устройствами Revit,
        /// используя заранее построенные графы трасс по уровням.  
        /// Метод определяет узлы графа, соответствующие стартовому и конечному устройству,
        /// выбирает правильный граф уровня и запускает поиск пути (алгоритм Дейкстры).
        /// </summary>
        /// 
        /// <param name="start">
        /// The starting Revit element.
        /// <para><b>RU:</b> Стартовый элемент Revit.</para>
        /// </param>
        /// 
        /// <param name="end">
        /// The target Revit element.
        /// <para><b>RU:</b> Конечный элемент Revit.</para>
        /// </param>
        /// 
        /// <param name="ExpanderId">
        /// Optional override key for the start node (used when routing begins from an expander).
        /// <para><b>RU:</b> Необязательный ключ стартового узла (используется при расчёте от расширителя).</para>
        /// </param>
        /// 
        /// <returns>
        /// The shortest route length, or <c>double.PositiveInfinity</c> if the route cannot be found.
        /// <para><b>RU:</b> Длина кратчайшего маршрута или <c>double.PositiveInfinity</c>, если маршрут не найден.</para>
        /// </returns>

        public double ComputeShortestRouteBetweenDevices(Element start, Element end, string ExpanderId = "")
        {
            try
            {
                // Проверяем, что стартовый элемент не null.
                // EN: Validate that start element is not null.
                if (start == null)
                    throw new ArgumentNullException(nameof(start), "Параметр start не может быть null");

                // Проверяем, что конечный элемент не null.
                // EN: Validate that end element is not null.
                if (end == null)
                    throw new ArgumentNullException(nameof(end), "Параметр end не может быть null");

                // Узлы графа для старта и финиша.
                // EN: Graph nodes for start and end.
                Graph.Node startNode;
                Graph.Node endNode;

                // Ключ стартового узла (ID устройства или ExpanderId).
                // EN: Key used to locate the start node.
                string startKey = string.Empty;

                try
                {
                    // Если указан ExpanderId — используем его вместо ID устройства.
                    // EN: If ExpanderId is provided, use it as the start key.
                    if (!string.IsNullOrEmpty(ExpanderId))
                    {
                        startKey = ExpanderId;

                        // Пытаемся найти узел расширителя.
                        // EN: Try to retrieve expander node.
                        if (!_deviceNodes.TryGetValue(startKey, out startNode))
                        {
                            // Формируем диагностическое сообщение.
                            // EN: Build diagnostic message.
                            string message = $"❌ Ошибка: Узел расширителя не найден!\n\n" +
                                $"Ключ расширителя: '{startKey}'\n\n" +
                                $"Доступные ключи в словаре _deviceNodes ({_deviceNodes.Count} шт.):\n";

                            // Выводим первые 20 ключей для отладки.
                            // EN: Show first 20 keys for debugging.
                            foreach (var key in _deviceNodes.Keys.Take(20))
                                message += $"  • {key}\n";

                            // Показываем окно Revit.
                            // EN: Show diagnostic dialog.
                            TaskDialog.Show("Этап 1: Поиск стартового узла", message);

                            // Возвращаем бесконечность — маршрут невозможен.
                            // EN: Return infinity — cannot compute route.
                            return double.PositiveInfinity;
                        }
                    }
                    else
                    {
                        // Если ExpanderId не указан — используем ID стартового элемента.
                        // EN: Use start element ID as key.
                        startKey = start.Id.ToString();

                        // Пытаемся найти узел устройства.
                        // EN: Try to retrieve start device node.
                        if (!_deviceNodes.TryGetValue(startKey, out startNode))
                        {
                            // Формируем сообщение об ошибке.
                            // EN: Build error message.
                            string message = $"❌ Ошибка: Стартовый узел не найден!\n\n" +
                                $"Ключ устройства (ID): '{startKey}'\n" +
                                $"Название элемента: '{start.Name}'\n" +
                                $"Тип элемента: {start.GetType().Name}\n\n" +
                                $"Доступные ключи в словаре _deviceNodes ({_deviceNodes.Count} шт.):\n";

                            foreach (var key in _deviceNodes.Keys.Take(20))
                                message += $"  • {key}\n";

                            // Если словарь пуст — графы не построены.
                            // EN: If dictionary is empty — graphs were not built.
                            if (_deviceNodes.Count == 0)
                                message += "\n⚠️  Словарь пуст! BuildGraphsByLevel() не был вызван.";

                            TaskDialog.Show("Этап 1: Поиск стартового узла", message);
                            return double.PositiveInfinity;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Ловим исключения при поиске стартового узла.
                    // EN: Catch exceptions during start node lookup.
                    TaskDialog.Show("ОШИБКА на Этапе 1",
                        $"Исключение при поиске стартового узла:\n{ex.GetType().Name}: {ex.Message}");
                    return double.PositiveInfinity;
                }

                // Ключ конечного узла — всегда ID элемента.
                // EN: End node key is always element ID.
                string endKey = end.Id.ToString();

                try
                {
                    // Пытаемся найти конечный узел.
                    // EN: Try to retrieve end node.
                    if (!_deviceNodes.TryGetValue(endKey, out endNode))
                    {
                        string message = $"❌ Ошибка: Конечный узел не найден!\n\n" +
                            $"Ключ устройства (ID): '{endKey}'\n" +
                            $"Название элемента: '{end.Name}'\n" +
                            $"Тип элемента: {end.GetType().Name}\n\n" +
                            $"Доступные ключи в словаре _deviceNodes ({_deviceNodes.Count} шт.):\n";

                        foreach (var key in _deviceNodes.Keys.Take(20))
                            message += $"  • {key}\n";

                        TaskDialog.Show("Этап 2: Поиск конечного узла", message);
                        return double.PositiveInfinity;
                    }
                }
                catch (Exception ex)
                {
                    // Ловим исключения при поиске конечного узла.
                    // EN: Catch exceptions during end node lookup.
                    TaskDialog.Show("ОШИБКА на Этапе 2",
                        $"Исключение при поиске конечного узла:\n{ex.GetType().Name}: {ex.Message}");
                    return double.PositiveInfinity;
                }

                long levelId = -1;

                try
                {
                    // Определяем уровень стартового устройства.
                    // EN: Determine level ID for start device.
                    levelId = GetLevelIdForDevice(start.Document, start as FamilyInstance);

                    // Пытаемся получить граф уровня.
                    // EN: Try to retrieve level graph.
                    if (!_graphsByLevel.TryGetValue(levelId, out var graph))
                    {
                        string message = $"❌ Ошибка: Граф уровня не найден!\n\n" +
                            $"ID уровня: {levelId}\n" +
                            $"Стартовый элемент: {start.Name}\n\n" +
                            $"Доступные уровни в словаре _graphsByLevel ({_graphsByLevel.Count} шт.):\n";

                        foreach (var lvlId in _graphsByLevel.Keys)
                            message += $"  • {lvlId}\n";

                        if (_graphsByLevel.Count == 0)
                            message += "\n⚠️  Граф пуст! BuildGraphsByLevel() не был вызван.";

                        TaskDialog.Show("Этап 3: Поиск графа уровня", message);
                        return double.PositiveInfinity;
                    }

                    // Запускаем поиск кратчайшего пути.
                    // EN: Run Dijkstra shortest path.
                    double result = graph.FindShortestPath(startNode, endNode);


                    // Если путь не найден — диагностируем и пытаемся исправить
                    // EN: If no route found — diagnose and attempt to fix.
                    // -----------------------------------------------------------------------------
                    // ROUTE FAILURE HANDLING LOGIC
                    // EN:
                    // At this point the shortest‑path search (Dijkstra) has completed.  
                    // If the result is PositiveInfinity, it means the algorithm exhausted all 
                    // reachable nodes starting from 'startNode' but never reached 'endNode'.  
                    // This indicates that the graph is NOT fully connected between these two nodes.  
                    //
                    // Typical reasons:
                    //   • The route graph is split into multiple disconnected components.
                    //   • There is a geometric break in the cable tray / conduit network.
                    //   • The device or expander is not attached to the route geometry.
                    //   • The device and expander belong to different level‑graphs.
                    //
                    // The new connectivity‑recovery logic works as follows:
                    //   1. We call FindBreakNode(start, end) to determine the last reachable node 
                    //      in the start‑component — the point where connectivity “breaks”.
                    //   2. If FindBreakNode returns null, it means end IS reachable, but Dijkstra 
                    //      still failed — this is an internal inconsistency (paradox).
                    //   3. If a break node exists, we find the best candidate on the end‑side 
                    //      using FindNearestToBreakOnEndSide(breakNode, end).
                    //   4. We create a “bridge” edge between the two components using ConnectComponents.
                    //   5. After restoring connectivity, we run Dijkstra again.
                    //
                    // RU:
                    // На этом этапе алгоритм поиска кратчайшего пути (Дейкстра) завершил работу.  
                    // Если результат равен PositiveInfinity, это означает, что алгоритм обошёл все 
                    // достижимые узлы, начиная от 'startNode', но так и не достиг 'endNode'.  
                    // Это признак того, что граф НЕ является связным между этими двумя узлами.
                    //
                    // Типичные причины:
                    //   • Граф маршрутов разбит на несколько несвязных компонент.
                    //   • В трассе (лотках/трубах) есть геометрический разрыв.
                    //   • Устройство или расширитель не привязаны к трассе.
                    //   • Устройство и расширитель находятся на разных уровнях.
                    //
                    // Новая логика восстановления связности работает так:
                    //   1. FindBreakNode(start, end) определяет последний достижимый узел — место,
                    //      где “обрывается” связность.
                    //   2. Если FindBreakNode вернул null, значит end достижим, но Дейкстра всё равно 
                    //      не нашла путь — это внутренняя ошибка (парадокс).
                    //   3. Если разрыв найден, FindNearestToBreakOnEndSide ищет лучший узел на стороне end.
                    //   4. ConnectComponents создаёт “мост” между компонентами графа.
                    //   5. После восстановления связности поиск пути выполняется повторно.
                    //
                    // Далее выполняется проверка:
                    // -----------------------------------------------------------------------------

                    if (double.IsPositiveInfinity(result))
                    {
                        // 1. Ищем узел, на котором оборвалась связность
                        var breakNode = graph.FindBreakNode(startNode, endNode, out var visitedStartSide);

                        // Если breakNode == null → граф связный, но Дейкстра не нашла путь (парадокс)
                        if (breakNode == null)
                        {
                            string message =
                                $"⚠️  ПАРАДОКС: Граф связный, но путь не найден!\n\n" +
                                $"От: {start.Name} (ID: {startKey})\n" +
                                $"До: {end.Name} (ID: {endKey})\n" +
                                $"Уровень: {levelId}\n\n" +
                                $"Узлов в графе: {graph.Nodes.Count()}\n" +
                                $"Рёбер в графе: {graph.Edges.Count()}\n\n" +
                                $"Это внутренняя ошибка алгоритма поиска пути.\n" +
                                $"Пожалуйста, проверьте корректность построения графа.";

                            TaskDialog.Show("Этап 4: Внутренняя ошибка", message);
                            return result;
                        }

                        // 2. Ищем лучший узел на стороне end
                        var endSideNode = graph.FindNearestToBreakOnEndSide(breakNode, endNode);

                        if (endSideNode == null)
                        {
                            string message =
                                $"⚠️  Маршрут не найден!\n\n" +
                                $"От: {start.Name} (ID: {startKey})\n" +
                                $"До: {end.Name} (ID: {endKey})\n" +
                                $"Уровень: {levelId}\n\n" +
                                $"Разрыв найден на узле: {breakNode.Point}\n" +
                                $"Но не удалось определить узел на стороне end.\n\n" +
                                $"Требуется ручная проверка трасс.";

                            TaskDialog.Show("Этап 4: Ошибка маршрута", message);
                            return result;
                        }

                        // 3. Создаём мост между компонентами
                        var bridge = graph.ConnectComponents(breakNode, endSideNode);

                        // 4. Повторяем поиск пути после восстановления связности
                        result = graph.FindShortestPath(startNode, endNode);

                        if (double.IsPositiveInfinity(result))
                        {
                            string message =
                                $"⛔ КРИТИЧЕСКАЯ ОШИБКА: Маршрут не найден даже после восстановления связности!\n\n" +
                                $"От: {start.Name} (ID: {startKey})\n" +
                                $"До: {end.Name} (ID: {endKey})\n" +
                                $"Уровень: {levelId}\n\n" +
                                $"Созданный мост:\n" +
                                $"  {breakNode.Point} ↔ {endSideNode.Point}\n\n" +
                                $"Узлов в графе: {graph.Nodes.Count()}\n" +
                                $"Рёбер в графе: {graph.Edges.Count()}\n\n" +
                                $"Статус: Путь недостижим даже после восстановления.\n" +
                                $"Требуется ручная проверка геометрии трасс.";

                            TaskDialog.Show("Этап 4: Критическая ошибка маршрута", message);
                        }
                        else
                        {
                            // Успех — путь найден после восстановления
                            // Можно вывести лог или просто продолжить выполнение
                        }
                    }
                    // Возвращаем результат.
                    // EN: Return computed distance.
                    return result;
                }
                catch (Exception ex)
                {
                    // Ловим исключения при работе с графом.
                    // EN: Catch exceptions during graph operations.
                    TaskDialog.Show("ОШИБКА на Этапе 3-4",
                        $"Исключение при работе с графом:\n" +
                        $"{ex.GetType().Name}: {ex.Message}\n\n" +
                        $"LevelId: {levelId}");
                    return double.PositiveInfinity;
                }
            }
            catch (Exception ex)
            {
                // Ловим любыеunexpected исключения.
                // EN: Catch any unexpected exceptions.
                TaskDialog.Show("НЕОЖИДАННАЯ ОШИБКА",
                    $"Необработанное исключение:\n{ex.GetType().FullName}\n{ex.Message}\n\n{ex.StackTrace}");
                return double.PositiveInfinity;
            }
        }

     
 
      

       

        // ============================================================
        // АВТОМАТИЧЕСКОЕ СОЕДИНЕНИЕ ТРАСС
        // ============================================================
        /// <summary>
        /// Automatically connects mechanical routing segments inside a level graph by:
        /// <para>
        /// • linking nearby collinear nodes within a given radius,  
        /// • inserting projection nodes when a segment passes close to another segment,  
        /// • detecting geometric intersections between segments and splitting them,  
        /// • ensuring that all routing lines form a continuous, topologically correct graph.  
        /// </para>
        /// 
        /// This method significantly improves graph connectivity before device attachment
        /// and shortest‑path calculations.
        /// 
        /// <para><b>RU:</b></para>
        /// Автоматически сшивает элементы трассировки внутри графа уровня, выполняя:
        /// <para>
        /// • соединение близко расположенных коллинеарных узлов в пределах заданного радиуса,  
        /// • вставку узлов‑проекций, если один сегмент проходит рядом с другим,  
        /// • поиск геометрических пересечений между сегментами и разрезание их,  
        /// • формирование непрерывного и корректного с топологической точки зрения графа трасс.  
        /// </para>
        /// 
        /// Метод существенно повышает связность графа перед привязкой устройств
        /// и вычислением кратчайших маршрутов.
        /// </summary>
        /// <param name="graph">
        /// The routing graph for the current level.
        /// <para>RU: Граф трасс текущего уровня.</para>
        /// </param>
        /// <param name="radiusFeet">
        /// Maximum distance (in feet) for auto‑connecting nearby collinear nodes.
        /// <para>RU: Максимальная дистанция (в футах) для автосоединения близких коллинеарных узлов.</para>
        /// </param>
        public void AutoConnectMechanicalEquipment(Graph graph, double radiusFeet)
        {
            // Делаем копию списка узлов, чтобы безопасно итерироваться,
            // даже если граф будет модифицироваться.
            // EN: Copy node list to allow safe iteration during graph modifications.
            var nodes = graph.Nodes.ToList();

            // То же самое — копия списка рёбер.
            // EN: Copy edge list for safe iteration.
            var edges = graph.Edges.ToList();

            // --- 1) Соединяем близкие коллинеарные узлы ---
            // EN: Step 1 — connect nearby collinear nodes.

            for (int i = 0; i < nodes.Count; i++)
            {
                for (int j = i + 1; j < nodes.Count; j++)
                {
                    // Берём пару узлов.
                    // EN: Take node pair.
                    var a = nodes[i];
                    var b = nodes[j];

                    // Вычисляем расстояние между ними.
                    // EN: Compute distance between nodes.
                    double dist = a.Point.DistanceTo(b.Point);

                    // Если расстояние больше радиуса — пропускаем.
                    // EN: Skip if distance exceeds autoconnect radius.
                    if (dist > radiusFeet)
                        continue;

                    // Если между узлами уже есть ребро — пропускаем.
                    // EN: Skip if edge already exists.
                    if (a.Edges.Any(e => e.A == b || e.B == b))
                        continue;

                    // Проверяем, что узлы коллинеарны (лежат на одной линии).
                    // EN: Check if nodes are collinear.
                    if (!AreCollinear(a, b))
                        continue;

                    // Добавляем ребро между узлами.
                    // EN: Add edge between collinear nodes.
                    graph.AddEdge(a, b);
                }
            }

            // --- 2) Вставляем узлы-проекции, если один сегмент проходит близко к другому ---
            // EN: Step 2 — insert projection nodes when a segment passes near another.

            foreach (var edge in edges)
            {
                // Берём точку B текущего ребра.
                // EN: Take endpoint B of current edge.
                var P = edge.B.Point;

                foreach (var other in edges)
                {
                    // Не сравниваем ребро само с собой.
                    // EN: Skip comparing edge with itself.
                    if (other == edge)
                        continue;

                    // Проецируем точку P на другое ребро.
                    // EN: Project point P onto the other segment.
                    XYZ proj = ProjectPointToSegment(P, other.A.Point, other.B.Point);

                    // Расстояние от точки до проекции.
                    // EN: Distance from P to projection.
                    double dist = proj.DistanceTo(P);

                    // Если проекция достаточно близко — создаём узел.
                    // EN: If projection is within radius — create projection node.
                    if (dist < radiusFeet)
                    {
                        // Создаём узел в точке проекции.
                        // EN: Add projection node.
                        var projNode = graph.AddNode(proj);

                        // Разрезаем другое ребро на два.
                        // EN: Split the other edge into two.
                        graph.RemoveEdge(other);
                        graph.AddEdge(other.A, projNode);
                        graph.AddEdge(projNode, other.B);

                        // Соединяем исходную точку с проекцией.
                        // EN: Connect original endpoint to projection node.
                        graph.AddEdge(edge.B, projNode);
                    }
                }
            }

            // --- 3) Разрезаем рёбра в точках пересечения ---
            // EN: Step 3 — split edges at geometric intersections.

            for (int i = 0; i < edges.Count; i++)
            {
                for (int j = i + 1; j < edges.Count; j++)
                {
                    var e1 = edges[i];
                    var e2 = edges[j];

                    // Проверяем пересечение двух отрезков.
                    // EN: Check if segments intersect.
                    if (TryIntersectSegments(e1.A.Point, e1.B.Point, e2.A.Point, e2.B.Point, out XYZ X))
                    {
                        // Создаём узел в точке пересечения.
                        // EN: Add intersection node.
                        var xNode = graph.AddNode(X);

                        // Удаляем оба исходных ребра.
                        // EN: Remove original edges.
                        graph.RemoveEdge(e1);
                        graph.RemoveEdge(e2);

                        // Разрезаем первое ребро на два.
                        // EN: Split first edge.
                        graph.AddEdge(e1.A, xNode);
                        graph.AddEdge(xNode, e1.B);

                        // Разрезаем второе ребро на два.
                        // EN: Split second edge.
                        graph.AddEdge(e2.A, xNode);
                        graph.AddEdge(xNode, e2.B);
                    }
                }
            }
        }

        /// <summary>
        /// Computes the intersection point of two 2D line segments (in the XY plane)
        /// and determines whether the intersection lies within both segments.
        /// 
        /// The method:
        /// <para>
        /// • solves the analytical intersection of two infinite lines,  
        /// • checks whether the intersection point lies on both finite segments,  
        /// • returns the intersection point with averaged Z‑coordinate,  
        /// • returns <c>false</c> if the segments are parallel or do not intersect.  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Вычисляет точку пересечения двух отрезков в плоскости XY и проверяет,
        /// принадлежит ли точка обоим отрезкам.
        /// 
        /// Метод:
        /// <para>
        /// • решает аналитическое пересечение двух прямых,  
        /// • проверяет, лежит ли точка на обоих конечных отрезках,  
        /// • возвращает точку пересечения с усреднённой координатой Z,  
        /// • возвращает <c>false</c>, если отрезки параллельны или не пересекаются.  
        /// </para>
        /// </summary>
        /// <param name="p1">
        /// First endpoint of segment 1.  
        /// <para>RU: Первая точка первого отрезка.</para>
        /// </param>
        /// <param name="p2">
        /// Second endpoint of segment 1.  
        /// <para>RU: Вторая точка первого отрезка.</para>
        /// </param>
        /// <param name="p3">
        /// First endpoint of segment 2.  
        /// <para>RU: Первая точка второго отрезка.</para>
        /// </param>
        /// <param name="p4">
        /// Second endpoint of segment 2.  
        /// <para>RU: Вторая точка второго отрезка.</para>
        /// </param>
        /// <param name="intersection">
        /// Output intersection point if segments intersect; otherwise <c>null</c>.  
        /// <para>RU: Выходная точка пересечения, либо <c>null</c>, если пересечения нет.</para>
        /// </param>
        /// <returns>
        /// <c>true</c> if the segments intersect; otherwise <c>false</c>.  
        /// <para>RU: <c>true</c>, если отрезки пересекаются; иначе <c>false</c>.</para>
        /// </returns>
        private bool TryIntersectSegments(XYZ p1, XYZ p2, XYZ p3, XYZ p4, out XYZ intersection)
        {
            // Инициализируем выходной параметр как null.
            // EN: Initialize output intersection point as null.
            intersection = null;

            // Извлекаем координаты X и Y для удобства и скорости.
            // EN: Extract X/Y coordinates for faster calculations.
            double x1 = p1.X, y1 = p1.Y;
            double x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y;
            double x4 = p4.X, y4 = p4.Y;

            // Вычисляем знаменатель формулы пересечения прямых.
            // Если denom = 0 → прямые параллельны или совпадают.
            // EN: Compute denominator; if zero, lines are parallel or collinear.
            double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);

            // Проверяем на параллельность (или почти параллельность).
            // EN: If denominator is near zero, no unique intersection exists.
            if (Math.Abs(denom) < 1e-9)
                return false;

            // Формула пересечения прямых в 2D (классическая аналитическая геометрия).
            // px, py — координаты точки пересечения бесконечных прямых.
            // EN: Compute intersection point of infinite lines.
            double px = ((x1 * y2 - y1 * x2) * (x3 - x4) -
                         (x1 - x2) * (x3 * y4 - y3 * x4)) / denom;

            double py = ((x1 * y2 - y1 * x2) * (y3 - y4) -
                         (y1 - y2) * (x3 * y4 - y3 * x4)) / denom;

            // Проверяем, лежит ли точка пересечения на первом отрезке.
            // EN: Check if intersection lies on segment p1–p2.
            if (!PointOnSegment(px, py, x1, y1, x2, y2))
                return false;

            // Проверяем, лежит ли точка пересечения на втором отрезке.
            // EN: Check if intersection lies on segment p3–p4.
            if (!PointOnSegment(px, py, x3, y3, x4, y4))
                return false;

            // Вычисляем Z как среднее четырёх Z-координат.
            // Это корректно, т.к. пересечение происходит в XY-плоскости.
            // EN: Compute Z as average of all four Z values (projection onto XY plane).
            double z = (p1.Z + p2.Z + p3.Z + p4.Z) / 4.0;

            // Создаём точку пересечения.
            // EN: Create intersection point.
            intersection = new XYZ(px, py, z);

            // Пересечение найдено.
            // EN: Intersection exists.
            return true;
        }

        /// <summary>
        /// Determines whether a 2D point (px, py) lies within the bounding box
        /// of a line segment defined by (x1, y1) → (x2, y2), using a small tolerance.
        /// 
        /// This method does not check collinearity — it only verifies that the point
        /// falls inside the axis-aligned bounding rectangle of the segment.
        /// 
        /// <para><b>RU:</b></para>
        /// Определяет, принадлежит ли точка (px, py) ограничивающему прямоугольнику
        /// отрезка, заданного точками (x1, y1) → (x2, y2), с использованием небольшого допуска.
        /// 
        /// Метод не проверяет коллинеарность — он лишь определяет, находится ли точка
        /// внутри ограничивающего прямоугольника отрезка.
        /// </summary>
        /// <param name="px">
        /// X‑coordinate of the point to test.
        /// <para>RU: Координата X проверяемой точки.</para>
        /// </param>
        /// <param name="py">
        /// Y‑coordinate of the point to test.
        /// <para>RU: Координата Y проверяемой точки.</para>
        /// </param>
        /// <param name="x1">
        /// X‑coordinate of the first segment endpoint.
        /// <para>RU: Координата X первой точки отрезка.</para>
        /// </param>
        /// <param name="y1">
        /// Y‑coordinate of the first segment endpoint.
        /// <para>RU: Координата Y первой точки отрезка.</para>
        /// </param>
        /// <param name="x2">
        /// X‑coordinate of the second segment endpoint.
        /// <para>RU: Координата X второй точки отрезка.</para>
        /// </param>
        /// <param name="y2">
        /// Y‑coordinate of the second segment endpoint.
        /// <para>RU: Координата Y второй точки отрезка.</para>
        /// </param>
        /// <returns>
        /// <c>true</c> if the point lies within the segment’s bounding box; otherwise <c>false</c>.  
        /// <para>RU: <c>true</c>, если точка находится внутри ограничивающего прямоугольника отрезка; иначе <c>false</c>.</para>
        /// </returns>
        private bool PointOnSegment(double px, double py, double x1, double y1, double x2, double y2)
        {
            // Вычисляем минимальное значение X для отрезка, с небольшим допуском.
            // EN: Compute minimum X of the segment with a small tolerance.
            double minX = Math.Min(x1, x2) - 1e-6;

            // Вычисляем максимальное значение X для отрезка, с небольшим допуском.
            // EN: Compute maximum X of the segment with a small tolerance.
            double maxX = Math.Max(x1, x2) + 1e-6;

            // Вычисляем минимальное значение Y для отрезка, с небольшим допуском.
            // EN: Compute minimum Y of the segment with a small tolerance.
            double minY = Math.Min(y1, y2) - 1e-6;

            // Вычисляем максимальное значение Y для отрезка, с небольшим допуском.
            // EN: Compute maximum Y of the segment with a small tolerance.
            double maxY = Math.Max(y1, y2) + 1e-6;

            // Проверяем, что точка (px, py) лежит внутри ограничивающего прямоугольника отрезка.
            // Если выходит за пределы — точка точно не принадлежит отрезку.
            // EN: Check if (px, py) lies inside the bounding box of the segment.
            // If outside — the point is not on the segment.
            if (px < minX || px > maxX || py < minY || py > maxY)
                return false;

            // Если точка внутри bounding box — считаем, что она принадлежит отрезку.
            // (Коллинеарность проверяется отдельно в TryIntersectSegments.)
            // EN: If inside bounding box — treat as on the segment.
            // (Collinearity is checked separately in TryIntersectSegments.)
            return true;
        }

        /// <summary>
        /// Projects a point <paramref name="p"/> onto a line segment defined by
        /// <paramref name="a"/> → <paramref name="b"/> and returns the closest point
        /// on that segment.
        /// 
        /// The method:
        /// <para>
        /// • handles degenerate segments (when A ≈ B),  
        /// • computes the projection parameter <c>t</c> using dot products,  
        /// • clamps <c>t</c> to the [0, 1] range to ensure the result lies on the segment,  
        /// • returns the projected point in 3D space.  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Проецирует точку <paramref name="p"/> на отрезок, заданный точками
        /// <paramref name="a"/> → <paramref name="b"/>, и возвращает ближайшую точку
        /// на этом отрезке.
        /// 
        /// Метод:
        /// <para>
        /// • корректно обрабатывает вырожденные отрезки (когда A ≈ B),  
        /// • вычисляет параметр проекции <c>t</c> через скалярные произведения,  
        /// • ограничивает <c>t</c> диапазоном [0, 1], чтобы точка лежала на отрезке,  
        /// • возвращает проекцию в трёхмерном пространстве.  
        /// </para>
        /// </summary>
        /// <param name="p">
        /// The point to project.  
        /// <para>RU: Точка, которую нужно спроецировать.</para>
        /// </param>
        /// <param name="a">
        /// First endpoint of the segment.  
        /// <para>RU: Первая точка отрезка.</para>
        /// </param>
        /// <param name="b">
        /// Second endpoint of the segment.  
        /// <para>RU: Вторая точка отрезка.</para>
        /// </param>
        /// <returns>
        /// The closest point on the segment to <paramref name="p"/>.  
        /// <para>RU: Ближайшая точка на отрезке к точке <paramref name="p"/>.</para>
        /// </returns>
        private XYZ ProjectPointToSegment(XYZ p, XYZ a, XYZ b)
        {
            // Если точки A и B практически совпадают — отрезок вырожденный.
            // В этом случае проекция любой точки на такой отрезок — это сама точка A.
            // EN: If A and B are almost identical, the segment is degenerate.
            // The projection of any point onto such a segment is simply A.
            if (a.IsAlmostEqualTo(b))
                return a;

            // Вектор AB = B - A.
            // EN: Vector AB from A to B.
            XYZ ab = b - a;

            // Вектор AP = P - A.
            // EN: Vector AP from A to the point P.
            XYZ ap = p - a;

            // Квадрат длины AB (быстрее, чем длина, и не требует корня).
            // EN: Squared length of AB (avoids expensive sqrt).
            double abLen2 = ab.DotProduct(ab);

            // Если длина AB почти нулевая — отрезок вырожденный.
            // EN: If AB is extremely small, treat as degenerate.
            if (abLen2 < 1e-9)
                return a;

            // Параметр t — скаляр проекции AP на AB:
            // t = (AP · AB) / |AB|²
            // EN: Projection parameter t = dot(AP, AB) / |AB|².
            double t = ap.DotProduct(ab) / abLen2;

            // Ограничиваем t диапазоном [0,1], чтобы проекция лежала на отрезке.
            // EN: Clamp t to [0,1] so projection stays within the segment.
            t = Math.Max(0.0, Math.Min(1.0, t));

            // Возвращаем точку A + t * AB — это и есть проекция P на отрезок AB.
            // EN: Return A + t·AB — the projection point on the segment.
            return a + t * ab;
        }

        /// <summary>
        /// Visualizes all level‑based routing graphs previously constructed in
        /// <c>_graphsByLevel</c> by rendering each graph using
        /// <see cref="VisualizeGraphWithCircles"/>.
        /// 
        /// This method is intended for debugging and inspection of the full
        /// multi‑level routing structure.
        /// 
        /// <para><b>RU:</b></para>
        /// Визуализирует все графы трасс по уровням, ранее сохранённые
        /// в <c>_graphsByLevel</c>, вызывая метод
        /// <see cref="VisualizeGraphWithCircles"/> для каждого уровня.
        /// 
        /// Метод предназначен для отладки и анализа полной структуры
        /// межуровневых трасс.
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.  
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="controllers">
        /// The list of controllers used to build the graphs (not used directly here).  
        /// <para>RU: Список контроллеров, использованный при построении графов
        /// (в данном методе не используется напрямую).</para>
        /// </param>
        public void TestVisualizeFullGraph(Document doc, List<ControllerWithLoops> controllers)
        {
            foreach (var graph in _graphsByLevel.Values)
                VisualizeGraphWithCircles(doc, graph);
        }

        /// <summary>
        /// Visualizes a routing graph on the current plan view by drawing:
        /// <para>
        /// • DirectShape line segments for all edges lying on the active view’s level,  
        /// • circular markers for all nodes on that level (approximated by polyline arcs).  
        /// </para>
        /// 
        /// The method:
        /// <para>
        /// • validates that the active view is a level plan,  
        /// • filters edges and nodes by matching Z‑elevation,  
        /// • draws each edge as a single DirectShape line,  
        /// • draws each node as a circle composed of multiple line segments.  
        /// </para>
        /// 
        /// <para><b>RU:</b></para>
        /// Визуализирует граф трасс на текущем плане уровня, создавая:
        /// <para>
        /// • линии DirectShape для всех рёбер, лежащих на уровне активного вида,  
        /// • круговые маркеры для всех узлов на этом уровне (аппроксимация окружности отрезками).  
        /// </para>
        /// 
        /// Метод:
        /// <para>
        /// • проверяет, что активный вид является планом уровня,  
        /// • фильтрует рёбра и узлы по совпадению координаты Z с уровнем вида,  
        /// • рисует каждое ребро одной линией DirectShape,  
        /// • рисует каждый узел окружностью, составленной из нескольких сегментов.  
        /// </para>
        /// </summary>
        /// <param name="doc">
        /// The active Revit document.  
        /// <para>RU: Текущий документ Revit.</para>
        /// </param>
        /// <param name="graph">
        /// The routing graph to visualize.  
        /// <para>RU: Граф трасс, который требуется визуализировать.</para>
        /// </param>
        public void VisualizeGraphWithCircles(Document doc, Graph graph)
        {
            using (Transaction t = new Transaction(doc, "Visualize Full Graph"))
            {
                t.Start();

                View view = doc.ActiveView;
                Level viewLevel = view.GenLevel;

                if (viewLevel == null)
                {
                    TaskDialog.Show("Ошибка", "Текущий вид не является планом уровня.");
                    t.RollBack();
                    return;
                }

                double levelZ = viewLevel.Elevation;
                double tol = doc.Application.ShortCurveTolerance;

                Category cat = Category.GetCategory(doc, BuiltInCategory.OST_Lines);

                double radius = 100.0 / 304.8;
                int segments = 24;

                foreach (var edge in graph.Edges)
                {
                    if (Math.Abs(edge.A.Point.Z - levelZ) > tol) continue;
                    if (Math.Abs(edge.B.Point.Z - levelZ) > tol) continue;

                    XYZ p1 = edge.A.Point;
                    XYZ p2 = edge.B.Point;

                    if (p1.DistanceTo(p2) < tol)
                        continue;

                    try
                    {
                        var ds = DirectShape.CreateElement(doc, cat.Id);
                        ds.SetShape(new List<GeometryObject>
                        {
                            Line.CreateBound(p1, p2)
                        });
                    }
                    catch { }
                }

                foreach (var node in graph.Nodes)
                {
                    if (Math.Abs(node.Point.Z - levelZ) > tol)
                        continue;

                    XYZ c = node.Point;
                    var segs = new List<GeometryObject>();

                    for (int i = 0; i < segments; i++)
                    {
                        double a1 = 2 * Math.PI * i / segments;
                        double a2 = 2 * Math.PI * (i + 1) / segments;

                        XYZ p1 = c + new XYZ(
                            radius * Math.Cos(a1),
                            radius * Math.Sin(a1),
                            0);

                        XYZ p2 = c + new XYZ(
                            radius * Math.Cos(a2),
                            radius * Math.Sin(a2),
                            0);

                        segs.Add(Line.CreateBound(p1, p2));
                    }

                    try
                    {
                        var ds = DirectShape.CreateElement(doc, cat.Id);
                        ds.SetShape(segs);
                    }
                    catch { }
                }

                t.Commit();
            }
        }
    }
}

