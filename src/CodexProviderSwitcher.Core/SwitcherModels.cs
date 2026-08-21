using System.Text.Json.Serialization;

namespace CodexProviderSwitcher.Core;

public enum ProviderMode
{
    Unknown,
    Official,
    ThirdParty
}

public enum SolContextWindowMode
{
    Default,
    Recommended,
    Custom
}

public sealed record SolContextWindowStatus(
    SolContextWindowMode Mode,
    long? ContextWindow,
    long? AutoCompactTokenLimit,
    bool Managed)
{
    public bool IsRecommended => Mode == SolContextWindowMode.Recommended;
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

/// <summary>
/// Central availability policy for routes retained only for recovery. Retired
/// profiles and credentials stay intact so users can switch away safely.
/// </summary>
public static class ProviderAvailabilityPolicy
{
    public const bool KimiRouteEnabled = false;

    public static bool IsKimiModel(string? model) =>
        string.Equals(
            model?.Trim(),
            AppPaths.DefaultKimiModel,
            StringComparison.OrdinalIgnoreCase);

    public static bool IsRetiredKimiRoute(string? baseUrl, string? model)
    {
        if (KimiRouteEnabled || !IsKimiModel(model))
        {
            return false;
        }

        try
        {
            return SettingsStore.IsKimiBaseUrl(baseUrl);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool IsRetiredKimiProfile(ProviderProfile? profile) =>
        profile is not null &&
        (string.Equals(profile.Kind, ProviderKinds.Kimi, StringComparison.Ordinal) ||
         IsRetiredKimiRoute(profile.BaseUrl, profile.Model));

    public static void RequireAvailableThirdPartyRoute(string? baseUrl, string? model)
    {
        if (IsRetiredKimiRoute(baseUrl, model))
        {
            throw new InvalidOperationException(Localizer.Text(
                "K3 线路已停用，不再支持新建、测试或切换。请选择官方 Codex 或随想当前支持的 OpenAI 模型。",
                "The K3 route has been retired and can no longer be created, tested, or selected. Choose Official Codex or a currently supported SuiXiang OpenAI model."));
        }
    }

    public static void RequireKimiRouteEnabled()
    {
        if (!KimiRouteEnabled)
        {
            throw new InvalidOperationException(Localizer.Text(
                "K3 线路已停用。旧配置和密钥会保留，请切换到官方 Codex 或其他可用线路。",
                "The K3 route has been retired. Existing configuration and credentials are preserved; switch to Official Codex or another available route."));
        }
    }
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

/// <summary>
/// Finds saved accounts by their complete provider route identity.  A Base
/// URL alone is not an account identity because one upstream can expose
/// multiple models and protocol adapters.
/// </summary>
public static class ProviderProfileRouteMatcher
{
    public static IReadOnlyList<ProviderProfile> FindExact(
        IEnumerable<ProviderProfile> profiles,
        string normalizedBaseUrl,
        string model,
        string? requiredKind = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        string expectedBaseUrl;
        try
        {
            expectedBaseUrl = ConfigService.NormalizeBaseUrl(normalizedBaseUrl);
        }
        catch (ArgumentException)
        {
            return Array.Empty<ProviderProfile>();
        }

        var expectedModel = model.Trim();
        if (expectedModel.Length == 0)
        {
            return Array.Empty<ProviderProfile>();
        }

        return profiles
            .Where(profile =>
            {
                if (profile is null)
                {
                    return false;
                }

                string candidateBaseUrl;
                try
                {
                    candidateBaseUrl = ConfigService.NormalizeBaseUrl(
                        profile.BaseUrl ?? string.Empty);
                }
                catch (ArgumentException)
                {
                    return false;
                }

                return string.Equals(
                           candidateBaseUrl,
                           expectedBaseUrl,
                           StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(
                           (profile.Model ?? string.Empty).Trim(),
                           expectedModel,
                           StringComparison.Ordinal) &&
                       (requiredKind is null ||
                        string.Equals(
                            profile.Kind,
                            requiredKind,
                            StringComparison.Ordinal));
            })
            .ToArray();
    }
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
