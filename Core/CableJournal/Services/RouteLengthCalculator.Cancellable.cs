using ARBM.CableJournalExporter.Models;
using ARBM.CableJournalExporter.Systems.Souye.Models;
using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Partial helpers: cancellation-aware overloads that wrap existing RouteLengthCalculator methods.
    /// These overloads are thin wrappers: they set diagnostic CurrentOperation on the provided
    /// CancellationMonitor, perform pre/post cancellation checks and delegate to the original methods.
    /// This approach avoids large edits of the main RouteLengthCalculator implementation while
    /// providing cooperative cancellation entry points used by the LoggedRouteLengthCalculator and callers.
    /// </summary>
    public partial class RouteLengthCalculator
    {
        /// <summary>
        /// Cancellation-aware wrapper for BuildGraphsByLevel.
        /// Calls the original BuildGraphsByLevel(doc, controllers, filterParam) implementation
        /// but sets CurrentOperation and checks cancellation before and after the call.
        /// </summary>
        public void BuildGraphsByLevel(Document doc, List<ApsControllerWithLoops> controllers, string filterParam = "", CancellationMonitor cancel = null)
        {
            // Quick pre-check
            if (cancel?.IsCancellationRequested == true)
            {
                ExecutionLogger.Log("RouteLengthCalculator.BuildGraphsByLevel", "cancelled_before_start", details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before BuildGraphsByLevel start.");
            }

            // Set diagnostic operation for watchdog / monitor
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel");

            try
            {
                // Delegate to existing implementation (same class, original method without CancellationMonitor).
                // Overload resolution will call the original BuildGraphsByLevel(Document, List<ApsControllerWithLoops>, string)
                BuildGraphsByLevel(doc, controllers, filterParam);
            }
            catch (OperationCanceledException)
            {
                // rethrow to caller (monitoring code handles logging)
                throw;
            }
            catch (Exception ex)
            {
                // Log unexpected error and rethrow
                try { ExecutionLogger.Log("RouteLengthCalculator.BuildGraphsByLevel", "error", details: ex.ToString()); } catch { }
                throw;
            }
            finally
            {
                // Clear diagnostic operation
                cancel?.ClearCurrentOperation();
            }
        }
        /// <summary>
        /// Cancellation-aware wrapper for BuildGraphsByLevel (Souye variant).
        /// Calls the original BuildGraphsByLevel(doc, data, filterParam) implementation
        /// but sets CurrentOperation and checks cancellation before and after the call.
        /// </summary>
        public void BuildGraphsByLevel(Document doc, SouyeControllerWithLoops data, string filterParam = "", CancellationMonitor cancel = null)
        {
            // Quick pre-check
            if (cancel?.IsCancellationRequested == true)
            {
                ExecutionLogger.Log("RouteLengthCalculator.BuildGraphsByLevel", "cancelled_before_start", details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before BuildGraphsByLevel start.");
            }

            // Set diagnostic operation for watchdog / monitor
            cancel?.SetCurrentOperation("RouteLengthCalculator.BuildGraphsByLevel (Souye)");

            try
            {
                // Delegate to existing implementation (same class, original method without CancellationMonitor).
                // Overload resolution will call the original BuildGraphsByLevel(Document, SouyeControllerWithLoops, string)
                BuildGraphsByLevel(doc, data, filterParam);
            }
            catch (OperationCanceledException)
            {
                // rethrow to caller (monitoring code handles logging)
                throw;
            }
            catch (Exception ex)
            {
                // Log unexpected error and rethrow
                try { ExecutionLogger.Log("RouteLengthCalculator.BuildGraphsByLevel", "error", details: ex.ToString()); } catch { }
                throw;
            }
            finally
            {
                // Clear diagnostic operation
                cancel?.ClearCurrentOperation();
            }
        }

        /// <summary>
        /// Cancellation-aware wrapper for ComputeShortestRouteBetweenDevices.
        /// Sets CurrentOperation, checks cancellation and delegates to the original method.
        /// Returns the same value as the original overload.
        /// </summary>
        public double ComputeShortestRouteBetweenDevices(Element start, Element end, string ExpanderId = "", CancellationMonitor cancel = null)
        {
            // Pre-check
            if (cancel?.IsCancellationRequested == true)
            {
                ExecutionLogger.Log("RouteLengthCalculator.ComputeShortestRouteBetweenDevices", "cancelled_before_start", details: cancel?.CancelReason);
                throw new OperationCanceledException("Cancelled before ComputeShortestRouteBetweenDevices call.");
            }

            cancel?.SetCurrentOperation($"ComputeShortestRoute:{start?.Id}->{end?.Id}");

            try
            {
                // Delegate to original implementation (the overload without CancellationMonitor).
                return ComputeShortestRouteBetweenDevices(start, end, ExpanderId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { ExecutionLogger.Log("RouteLengthCalculator.ComputeShortestRouteBetweenDevices", "error", details: ex.ToString()); } catch { }
                throw;
            }
            finally
            {
                cancel?.ClearCurrentOperation();
            }
        }
    }
}
