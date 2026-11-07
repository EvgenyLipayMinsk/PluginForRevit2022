using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PluginForRevit2022.Mcp
{
    /// <summary>
    /// WebSocket-сервер на localhost, к которому подключается MCP-сервер (Node.js).
    /// Схема взята из REVIT_MCP_study (MIT): HttpListener + одно эксклюзивное подключение.
    ///  - Рукопожатие с заголовком Origin отклоняется (403): так страница в браузере
    ///    не сможет управлять Revit. MCP-сервер на Node заголовок Origin не отправляет.
    ///  - Пока одно подключение активно, новые получают 409.
    /// Все методы безопасно вызывать из UI-потока Revit; диалоги здесь не показываются.
    /// </summary>
    internal sealed class McpSocketService
    {
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        private HttpListener _listener;
        private CancellationTokenSource _cts;
        private WebSocket _activeSocket;
        private string _activeRemote;
        private DateTime? _connectedAt;

        public McpSocketService(int port)
        {
            Port = port;
        }

        public int Port { get; }

        public bool IsRunning { get; private set; }

        /// <summary>Пришла команда от MCP-сервера. Вызывается в фоновом потоке.</summary>
        public event Action<McpRequest> CommandReceived;

        /// <summary>Изменилось состояние подключения. Вызывается в фоновом потоке.</summary>
        public event Action ConnectionChanged;

        public (bool connected, string remote, DateTime? since) GetStatus()
        {
            lock (_sync)
            {
                bool connected = _activeSocket != null && _activeSocket.State == WebSocketState.Open;
                return (connected, connected ? _activeRemote : null, connected ? _connectedAt : null);
            }
        }

        /// <summary>
        /// Запускает прослушивание порта. Бросает исключение с понятным текстом, если порт занят.
        /// </summary>
        public void Start()
        {
            if (IsRunning)
            {
                return;
            }

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://localhost:{Port}/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                listener.Close();
                throw new InvalidOperationException(
                    $"Не удалось занять порт {Port}: {ex.Message}. " +
                    "Возможно, его использует другая программа или другой экземпляр Revit.", ex);
            }

            _listener = listener;
            _cts = new CancellationTokenSource();
            IsRunning = true;
            McpLog.Info($"Сервер запущен на localhost:{Port}");

            CancellationToken token = _cts.Token;
            Task.Run(() => AcceptLoopAsync(listener, token));
        }

        /// <summary>Останавливает сервер и закрывает подключение.</summary>
        public void Stop()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            try { _cts?.Cancel(); } catch { /* ignore */ }

            DropActiveConnection();

            try { _listener?.Stop(); } catch { /* ignore */ }
            try { _listener?.Close(); } catch { /* ignore */ }
            _listener = null;

            McpLog.Info("Сервер остановлен");
            ConnectionChanged?.Invoke();
        }

        /// <summary>
        /// Сброс: разрывает текущее подключение и заново открывает порт.
        /// MCP-сервер переподключится автоматически при следующем запросе.
        /// </summary>
        public void Reset()
        {
            McpLog.Info("Сброс соединения и порта");
            Stop();
            Start();
        }

        public async Task SendAsync(McpResponse response)
        {
            WebSocket socket;
            lock (_sync)
            {
                socket = _activeSocket;
            }
            if (socket == null || socket.State != WebSocketState.Open)
            {
                McpLog.Info($"Ответ {response.RequestId} не отправлен: нет подключения");
                return;
            }

            byte[] bytes = Encoding.UTF8.GetBytes(McpJson.Serialize(response));
            await _sendLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                McpLog.Error($"Ошибка отправки ответа {response.RequestId}", ex);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task AcceptLoopAsync(HttpListener listener, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        McpLog.Error("Ошибка приёма подключения", ex);
                    }
                    return; // слушатель остановлен
                }

                try
                {
                    await HandleContextAsync(context, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    McpLog.Error("Ошибка обработки подключения", ex);
                }
            }
        }

        private async Task HandleContextAsync(HttpListenerContext context, CancellationToken token)
        {
            if (!context.Request.IsWebSocketRequest)
            {
                Reject(context, 400);
                return;
            }

            if (!string.IsNullOrEmpty(context.Request.Headers["Origin"]))
            {
                McpLog.Info($"Отклонено подключение с Origin '{context.Request.Headers["Origin"]}' (403)");
                Reject(context, 403);
                return;
            }

            lock (_sync)
            {
                if (_activeSocket != null && _activeSocket.State == WebSocketState.Open)
                {
                    Reject(context, 409);
                    return;
                }
            }

            HttpListenerWebSocketContext wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
            string remote = context.Request.RemoteEndPoint?.ToString();
            lock (_sync)
            {
                _activeSocket = wsContext.WebSocket;
                _activeRemote = remote;
                _connectedAt = DateTime.Now;
            }
            McpLog.Info($"MCP-сервер подключён: {remote}");
            ConnectionChanged?.Invoke();

            _ = Task.Run(() => ReceiveLoopAsync(wsContext.WebSocket, token));
        }

        private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken token)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
                {
                    using (var message = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                            if (result.MessageType == WebSocketMessageType.Close)
                            {
                                return;
                            }
                            message.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        string json = Encoding.UTF8.GetString(message.ToArray());
                        McpRequest request;
                        try
                        {
                            request = McpJson.ParseRequest(json);
                        }
                        catch (Exception ex)
                        {
                            McpLog.Error("Некорректный JSON от MCP-сервера", ex);
                            continue;
                        }

                        if (request?.CommandName == null)
                        {
                            await SendAsync(McpResponse.Fail(request?.RequestId, "Не указано имя команды (CommandName)."))
                                .ConfigureAwait(false);
                            continue;
                        }

                        McpLog.Info($"Команда {request.CommandName} ({request.RequestId})");
                        CommandReceived?.Invoke(request);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // остановка сервера
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    McpLog.Error("Соединение прервано", ex);
                }
            }
            finally
            {
                bool wasActive;
                lock (_sync)
                {
                    wasActive = ReferenceEquals(_activeSocket, socket);
                    if (wasActive)
                    {
                        _activeSocket = null;
                        _activeRemote = null;
                        _connectedAt = null;
                    }
                }
                try { socket.Dispose(); } catch { /* ignore */ }
                if (wasActive)
                {
                    McpLog.Info("MCP-сервер отключён");
                    ConnectionChanged?.Invoke();
                }
            }
        }

        private void DropActiveConnection()
        {
            WebSocket socket;
            lock (_sync)
            {
                socket = _activeSocket;
                _activeSocket = null;
                _activeRemote = null;
                _connectedAt = null;
            }
            if (socket != null)
            {
                try { socket.Abort(); } catch { /* ignore */ }
                try { socket.Dispose(); } catch { /* ignore */ }
            }
        }

        private static void Reject(HttpListenerContext context, int statusCode)
        {
            try
            {
                context.Response.StatusCode = statusCode;
                context.Response.Close();
            }
            catch
            {
                // ignore
            }
        }
    }
}
