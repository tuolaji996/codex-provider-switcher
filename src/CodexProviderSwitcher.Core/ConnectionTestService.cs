using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed partial class ConnectionTestService
{
    private readonly HttpClient _client;

    public ConnectionTestService()
    {
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };
    }

    public async Task<ConnectionTestResult> TestResponsesApiAsync(
        string baseUrl,
        string model,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var effectiveToken = timeout.Token;
        var normalized = ConfigService.NormalizeBaseUrl(baseUrl);
        var endpoint = new Uri($"{normalized}/responses", UriKind.Absolute);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        var payload = new
        {
            model = model.Trim(),
            input = "Reply with exactly OK.",
            stream = true,
            max_output_tokens = 16
        };
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

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
                var body = await ReadLimitedBodyAsync(response, effectiveToken);
                var detail = SummarizeError(RedactSecrets(body, apiKey));
                return statusCode switch
                {
                    401 or 403 => new ConnectionTestResult(
                        false,
                        "认证失败：请撤销已暴露的旧密钥，并粘贴新密钥。",
                        statusCode),
                    404 => new ConnectionTestResult(
                        false,
                        "该服务没有 /responses 接口。只支持 Chat Completions 的线路不能直接用于 Codex。",
                        statusCode),
                    _ => new ConnectionTestResult(
                        false,
                        $"Responses API 返回 HTTP {statusCode}。{detail}",
                        statusCode)
                };
            }

            if (!mediaType.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            {
                return new ConnectionTestResult(
                    false,
                    $"接口有响应，但流式格式是 {mediaType}；Codex 需要 Responses SSE 流。",
                    statusCode);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(effectiveToken);
            using var reader = new StreamReader(stream);
            var firstEvent = await reader.ReadLineAsync(effectiveToken);
            if (string.IsNullOrWhiteSpace(firstEvent))
            {
                return new ConnectionTestResult(
                    false,
                    "接口返回了空的 SSE 流，尚不能确认 Codex 兼容性。",
                    statusCode);
            }

            return new ConnectionTestResult(
                true,
                "连接成功：认证、Responses API 与 SSE 流均可用。",
                statusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionTestResult(false, "连接超时（45 秒）。");
        }
        catch (HttpRequestException exception)
        {
            return new ConnectionTestResult(
                false,
                $"网络连接失败：{exception.Message}");
        }
    }

    public static string EndpointFingerprint(string baseUrl, string model) =>
        $"{ConfigService.NormalizeBaseUrl(baseUrl)}|{model.Trim()}";

    private static async Task<string> ReadLimitedBodyAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        return content.Length <= 600 ? content : content[..600];
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
        return $" 服务信息：{singleLine}";
    }

    private static string RedactSecrets(string value, string activeKey)
    {
        var redacted = string.IsNullOrEmpty(activeKey)
            ? value
            : value.Replace(activeKey, "<redacted>", StringComparison.Ordinal);
        return ApiKeyPattern().Replace(redacted, "<redacted>");
    }

    [GeneratedRegex(@"sk-[A-Za-z0-9_-]+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();
}
