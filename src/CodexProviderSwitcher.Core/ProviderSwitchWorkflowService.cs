namespace CodexProviderSwitcher.Core;

public sealed record ThirdPartySwitchRequest(
    string Model,
    string BaseUrl,
    string TokenBrokerWindowsPath,
    string CredentialTarget);

public sealed record KimiSwitchRequest(
    string Model,
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
    private readonly KimiModelCatalogService _kimiModelCatalogService;

    public ProviderSwitchWorkflowService(
        ConfigService? configService = null,
        KimiModelCatalogService? kimiModelCatalogService = null)
    {
        _configService = configService ?? new ConfigService();
        _kimiModelCatalogService = kimiModelCatalogService ?? new KimiModelCatalogService();
    }

    public ProviderSwitchResult SwitchToThirdParty(
        ThirdPartySwitchRequest request,
        string? configPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TokenBrokerWindowsPath);
        CredentialTargetFactory.RequireValid(request.CredentialTarget);
        var expectedBaseUrl = ConfigService.NormalizeBaseUrl(request.BaseUrl);
        var expectedModel = request.Model.Trim();

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
                    status.Model,
                    expectedModel,
                    StringComparison.Ordinal) &&
                string.Equals(
                    status.ReviewModel,
                    expectedModel,
                    StringComparison.Ordinal) &&
                string.Equals(
                    status.BaseUrl,
                    expectedBaseUrl,
                    StringComparison.OrdinalIgnoreCase) &&
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
        var expectedModel = request.Model.Trim();
        var expectedReviewModel = string.IsNullOrWhiteSpace(request.ReviewModel)
            ? null
            : request.ReviewModel.Trim();

        return WriteAndVerify(
            original => _configService.BuildOfficialConfig(
                original,
                request.Model,
                request.ReviewModel),
            status =>
                status.Mode == ProviderMode.Official &&
                status.ProviderId == AppPaths.StableProviderId &&
                status.UsesOfficialAuthentication &&
                string.Equals(
                    status.Model,
                    expectedModel,
                    StringComparison.Ordinal) &&
                string.Equals(
                    status.ReviewModel,
                    expectedReviewModel,
                    StringComparison.Ordinal),
            configPath);
    }

    public ProviderSwitchResult SwitchToKimi(
        KimiSwitchRequest request,
        string? configPath = null,
        string? codexHome = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.TokenBrokerWindowsPath);
        CredentialTargetFactory.RequireValid(request.CredentialTarget);
        var expectedModel = request.Model.Trim();
        var expectedBaseUrl = ConfigService.NormalizeBaseUrl(
            AppPaths.KimiRouterBaseUrl);

        var catalogPath = KimiModelCatalogService.GetCatalogPath(codexHome);
        var catalogExisted = File.Exists(catalogPath);
        var previousCatalog = catalogExisted ? File.ReadAllBytes(catalogPath) : null;
        try
        {
            _kimiModelCatalogService.EnsureCatalog(expectedModel, codexHome);
            return WriteAndVerify(
                original => _configService.BuildKimiConfig(
                    original,
                    request.Model,
                    request.TokenBrokerWindowsPath,
                    request.CredentialTarget),
                status =>
                    status.Mode == ProviderMode.ThirdParty &&
                    status.ProviderId == AppPaths.StableProviderId &&
                    string.Equals(status.Model, expectedModel, StringComparison.Ordinal) &&
                    string.Equals(status.ReviewModel, expectedModel, StringComparison.Ordinal) &&
                    string.Equals(status.BaseUrl, expectedBaseUrl, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        status.ModelCatalogJson,
                        AppPaths.KimiModelCatalogFileName,
                        StringComparison.Ordinal) &&
                    string.Equals(
                        status.CredentialTarget,
                        request.CredentialTarget,
                        StringComparison.Ordinal),
                configPath);
        }
        catch (Exception exception)
        {
            try
            {
                if (catalogExisted)
                {
                    AtomicFile.WriteAllBytes(catalogPath, previousCatalog!);
                }
                else if (File.Exists(catalogPath))
                {
                    File.Delete(catalogPath);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The Kimi switch failed and the previous model catalog could not be restored.",
                    exception,
                    rollbackException);
            }

            throw;
        }
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
