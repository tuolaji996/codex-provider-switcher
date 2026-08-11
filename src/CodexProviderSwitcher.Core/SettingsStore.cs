using System.Text.Json;

namespace CodexProviderSwitcher.Core;

public sealed record SettingsLoadResult(
    SwitcherSettings Settings,
    bool IsNewInstall,
    bool WasMigrated,
    string? RecoveryNotice = null);

public sealed class SettingsStore
{
    private readonly string _settingsPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsPath;
    }

    public SwitcherSettings Load(ConfigStatus currentStatus) =>
        LoadWithStatus(currentStatus).Settings;

    public SettingsLoadResult LoadWithStatus(ConfigStatus currentStatus)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_settingsPath) ??
            throw new InvalidOperationException("The settings path has no parent directory."));

        var isNewInstall = !File.Exists(_settingsPath);
        var wasMigrated = false;
        var changed = false;
        string? recoveryNotice = null;
        SwitcherSettings settings;

        if (isNewInstall)
        {
            settings = new SwitcherSettings();
            changed = true;
        }
        else
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                using var document = JsonDocument.Parse(json);
                settings = JsonSerializer.Deserialize<SwitcherSettings>(json, JsonOptions)
                    ?? throw new JsonException("The settings document was empty.");
                wasMigrated = !HasProperty(document.RootElement, "SettingsSchemaVersion");
            }
            catch (JsonException)
            {
                var quarantinePath = QuarantineCorruptSettingsFile();
                settings = new SwitcherSettings();
                recoveryNotice = Localizer.Format(
                    "设置文件无法读取，已保留为：{0}",
                    "The unreadable settings file was retained as: {0}",
                    Path.GetFileName(quarantinePath));
                changed = true;
            }
        }

        if (NormalizeSettings(settings))
        {
            changed = true;
        }

        if (wasMigrated || settings.SettingsSchemaVersion < SwitcherSettings.CurrentSchemaVersion)
        {
            MigrateLegacySettings(settings);
            wasMigrated = true;
            changed = true;
        }
        else if (EnsureCurrentProfile(settings, useLegacyCredentialTarget: false))
        {
            changed = true;
        }

        if (SyncCurrentStatus(settings, currentStatus))
        {
            changed = true;
        }

        settings.SyncLegacyThirdPartyFields();
        if (changed)
        {
            Save(settings);
        }

        return new SettingsLoadResult(
            settings,
            isNewInstall,
            wasMigrated,
            recoveryNotice);
    }

    public void Save(SwitcherSettings settings)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_settingsPath) ??
            throw new InvalidOperationException("The settings path has no parent directory."));
        settings.SettingsSchemaVersion = SwitcherSettings.CurrentSchemaVersion;
        settings.SyncLegacyThirdPartyFields();
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_settingsPath, json);
    }

    private static bool NormalizeSettings(SwitcherSettings settings)
    {
        var changed = false;
        var normalizedLanguage = Localizer.NormalizeCode(settings.UiLanguage);
        if (!string.Equals(settings.UiLanguage, normalizedLanguage, StringComparison.Ordinal))
        {
            settings.UiLanguage = normalizedLanguage;
            changed = true;
        }

        var normalizedTheme = ThemePreference.NormalizeCode(settings.UiTheme);
        if (!string.Equals(settings.UiTheme, normalizedTheme, StringComparison.Ordinal))
        {
            settings.UiTheme = normalizedTheme;
            changed = true;
        }

        return changed;
    }

    private static void MigrateLegacySettings(SwitcherSettings settings)
    {
        EnsureCurrentProfile(settings, useLegacyCredentialTarget: true);
        settings.SettingsSchemaVersion = SwitcherSettings.CurrentSchemaVersion;
        // Existing v1.3 users already had a working screen, so never interrupt
        // them with the first-run wizard after an upgrade.
        settings.OnboardingCompleted = true;
        settings.OnboardingVersion = Math.Max(settings.OnboardingVersion, 1);
    }

    private static bool EnsureCurrentProfile(
        SwitcherSettings settings,
        bool useLegacyCredentialTarget)
    {
        if (settings.ActiveProviderProfile is { } active)
        {
            var changed = false;
            if (!Guid.TryParse(active.Id, out _))
            {
                active.Id = Guid.NewGuid().ToString("N");
                settings.ActiveProviderProfileId = active.Id;
                changed = true;
            }

            if (!CredentialTargetFactory.IsValid(active.CredentialTarget))
            {
                active.CredentialTarget = useLegacyCredentialTarget
                    ? AppPaths.LegacySuiXiangCredentialTarget
                    : CredentialTargetFactory.CreateForProfileId(active.Id);
                changed = true;
            }

            return changed;
        }

        var profileId = Guid.NewGuid().ToString("N");
        var baseUrl = string.IsNullOrWhiteSpace(settings.ThirdPartyBaseUrl)
            ? AppPaths.DefaultBaseUrl
            : settings.ThirdPartyBaseUrl;
        var model = string.IsNullOrWhiteSpace(settings.ThirdPartyModel)
            ? AppPaths.DefaultThirdPartyModel
            : settings.ThirdPartyModel;
        var profile = new ProviderProfile
        {
            Id = profileId,
            // A SuiXiang endpoint is a normal direct Responses route unless
            // the user selected the experimental K3 bridge explicitly.  Do
            // not infer the bridge merely from the host name, because the
            // same endpoint may expose other models directly.
            Kind = IsSuiXiangBaseUrl(baseUrl)
                ? ProviderKinds.SuiXiang
                : ProviderKinds.Custom,
            BaseUrl = baseUrl,
            Model = model,
            CredentialTarget = useLegacyCredentialTarget
                ? AppPaths.LegacySuiXiangCredentialTarget
                : CredentialTargetFactory.CreateForProfileId(profileId)
        };
        settings.ProviderProfiles.Add(profile);
        settings.ActiveProviderProfileId = profile.Id;
        return true;
    }

    private static bool SyncCurrentStatus(
        SwitcherSettings settings,
        ConfigStatus currentStatus)
    {
        var changed = false;
        if (currentStatus.Mode == ProviderMode.Official)
        {
            if (!string.IsNullOrWhiteSpace(currentStatus.Model) &&
                !string.Equals(
                    settings.OfficialModel,
                    currentStatus.Model,
                    StringComparison.Ordinal))
            {
                settings.OfficialModel = currentStatus.Model;
                changed = true;
            }

            if (!string.Equals(
                    settings.OfficialReviewModel,
                    currentStatus.ReviewModel,
                    StringComparison.Ordinal))
            {
                settings.OfficialReviewModel = currentStatus.ReviewModel;
                changed = true;
            }

            return changed;
        }

        if (currentStatus.Mode != ProviderMode.ThirdParty)
        {
            return changed;
        }

        EnsureCurrentProfile(settings, useLegacyCredentialTarget: false);
        var profile = FindProfileForCurrentStatus(settings, currentStatus) ??
                      settings.EnsureActiveProviderProfile();
        if (!string.Equals(settings.ActiveProviderProfileId, profile.Id, StringComparison.Ordinal))
        {
            settings.ActiveProviderProfileId = profile.Id;
            changed = true;
        }

        var kimiRoute = IsKimiStatus(currentStatus) ||
                        (profile.Kind == ProviderKinds.Kimi &&
                         IsKimiBaseUrl(profile.BaseUrl) &&
                         string.Equals(
                             currentStatus.Model,
                             AppPaths.DefaultKimiModel,
                             StringComparison.Ordinal));
        if (kimiRoute)
        {
            if (!string.Equals(profile.Kind, ProviderKinds.Kimi, StringComparison.Ordinal))
            {
                profile.Kind = ProviderKinds.Kimi;
                changed = true;
            }

            // The live config points at the local compatibility router. Keep
            // the profile's upstream SuiXiang URL as the credential/profile
            // identity instead of replacing it with the loopback transport.
            if (!IsKimiBaseUrl(profile.BaseUrl))
            {
                profile.BaseUrl = AppPaths.KimiUpstreamBaseUrl;
                changed = true;
            }
        }
        else if (!string.IsNullOrWhiteSpace(currentStatus.BaseUrl) &&
                 !string.Equals(profile.BaseUrl, currentStatus.BaseUrl, StringComparison.Ordinal))
        {
            profile.BaseUrl = currentStatus.BaseUrl;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(currentStatus.Model) &&
            !string.Equals(profile.Model, currentStatus.Model, StringComparison.Ordinal))
        {
            profile.Model = currentStatus.Model;
            changed = true;
        }

        if (CredentialTargetFactory.IsValid(currentStatus.CredentialTarget) &&
            !string.Equals(
                profile.CredentialTarget,
                currentStatus.CredentialTarget,
                StringComparison.Ordinal))
        {
            profile.CredentialTarget = currentStatus.CredentialTarget!;
            changed = true;
        }

        return changed;
    }

    private static ProviderProfile? FindProfileForCurrentStatus(
        SwitcherSettings settings,
        ConfigStatus status)
    {
        if (CredentialTargetFactory.IsValid(status.CredentialTarget))
        {
            var byCredentialTarget = settings.ProviderProfiles.FirstOrDefault(profile =>
                string.Equals(
                    profile.CredentialTarget,
                    status.CredentialTarget,
                    StringComparison.Ordinal));
            if (byCredentialTarget is not null)
            {
                return byCredentialTarget;
            }
        }

        if (IsKimiStatus(status))
        {
            var kimiProfile = settings.ProviderProfiles.FirstOrDefault(profile =>
                profile.Kind == ProviderKinds.Kimi &&
                string.Equals(profile.Model, status.Model, StringComparison.Ordinal));
            if (kimiProfile is not null)
            {
                return kimiProfile;
            }
        }

        return settings.ProviderProfiles.FirstOrDefault(profile =>
            string.Equals(profile.BaseUrl, status.BaseUrl, StringComparison.Ordinal) &&
            string.Equals(profile.Model, status.Model, StringComparison.Ordinal));
    }

    private string QuarantineCorruptSettingsFile()
    {
        var directory = Path.GetDirectoryName(_settingsPath)
            ?? throw new InvalidOperationException("The settings path has no parent directory.");
        var name = Path.GetFileNameWithoutExtension(_settingsPath);
        var extension = Path.GetExtension(_settingsPath);
        var quarantinePath = Path.Combine(
            directory,
            $"{name}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}{extension}");
        File.Move(_settingsPath, quarantinePath, false);
        return quarantinePath;
    }

    private static bool HasProperty(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().Any(property =>
            property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns true only for the fixed SuiXiang upstream identity used by the
    /// experimental K3 bridge.  The bridge still requires model <c>k3</c> and
    /// profile kind <see cref="ProviderKinds.Kimi"/>; other SuiXiang models
    /// remain ordinary direct Responses routes.
    /// </summary>
    public static bool IsKimiBaseUrl(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase) &&
        uri.IsDefaultPort &&
        string.Equals(
            uri.AbsolutePath.TrimEnd('/'),
            "/v1",
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    public static bool IsKimiLoopbackBaseUrl(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
        uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
        uri.Port == AppPaths.KimiRouterPort &&
        string.Equals(
            uri.AbsolutePath.TrimEnd('/'),
            "/v1",
            StringComparison.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment);

    private static bool IsKimiStatus(ConfigStatus status) =>
        string.Equals(
            status.ModelCatalogJson,
            AppPaths.KimiModelCatalogFileName,
            StringComparison.Ordinal) ||
        IsKimiLoopbackBaseUrl(status.BaseUrl);

    private static bool IsSuiXiangBaseUrl(string? baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase);
}
