using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed record ReleaseUpdateInfo(
    Version CurrentVersion,
    Version LatestVersion,
    string LatestTag,
    Uri ReleaseUri)
{
    public bool IsUpdateAvailable => LatestVersion.CompareTo(CurrentVersion) > 0;
}

public sealed partial class GitHubReleaseUpdateService
{
    public const string Repository = "tuolaji996/codex-provider-switcher";
    public const string RepositoryUrl =
        "https://github.com/tuolaji996/codex-provider-switcher";
    public const string LatestReleaseApiUrl =
        "https://api.github.com/repos/tuolaji996/codex-provider-switcher/releases/latest";

    private readonly HttpClient _client;

    public GitHubReleaseUpdateService(HttpClient? client = null)
    {
        _client = client ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
    }

    public async Task<ReleaseUpdateInfo> CheckAsync(
        Version currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);

        var normalizedCurrentVersion = NormalizeVersion(currentVersion);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            LatestReleaseApiUrl);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue(
                "CodexProviderSwitcher",
                normalizedCurrentVersion.ToString(3)));
        request.Headers.TryAddWithoutValidation(
            "X-GitHub-Api-Version",
            "2026-03-10");

        using var response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(
            cancellationToken);
        using var document = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("tag_name", out var tagElement) ||
            tagElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(tagElement.GetString()))
        {
            throw new InvalidDataException(
                "The latest GitHub release did not contain a version tag.");
        }

        var latestTag = tagElement.GetString()!.Trim();
        var latestVersion = ParseReleaseVersion(latestTag);
        var releaseUri = new Uri(
            $"{RepositoryUrl}/releases/tag/{Uri.EscapeDataString(latestTag)}");
        return new ReleaseUpdateInfo(
            normalizedCurrentVersion,
            latestVersion,
            latestTag,
            releaseUri);
    }

    public static Version ParseReleaseVersion(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);

        var match = ReleaseTagPattern().Match(tag.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            throw new InvalidDataException(
                $"The GitHub release tag is not a supported version: {tag}");
        }

        return new Version(major, minor, patch);
    }

    public static Version NormalizeVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new Version(
            version.Major,
            Math.Max(0, version.Minor),
            Math.Max(0, version.Build));
    }

    [GeneratedRegex(
        @"^v?(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTagPattern();
}
