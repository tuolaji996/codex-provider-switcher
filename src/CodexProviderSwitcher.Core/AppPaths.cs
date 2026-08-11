namespace CodexProviderSwitcher.Core;

public static class AppPaths
{
    public const string StableProviderId = "OpenAI";
    // Retained for v1.3 migrations so existing saved keys continue to work.
    public const string LegacySuiXiangCredentialTarget = "CodexProviderSwitcher:sui-xiang";
    public const string CredentialTarget = LegacySuiXiangCredentialTarget;
    public const string DefaultBaseUrl = "https://sui-xiang.com/v1";
    public const string DefaultThirdPartyModel = "codex-auto-review";
    public const string DefaultThirdPartyImageModel = "gpt-image-2";
    // Kimi/K3 is routed through the configured SuiXiang-compatible upstream.
    // The local adapter still exposes Responses to Codex, but its upstream wire
    // protocol is Chat Completions at this base URL.
    public const string KimiUpstreamBaseUrl = "https://sui-xiang.com/v1";
    public const string DefaultKimiUpstreamBaseUrl = KimiUpstreamBaseUrl;
    public const string DefaultKimiModel = "k3";
    public const string KimiRouterBaseUrl = "http://127.0.0.1:17866/v1";
    public const string KimiLoopbackBaseUrl = KimiRouterBaseUrl;
    public const int KimiRouterPort = 17866;
    public const string KimiModelCatalogFileName =
        "codex-provider-switcher-kimi-model-catalog.json";
    public const string KimiModelCatalogRelativePath = KimiModelCatalogFileName;
    public const string KimiRouterExecutableName = "CodexProviderKimiRouter.exe";
    public const string KimiRouterExeName = KimiRouterExecutableName;
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

    public static string KimiModelCatalogPath =>
        Path.Combine(CodexHome, KimiModelCatalogFileName);

    public static string AgentsDirectory => Path.Combine(CodexHome, "agents");

    public static string LunaWorkerAgentPath =>
        Path.Combine(AgentsDirectory, "luna-worker.toml");

    public static string DisabledLunaWorkerAgentPath =>
        Path.Combine(AgentsDirectory, "luna-worker.toml.disabled-by-provider-switcher");

    public static string LocalDataRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexProviderSwitcher");

    public static string SettingsPath => Path.Combine(LocalDataRoot, "settings.json");

    public static string BackupsRoot => Path.Combine(LocalDataRoot, "Backups");

    public static string DiagnosticsRoot => Path.Combine(LocalDataRoot, "Diagnostics");

    public static string WebView2Root => Path.Combine(LocalDataRoot, "WebView2");

    public static string SuiXiangWebView2UserDataFolder =>
        Path.Combine(WebView2Root, "SuiXiang");
}
