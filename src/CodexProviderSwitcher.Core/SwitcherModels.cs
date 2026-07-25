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

public enum ImageGenerationPath
{
    None,
    ImageApi
}

public sealed record ImageGenerationTestResult(
    bool Success,
    string Summary,
    ImageGenerationPath Path = ImageGenerationPath.None,
    string? ArtifactPath = null,
    int? StatusCode = null);

public sealed record ResponsesStreamSummary(
    bool SawJsonEvent,
    bool Completed,
    bool Failed,
    string OutputText,
    string? ResponseId,
    string? FunctionCallId,
    string? FunctionName,
    string? FunctionArguments,
    string? OutputItemsJson,
    string? Error);

public sealed class SwitcherSettings
{
    public string UiLanguage { get; set; } = Localizer.ChineseCode;

    public string UiTheme { get; set; } = ThemePreference.LightCode;

    public string OfficialModel { get; set; } = AppPaths.DefaultOfficialModel;

    public string? OfficialReviewModel { get; set; } = "gpt-5.5";

    public string ThirdPartyBaseUrl { get; set; } = AppPaths.DefaultBaseUrl;

    public string ThirdPartyModel { get; set; } = AppPaths.DefaultThirdPartyModel;

    public bool RestartAfterSwitch { get; set; } = true;

    public DateTimeOffset? LastSuccessfulCompatibilityTestUtc { get; set; }

    public string? LastTestedEndpointFingerprint { get; set; }

    public DateTimeOffset? LastSuccessfulToolTestUtc { get; set; }

    public string? LastToolTestedEndpointFingerprint { get; set; }

    public DateTimeOffset? LastSuccessfulImageTestUtc { get; set; }

    public string? LastImageTestedEndpointFingerprint { get; set; }

    public string? LastGeneratedImagePath { get; set; }
}
