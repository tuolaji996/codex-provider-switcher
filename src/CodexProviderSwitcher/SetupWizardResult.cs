namespace CodexProviderSwitcher;

public sealed record SetupWizardResult(
    bool UseOfficial,
    string ProviderKind,
    string DisplayName,
    string BaseUrl,
    string Model,
    string? ApiKey);
