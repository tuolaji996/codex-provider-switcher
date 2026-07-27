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

        if (!string.IsNullOrWhiteSpace(currentStatus.BaseUrl) &&
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

    private static bool IsSuiXiangBaseUrl(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("sui-xiang.com", StringComparison.OrdinalIgnoreCase);
}
