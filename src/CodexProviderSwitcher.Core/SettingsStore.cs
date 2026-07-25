using System.Text.Json;

namespace CodexProviderSwitcher.Core;

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

    public SwitcherSettings Load(ConfigStatus currentStatus)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_settingsPath) ??
            throw new InvalidOperationException("The settings path has no parent directory."));

        SwitcherSettings settings;
        if (File.Exists(_settingsPath))
        {
            try
            {
                var json = File.ReadAllText(_settingsPath);
                settings = JsonSerializer.Deserialize<SwitcherSettings>(json, JsonOptions)
                    ?? new SwitcherSettings();
            }
            catch (JsonException)
            {
                settings = new SwitcherSettings();
            }
        }
        else
        {
            settings = new SwitcherSettings();
        }

        settings.UiLanguage = Localizer.NormalizeCode(settings.UiLanguage);
        settings.UiTheme = ThemePreference.NormalizeCode(settings.UiTheme);

        if (currentStatus.Mode == ProviderMode.Official)
        {
            if (!string.IsNullOrWhiteSpace(currentStatus.Model))
            {
                settings.OfficialModel = currentStatus.Model;
            }

            settings.OfficialReviewModel = currentStatus.ReviewModel;
        }
        else if (currentStatus.Mode == ProviderMode.ThirdParty)
        {
            if (!string.IsNullOrWhiteSpace(currentStatus.BaseUrl))
            {
                settings.ThirdPartyBaseUrl = currentStatus.BaseUrl;
            }

            if (!string.IsNullOrWhiteSpace(currentStatus.Model))
            {
                settings.ThirdPartyModel = currentStatus.Model;
            }
        }

        Save(settings);
        return settings;
    }

    public void Save(SwitcherSettings settings)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(_settingsPath) ??
            throw new InvalidOperationException("The settings path has no parent directory."));
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(_settingsPath, json);
    }
}
