using System.Net;

namespace CodexProviderKimiRouter;

/// <summary>
/// Runtime settings for the loopback Kimi/K3 protocol router.
///
/// The router never reads an API key from its process environment. Codex sends
/// the SuiXiang credential in the loopback Authorization header and the router
/// forwards that bearer token only to the pinned upstream.
/// </summary>
public sealed class KimiRouterOptions
{
    public const string DefaultUpstreamBaseUrl = "https://sui-xiang.com/v1";
    public const string DefaultModelName = "k3";
    public const int DefaultListenPort = 17866;

    public KimiRouterOptions(
        Uri upstreamBaseUri,
        string defaultModel = DefaultModelName,
        int listenPort = DefaultListenPort)
    {
        UpstreamBaseUri = NormalizeBaseUri(upstreamBaseUri);
        DefaultModel = string.IsNullOrWhiteSpace(defaultModel)
            ? DefaultModelName
            : defaultModel.Trim();
        ListenPort = listenPort is >= 1 and <= 65535
            ? listenPort
            : DefaultListenPort;
    }

    public Uri UpstreamBaseUri { get; }

    public string DefaultModel { get; }

    public int ListenPort { get; }

    public Uri ChatCompletionsUri => BuildChatCompletionsUri(UpstreamBaseUri);

    public Uri ListenUri => new($"http://127.0.0.1:{ListenPort}/");

    public static KimiRouterOptions LoadFromEnvironment()
    {
        // Production routing is deliberately pinned.  The router receives a
        // Codex bearer credential and must never be redirectable through an
        // inherited environment override.  Test callers can still use the
        // public constructor with a fake upstream handler.
        var baseUrl = DefaultUpstreamBaseUrl;
        var model = DefaultModelName;
        var port = ParsePort(Environment.GetEnvironmentVariable("KIMI_ROUTER_PORT"));

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var upstreamUri) ||
            upstreamUri is null ||
            upstreamUri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(
                upstreamUri.ToString().TrimEnd('/'),
                DefaultUpstreamBaseUrl,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The Kimi router upstream is pinned to https://sui-xiang.com/v1.");
        }

        return new KimiRouterOptions(upstreamUri, model, port);
    }

    public static Uri BuildChatCompletionsUri(Uri upstreamBaseUri)
    {
        ArgumentNullException.ThrowIfNull(upstreamBaseUri);
        var text = upstreamBaseUri.ToString().TrimEnd('/');
        if (text.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(text, UriKind.Absolute);
        }

        return new Uri(text + "/chat/completions", UriKind.Absolute);
    }

    private static Uri NormalizeBaseUri(Uri uri)
    {
        if (uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The upstream URI must use HTTP or HTTPS.", nameof(uri));
        }

        return new Uri(uri.ToString().TrimEnd('/'), UriKind.Absolute);
    }

    private static int ParsePort(string? value)
    {
        return int.TryParse(value, out var port) && port is >= 1 and <= 65535
            ? port
            : DefaultListenPort;
    }

}
