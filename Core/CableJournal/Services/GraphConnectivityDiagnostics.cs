using System;
using System.Collections.Generic;
using System.Linq;
using static ARBM.CableJournalExporter.Services.RouteLengthCalculator;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Provides diagnostic and repair utilities for routing graph connectivity issues.
    /// 
    /// <para><b>RU:</b></para>
    /// Предоставляет диагностические и восстановительные утилиты для проблем с связностью графов трасс.
    /// </summary>
    public class GraphConnectivityDiagnostics
    {
        /// <summary>
        /// Report containing detailed connectivity analysis of a graph segment.
        /// 
        /// <para><b>RU:</b></para>
        /// Отчёт с детальным анализом связности сегмента графа.
        /// </summary>
        public class ConnectivityReport
        {
            /// <summary>
            /// Indicates whether the start node exists in the graph.
            /// <para>RU: Указывает, существует ли стартовый узел в графе.</para>
            /// </summary>
            public bool StartExists { get; set; }

            /// <summary>
            /// Indicates whether the end node exists in the graph.
            /// <para>RU: Указывает, существует ли конечный узел в графе.</para>
            /// </summary>
            public bool EndExists { get; set; }

            /// <summary>
            /// Indicates whether the start node has any connected edges.
            /// <para>RU: Указывает, имеет ли стартовый узел подключённые рёбра.</para>
            /// </summary>
            public bool StartHasEdges { get; set; }

            /// <summary>
            /// Indicates whether the end node has any connected edges.
            /// <para>RU: Указывает, имеет ли конечный узел подключённые рёбра.</para>
            /// </summary>
            public bool EndHasEdges { get; set; }

            /// <summary>
            /// Indicates whether the end node is reachable from the start node via graph traversal.
            /// <para>RU: Указывает, достижим ли конечный узел от стартового.</para>
            /// </summary>
            public bool EndReachable { get; set; }

            /// <summary>
            /// Collection of nodes that are not reachable from the start node.
            /// <para>RU: Коллекция узлов, недостижимых от стартового узла.</para>
            /// </summary>
            public List<Graph.Node> UnreachableNodes { get; set; } = new List<Graph.Node>();

            /// <summary>
            /// Collection of nodes that have no connected edges (completely isolated).
            /// <para>RU: Коллекция узлов, полностью изолированных (без рёбер).</para>
            /// </summary>
            public List<Graph.Node> IsolatedNodes { get; set; } = new List<Graph.Node>();

            /// <summary>
            /// Human-readable summary of connectivity issues found.
            /// <para>RU: Удобочитаемое резюме найденных проблем связности.</para>
            /// </summary>
            public string Summary { get; set; }
        }

        /// <summary>
        /// Report describing actions taken to repair graph connectivity.
        /// 
        /// <para><b>RU:</b></para>
        /// Отчёт с описанием действий по восстановлению связности графа.
        /// </summary>
        public class FixReport
        {
            /// <summary>
            /// List of actions performed during the repair process.
            /// <para>RU: Список действий, выполненных во время восстановления.</para>
            /// </summary>
            public List<string> Actions { get; set; } = new List<string>();

            /// <summary>
            /// Gets the combined summary of all performed actions.
            /// <para>RU: Получает объединённое резюме всех выполненных действий.</para>
            /// </summary>
            public string Summary => string.Join("\n", Actions);

            /// <summary>
            /// Indicates whether the repair was successful in restoring connectivity.
            /// <para>RU: Указывает, успешно ли восстановлена связность.</para>
            /// </summary>
            public bool IsSuccessful { get; set; }
        }

        private readonly Graph _graph;

        /// <summary>
        /// Initializes a new instance of the GraphConnectivityDiagnostics class.
        /// </summary>
        /// <param name="graph">The graph to diagnose and repair.</param>
        public GraphConnectivityDiagnostics(Graph graph)
        {
            _graph = graph;
        }

        /// <summary>
        /// Analyzes graph connectivity between two nodes using BFS traversal.
        /// 
        /// <para><b>RU:</b></para>
        /// Анализирует связность графа между двумя узлами, используя BFS.
        /// </summary>
        /// <param name="start">The start node for connectivity analysis.</param>
        /// <param name="end">The target node to check reachability.</param>
        /// <returns>A ConnectivityReport containing detailed analysis results.</returns>
        public ConnectivityReport CheckConnectivity(Graph.Node start, Graph.Node end)
        {
            var report = new ConnectivityReport();

            // 1) Проверяем наличие узлов в графе
            // EN: Check if nodes exist in the graph.
            report.StartExists = _graph.Nodes.Contains(start);
            report.EndExists = _graph.Nodes.Contains(end);

            if (!report.StartExists || !report.EndExists)
            {
                report.Summary = "Один из узлов отсутствует в графе.";
                return report;
            }

            // 2) Проверяем наличие рёбер у стартового и конечного узлов
            // EN: Check if nodes have any connected edges.
            report.StartHasEdges = start.Edges.Count > 0;
            report.EndHasEdges = end.Edges.Count > 0;

            // 3) BFS для проверки достижимости
            // EN: BFS traversal to check reachability.
            var visited = new HashSet<Graph.Node>();
            var queue = new Queue<Graph.Node>();
            queue.Enqueue(start);
            visited.Add(start);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();

                if (node == end)
                {
                    report.EndReachable = true;
                    break;
                }

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

            // 4) Находим недостижимые узлы
            // EN: Find unreachable nodes.
            foreach (var n in _graph.Nodes)
            {
                if (!visited.Contains(n))
                    report.UnreachableNodes.Add(n);
            }

            // 5) Находим изолированные узлы (без рёбер)
            // EN: Find isolated nodes with no edges.
            foreach (var n in _graph.Nodes)
            {
                if (n.Edges.Count == 0)
                    report.IsolatedNodes.Add(n);
            }

            // 6) Формируем резюме
            // EN: Build summary.
            var summaryParts = new List<string>();

            if (!report.StartHasEdges)
                summaryParts.Add("Стартовый узел не имеет рёбер.");

            if (!report.EndHasEdges)
                summaryParts.Add("Конечный узел не имеет рёбер.");

            if (!report.EndReachable)
                summaryParts.Add("Конечный узел недостижим от стартового.");

            if (report.IsolatedNodes.Count > 0)
                summaryParts.Add($"Изолированных узлов: {report.IsolatedNodes.Count}.");

            if (report.UnreachableNodes.Count > 0)
                summaryParts.Add($"Недостижимых узлов: {report.UnreachableNodes.Count}.");

            report.Summary = summaryParts.Count > 0
                ? string.Join(" ", summaryParts)
                : "Граф связный, путь должен существовать.";

            return report;
        }

        /// <summary>
        /// Attempts to repair graph connectivity by reconnecting isolated and unreachable nodes.
        /// 
        /// <para><b>RU:</b></para>
        /// Попытается восстановить связность графа, переподключив изолированные
        /// и недостижимые узлы к ближайшим узлам.
        /// </summary>
        /// <param name="start">The start node.</param>
        /// <param name="end">The end node.</param>
        /// <param name="connectivityReport">The connectivity report from CheckConnectivity.</param>
        /// <returns>A FixReport describing actions taken and success status.</returns>
        public FixReport FixConnectivity(Graph.Node start, Graph.Node end, ConnectivityReport connectivityReport)
        {
            var fix = new FixReport();

            // 1) Критические ошибки — узлы отсутствуют
            // EN: Critical errors — nodes don't exist.
            if (!connectivityReport.StartExists)
            {
                fix.Actions.Add("❌ Стартовый узел отсутствует в графе — невозможно исправить.");
                fix.IsSuccessful = false;
                return fix;
            }

            if (!connectivityReport.EndExists)
            {
                fix.Actions.Add("❌ Конечный узел отсутствует в графе — невозможно исправить.");
                fix.IsSuccessful = false;
                return fix;
            }

            // Если путь уже существует — ничего не делаем
            // EN: If path already exists, no repair needed.
            if (connectivityReport.EndReachable)
            {
                fix.Actions.Add("✔ Путь уже существует — исправления не требуются.");
                fix.IsSuccessful = true;
                return fix;
            }

            // 2) Если стартовый узел не имеет рёбер — привязываем
            // EN: Connect start node if it has no edges.
            if (!connectivityReport.StartHasEdges)
            {
                var nearest = FindNearestNode(start);
                if (nearest != null && nearest != start)
                {
                    _graph.AddEdge(start, nearest);
                    fix.Actions.Add($"✔ Стартовый узел привязан к ближайшему узлу.");
                }
                else
                {
                    fix.Actions.Add("⚠ Не удалось найти ближайший узел для старта.");
                }
            }

            // 3) Если конечный узел не имеет рёбер — привязываем
            // EN: Connect end node if it has no edges.
            if (!connectivityReport.EndHasEdges)
            {
                var nearest = FindNearestNode(end);
                if (nearest != null && nearest != end)
                {
                    _graph.AddEdge(end, nearest);
                    fix.Actions.Add($"✔ Конечный узел привязан к ближайшему узлу.");
                }
                else
                {
                    fix.Actions.Add("⚠ Не удалось найти ближайший узел для конца.");
                }
            }

            // 4) Исправляем изолированные узлы
            // EN: Fix isolated nodes.
            foreach (var iso in connectivityReport.IsolatedNodes)
            {
                if (iso == start || iso == end)
                    continue;

                var nearest = FindNearestNode(iso);
                if (nearest != null && nearest != iso)
                {
                    _graph.AddEdge(iso, nearest);
                    fix.Actions.Add($"✔ Изолированный узел переподключен.");
                }
            }

            // 5) Исправляем недостижимые узлы
            // EN: Fix unreachable nodes.
            foreach (var unreachable in connectivityReport.UnreachableNodes)
            {
                if (unreachable == start || unreachable == end)
                    continue;

                var nearest = FindNearestNode(unreachable);
                if (nearest != null && nearest != unreachable)
                {
                    _graph.AddEdge(unreachable, nearest);
                    fix.Actions.Add($"✔ Недостижимый узел переподключен.");
                }
            }

            // 6) Проверяем результат после исправлений
            // EN: Verify connectivity after repairs.
            var finalReport = CheckConnectivity(start, end);

            if (finalReport.EndReachable)
            {
                fix.Actions.Add("🎉 Путь успешно восстановлен!");
                fix.IsSuccessful = true;
            }
            else
            {
                fix.Actions.Add("⚠ Путь остался недостижим — требуется ручная проверка.");
                fix.IsSuccessful = false;
            }

            return fix;
        }

        /// <summary>
        /// Finds the nearest node to the specified node based on geometric distance.
        /// </summary>
        /// <param name="node">The reference node.</param>
        /// <returns>The nearest node, or null if no other nodes exist.</returns>
        private Graph.Node FindNearestNode(Graph.Node node)
        {
            Graph.Node nearest = null;
            double minDist = double.MaxValue;

            foreach (var n in _graph.Nodes)
            {
                if (n == node)
                    continue;

                double dist = n.Point.DistanceTo(node.Point);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = n;
                }
            }

            return nearest;
        }
    }
}
