using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Thin wrapper around RouteLengthCalculator that measures and logs
    /// high‑level timings and statistics. This wrapper does not change
    /// algorithmic behavior — it only delegates to the inner implementation
    /// and records metrics before/after calls.
    /// 
    /// Дополнительно поддерживает cooperative cancellation через CancellationMonitor:
    /// - BuildGraphsByLevel принимает опциональный <see cref="CancellationMonitor"/> и устанавливает текущую операцию,
    ///   проверяет флаг отмены до/после тяжёлых шагов и логирует отмену.
    /// - ComputeShortestRouteBetweenDevices принимает опциональный монитор и логирует контекст.
    /// </summary>
    internal class LoggedRouteLengthCalculator
    {
        private readonly RouteLengthCalculator _inner;

        public LoggedRouteLengthCalculator()
        {
            _inner = new RouteLengthCalculator();
        }

        /// <summary>
        /// Builds graphs by level and logs counts and timings around the call.
        /// Accepts optional <paramref name="cancel"/> to support cooperative cancellation.
        /// </summary>
        public void BuildGraphsByLevel(Document doc, List<ApsControllerWithLoops> controllers, string filterParam = "", CancellationMonitor cancel = null)
        {
            var sw = Stopwatch.StartNew();
            ExecutionLogger.Log("BuildGraphsByLevel", "start", details: $"filterParam={filterParam},controllers={controllers?.Count ?? 0}");

            // If cancellation requested before starting — abort early.
            if (cancel?.IsCancellationRequested == true)
            {
                ExecutionLogger.Log("BuildGraphsByLevel", "cancelled_before_start", details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before BuildGraphsByLevel start.");
            }

            // Set diagnostic operation for watchdog/monitoring.
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel");
            try
            {
                // Delegate to original implementation.
                // Note: RouteLengthCalculator.BuildGraphsByLevel был обновлён для принятия CancellationMonitor в патче.
                _inner.BuildGraphsByLevel(doc, controllers, filterParam, cancel);
            }
            catch (OperationCanceledException oce)
            {
                // Логируем совместную отмену и повторно выбрасываем исключение.
                sw.Stop();
                ExecutionLogger.Log("BuildGraphsByLevel", "cancelled", durationMs: sw.ElapsedMilliseconds, details: oce.Message);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ExecutionLogger.Log("BuildGraphsByLevel", "error", durationMs: sw.ElapsedMilliseconds, details: ex.ToString());
                throw;
            }
            finally
            {
                // Сбрасываем текущую операцию независимо от результата.
                cancel?.ClearCurrentOperation();

                sw.Stop();

                // Collect counts from internal structures exposed by original class.
                int graphsCount = _inner._graphsByLevel?.Count ?? 0;
                long totalNodes = 0;
                long totalEdges = 0;

                if (_inner._graphsByLevel != null)
                {
                    foreach (var g in _inner._graphsByLevel.Values)
                    {
                        // Safe access: Graph exposes Nodes/Edges enumerables.
                        totalNodes += g.Nodes == null ? 0 : System.Linq.Enumerable.Count(g.Nodes);
                        totalEdges += g.Edges == null ? 0 : System.Linq.Enumerable.Count(g.Edges);
                    }
                }

                int deviceNodes = _inner._deviceNodes?.Count ?? 0;

                var counts = new Dictionary<string, object>
                {
                    ["graphs"] = graphsCount,
                    ["nodes"] = totalNodes,
                    ["edges"] = totalEdges,
                    ["deviceNodes"] = deviceNodes
                };

                ExecutionLogger.Log("BuildGraphsByLevel", "end", durationMs: sw.ElapsedMilliseconds, counts: counts);
            }
        }
        /// <summary>
        /// Builds graphs by level for Souye system and logs counts and timings around the call.
        /// Accepts optional <paramref name="cancel"/> to support cooperative cancellation.
        /// </summary>
        public void BuildGraphsByLevel(Document doc, SouyeControllerWithLoops data, string filterParam = "", CancellationMonitor cancel = null)
        {
            var sw = Stopwatch.StartNew();
            ExecutionLogger.Log("BuildGraphsByLevel", "start", details: $"filterParam={filterParam},souye=true");

            // If cancellation requested before starting — abort early.
            if (cancel?.IsCancellationRequested == true)
            {
                ExecutionLogger.Log("BuildGraphsByLevel", "cancelled_before_start", details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before BuildGraphsByLevel start.");
            }

            // Set diagnostic operation for watchdog/monitoring.
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel (Souye)");
            try
            {
                // Delegate to original implementation.
                _inner.BuildGraphsByLevel(doc, data, filterParam, cancel);
            }
            catch (OperationCanceledException oce)
            {
                // Логируем совместную отмену и повторно выбрасываем исключение.
                sw.Stop();
                ExecutionLogger.Log("BuildGraphsByLevel", "cancelled", durationMs: sw.ElapsedMilliseconds, details: oce.Message);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ExecutionLogger.Log("BuildGraphsByLevel", "error", durationMs: sw.ElapsedMilliseconds, details: ex.ToString());
                throw;
            }
            finally
            {
                // Сбрасываем текущую операцию независимо от результата.
                cancel?.ClearCurrentOperation();

                sw.Stop();

                // Collect counts from internal structures exposed by original class.
                int graphsCount = _inner._graphsByLevel?.Count ?? 0;
                long totalNodes = 0;
                long totalEdges = 0;

                if (_inner._graphsByLevel != null)
                {
                    foreach (var g in _inner._graphsByLevel.Values)
                    {
                        // Safe access: Graph exposes Nodes/Edges enumerables.
                        totalNodes += g.Nodes == null ? 0 : System.Linq.Enumerable.Count(g.Nodes);
                        totalEdges += g.Edges == null ? 0 : System.Linq.Enumerable.Count(g.Edges);
                    }
                }

                int deviceNodes = _inner._deviceNodes?.Count ?? 0;

                var counts = new Dictionary<string, object>
                {
                    ["graphs"] = graphsCount,
                    ["nodes"] = totalNodes,
                    ["edges"] = totalEdges,
                    ["deviceNodes"] = deviceNodes
                };

                ExecutionLogger.Log("BuildGraphsByLevel", "end", durationMs: sw.ElapsedMilliseconds, counts: counts);
            }
        }

        /// <summary>
        /// Compute shortest route and log timing plus basic diagnostics.
        /// Accepts optional <paramref name="cancel"/> to set current operation for diagnostics.
        /// The underlying implementation currently does not accept cancellation token,
        /// so we perform pre/post checks and log the context for watchdog.
        /// </summary>
        public double ComputeShortestRouteBetweenDevices(Element start, Element end, string ExpanderId = "", CancellationMonitor cancel = null)
        {
            var sw = Stopwatch.StartNew();
            ExecutionLogger.Log("ComputeShortestRouteBetweenDevices", "start", details: $"startId={start?.Id.ToString()},endId={end?.Id.ToString()},expander={ExpanderId}");

            if (cancel?.IsCancellationRequested == true)
            {
                sw.Stop();
                ExecutionLogger.Log("ComputeShortestRouteBetweenDevices", "cancelled_before_call", durationMs: sw.ElapsedMilliseconds, details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before ComputeShortestRouteBetweenDevices call.");
            }

            cancel?.SetCurrentOperation($"ComputeShortestRouteBetweenDevices:{start?.Id}->{end?.Id}");

            try
            {
                // Delegate to original implementation (no cancellation parameter).
                double res = _inner.ComputeShortestRouteBetweenDevices(start, end, ExpanderId);

                sw.Stop();

                var counts = new Dictionary<string, object>
                {
                    ["deviceNodes"] = _inner._deviceNodes?.Count ?? 0,
                    ["graphs"] = _inner._graphsByLevel?.Count ?? 0
                };

                ExecutionLogger.Log("ComputeShortestRouteBetweenDevices", "end", durationMs: sw.ElapsedMilliseconds, counts: counts, details: $"resultFeet={res}");
                return res;
            }
            catch (OperationCanceledException oce)
            {
                sw.Stop();
                ExecutionLogger.Log("ComputeShortestRouteBetweenDevices", "cancelled", durationMs: sw.ElapsedMilliseconds, details: oce.Message);
                throw;
            }
            catch (Exception ex)
            {
                sw.Stop();
                ExecutionLogger.Log("ComputeShortestRouteBetweenDevices", "error", durationMs: sw.ElapsedMilliseconds, details: ex.ToString());
                throw;
            }
            finally
            {
                cancel?.ClearCurrentOperation();
            }
        }

        /// <summary>
        /// Возвращает внутренний экземпляр RouteLengthCalculator.
        /// EN: Returns the inner RouteLengthCalculator instance.
        /// </summary>
        public RouteLengthCalculator Inner => _inner;

        // Expose accessors if needed by callers (read-only).
        public Dictionary<long, RouteLengthCalculator.Graph> GraphsByLevel => _inner._graphsByLevel;
        public Dictionary<string, RouteLengthCalculator.Graph.Node> DeviceNodes => _inner._deviceNodes;
    }
}
