namespace CodexProviderSwitcher.Core;

public static class AppPaths
{
    public const string StableProviderId = "OpenAI";
    public const string CredentialTarget = "CodexProviderSwitcher:sui-xiang";
    public const string DefaultBaseUrl = "https://sui-xiang.com/v1";
    public const string DefaultThirdPartyModel = "codex-auto-review";
    public const string DefaultOfficialModel = "gpt-5.6-sol";
    public const string CodexAppId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string CodexHome
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(UserProfile, ".codex")
                : Environment.ExpandEnvironmentVariables(configured);
        }
    }

    public static string ConfigPath => Path.Combine(CodexHome, "config.toml");

    public static string LocalDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexProviderSwitcher");

    public static string SettingsPath => Path.Combine(LocalDataRoot, "settings.json");

    public static string BackupsRoot => Path.Combine(LocalDataRoot, "Backups");
}
