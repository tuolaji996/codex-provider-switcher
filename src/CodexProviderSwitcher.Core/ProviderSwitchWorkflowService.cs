namespace CodexProviderSwitcher.Core;

public sealed record ThirdPartySwitchRequest(
    string Model,
    string BaseUrl,
    string TokenBrokerWindowsPath,
    string CredentialTarget);

public sealed record OfficialSwitchRequest(
    string Model,
    string? ReviewModel);

public sealed record ProviderSwitchResult(
    string BackupFolder,
    ConfigStatus VerifiedStatus);

// Owns the reversible config transaction shared by the daily UI and onboarding.
public sealed class ProviderSwitchWorkflowService
{
    private readonly ConfigService _configService;

    public ProviderSwitchWorkflowService(ConfigService? configService = null)
    {
        _configService = configService ?? new ConfigService();
    }

    public ProviderSwitchResult SwitchToThirdParty(
        ThirdPartySwitchRequest request,
        string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TokenBrokerWindowsPath);
        CredentialTargetFactory.RequireValid(request.CredentialTarget);

        return WriteAndVerify(
            original => _configService.BuildThirdPartyConfig(
                original,
                request.Model,
                request.BaseUrl,
                request.TokenBrokerWindowsPath,
                request.CredentialTarget),
            status =>
                status.Mode == ProviderMode.ThirdParty &&
                status.ProviderId == AppPaths.StableProviderId &&
                string.Equals(
                    status.CredentialTarget,
                    request.CredentialTarget,
                    StringComparison.Ordinal),
            configPath);
    }

    public ProviderSwitchResult SwitchToOfficial(
        OfficialSwitchRequest request,
        string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        return WriteAndVerify(
            original => _configService.BuildOfficialConfig(
                original,
                request.Model,
                request.ReviewModel),
            status =>
                status.Mode == ProviderMode.Official &&
                status.ProviderId == AppPaths.StableProviderId &&
                status.UsesOfficialAuthentication,
            configPath);
    }

    private ProviderSwitchResult WriteAndVerify(
        Func<string, string> buildConfig,
        Func<ConfigStatus, bool> isExpectedStatus,
        string? configPath)
    {
        configPath ??= AppPaths.ConfigPath;
        var backupFolder = _configService.CreateBackup(configPath);
        var original = File.ReadAllText(configPath);
        var wroteConfig = false;

        try
        {
            var updated = buildConfig(original);
            _configService.WriteConfig(updated, configPath);
            wroteConfig = true;

            var verification = _configService.ReadStatus(configPath);
            if (!isExpectedStatus(verification))
            {
                throw new InvalidOperationException(
                    $"Post-write verification failed. Backup: {backupFolder}");
            }

            return new ProviderSwitchResult(backupFolder, verification);
        }
        catch (Exception exception) when (wroteConfig)
        {
            try
            {
                _configService.WriteConfig(original, configPath);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The switch failed and the original configuration could not be restored.",
                    exception,
                    rollbackException);
            }

            throw new InvalidOperationException(
                $"The switch failed and the original configuration was restored. Backup: {backupFolder}",
                exception);
        }
    }
}
