using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexProviderSwitcher.Core;

public sealed record ModelDiscoveryResult(
    bool Success,
    IReadOnlyList<string> Models,
    string Summary,
    int? StatusCode = null);

public sealed class ModelDiscoveryService
{
    // Keep the endpoint bounded even when a proxy omits Content-Length.
    public const int MaxResponseBodyBytes = 1 * 1024 * 1024;

    public const int MaxModelCount = 256;

    public const int MaxModelIdLength = 256;

    private static readonly Encoding Utf8Strict =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _client;

    public ModelDiscoveryService()
        : this(new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        })
    {
    }

    public ModelDiscoveryService(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public ModelDiscoveryService(HttpMessageHandler handler)
        : this(new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler))))
    {
    }

    public async Task<ModelDiscoveryResult> DiscoverAsync(
        string baseUrl,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Failure(
                "Base URL 无效，无法读取模型列表。",
                "The base URL is invalid; the model list could not be read.");
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Failure(
                "未提供 API Key，无法读取模型列表。",
                "No API key was provided; the model list could not be read.");
        }

        Uri endpoint;
        try
        {
            endpoint = BuildModelsEndpoint(baseUrl);
        }
        catch (ArgumentException)
        {
            return Failure(
                "Base URL 无效，无法读取模型列表。",
                "The base URL is invalid; the model list could not be read.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        }
        catch (FormatException)
        {
            // Do not return the exception text: it can contain a malformed key.
            return Failure(
                "API Key 格式无效，无法读取模型列表。",
                "The API key format is invalid; the model list could not be read.");
        }

        try
        {
            using var response = await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var statusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return DescribeHttpFailure(statusCode);
            }

            string body;
            try
            {
                body = await ReadBoundedBodyAsync(
                    response.Content,
                    cancellationToken);
            }
            catch (ResponseBodyLimitException)
            {
                return Failure(
                    "模型接口响应超过安全上限。",
                    "The models response exceeded the safety limit.",
                    statusCode);
            }
            catch (DecoderFallbackException)
            {
                return Failure(
                    "模型接口返回了无效文本。",
                    "The models endpoint returned invalid text.",
                    statusCode);
            }

            if (string.IsNullOrWhiteSpace(body))
            {
                return Failure(
                    "模型接口返回了空响应。",
                    "The models endpoint returned an empty response.",
                    statusCode);
            }

            return ParseBody(body, statusCode, baseUrl);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure(
                "读取模型列表请求超时。",
                "The model-list request timed out.");
        }
        catch (HttpRequestException)
        {
            return Failure(
                "网络连接失败，无法读取模型列表。",
                "The network request failed; the model list could not be read.");
        }
    }

    private static Uri BuildModelsEndpoint(string baseUrl)
    {
        var normalized = ConfigService.NormalizeBaseUrl(baseUrl);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var baseUri) ||
            !string.IsNullOrEmpty(baseUri.Query) ||
            !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new ArgumentException(
                "The base URL must be an absolute URL without a query or fragment.",
                nameof(baseUrl));
        }

        var endpoint = $"{normalized}/models";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri))
        {
            throw new ArgumentException(
                "The models endpoint could not be constructed.",
                nameof(baseUrl));
        }

        return endpointUri;
    }

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBodyBytes)
        {
            throw new ResponseBodyLimitException();
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var count = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return Utf8Strict.GetString(
                    buffer.GetBuffer(),
                    0,
                    checked((int)buffer.Length));
            }

            if (buffer.Length + count > MaxResponseBodyBytes)
            {
                throw new ResponseBodyLimitException();
            }

            await buffer.WriteAsync(chunk.AsMemory(0, count), cancellationToken);
        }
    }

    private static ModelDiscoveryResult ParseBody(
        string body,
        int statusCode,
        string baseUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(
                body,
                new JsonDocumentOptions
                {
                    MaxDepth = 32
                });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Array)
            {
                return Failure(
                    "模型接口返回的数据格式不受支持。",
                    "The models endpoint returned an unsupported data format.",
                    statusCode);
            }

            if (data.GetArrayLength() > MaxModelCount)
            {
                return Failure(
                    "模型接口返回的模型数量超过安全上限。",
                    "The models response exceeded the model-count safety limit.",
                    statusCode);
            }

            var models = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("id", out var id) ||
                    id.ValueKind != JsonValueKind.String)
                {
                    return Failure(
                        "模型接口返回的数据格式不受支持。",
                        "The models endpoint returned an unsupported data format.",
                        statusCode);
                }

                var modelId = id.GetString()?.Trim() ?? string.Empty;
                if (modelId.Length == 0)
                {
                    continue;
                }

                // Retired routes must not reappear through a provider's live
                // catalog even if a UI caller forgets to filter suggestions.
                if (ProviderAvailabilityPolicy.IsRetiredKimiRoute(baseUrl, modelId))
                {
                    continue;
                }

                if (modelId.Length > MaxModelIdLength ||
                    modelId.Any(char.IsControl))
                {
                    return Failure(
                        "模型接口返回的模型 ID 超过安全限制。",
                        "A model ID exceeded the safety limit.",
                        statusCode);
                }

                models.Add(modelId);
            }

            if (models.Count == 0)
            {
                return Failure(
                    "模型接口没有返回可用模型。",
                    "The models endpoint returned no usable models.",
                    statusCode);
            }

            var orderedModels = models
                .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(model => model, StringComparer.Ordinal)
                .ToArray();
            return new ModelDiscoveryResult(
                true,
                orderedModels,
                Localizer.Format(
                    "已读取 {0} 个模型 ID；兼容性仍需单独验证。",
                    "Loaded {0} model IDs; compatibility is verified separately.",
                    orderedModels.Length),
                statusCode);
        }
        catch (JsonException)
        {
            return Failure(
                "模型接口返回了无效 JSON。",
                "The models endpoint returned invalid JSON.",
                statusCode);
        }
    }

    private static ModelDiscoveryResult DescribeHttpFailure(int statusCode) =>
        statusCode switch
        {
            401 or 403 => Failure(
                "认证失败，无法读取模型列表。",
                "Authentication failed; the model list could not be read.",
                statusCode),
            404 => Failure(
                "模型接口不存在。",
                "The models endpoint does not exist.",
                statusCode),
            _ => Failure(
                $"模型接口返回 HTTP {statusCode}。",
                $"The models endpoint returned HTTP {statusCode}.",
                statusCode)
        };

    private static ModelDiscoveryResult Failure(
        string chinese,
        string english,
        int? statusCode = null) =>
        new(
            false,
            Array.Empty<string>(),
            Localizer.Text(chinese, english),
            statusCode);

    private sealed class ResponseBodyLimitException : IOException;
}
