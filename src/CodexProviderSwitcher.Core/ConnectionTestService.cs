using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CodexProviderSwitcher.Core;

public sealed partial class ConnectionTestService
{
    private const string ProbeFunctionName = "capability_probe";
    private const int MaxSseBodyBytes = 8 * 1024 * 1024;
    private const int MaxJsonBodyBytes = 64 * 1024 * 1024;
    private const int MaxErrorBodyBytes = 64 * 1024;
    private readonly HttpClient _client;

    public ConnectionTestService()
        : this(new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(3)
        })
    {
    }

    // The GUI uses the default client; the injectable client keeps protocol
    // tests deterministic without sending test traffic to a real provider.
    public ConnectionTestService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<ConnectionTestResult> TestResponsesApiAsync(
        string baseUrl,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
        var payload = new
        {
            model = model.Trim(),
            input = "Reply with exactly OK.",
            stream = true,
            max_output_tokens = 16
        };
        var transport = await SendResponsesStreamAsync(
            baseUrl,
            payload,
            apiKey,
            TimeSpan.FromSeconds(60),
            cancellationToken);
        if (!transport.Success)
        {
            return new ConnectionTestResult(
                false,
                DescribeTransportFailure(transport, "Responses API"),
                transport.StatusCode);
        }

        var stream = transport.Stream!;
        if (stream.Failed)
        {
            return new ConnectionTestResult(
                false,
                F(
                    "Responses 流报告失败：{0}",
                    "The Responses stream reported a failure: {0}",
                    string.IsNullOrWhiteSpace(stream.Error)
                        ? T(
                            "上游返回了 response.failed，但没有附带错误消息。",
                            "The upstream returned response.failed without an error message.")
                        : stream.Error),
                transport.StatusCode);
        }

        if (!stream.Completed || string.IsNullOrWhiteSpace(stream.OutputText))
        {
            return new ConnectionTestResult(
                false,
                T(
                    "接口返回了 SSE，但没有完整的 response.completed 文本结果。",
                    "The endpoint returned SSE but no complete response.completed text result."),
                transport.StatusCode);
        }

        return new ConnectionTestResult(
            true,
            T(
                "连接成功：认证、Responses API、SSE 完整事件与文本输出均可用。",
                "Connection succeeded: authentication, the Responses API, complete SSE events, and text output are all available."),
            transport.StatusCode);
    }

    /// <summary>
    /// Verifies the upstream Chat Completions contract used by the K3 adapter.
    /// This deliberately targets the provider endpoint rather than Windows
    /// loopback: the actual Responses adapter runs in Codex's WSL namespace.
    /// </summary>
    public async Task<ConnectionTestResult> TestChatCompletionsApiAsync(
        string baseUrl,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ProviderAvailabilityPolicy.RequireKimiRouteEnabled();
        var payload = new
        {
            model = model.Trim(),
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = "Reply with exactly OK."
                }
            },
            max_tokens = 16,
            stream = false
        };
        var transport = await PostJsonAsync(
            $"{ConfigService.NormalizeBaseUrl(baseUrl)}/chat/completions",
            payload,
            apiKey,
            TimeSpan.FromSeconds(60),
            cancellationToken).ConfigureAwait(false);
        if (!transport.Success)
        {
            return new ConnectionTestResult(
                false,
                DescribeJsonFailure(transport, "Chat Completions API"),
                transport.StatusCode);
        }

        try
        {
            using var response = JsonDocument.Parse(transport.Body!);
            var choices = response.RootElement.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                throw new JsonException();
            }

            var message = choices[0].GetProperty("message");
            if (!message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
            {
                throw new JsonException();
            }
        }
        catch (JsonException)
        {
            return new ConnectionTestResult(
                false,
                T(
                    "Chat Completions 返回成功，但没有可用的 assistant 文本。",
                    "Chat Completions succeeded but did not return usable assistant text."),
                transport.StatusCode);
        }

        return new ConnectionTestResult(
            true,
            T(
                "连接成功：随想 K3 上游 Chat Completions、认证与文本输出均可用。",
                "Connection succeeded: the SuiXiang K3 upstream Chat Completions endpoint, authentication, and text output are available."),
            transport.StatusCode);
    }

    public async Task<ConnectionTestResult> TestFunctionCallingAsync(
        string baseUrl,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
        var tool = new
        {
            type = "function",
            name = ProbeFunctionName,
            description = "A harmless compatibility probe. Return the supplied value.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    value = new
                    {
                        type = "string"
                    }
                },
                required = new[] { "value" },
                additionalProperties = false
            },
            strict = false
        };
        const string userPrompt =
            "Call capability_probe exactly once with value set to ready. Do not answer with text.";
        var firstPayload = new
        {
            model = model.Trim(),
            input = userPrompt,
            tools = new[] { tool },
            tool_choice = "auto",
            store = false,
            include = new[] { "reasoning.encrypted_content" },
            stream = true,
            max_output_tokens = 128
        };
        var first = await SendResponsesStreamAsync(
            baseUrl,
            firstPayload,
            apiKey,
            TimeSpan.FromSeconds(90),
            cancellationToken);
        if (!first.Success)
        {
            return new ConnectionTestResult(
                false,
                DescribeTransportFailure(
                    first,
                    T("工具调用请求", "Tool-call request")),
                first.StatusCode);
        }

        var firstStream = first.Stream!;
        if (firstStream.Failed ||
            !firstStream.Completed ||
            !string.Equals(
                firstStream.FunctionName,
                ProbeFunctionName,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(firstStream.FunctionCallId) ||
            string.IsNullOrWhiteSpace(firstStream.FunctionArguments) ||
            string.IsNullOrWhiteSpace(firstStream.OutputItemsJson))
        {
            return new ConnectionTestResult(
                false,
                T(
                    "第三方 Responses 可以返回文本，但没有产生完整的 function_call；插件工具不能确认可用。",
                    "The third-party Responses endpoint returned text but did not produce a complete function_call; plugin tools cannot be confirmed."),
                first.StatusCode);
        }

        try
        {
            using var arguments = JsonDocument.Parse(firstStream.FunctionArguments);
            if (!arguments.RootElement.TryGetProperty("value", out var value) ||
                !string.Equals(value.GetString(), "ready", StringComparison.Ordinal))
            {
                return new ConnectionTestResult(
                    false,
                    T(
                        "function_call 参数结构不符合 Responses 工具协议。",
                        "The function_call arguments do not match the Responses tool protocol."),
                    first.StatusCode);
            }
        }
        catch (JsonException)
        {
            return new ConnectionTestResult(
                false,
                T(
                    "function_call arguments 不是有效 JSON。",
                    "The function_call arguments are not valid JSON."),
                first.StatusCode);
        }

        var proof = $"provider-tool-proof-{Guid.NewGuid():N}";
        List<object> secondInput =
        [
            new
            {
                role = "user",
                content = userPrompt
            }
        ];
        try
        {
            using var outputItems = JsonDocument.Parse(firstStream.OutputItemsJson);
            if (outputItems.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException();
            }

            secondInput.AddRange(
                outputItems.RootElement
                    .EnumerateArray()
                    .Select(item => (object)item.Clone()));
        }
        catch (JsonException)
        {
            return new ConnectionTestResult(
                false,
                T(
                    "第一轮 response.output 无法完整回放；reasoning 工具上下文不完整。",
                    "The first response.output could not be replayed completely; the reasoning tool context is incomplete."),
                first.StatusCode);
        }

        secondInput.Add(new
        {
            type = "function_call_output",
            call_id = firstStream.FunctionCallId,
            output = proof
        });
        var secondPayload = new
        {
            model = model.Trim(),
            instructions =
                "Reply with exactly the complete capability_probe tool output. " +
                "Do not add punctuation, formatting, or any other text.",
            input = secondInput,
            tools = new[] { tool },
            tool_choice = "auto",
            store = false,
            include = new[] { "reasoning.encrypted_content" },
            stream = true,
            max_output_tokens = 32
        };
        var second = await SendResponsesStreamAsync(
            baseUrl,
            secondPayload,
            apiKey,
            TimeSpan.FromSeconds(90),
            cancellationToken);
        if (!second.Success)
        {
            return new ConnectionTestResult(
                false,
                DescribeTransportFailure(
                    second,
                    T("工具结果回传请求", "Tool-result replay request")),
                second.StatusCode);
        }

        var secondStream = second.Stream!;
        if (secondStream.Failed ||
            !secondStream.Completed ||
            !string.Equals(
                secondStream.OutputText.Trim(),
                proof,
                StringComparison.Ordinal))
        {
            return new ConnectionTestResult(
                false,
                T(
                    "模型产生了 function_call，但没有原样返回随机工具回执；" +
                    "工具结果可能在中转层丢失，插件仍不算端到端兼容。",
                    "The model produced a function_call but did not return the random tool receipt exactly; " +
                    "the relay may have dropped the tool result, so plugin compatibility is not end-to-end."),
                second.StatusCode);
        }

        return new ConnectionTestResult(
            true,
            T(
                "插件协议通过：function_call 与工具结果回传均成功。",
                "Plugin protocol passed: function_call and tool-result replay both succeeded."),
            second.StatusCode);
    }

    public async Task<ImageGenerationTestResult> TestImageGenerationAsync(
        string baseUrl,
        string imageModel,
        string apiKey,
        CancellationToken cancellationToken = default) =>
        await TestImageApiAsync(
            baseUrl,
            imageModel,
            apiKey,
            cancellationToken);

    public static string EndpointFingerprint(string baseUrl, string model) =>
        $"{ConfigService.NormalizeBaseUrl(baseUrl)}|{model.Trim()}";

    public static ResponsesStreamSummary AnalyzeSse(string content)
    {
        var accumulator = new StreamAccumulator();
        var eventData = new StringBuilder();
        foreach (var rawLine in content
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                ProcessEventData(eventData, accumulator);
                continue;
            }

            if (!rawLine.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (eventData.Length > 0)
            {
                eventData.Append('\n');
            }

            eventData.Append(rawLine[5..].TrimStart());
        }

        ProcessEventData(eventData, accumulator);
        return accumulator.ToSummary();
    }

    public static byte[]? ExtractImageApiBytes(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("b64_json", out var encoded) ||
                    encoded.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                return TryDecodeBase64(encoded.GetString());
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    public static bool IsDecodableImage(byte[] bytes)
    {
        try
        {
            ValidateDecodableImage(bytes);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private async Task<ImageGenerationTestResult> TestImageApiAsync(
        string baseUrl,
        string imageModel,
        string apiKey,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = imageModel.Trim(),
            prompt =
                "A simple diagnostic image: one green square centered on a plain white background, no text.",
            background = "auto",
            quality = "auto",
            size = "auto"
        };
        var response = await PostJsonAsync(
            $"{ConfigService.NormalizeBaseUrl(baseUrl)}/images/generations",
            payload,
            apiKey,
            TimeSpan.FromMinutes(3),
            cancellationToken);
        if (!response.Success)
        {
            return new ImageGenerationTestResult(
                false,
                DescribeJsonFailure(response, "Images API"),
                StatusCode: response.StatusCode);
        }

        var bytes = ExtractImageApiBytes(response.Body!);
        if (bytes is null)
        {
            return new ImageGenerationTestResult(
                false,
                T(
                    "Images API 请求成功，但没有返回 data[0].b64_json 图片数据。",
                    "The Images API request succeeded but returned no data[0].b64_json image data."),
                StatusCode: response.StatusCode);
        }

        var path = SaveDiagnosticImage(bytes, "images-api");
        return new ImageGenerationTestResult(
            true,
            F(
                "Codex 当前 Images API 后端已实际生成图片：{0}",
                "Codex's current Images API backend generated an image: {0}",
                path),
            ImageGenerationPath.ImageApi,
            path,
            response.StatusCode);
    }

    private async Task<StreamRequestResult> SendResponsesStreamAsync(
        string baseUrl,
        object payload,
        string apiKey,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        var effectiveToken = timeout.Token;
        var endpoint = new Uri(
            $"{ConfigService.NormalizeBaseUrl(baseUrl)}/responses",
            UriKind.Absolute);

        using var request = CreateJsonRequest(endpoint, payload, apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                effectiveToken);
            var statusCode = (int)response.StatusCode;
            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!response.IsSuccessStatusCode)
            {
                string errorBody;
                try
                {
                    errorBody = await ReadBoundedBodyAsync(
                        response.Content,
                        MaxErrorBodyBytes,
                        effectiveToken);
                }
                catch (ResponseBodyLimitException exception)
                {
                    return new StreamRequestResult(
                        false,
                        statusCode,
                        mediaType,
                        null,
                        null,
                        exception.Message);
                }

                return new StreamRequestResult(
                    false,
                    statusCode,
                    mediaType,
                    null,
                    SummarizeError(RedactSecrets(errorBody, apiKey)),
                    null);
            }

            if (!mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return new StreamRequestResult(
                    false,
                    statusCode,
                    mediaType,
                    null,
                    F(
                        "返回格式是 {0}，不是 text/event-stream。",
                        "The response format is {0}, not text/event-stream.",
                        mediaType),
                    null);
            }

            string streamBody;
            try
            {
                streamBody = await ReadBoundedBodyAsync(
                    response.Content,
                    MaxSseBodyBytes,
                    effectiveToken);
            }
            catch (ResponseBodyLimitException exception)
            {
                return new StreamRequestResult(
                    false,
                    statusCode,
                    mediaType,
                    null,
                    null,
                    exception.Message);
            }

            return new StreamRequestResult(
                true,
                statusCode,
                mediaType,
                AnalyzeSse(streamBody),
                null,
                null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new StreamRequestResult(
                false,
                null,
                string.Empty,
                null,
                null,
                F(
                    "请求超时（{0:0} 秒）。",
                    "The request timed out after {0:0} seconds.",
                    timeoutDuration.TotalSeconds));
        }
        catch (HttpRequestException exception)
        {
            return new StreamRequestResult(
                false,
                null,
                string.Empty,
                null,
                null,
                F(
                    "网络连接失败：{0}",
                    "Network connection failed: {0}",
                    exception.Message));
        }
    }

    private async Task<JsonRequestResult> PostJsonAsync(
        string endpoint,
        object payload,
        string apiKey,
        TimeSpan timeoutDuration,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(timeoutDuration);
        var effectiveToken = timeout.Token;
        using var request = CreateJsonRequest(new Uri(endpoint, UriKind.Absolute), payload, apiKey);

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                effectiveToken);
            var statusCode = (int)response.StatusCode;
            string body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    response.Content,
                    response.IsSuccessStatusCode
                        ? MaxJsonBodyBytes
                        : MaxErrorBodyBytes,
                    effectiveToken);
            }
            catch (ResponseBodyLimitException exception)
            {
                return new JsonRequestResult(
                    false,
                    statusCode,
                    null,
                    null,
                    exception.Message);
            }

            if (!response.IsSuccessStatusCode)
            {
                var limited = body.Length <= 1200 ? body : body[..1200];
                return new JsonRequestResult(
                    false,
                    statusCode,
                    null,
                    SummarizeError(RedactSecrets(limited, apiKey)),
                    null);
            }

            return new JsonRequestResult(true, statusCode, body, null, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new JsonRequestResult(
                false,
                null,
                null,
                null,
                F(
                    "请求超时（{0:0} 秒）。",
                    "The request timed out after {0:0} seconds.",
                    timeoutDuration.TotalSeconds));
        }
        catch (HttpRequestException exception)
        {
            return new JsonRequestResult(
                false,
                null,
                null,
                null,
                F(
                    "网络连接失败：{0}",
                    "Network connection failed: {0}",
                    exception.Message));
        }
    }

    private static HttpRequestMessage CreateJsonRequest(
        Uri endpoint,
        object payload,
        string apiKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                apiKey.Trim());
        }
        catch (FormatException exception)
        {
            request.Dispose();
            throw new InvalidOperationException(
                T(
                    "API Key 格式无效；请求未发送。",
                    "The API key format is invalid; the request was not sent."),
                exception);
        }
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");
        return request;
    }

    private static string DescribeTransportFailure(
        StreamRequestResult result,
        string label)
    {
        if (!string.IsNullOrWhiteSpace(result.ExceptionMessage))
        {
            return result.ExceptionMessage;
        }

        return result.StatusCode switch
        {
            401 or 403 => T(
                "认证失败：请检查或更换第三方 API Key。",
                "Authentication failed. Check or replace the third-party API key."),
            404 => F(
                "该服务没有所需接口：{0}。",
                "The service does not provide the required endpoint: {0}.",
                label),
            _ when result.StatusCode is not null =>
                F(
                    "{0} 返回 HTTP {1}。{2}",
                    "{0} returned HTTP {1}. {2}",
                    label,
                    result.StatusCode,
                    result.ErrorSummary),
            _ => F(
                "{0} 未通过。{1}",
                "{0} failed. {1}",
                label,
                result.ErrorSummary)
        };
    }

    private static string DescribeJsonFailure(JsonRequestResult result, string label)
    {
        if (!string.IsNullOrWhiteSpace(result.ExceptionMessage))
        {
            return result.ExceptionMessage;
        }

        return result.StatusCode switch
        {
            401 or 403 => T(
                "认证失败：请检查或更换第三方 API Key。",
                "Authentication failed. Check or replace the third-party API key."),
            404 => F(
                "{0} 接口不存在。",
                "The {0} endpoint does not exist.",
                label),
            _ when result.StatusCode is not null =>
                F(
                    "{0} 返回 HTTP {1}。{2}",
                    "{0} returned HTTP {1}. {2}",
                    label,
                    result.StatusCode,
                    result.ErrorSummary),
            _ => F(
                "{0} 未通过。{1}",
                "{0} failed. {1}",
                label,
                result.ErrorSummary)
        };
    }

    private static void ProcessEventData(
        StringBuilder eventData,
        StreamAccumulator accumulator)
    {
        if (eventData.Length == 0)
        {
            return;
        }

        var data = eventData.ToString().Trim();
        eventData.Clear();
        if (data.Length == 0 || data == "[DONE]")
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            accumulator.SawJsonEvent = true;
            AnalyzeJsonEvent(document.RootElement, accumulator);
        }
        catch (JsonException)
        {
            accumulator.Failed = true;
            accumulator.Error ??= T(
                "SSE data 不是有效 JSON。",
                "SSE data is not valid JSON.");
        }
    }

    private static void AnalyzeJsonEvent(JsonElement root, StreamAccumulator accumulator)
    {
        var eventType = ReadString(root, "type");
        switch (eventType)
        {
            case "response.completed":
                accumulator.Completed = true;
                break;
            case "response.failed":
            case "response.error":
            case "error":
            case "response.incomplete":
                accumulator.Failed = true;
                break;
            case "response.output_text.delta":
                if (root.TryGetProperty("delta", out var delta) &&
                    delta.ValueKind == JsonValueKind.String)
                {
                    accumulator.OutputText.Append(delta.GetString());
                }
                break;
            case "response.output_text.done":
                if (accumulator.OutputText.Length == 0 &&
                    root.TryGetProperty("text", out var text) &&
                    text.ValueKind == JsonValueKind.String)
                {
                    accumulator.OutputText.Append(text.GetString());
                }
                break;
            case "response.function_call_arguments.delta":
                var argumentsDelta = ReadString(root, "delta");
                if (!string.IsNullOrEmpty(argumentsDelta))
                {
                    accumulator.FunctionArguments =
                        (accumulator.FunctionArguments ?? string.Empty) + argumentsDelta;
                }
                break;
            case "response.function_call_arguments.done":
                var completedArguments = ReadString(root, "arguments");
                if (!string.IsNullOrWhiteSpace(completedArguments))
                {
                    accumulator.FunctionArguments = completedArguments;
                }
                break;
        }

        if (root.TryGetProperty("response", out var response) &&
            response.ValueKind == JsonValueKind.Object)
        {
            AnalyzeResponseObject(response, accumulator);
        }
        else if (ReadString(root, "object") == "response")
        {
            AnalyzeResponseObject(root, accumulator);
        }

        if (root.TryGetProperty("item", out var item) &&
            item.ValueKind == JsonValueKind.Object)
        {
            if (eventType == "response.output_item.done")
            {
                accumulator.AddCompletedOutputItem(item);
            }

            AnalyzeOutputItem(item, accumulator);
        }

        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            accumulator.Failed = true;
            accumulator.Error ??= ReadErrorDescription(error);
        }

        // Some relays emit a terminal response.failed/response.error event
        // without the nested response object. Preserve its top-level message
        // or code so the UI does not reduce a real upstream failure to a
        // misleading "no error details" message.
        if (accumulator.Failed)
        {
            accumulator.Error ??= FirstNonBlank(
                ReadString(root, "message"),
                ReadString(root, "code"),
                ReadString(root, "type"));
        }
    }

    private static void AnalyzeResponseObject(
        JsonElement response,
        StreamAccumulator accumulator)
    {
        accumulator.ResponseId ??= ReadString(response, "id");
        var status = ReadString(response, "status");
        if (status == "completed")
        {
            accumulator.Completed = true;
        }
        else if (status is "failed" or "incomplete" or "cancelled")
        {
            accumulator.Failed = true;
        }

        if (response.TryGetProperty("error", out var error) &&
            error.ValueKind is JsonValueKind.Object or JsonValueKind.String)
        {
            accumulator.Failed = true;
            accumulator.Error ??= ReadErrorDescription(error);
        }

        if (!response.TryGetProperty("output", out var output) ||
            output.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        if (output.GetArrayLength() > 0)
        {
            accumulator.OutputItemsJson = output.GetRawText();
        }

        foreach (var item in output.EnumerateArray())
        {
            AnalyzeOutputItem(item, accumulator);
        }
    }

    private static void AnalyzeOutputItem(
        JsonElement item,
        StreamAccumulator accumulator)
    {
        var itemType = ReadString(item, "type");
        if (itemType == "function_call")
        {
            accumulator.FunctionCallId ??= ReadString(item, "call_id");
            accumulator.FunctionName ??= ReadString(item, "name");
            var arguments = ReadString(item, "arguments");
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                accumulator.FunctionArguments = arguments;
            }
            return;
        }

        if (itemType != "message" ||
            accumulator.OutputText.Length > 0 ||
            !item.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var part in content.EnumerateArray())
        {
            if (ReadString(part, "type") == "output_text")
            {
                var text = ReadString(part, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    accumulator.OutputText.Append(text);
                }
            }
        }
    }

    private static string? ReadString(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string? ReadErrorDescription(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return FirstNonBlank(error.GetString());
        }

        if (error.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return FirstNonBlank(
            ReadString(error, "message"),
            ReadString(error, "code"),
            ReadString(error, "type"));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static byte[]? TryDecodeBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static string SaveDiagnosticImage(byte[] bytes, string prefix)
    {
        if (bytes.Length < 1024)
        {
            throw new InvalidDataException(T(
                "图片数据过小，无法确认是有效生成结果。",
                "The image data is too small to confirm a valid generated result."));
        }

        var extension = DetectImageExtension(bytes) ??
            throw new InvalidDataException(T(
                "返回的数据不是可识别的 PNG、JPEG 或 WebP 图片。",
                "The returned data is not a recognized PNG, JPEG, or WebP image."));
        ValidateDecodableImage(bytes);
        Directory.CreateDirectory(AppPaths.DiagnosticsRoot);
        var path = Path.Combine(
            AppPaths.DiagnosticsRoot,
            $"{prefix}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.{extension}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string? DetectImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 8 &&
            bytes[0] == 0x89 &&
            bytes[1] == 0x50 &&
            bytes[2] == 0x4E &&
            bytes[3] == 0x47)
        {
            return "png";
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xFF &&
            bytes[1] == 0xD8 &&
            bytes[2] == 0xFF)
        {
            return "jpg";
        }

        if (bytes.Length >= 12 &&
            Encoding.ASCII.GetString(bytes, 0, 4) == "RIFF" &&
            Encoding.ASCII.GetString(bytes, 8, 4) == "WEBP")
        {
            return "webp";
        }

        return null;
    }

    private static void ValidateDecodableImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                throw new InvalidDataException(T(
                    "图片没有可解码帧。",
                    "The image has no decodable frames."));
            }

            var frame = decoder.Frames[0];
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width is < 1 or > 8192 ||
                height is < 1 or > 8192 ||
                (long)width * height > 64L * 1024 * 1024)
            {
                throw new InvalidDataException(T(
                    "图片尺寸超出诊断工具的安全范围。",
                    "The image dimensions exceed the diagnostic tool's safety limits."));
            }

            var bitsPerPixel = Math.Max(frame.Format.BitsPerPixel, 1);
            var stride = checked((width * bitsPerPixel + 7) / 8);
            var row = new byte[stride];
            for (var y = 0; y < height; y++)
            {
                frame.CopyPixels(
                    new Int32Rect(0, y, width, 1),
                    row,
                    stride,
                    0);
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is FileFormatException or
            NotSupportedException or
            ArgumentException or
            OverflowException or
            IOException or
            System.Runtime.InteropServices.COMException)
        {
            throw new InvalidDataException(
                T(
                    "返回的数据无法完整解码为图片。",
                    "The returned data could not be fully decoded as an image."),
                exception);
        }
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new ResponseBodyLimitException(maximumBytes);
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var contentLength = content.Headers.ContentLength;
        var initialCapacity =
            contentLength.HasValue &&
            contentLength.Value > 0 &&
            contentLength.Value <= maximumBytes
                ? (int)contentLength.Value
                : 0;
        using var buffer = new MemoryStream(initialCapacity);
        var chunk = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return Encoding.UTF8.GetString(
                    buffer.GetBuffer(),
                    0,
                    checked((int)buffer.Length));
            }

            if (buffer.Length + count > maximumBytes)
            {
                throw new ResponseBodyLimitException(maximumBytes);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
    }

    private static string SummarizeError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var singleLine = string.Join(
            " ",
            body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return F(
            "服务信息：{0}",
            "Service details: {0}",
            singleLine);
    }

    private static string RedactSecrets(string value, string activeKey)
    {
        var redacted = string.IsNullOrEmpty(activeKey)
            ? value
            : value.Replace(activeKey, "<redacted>", StringComparison.Ordinal);
        return ApiKeyPattern().Replace(redacted, "<redacted>");
    }

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);

    private sealed record StreamRequestResult(
        bool Success,
        int? StatusCode,
        string MediaType,
        ResponsesStreamSummary? Stream,
        string? ErrorSummary,
        string? ExceptionMessage);

    private sealed record JsonRequestResult(
        bool Success,
        int? StatusCode,
        string? Body,
        string? ErrorSummary,
        string? ExceptionMessage);

    private sealed class ResponseBodyLimitException(int maximumBytes)
        : IOException(
            F(
                "服务响应超过安全上限（{0:0.#} MB）。",
                "The service response exceeded the safety limit ({0:0.#} MB).",
                maximumBytes / (1024 * 1024.0)));

    private sealed class StreamAccumulator
    {
        public bool SawJsonEvent { get; set; }

        public bool Completed { get; set; }

        public bool Failed { get; set; }

        public StringBuilder OutputText { get; } = new();

        public string? ResponseId { get; set; }

        public string? FunctionCallId { get; set; }

        public string? FunctionName { get; set; }

        public string? FunctionArguments { get; set; }

        public string? OutputItemsJson { get; set; }

        public string? Error { get; set; }

        private List<string> CompletedOutputItems { get; } = [];

        public void AddCompletedOutputItem(JsonElement item)
        {
            CompletedOutputItems.Add(item.GetRawText());
        }

        public ResponsesStreamSummary ToSummary() =>
            new(
                SawJsonEvent,
                Completed,
                Failed,
                OutputText.ToString(),
                ResponseId,
                FunctionCallId,
                FunctionName,
                FunctionArguments,
                OutputItemsJson ??
                (CompletedOutputItems.Count == 0
                    ? null
                    : $"[{string.Join(",", CompletedOutputItems)}]"),
                Error);
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();
}
