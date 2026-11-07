using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.UI;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// Связывает WebSocket-сервер, очередь команд Revit и кнопки на ленте.
    /// </summary>
    internal static class McpHost
    {
        /// <summary>
        /// Порт надстройки. 8964 занят надстройкой RevitMCP, поэтому у нас 8965.
        /// Должен совпадать с AERBIM_MCP_PORT в настройках MCP-сервера (.mcp.json).
        /// </summary>
        public const int Port = 8965;

        private static McpEventQueue _queue;
        private static McpSocketService _service;
        private static PushButton _toggleButton;

        public static bool IsRunning => _service != null && _service.IsRunning;

        /// <summary>Вызывается из App.OnStartup (контекст Revit API).</summary>
        public static void Initialize(PushButton toggleButton)
        {
            _queue = new McpEventQueue();
            _toggleButton = toggleButton;
            UpdateButton();
        }

        public static void Start()
        {
            if (IsRunning)
            {
                return;
            }

            var service = new McpSocketService(Port);
            service.CommandReceived += OnCommandReceived;
            service.Start(); // при занятом порте бросит InvalidOperationException
            _service = service;
            UpdateButton();
        }

        public static void Stop()
        {
            if (_service == null)
            {
                return;
            }

            _service.CommandReceived -= OnCommandReceived;
            _service.Stop();
            _service = null;
            UpdateButton();
        }

        /// <summary>Разрывает текущее подключение и заново открывает порт (или просто запускает сервер).</summary>
        public static void Reset()
        {
            if (_service == null)
            {
                Start();
                return;
            }

            _service.Reset();
            UpdateButton();
        }

        public static string GetStatusText()
        {
            if (!IsRunning)
            {
                return $"MCP выключен (порт {Port}).";
            }

            var (connected, remote, since) = _service.GetStatus();
            return connected
                ? $"MCP включён, порт {Port}.\nПодключён MCP-сервер: {remote}, с {since:HH:mm:ss}."
                : $"MCP включён, порт {Port}.\nОжидаю подключения MCP-сервера.";
        }

        private static void OnCommandReceived(McpRequest request)
        {
            McpSocketService service = _service;
            _queue.Enqueue(uiApp =>
            {
                McpResponse response = McpCommandExecutor.Execute(uiApp, request);
                _ = service?.SendAsync(response);
            });
        }

        private static void UpdateButton()
        {
            if (_toggleButton == null)
            {
                return;
            }

            bool on = IsRunning;
            _toggleButton.ItemText = on ? "MCP\rвкл" : "MCP\rвыкл";
            _toggleButton.ToolTip = on
                ? $"MCP-соединение включено (localhost:{Port}). Нажмите, чтобы выключить."
                : $"MCP-соединение выключено. Нажмите, чтобы ИИ-клиент мог подключиться к Revit через localhost:{Port}.";
            _toggleButton.LargeImage = CreateIndicator(on ? Color.FromRgb(46, 160, 67) : Color.FromRgb(140, 140, 140), 32);
            _toggleButton.Image = CreateIndicator(on ? Color.FromRgb(46, 160, 67) : Color.FromRgb(140, 140, 140), 16);
        }

        /// <summary>Иконка-индикатор: цветной круг с коротким текстом (по умолчанию MCP).</summary>
        internal static BitmapSource CreateIndicator(Color color, int size, string label = "MCP")
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawEllipse(new SolidColorBrush(color), null, new Point(size / 2.0, size / 2.0), size / 2.0 - 1, size / 2.0 - 1);
                if (size >= 24)
                {
                    var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold"), size * 0.3, Brushes.White, 1.0);
                    dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
                }
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
