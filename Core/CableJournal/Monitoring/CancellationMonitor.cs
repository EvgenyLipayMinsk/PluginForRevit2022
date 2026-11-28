using System;

namespace ARBM.CableJournalExporter.Services
{
    /// <summary>
    /// Lightweight cooperative cancellation monitor used by watchdog.
    /// Methods set CurrentOperation for diagnostics and check IsCancellationRequested periodically.
    /// RequestCancel marks cancellation and stores a short reason.
    /// This class is intentionally minimal and thread‑safe for simple polling checks.
    /// </summary>
    public class CancellationMonitor
    {
        // 0 = not requested, 1 = requested
        // volatile гарантирует, что поток всегда
        // увидит актуальное значение переменной
        // volatile говорит процессору и CLR:
        // “Не кешируй это поле, всегда читай его прямо из памяти,
        // и сразу записывай обратно.”
        private volatile int _requested;

        private readonly object _sync = new object();

        /// <summary>
        /// True when cancellation was requested by watchdog or caller.
        /// Long-running loops must check this flag and exit cooperatively.
        /// </summary>
        public bool IsCancellationRequested => _requested != 0;

        /// <summary>
        /// Short description of current operation performed by worker.
        /// Consumer (watchdog) or caller sets this to help diagnostics on timeout.
        /// </summary>
        public string CurrentOperation { get; private set; }

        /// <summary>
        /// Optional human-readable cancel reason (set by RequestCancel).
        /// </summary>
        public string CancelReason { get; private set; }

        /// <summary>
        /// Set current operation (lightweight).
        /// </summary>
        public void SetCurrentOperation(string operation)
        {
            CurrentOperation = operation;
        }

        /// <summary>
        /// Clear current operation.
        /// </summary>
        public void ClearCurrentOperation()
        {
            CurrentOperation = null;
        }

        /// <summary>
        /// Request cancellation and set reason.
        /// Safe to call from any thread (watchdog).
        /// </summary>
        public void RequestCancel(string reason = null)
        {
            // lock обеспечивает эксклюзивный доступ к критической секции,
            // чтобы два потока не записали данные одновременно и
            // не сломали состояние объекта
            // то механизм синхронизации, который гарантирует:
            // Только один поток может выполнять код внутри блока lock одновременно.
            // Если два потока одновременно вызовут RequestCancel(), то:
            // первый войдёт в lock,
           // второй будет ждать, пока первый выйдет,
           // затем второй войдёт.
           // Это защищает критическую секцию от гонок.
            lock (_sync)
            {
                CancelReason = reason ?? "requested";
                _requested = 1;
            }
        }
    }
}
