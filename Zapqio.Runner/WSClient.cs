using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    public class WSClient : IAsyncDisposable
    {
        private readonly AppSettings _settings;
        private readonly ILogger<WSClient> _logger;
        private readonly MethodsProvider _methodsProvider;
        ClientWebSocket _client;

        public List<MessageMethod> Methods { get; private set; }
        public string Name { get; private set; }

        public Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffor, CancellationToken cancellationToken) => _client.ReceiveAsync(buffor, cancellationToken);

        public WSClient(AppSettings settings, ILogger<WSClient> logger, MethodsProvider methodsProvider)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _methodsProvider = methodsProvider;
            try
            {
                _client = new ClientWebSocket();
                AddHeaders(_client, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize WebSocket client");
                throw;
            }
        }
        private static void AddHeaders(ClientWebSocket client, AppSettings settings)
        {
            try
            {
                if (client == null) throw new ArgumentNullException(nameof(client));
                if (settings == null) throw new ArgumentNullException(nameof(settings));

                if (!string.IsNullOrEmpty(settings.Token))
                {
                    client.Options.SetRequestHeader("X-Zapqio-Token", settings.Token);
                }
                if (!string.IsNullOrEmpty(settings.Name))
                {
                    client.Options.SetRequestHeader("X-Zapqio-Name", settings.Name);
                }
                client.Options.SetRequestHeader(ProtocolVersion.Header, ProtocolVersion.Current.ToString());
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Invalid header value provided", ex);
            }
        }


        public async Task Connect()
        {
            try
            {
                if (_client.State == WebSocketState.Open)
                {
                    return;
                }
                if (_client.State != WebSocketState.None)
                {
                    _logger.LogInformation("Reconnecting WebSocket");
                    try
                    {
                        _client.Abort();
                        _client.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error during cleanup before reconnection");
                    }
                    _client = new ClientWebSocket();
                    AddHeaders(_client, _settings);
                }

                if (string.IsNullOrEmpty(_settings.Url))
                {
                    throw new InvalidOperationException("WebSocket URL is not configured");
                }

                var uri = new Uri($"{_settings.Url}/ws-runner");
                var cancel = new CancellationTokenSource(10000);
                await _client.ConnectAsync(uri, cancel.Token);
                _logger.LogInformation("Successfully connected to WebSocket at {Uri}", uri);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connection attempt timed out after 10 seconds");
            }
            catch (UriFormatException ex)
            {
                _logger.LogError(ex, "Invalid WebSocket URL format: {Url}", _settings.Url);
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "WebSocket connection failed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during connection");
            }
        }
        private async Task<bool> SendMessage(MessageType type, object data)
        {
            try
            {
                var m = new Message
                {
                    Type = type,
                    Data = data == null ? null : (data is string ? data as string : JsonSerializer.Serialize(data, JsonDefaults.Options))
                };
                if (_client.State == WebSocketState.Open)
                {
                    var buff = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(m, JsonDefaults.Options));

                    var cancel = new CancellationTokenSource(10000);
                    await _client.SendAsync(buff, WebSocketMessageType.Text, true, cancel.Token);
                }
                else
                {
                    _logger.LogWarning($"Cannot send message of type {type}, WebSocket is not open. State: {_client.State}");
                }
                return true;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning($"Sending message of type {type} timed out");
                return false;
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, $"WebSocket error while sending message of type {type}");
                return false;
            }
            catch (ObjectDisposedException)
            {
                _logger.LogError($"WebSocket was disposed while trying to send message of type {type}");
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Unexpected error while sending message of type {type}");
                return false;
            }
        }
        public Task<bool> SendQueryOnJob()
        {
            return SendMessage(MessageType.Job, null);
        }
        public Task<bool> SendJobReturn(Guid Id, MessageResponseStatus status, string data)
        {
            var m = new MessageJobReturn
            {
                Data = data,
                Id = Id,
                Status = status
            };
            return SendMessage(MessageType.JobReturn, m);
        }
        public Task<bool> SendLogs(MessageLog log)
        {
            return SendMessage(MessageType.Log, log);
        }
        public bool Connected()
        {
            return _client?.State == WebSocketState.Open;
        }
        public Task<bool> SendInfo()
        {
            var l = new List<MessageMethod>();
            var methods = _methodsProvider.GetMethods();
            foreach (var item in methods)
            {
                var m = new MessageMethod
                {
                    Name = item.NameMethod(),
                    In = item.InData() != null ? NJsonSchema.JsonSchema.FromType(item.InData()).ToJson() : null,
                    Out = item.OutData() != null ? NJsonSchema.JsonSchema.FromType(item.OutData()).ToJson() : null,
                };
                l.Add(m);
            }
            var i = new MessageInfo
            {
                Methods = l,
                Name = _settings.Name
            };
            return SendMessage(MessageType.Info, i);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_client != null)
                {
                    if (_client.State == WebSocketState.Open || _client.State == WebSocketState.CloseReceived)
                    {
                        var cancel = new CancellationTokenSource(5000);
                        await _client.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", cancel.Token);
                    }
                    _client.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Closing WebSocket timed out during disposal");
                _client?.Abort();
                _client?.Dispose();
            }
            catch (WebSocketException ex)
            {
                _logger.LogError(ex, "WebSocket error during disposal");
                _client?.Abort();
                _client?.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed, nothing to do
                _logger.LogDebug("WebSocket was already disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during WebSocket disposal");
                try
                {
                    _client?.Abort();
                    _client?.Dispose();
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
