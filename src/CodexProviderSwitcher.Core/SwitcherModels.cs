using System.Text.Json.Serialization;

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
    bool UsesOfficialAuthentication,
    string? CredentialTarget = null,
    string? ModelCatalogJson = null);

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

public static class ProviderKinds
{
    public const string SuiXiang = "sui-xiang";
    public const string Kimi = "kimi";
    public const string Custom = "custom";
}

public sealed class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Kind { get; set; } = ProviderKinds.Custom;

    public string DisplayName { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = AppPaths.DefaultBaseUrl;

    public string Model { get; set; } = AppPaths.DefaultThirdPartyModel;

    // Only the Credential Manager target is persisted. The secret never is.
    public string CredentialTarget { get; set; } =
        AppPaths.LegacySuiXiangCredentialTarget;
}

public sealed class SwitcherSettings
{
    public const int CurrentSchemaVersion = 2;

    public int SettingsSchemaVersion { get; set; } = CurrentSchemaVersion;

    public bool OnboardingCompleted { get; set; }

    public int OnboardingVersion { get; set; } = 1;

    public string UiLanguage { get; set; } = Localizer.ChineseCode;

    public string UiTheme { get; set; } = ThemePreference.LightCode;

    public string OfficialModel { get; set; } = AppPaths.DefaultOfficialModel;

    public string? OfficialReviewModel { get; set; } = "gpt-5.5";

    public List<ProviderProfile> ProviderProfiles { get; set; } = [];

    public string? ActiveProviderProfileId { get; set; }

    private string _thirdPartyBaseUrl = AppPaths.DefaultBaseUrl;

    private string _thirdPartyModel = AppPaths.DefaultThirdPartyModel;

    // Flat properties remain serialized for an easy, lossless v1.3 migration.
    // The active profile is now the canonical source once it exists.
    public string ThirdPartyBaseUrl
    {
        get => ActiveProviderProfile?.BaseUrl ?? _thirdPartyBaseUrl;
        set
        {
            _thirdPartyBaseUrl = value;
            if (ActiveProviderProfile is { } profile)
            {
                profile.BaseUrl = value;
            }
        }
    }

    public string ThirdPartyModel
    {
        get => ActiveProviderProfile?.Model ?? _thirdPartyModel;
        set
        {
            _thirdPartyModel = value;
            if (ActiveProviderProfile is { } profile)
            {
                profile.Model = value;
            }
        }
    }

    [JsonIgnore]
    public ProviderProfile? ActiveProviderProfile =>
        ProviderProfiles.FirstOrDefault(profile =>
            string.Equals(
                profile.Id,
                ActiveProviderProfileId,
                StringComparison.Ordinal));

    public ProviderProfile EnsureActiveProviderProfile()
    {
        if (ActiveProviderProfile is { } active)
        {
            return active;
        }

        var profileId = Guid.NewGuid().ToString("N");
        var profile = new ProviderProfile
        {
            Id = profileId,
            Kind = ProviderKinds.Custom,
            DisplayName = string.Empty,
            BaseUrl = _thirdPartyBaseUrl,
            Model = _thirdPartyModel,
            CredentialTarget = CredentialTargetFactory.CreateForProfileId(profileId)
        };
        ProviderProfiles.Add(profile);
        ActiveProviderProfileId = profile.Id;
        return profile;
    }

    public void SyncLegacyThirdPartyFields()
    {
        if (ActiveProviderProfile is not { } profile)
        {
            return;
        }

        _thirdPartyBaseUrl = profile.BaseUrl;
        _thirdPartyModel = profile.Model;
    }

    public bool RestartAfterSwitch { get; set; } = true;

    public DateTimeOffset? LastSuccessfulCompatibilityTestUtc { get; set; }

    public string? LastTestedEndpointFingerprint { get; set; }

    public DateTimeOffset? LastSuccessfulToolTestUtc { get; set; }

    public string? LastToolTestedEndpointFingerprint { get; set; }

    public DateTimeOffset? LastSuccessfulImageTestUtc { get; set; }

    public string? LastImageTestedEndpointFingerprint { get; set; }

    public string? LastGeneratedImagePath { get; set; }
}
