using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Zapqio.Runner.Protocol;
using Zapqio.Runner.Protocol.Enums;

namespace Zapqio.Runner
{
    public class WSClient : IAsyncDisposable
    {
        /// <summary>
        /// Wynik próby uzgodnienia połączenia. Odmowa nie jest tu wyjątkiem, bo dla pętli głównej to
        /// zwykły stan - liczy się wyłącznie to, jak długo odczekać przed kolejną próbą.
        /// </summary>
        public readonly record struct ConnectResult(bool Connected, HttpStatusCode? Status, TimeSpan? RetryAfter)
        {
            public static ConnectResult Ok() => new(true, null, null);

            public static ConnectResult Failed(HttpStatusCode? status = null, TimeSpan? retryAfter = null)
                => new(false, status, retryAfter);
        }

        /// <summary>
        /// Ile najwyżej honorujemy z nagłówka <c>Retry-After</c>. Zepsuta albo wroga wartość
        /// zaparkowałaby runnera na dowolnie długo, a to gorsze niż ponowienie za wcześnie.
        /// </summary>
        private const int MaxRetryAfterSeconds = 300;

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
                ConfigureClient(_client, settings);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize WebSocket client");
                throw;
            }
        }
        private static void ConfigureClient(ClientWebSocket client, AppSettings settings)
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

                // Bez tego nieudane uzgadnianie daje wyłącznie WebSocketException, w którym status
                // odpowiedzi już nie istnieje - a wtedy 429 jest nie do odróżnienia od zerwanego
                // połączenia i runner ponawia w tym samym rytmie, zamiast się wycofać.
                client.Options.CollectHttpResponseDetails = true;
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException("Invalid header value provided", ex);
            }
        }


        public async Task<ConnectResult> Connect()
        {
            try
            {
                if (_client.State == WebSocketState.Open)
                {
                    return ConnectResult.Ok();
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
                    ConfigureClient(_client, _settings);
                }

                if (string.IsNullOrEmpty(_settings.Url))
                {
                    throw new InvalidOperationException("WebSocket URL is not configured");
                }

                var uri = new Uri($"{_settings.Url}/ws-runner");
                var cancel = new CancellationTokenSource(10000);
                await _client.ConnectAsync(uri, cancel.Token);
                _logger.LogInformation("Successfully connected to WebSocket at {Uri}", uri);
                return ConnectResult.Ok();
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Connection attempt timed out after 10 seconds");
                return ConnectResult.Failed();
            }
            catch (UriFormatException ex)
            {
                _logger.LogError(ex, "Invalid WebSocket URL format: {Url}", _settings.Url);
                return ConnectResult.Failed();
            }
            catch (WebSocketException ex)
            {
                return HandshakeRefused(ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error during connection");
                return ConnectResult.Failed();
            }
        }

        /// <summary>
        /// Rozbiera nieudane uzgadnianie na to, czego potrzebuje pętla główna: status odpowiedzi i
        /// ewentualny termin ponowienia. Status bywa nieznany - gdy do serwera w ogóle nie doszliśmy,
        /// odpowiedzi HTTP nie było.
        /// </summary>
        private ConnectResult HandshakeRefused(WebSocketException ex)
        {
            var status = _client.HttpStatusCode;
            if (status == default)
            {
                _logger.LogError(ex, "WebSocket connection failed");
                return ConnectResult.Failed();
            }

            var retryAfter = ReadRetryAfter();

            if (status == HttpStatusCode.TooManyRequests)
            {
                // Świadomie nie jako błąd: serwer nie doszedł nawet do tokenu (§3 protokołu), więc
                // odmowa nie mówi nic o tożsamości runnera i nie ma tu czego naprawiać.
                _logger.LogWarning(
                    "Serwer ogranicza tempo uzgodnień (429){RetryAfter}",
                    retryAfter is null ? "" : $", prosi o odczekanie {retryAfter.Value.TotalSeconds:0}s");
            }
            else
            {
                _logger.LogError(ex, "Serwer odrzucił uzgadnianie ze statusem {Status}", (int)status);
            }

            return ConnectResult.Failed(status, retryAfter);
        }

        /// <summary>
        /// Czyta <c>Retry-After</c> z odpowiedzi na odrzucone uzgadnianie. Protokół (§3) określa tę
        /// wartość jako liczbę sekund, więc daty HTTP nie próbujemy rozumieć - brak wartości albo
        /// wartość nie do sparsowania znaczy tyle, że o terminie decyduje sam runner.
        /// </summary>
        private TimeSpan? ReadRetryAfter()
        {
            var headers = _client.HttpResponseHeaders;
            if (headers is null)
                return null;

            var raw = headers
                .FirstOrDefault(h => string.Equals(h.Key, "Retry-After", StringComparison.OrdinalIgnoreCase))
                .Value?.FirstOrDefault();

            if (!int.TryParse(raw, out var seconds) || seconds < 0)
                return null;

            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds));
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
                    //nic nie poszło w gniazdo, więc to nie jest sukces - inaczej wołający uzna, że platforma dostała wiadomość
                    return false;
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
