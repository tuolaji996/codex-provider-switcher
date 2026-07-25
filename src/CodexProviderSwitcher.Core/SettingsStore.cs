using System.Text.Json;

namespace CodexProviderSwitcher.Core;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public SwitcherSettings Load(ConfigStatus currentStatus)
    {
        Directory.CreateDirectory(AppPaths.LocalDataRoot);

        SwitcherSettings settings;
        if (File.Exists(AppPaths.SettingsPath))
        {
            try
            {
                var json = File.ReadAllText(AppPaths.SettingsPath);
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
        Directory.CreateDirectory(AppPaths.LocalDataRoot);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        AtomicFile.WriteAllText(AppPaths.SettingsPath, json);
    }
}
