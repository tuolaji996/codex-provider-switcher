namespace CodexProviderSwitcher.Core;

public enum ProviderMode
{
    Unknown,
    Official,
    ThirdParty
}

public sealed record ConfigStatus(
    ProviderMode Mode,
    string ProviderId,
    string? Model,
    string? ReviewModel,
    string? BaseUrl,
    bool UsesOfficialAuthentication);

public sealed record SessionHealth(
    int TotalFiles,
    int StableProviderFiles,
    int OtherProviderFiles,
    int UnreadableFiles,
    int EmptyPlaceholderFiles);

public sealed record ConnectionTestResult(
    bool Success,
    string Summary,
    int? StatusCode = null);

public sealed class SwitcherSettings
{
    public string OfficialModel { get; set; } = AppPaths.DefaultOfficialModel;

    public string? OfficialReviewModel { get; set; } = "gpt-5.5";

    public string ThirdPartyBaseUrl { get; set; } = AppPaths.DefaultBaseUrl;

    public string ThirdPartyModel { get; set; } = AppPaths.DefaultThirdPartyModel;

    public bool RestartAfterSwitch { get; set; } = true;

    public DateTimeOffset? LastSuccessfulCompatibilityTestUtc { get; set; }

    public string? LastTestedEndpointFingerprint { get; set; }
}
