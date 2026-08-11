using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProviderKimiRouter;

/// <summary>
/// Small HTTP/1.1 loopback server for the Kimi Responses adapter. TcpListener
/// is used instead of HttpListener/Kestrel so a normal Windows user does not
/// need an URLACL reservation or an ASP.NET Core shared runtime.
/// </summary>
public sealed class KimiRouterServer : IAsyncDisposable
{
    private const int MaxHeaderBytes = 64 * 1024;
    private const int MaxBodyBytes = 8 * 1024 * 1024;
    private const int MaxUpstreamErrorBytes = 1024 * 1024;
    private const int MaxSseLineChars = 256 * 1024;
    private const int MaxSseDataBytes = 2 * 1024 * 1024;
    private const int MaxConcurrentRequests = 16;
    private static readonly TimeSpan RequestReadTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(2);
    private readonly KimiRouterOptions _options;
    private readonly HttpClient _httpClient;
    private readonly bool _disposeHttpClient;
    private readonly KimiResponseHistoryCache _historyCache = new();
    private readonly SemaphoreSlim _requestGate = new(MaxConcurrentRequests, MaxConcurrentRequests);
    private TcpListener? _listener;

    public KimiRouterServer(KimiRouterOptions options, HttpMessageHandler? upstreamHandler = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (upstreamHandler is null)
        {
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
            _disposeHttpClient = true;
        }
        else
        {
            _httpClient = new HttpClient(upstreamHandler, disposeHandler: false)
            {
                Timeout = TimeSpan.FromMinutes(10)
            };
        }
    }

    public Uri ListenUri => _options.ListenUri;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            throw new InvalidOperationException("The Kimi router is already running.");
        }

        _listener = new TcpListener(IPAddress.Loopback, _options.ListenPort);
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleClientAsync(client, cancellationToken);
            }
        }
        finally
        {
            _listener.Stop();
            _listener = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener?.Stop();
        _listener = null;
        if (_disposeHttpClient)
        {
            _httpClient.Dispose();
        }

        _requestGate.Dispose();

        return ValueTask.CompletedTask;
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        using (var stream = client.GetStream())
        {
            var entered = false;
            try
            {
                entered = await _requestGate.WaitAsync(
                    QueueWaitTimeout,
                    cancellationToken).ConfigureAwait(false);
                if (!entered)
                {
                    await WriteJsonAsync(
                        stream,
                        HttpStatusCode.TooManyRequests,
                        ErrorBody("router_busy", "The Kimi router is busy; retry the request."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);
                readTimeout.CancelAfter(RequestReadTimeout);
                HttpRequestData? request;
                try
                {
                    request = await ReadRequestAsync(stream, readTimeout.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await WriteJsonAsync(
                        stream,
                        HttpStatusCode.RequestTimeout,
                        ErrorBody("request_timeout", "The request headers or body took too long to arrive."),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (request is null)
                {
                    return;
                }

                await DispatchAsync(request, stream, cancellationToken).ConfigureAwait(false);
            }
            catch (RequestFormatException exception)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    ErrorBody("invalid_request_error", exception.Message),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller closed the local connection or the process is stopping.
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await WriteJsonAsync(
                        stream,
                        HttpStatusCode.GatewayTimeout,
                        ErrorBody("upstream_timeout", "The Kimi upstream timed out."),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The connection was already closed.
                }
            }
            catch (IOException)
            {
                // A disconnected local client does not need a second response.
            }
            catch (Exception)
            {
                try
                {
                    await WriteJsonAsync(
                        stream,
                        HttpStatusCode.BadGateway,
                        ErrorBody("router_error", "The Kimi router could not complete the request."),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // The connection was already closed.
                }
            }
            finally
            {
                if (entered)
                {
                    _requestGate.Release();
                }
            }
        }
    }

    private async Task DispatchAsync(
        HttpRequestData request,
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        if (request.Method == "GET" && request.Path is ("/health" or "/v1/health"))
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.OK,
                new JsonObject
                {
                    ["status"] = "ok",
                    ["service"] = "codex-provider-kimi-router",
                    ["listen"] = ListenUri.ToString(),
                    ["upstream"] = _options.UpstreamBaseUri.ToString(),
                    ["pid"] = Environment.ProcessId
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method == "GET" && request.Path is ("/v1/models" or "/models"))
        {
            await WriteJsonAsync(stream, HttpStatusCode.OK, BuildModelsResponse(), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (request.Path != "/v1/responses")
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.NotFound,
                ErrorBody("not_found", "The Kimi router endpoint was not found."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (request.Method != "POST")
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.MethodNotAllowed,
                ErrorBody("method_not_allowed", "The Responses endpoint accepts POST only."),
                cancellationToken,
                "Allow: POST\r\n").ConfigureAwait(false);
            return;
        }

        var bearer = ResolveBearerToken(request.Headers);
        if (bearer is null)
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.Unauthorized,
                ErrorBody("authentication_error", "A Bearer credential is required."),
                cancellationToken,
                "WWW-Authenticate: Bearer\r\n").ConfigureAwait(false);
            return;
        }

        JsonDocument requestDocument;
        try
        {
            requestDocument = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            await WriteJsonAsync(
                stream,
                HttpStatusCode.BadRequest,
                ErrorBody("invalid_request_error", "The Responses request body is not valid JSON."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using (requestDocument)
        {
            var responsesRequest = requestDocument.RootElement;
            if (responsesRequest.ValueKind != JsonValueKind.Object)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    ErrorBody("invalid_request_error", "The Responses request body must be a JSON object."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var requestedModel = ReadString(responsesRequest, "model") ?? _options.DefaultModel;
            if (!string.Equals(
                    requestedModel,
                    KimiRouterOptions.DefaultModelName,
                    StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.BadRequest,
                    ErrorBody(
                        "unsupported_model",
                        $"The Kimi router currently supports only {KimiRouterOptions.DefaultModelName}."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var chatRequest = KimiResponsesTranslator.BuildChatCompletionsRequest(responsesRequest);
            // The upstream contract is intentionally narrower than the
            // Responses input contract: always send the canonical SuiXiang
            // model id even if a caller used a different casing for `k3`.
            chatRequest["model"] = KimiRouterOptions.DefaultModelName;
            var customToolNames = KimiResponsesTranslator.GetCustomToolNames(responsesRequest);
            var previousResponseId = ReadString(responsesRequest, "previous_response_id");
            var previousMessages = new JsonArray();
            var previousFound = previousResponseId is not null &&
                _historyCache.TryGet(previousResponseId, out previousMessages);
            if (previousResponseId is not null && !previousFound)
            {
                await WriteJsonAsync(
                    stream,
                    HttpStatusCode.Conflict,
                    ErrorBody(
                        "previous_response_not_found",
                        "The previous response is no longer available in this router process."),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (previousResponseId is not null)
            {
                KimiResponseHistoryCache.Prepend(chatRequest, previousMessages);
            }

            var streamResponse = ReadBoolean(responsesRequest, "stream") == true;
            await CallUpstreamAsync(
                stream,
                chatRequest,
                requestedModel,
                streamResponse,
                bearer,
                customToolNames,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CallUpstreamAsync(
        NetworkStream clientStream,
        JsonObject chatRequest,
        string requestedModel,
        bool streamResponse,
        string bearer,
        ISet<string> customToolNames,
        CancellationToken cancellationToken)
    {
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, _options.ChatCompletionsUri)
        {
            Content = new StringContent(
                chatRequest.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                Encoding.UTF8,
                "application/json")
        };
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        upstreamRequest.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(streamResponse ? "text/event-stream" : "application/json"));

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await _httpClient.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteJsonAsync(
                clientStream,
                HttpStatusCode.BadGateway,
                ErrorBody("upstream_unavailable", "The Kimi upstream could not be reached."),
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await WriteJsonAsync(
                clientStream,
                HttpStatusCode.GatewayTimeout,
                ErrorBody("upstream_timeout", "The Kimi upstream timed out."),
                cancellationToken).ConfigureAwait(false);
            return;
        }

        using (upstreamResponse)
        {
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                await WriteUpstreamFailureAsync(
                    clientStream,
                    upstreamResponse,
                    bearer,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!streamResponse)
            {
                try
                {
                    var content = await ReadBoundedTextAsync(
                        upstreamResponse.Content,
                        MaxBodyBytes,
                        cancellationToken).ConfigureAwait(false);
                    var translated = KimiResponsesTranslator.TranslateNonStreaming(
                        content,
                        requestedModel,
                        null,
                        customToolNames);
                    StoreNonStreamingHistory(chatRequest, content, translated);
                    await WriteRawAsync(
                        clientStream,
                        HttpStatusCode.OK,
                        "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(translated),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    await WriteJsonAsync(
                        clientStream,
                        HttpStatusCode.BadGateway,
                        ErrorBody("upstream_protocol_error", "The Kimi upstream returned invalid JSON."),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    await WriteJsonAsync(
                        clientStream,
                        HttpStatusCode.BadGateway,
                        ErrorBody("upstream_protocol_error", "The Kimi upstream response was too large."),
                        cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await StreamTranslatedResponseAsync(
                clientStream,
                upstreamResponse,
                requestedModel,
                customToolNames,
                chatRequest,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task StreamTranslatedResponseAsync(
        NetworkStream clientStream,
        HttpResponseMessage upstreamResponse,
        string requestedModel,
        ISet<string> customToolNames,
        JsonObject chatRequest,
        CancellationToken cancellationToken)
    {
        var headersWritten = false;
        KimiResponsesStreamState? state = null;
        List<KimiResponsesSseEvent>? events = null;
        try
        {
            await using var upstreamStream = await upstreamResponse.Content
                .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(upstreamStream, Encoding.UTF8);
            var pending = new List<string>();
            var pendingBytes = 0;
            events = new List<KimiResponsesSseEvent>();
            state = new KimiResponsesStreamState(
                requestedModel,
                null,
                customToolNames,
                events.Add);

            await WriteHeadersAsync(
                clientStream,
                HttpStatusCode.OK,
                "text/event-stream; charset=utf-8",
                "Cache-Control: no-cache\r\nTransfer-Encoding: chunked\r\n").ConfigureAwait(false);
            headersWritten = true;

            state.EmitInitialEvents();
            await FlushEventsAsync(clientStream, events, cancellationToken).ConfigureAwait(false);

            var sawDone = false;
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (line.Length > MaxSseLineChars)
                {
                    throw new InvalidDataException("The Kimi upstream SSE line was too large.");
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line[5..].TrimStart();
                    if (value.Length > 0)
                    {
                        pendingBytes += Encoding.UTF8.GetByteCount(value);
                        if (pendingBytes > MaxSseDataBytes)
                        {
                            throw new InvalidDataException("The Kimi upstream SSE event was too large.");
                        }

                        pending.Add(value);
                    }

                    continue;
                }

                // SSE permits comments and metadata fields. Unknown non-empty
                // lines are rejected instead of being silently dropped.
                if (line.Length != 0 &&
                    !line.StartsWith(":", StringComparison.Ordinal) &&
                    !line.StartsWith("event:", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("id:", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("retry:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new JsonException("The Kimi upstream returned malformed SSE.");
                }

                if (line.Length != 0 || pending.Count == 0)
                {
                    continue;
                }

                var data = string.Join("\n", pending);
                pending.Clear();
                pendingBytes = 0;
                if (data == "[DONE]")
                {
                    sawDone = true;
                    break;
                }

                if (!TryParseJson(data, out var chunk))
                {
                    throw new JsonException("The Kimi upstream returned invalid SSE JSON.");
                }

                using (chunk)
                {
                    state.ProcessChunk(chunk.RootElement);
                }

                await FlushEventsAsync(clientStream, events, cancellationToken).ConfigureAwait(false);
            }

            if (!sawDone)
            {
                throw new InvalidDataException("The Kimi upstream SSE stream ended before [DONE].");
            }

            state.Complete();
            await FlushEventsAsync(clientStream, events, cancellationToken).ConfigureAwait(false);
            var historyMessages = (chatRequest["messages"] as JsonArray)?.DeepClone() as JsonArray ?? new JsonArray();
            historyMessages.Add(state.BuildAssistantMessageForHistory());
            _historyCache.Store(state.ResponseId, historyMessages);
            await WriteChunkTerminatorAsync(clientStream, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (headersWritten && state is not null)
            {
                state.Fail("upstream_timeout", "The Kimi upstream timed out.");
                if (events is not null)
                {
                    await FlushEventsAsync(clientStream, events, cancellationToken).ConfigureAwait(false);
                    await WriteChunkTerminatorAsync(clientStream, cancellationToken).ConfigureAwait(false);
                }
                return;
            }

            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or HttpRequestException)
        {
            if (!headersWritten || state is null)
            {
                throw;
            }

            state.Fail(
                exception is InvalidDataException ? "upstream_truncated" : "malformed_sse",
                exception is InvalidDataException
                    ? exception.Message
                    : "The Kimi upstream returned a malformed response.");
            if (events is not null)
            {
                await FlushEventsAsync(clientStream, events, cancellationToken).ConfigureAwait(false);
                await WriteChunkTerminatorAsync(clientStream, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void StoreNonStreamingHistory(JsonObject chatRequest, string upstreamJson, string translatedJson)
    {
        try
        {
            using var upstream = JsonDocument.Parse(upstreamJson);
            using var translated = JsonDocument.Parse(translatedJson);
            var responseId = translated.RootElement.TryGetProperty("id", out var id)
                ? id.GetString()
                : null;
            var assistant = KimiResponsesTranslator.BuildAssistantMessageForHistory(upstream.RootElement);
            if (responseId is null || assistant is null || chatRequest["messages"] is not JsonArray messages)
            {
                return;
            }

            var history = (JsonArray)messages.DeepClone();
            history.Add(assistant);
            _historyCache.Store(responseId, history);
        }
        catch (JsonException)
        {
            // A malformed upstream response is reported to the caller; no history is cached.
        }
    }

    private async Task WriteUpstreamFailureAsync(
        NetworkStream clientStream,
        HttpResponseMessage upstreamResponse,
        string bearer,
        CancellationToken cancellationToken)
    {
        string body;
        try
        {
            body = await ReadBoundedTextAsync(
                upstreamResponse.Content,
                MaxUpstreamErrorBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            body = string.Empty;
        }

        if (!string.IsNullOrEmpty(bearer))
        {
            body = body.Replace(bearer, "[REDACTED]", StringComparison.Ordinal);
        }

        var statusClass = (int)upstreamResponse.StatusCode is >= 400 and < 500
            ? "upstream-4xx"
            : "upstream-5xx";
        var extraHeaders =
            $"X-Kimi-Router-Error-Class: {statusClass}\r\n" +
            $"X-Kimi-Router-Upstream-Status: {(int)upstreamResponse.StatusCode}\r\n";
        await WriteRawAsync(
            clientStream,
            upstreamResponse.StatusCode,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(body)
                ? ErrorBody("upstream_http_error", $"Kimi upstream returned {(int)upstreamResponse.StatusCode}.").ToJsonString()
                : body),
            cancellationToken,
            extraHeaders).ConfigureAwait(false);
    }

    private static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 &&
            content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException("The upstream response was too large.");
        }

        await using var stream = await content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(
                chunk.AsMemory(),
                cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (buffer.Length + count > maxBytes)
            {
                throw new InvalidDataException("The upstream response was too large.");
            }

            await buffer.WriteAsync(
                chunk.AsMemory(0, count),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private JsonObject BuildModelsResponse()
    {
        var data = new JsonArray();
        var names = new[] { KimiRouterOptions.DefaultModelName };
        foreach (var name in names)
        {
            data.Add(new JsonObject
            {
                ["id"] = name,
                ["object"] = "model",
                ["owned_by"] = "sui-xiang"
            });
        }

        return new JsonObject
        {
            ["object"] = "list",
            ["data"] = data
        };
    }

    private static string? ResolveBearerToken(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.TryGetValue("Authorization", out var authorization) &&
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization[7..].Trim();
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        return null;
    }

    private static async Task<HttpRequestData?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(4096);
        var buffer = new byte[4096];
        var headerEnd = -1;
        while (bytes.Count < MaxHeaderBytes)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            bytes.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEnd = FindHeaderEnd(bytes);
            if (headerEnd >= 0)
            {
                break;
            }
        }

        if (headerEnd < 0)
        {
            throw new RequestFormatException("HTTP request headers are too large.");
        }

        var headerText = Encoding.ASCII.GetString(bytes.ToArray(), 0, headerEnd);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        if (lines.Length == 0)
        {
            throw new RequestFormatException("HTTP request headers are missing.");
        }

        var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            throw new RequestFormatException("HTTP request line is invalid.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            headers[lines[index][..separator].Trim()] = lines[index][(separator + 1)..].Trim();
        }

        if (headers.ContainsKey("Transfer-Encoding"))
        {
            throw new RequestFormatException("Chunked request bodies are not supported.");
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthText) &&
            (!int.TryParse(contentLengthText, out contentLength) || contentLength < 0))
        {
            throw new RequestFormatException("Content-Length is invalid.");
        }

        if (contentLength > MaxBodyBytes)
        {
            throw new RequestFormatException("Request body is too large.");
        }

        var bodyStart = headerEnd + 4;
        var body = new byte[contentLength];
        var available = Math.Max(0, bytes.Count - bodyStart);
        if (available > 0)
        {
            Array.Copy(bytes.ToArray(), bodyStart, body, 0, Math.Min(available, body.Length));
        }

        var offset = Math.Min(available, body.Length);
        while (offset < body.Length)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new RequestFormatException("HTTP request body ended unexpectedly.");
            }

            offset += read;
        }

        var path = requestLine[1].Split('?', 2)[0];
        return new HttpRequestData(requestLine[0].ToUpperInvariant(), path, headers, body);
    }

    private static async Task WriteJsonAsync(
        NetworkStream stream,
        HttpStatusCode status,
        JsonObject body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        await WriteRawAsync(
            stream,
            status,
            "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(body.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            cancellationToken,
            extraHeaders).ConfigureAwait(false);
    }

    private static async Task WriteRawAsync(
        NetworkStream stream,
        HttpStatusCode status,
        string contentType,
        byte[] body,
        CancellationToken cancellationToken,
        string extraHeaders = "")
    {
        await WriteHeadersAsync(stream, status, contentType, extraHeaders + $"Content-Length: {body.Length}\r\n").ConfigureAwait(false);
        await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteHeadersAsync(
        NetworkStream stream,
        HttpStatusCode status,
        string contentType,
        string extraHeaders)
    {
        var reason = ((int)status).ToString(CultureInfo.InvariantCulture) switch
        {
            "200" => "OK",
            "400" => "Bad Request",
            "401" => "Unauthorized",
            "404" => "Not Found",
            "405" => "Method Not Allowed",
            "409" => "Conflict",
            "429" => "Too Many Requests",
            "408" => "Request Timeout",
            "500" => "Internal Server Error",
            "502" => "Bad Gateway",
            "503" => "Service Unavailable",
            "504" => "Gateway Timeout",
            _ => "Error"
        };
        var headers =
            $"HTTP/1.1 {(int)status} {reason}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            "Connection: close\r\n" +
            extraHeaders +
            "\r\n";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(headers)).AsTask();
    }

    private static async Task FlushEventsAsync(
        NetworkStream stream,
        List<KimiResponsesSseEvent> events,
        CancellationToken cancellationToken)
    {
        foreach (var item in events)
        {
            await WriteChunkAsync(stream, item.ToSse(), cancellationToken).ConfigureAwait(false);
        }

        events.Clear();
    }

    private static async Task WriteChunkAsync(
        NetworkStream stream,
        string text,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var prefix = Encoding.ASCII.GetBytes(bytes.Length.ToString("X", CultureInfo.InvariantCulture) + "\r\n");
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("\r\n"), cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteChunkTerminatorAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        return stream.WriteAsync(Encoding.ASCII.GetBytes("0\r\n\r\n"), cancellationToken).AsTask();
    }

    private static int FindHeaderEnd(List<byte> bytes)
    {
        for (var index = 3; index < bytes.Count; index++)
        {
            if (bytes[index - 3] == '\r' && bytes[index - 2] == '\n' &&
                bytes[index - 1] == '\r' && bytes[index] == '\n')
            {
                return index - 3;
            }
        }

        return -1;
    }

    private static JsonObject ErrorBody(string type, string message)
    {
        return new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["type"] = type,
                ["message"] = message
            }
        };
    }

    private static string? ReadString(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static bool? ReadBoolean(JsonElement value, string propertyName)
    {
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private sealed record HttpRequestData(
        string Method,
        string Path,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);

    private sealed class RequestFormatException : Exception
    {
        public RequestFormatException(string message)
            : base(message)
        {
        }
    }
}
