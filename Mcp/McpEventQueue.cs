using System;
using System.Collections.Concurrent;
using Autodesk.Revit.UI;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Переносит команды из фонового потока WebSocket в UI-поток Revit:
    /// Revit API можно вызывать только оттуда.
    /// Очередь FIFO: команды, пришедшие подряд, не теряются (решение из REVIT_MCP_study, MIT).
    /// Создавать только в контексте Revit API (OnStartup или команда).
    /// </summary>
    internal sealed class McpEventQueue : IExternalEventHandler
    {
        private readonly ConcurrentQueue<Action<UIApplication>> _queue = new ConcurrentQueue<Action<UIApplication>>();
        private readonly ExternalEvent _externalEvent;

        public McpEventQueue()
        {
            _externalEvent = ExternalEvent.Create(this);
        }

        public void Enqueue(Action<UIApplication> action)
        {
            if (action == null)
            {
                return;
            }

            _queue.Enqueue(action);
            ExternalEventRequest result = _externalEvent.Raise();
            if (result != ExternalEventRequest.Accepted && result != ExternalEventRequest.Pending)
            {
                McpLog.Info($"ExternalEvent.Raise вернул {result}; команда выполнится при следующем вызове");
            }
        }

        public void Execute(UIApplication app)
        {
            while (_queue.TryDequeue(out Action<UIApplication> action))
            {
                try
                {
                    action(app);
                }
                catch (Exception ex)
                {
                    McpLog.Error("Ошибка выполнения команды", ex);
                }
            }
        }

        public string GetName() => "AerBIM MCP command queue";
    }
}
