using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CodexProviderSwitcher.Core;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

Check(
    Localizer.NormalizeCode(null) == Localizer.ChineseCode,
    "A missing language did not default to Chinese.");
Check(
    Localizer.NormalizeCode("ENG") == Localizer.EnglishCode,
    "The ENG language alias was not normalized.");
Check(
    Localizer.NormalizeCode("en-CA") == Localizer.EnglishCode,
    "An English locale was not normalized.");
Check(
    Localizer.NormalizeCode("unsupported") == Localizer.ChineseCode,
    "An unsupported language did not fall back to Chinese.");
Check(
    ThemePreference.NormalizeCode(null) == ThemePreference.LightCode,
    "A missing theme did not default to light.");
Check(
    ThemePreference.NormalizeCode(" LIGHT ") == ThemePreference.LightCode,
    "The light theme code was not normalized.");
Check(
    ThemePreference.NormalizeCode("dark") == ThemePreference.DarkCode,
    "The dark theme code was not normalized.");
Check(
    ThemePreference.NormalizeCode("SYSTEM") == ThemePreference.SystemCode,
    "The system theme code was not normalized.");
Check(
    ThemePreference.NormalizeCode("unsupported") == ThemePreference.LightCode,
    "An unsupported theme did not fall back to light.");

var updateHandler = new StubHttpMessageHandler(
    HttpStatusCode.OK,
    """
    {
      "tag_name": "v1.3.8",
      "html_url": "https://malicious.example/not-used"
    }
    """);
using var updateClient = new HttpClient(updateHandler);
var updateService = new GitHubReleaseUpdateService(updateClient);
var availableUpdate = await updateService.CheckAsync(new Version(1, 3, 7, 0));
Check(availableUpdate.IsUpdateAvailable, "A newer GitHub release was not detected.");
Check(
    availableUpdate.CurrentVersion == new Version(1, 3, 7) &&
    availableUpdate.LatestVersion == new Version(1, 3, 8) &&
    availableUpdate.LatestTag == "v1.3.8",
    "The GitHub release versions were not normalized correctly.");
Check(
    availableUpdate.ReleaseUri == new Uri(
        "https://github.com/tuolaji996/codex-provider-switcher/releases/tag/v1.3.8"),
    "The update link did not stay on the managed GitHub repository.");
Check(
    updateHandler.RequestUri == new Uri(
        GitHubReleaseUpdateService.LatestReleaseApiUrl) &&
    updateHandler.Accept == "application/vnd.github+json" &&
    updateHandler.ApiVersion == "2026-03-10" &&
    !updateHandler.HasAuthorization &&
    updateHandler.UserAgent.Contains(
        "CodexProviderSwitcher/1.3.7",
        StringComparison.Ordinal),
    "The GitHub latest-release request is missing required API headers.");

var currentReleaseService = new GitHubReleaseUpdateService(
    new HttpClient(new StubHttpMessageHandler(
        HttpStatusCode.OK,
        "{\"tag_name\":\"v1.3.7\"}")));
Check(
    !(await currentReleaseService.CheckAsync(new Version(1, 3, 7))).IsUpdateAvailable,
    "The current release was incorrectly reported as an update.");
var olderReleaseService = new GitHubReleaseUpdateService(
    new HttpClient(new StubHttpMessageHandler(
        HttpStatusCode.OK,
        "{\"tag_name\":\"v1.3.4\"}")));
Check(
    !(await olderReleaseService.CheckAsync(new Version(1, 3, 7))).IsUpdateAvailable,
    "An older release was incorrectly reported as an update.");

var invalidReleaseRejected = false;
try
{
    var invalidReleaseService = new GitHubReleaseUpdateService(
        new HttpClient(new StubHttpMessageHandler(
            HttpStatusCode.OK,
            "{\"tag_name\":\"nightly-latest\"}")));
    await invalidReleaseService.CheckAsync(new Version(1, 3, 7));
}
catch (InvalidDataException)
{
    invalidReleaseRejected = true;
}
Check(invalidReleaseRejected, "An invalid GitHub release tag was accepted.");

var failedReleaseRequestRejected = false;
try
{
    var failedReleaseService = new GitHubReleaseUpdateService(
        new HttpClient(new StubHttpMessageHandler(
            HttpStatusCode.Forbidden,
            "{\"message\":\"rate limited\"}")));
    await failedReleaseService.CheckAsync(new Version(1, 3, 5));
}
catch (HttpRequestException)
{
    failedReleaseRequestRejected = true;
}
Check(failedReleaseRequestRejected, "A failed GitHub release request was accepted.");

const string modelDiscoveryKey = "model-discovery-test-key";
var modelDiscoveryHandler = new ModelDiscoveryStubHttpMessageHandler(
    HttpStatusCode.OK,
    """
    {
      "data": [
        { "id": " z-model " },
        { "id": "z-model" },
        { "id": " " },
        { "id": "" },
        { "id": "a-model" }
      ]
    }
    """);
var modelDiscovery = new ModelDiscoveryService(
    new HttpClient(modelDiscoveryHandler));
var discoveredModels = await modelDiscovery.DiscoverAsync(
    " https://models.example/v1/ ",
    modelDiscoveryKey);
Check(
    discoveredModels.Success &&
    discoveredModels.Models.SequenceEqual(new[] { "a-model", "z-model" }) &&
    discoveredModels.StatusCode == 200,
    "Model discovery did not normalize, deduplicate, and sort model IDs.");
Check(
    modelDiscoveryHandler.RequestUri ==
        new Uri("https://models.example/v1/models") &&
    modelDiscoveryHandler.Method == HttpMethod.Get &&
    modelDiscoveryHandler.AuthorizationScheme == "Bearer" &&
    modelDiscoveryHandler.AuthorizationParameter == modelDiscoveryKey,
    "Model discovery did not issue the expected authenticated GET request.");
Check(
    !discoveredModels.Summary.Contains(modelDiscoveryKey, StringComparison.Ordinal),
    "Model discovery included the API key in a success summary.");

Localizer.Use(AppLanguage.English);
var englishModelDiscovery = await new ModelDiscoveryService(
    new HttpClient(new ModelDiscoveryStubHttpMessageHandler(
        HttpStatusCode.OK,
        "{\"data\":[{\"id\":\"english-model\"}]}")))
    .DiscoverAsync("https://models.example/v1", modelDiscoveryKey);
Check(
    englishModelDiscovery.Success &&
    englishModelDiscovery.Summary ==
        "Loaded 1 model IDs; compatibility is verified separately.",
    "Model discovery did not use the active English localization.");
Localizer.Use(AppLanguage.Chinese);

foreach (var malformedModelsBody in new[]
         {
             "{not-json",
             "{\"data\":{}}",
             "{\"data\":[{\"id\":42}]}"
         })
{
    var malformedResult = await new ModelDiscoveryService(
        new HttpClient(new ModelDiscoveryStubHttpMessageHandler(
            HttpStatusCode.OK,
            malformedModelsBody)))
        .DiscoverAsync("https://models.example/v1", modelDiscoveryKey);
    Check(
        !malformedResult.Success &&
        malformedResult.StatusCode == 200 &&
        malformedResult.Models.Count == 0 &&
        !malformedResult.Summary.Contains(
            modelDiscoveryKey,
            StringComparison.Ordinal),
        "Malformed model discovery data was not rejected safely.");
}

foreach (var statusCode in new[]
         {
             HttpStatusCode.Unauthorized,
             HttpStatusCode.NotFound,
             HttpStatusCode.ServiceUnavailable
         })
{
    var statusResult = await new ModelDiscoveryService(
        new HttpClient(new ModelDiscoveryStubHttpMessageHandler(
            statusCode,
            $"{{\"error\":\"{modelDiscoveryKey}\"}}")))
        .DiscoverAsync("https://models.example/v1", modelDiscoveryKey);
    Check(
        !statusResult.Success &&
        statusResult.StatusCode == (int)statusCode &&
        statusResult.Models.Count == 0 &&
        !statusResult.Summary.Contains(
            modelDiscoveryKey,
            StringComparison.Ordinal),
        $"HTTP {(int)statusCode} model discovery failure was not classified safely.");
}

var oversizedModelBody =
    "{\"data\":[]}" + new string('x', ModelDiscoveryService.MaxResponseBodyBytes);
var oversizedModelResult = await new ModelDiscoveryService(
    new HttpClient(new ModelDiscoveryStubHttpMessageHandler(
        HttpStatusCode.OK,
        oversizedModelBody)))
    .DiscoverAsync("https://models.example/v1", modelDiscoveryKey);
Check(
    !oversizedModelResult.Success &&
    oversizedModelResult.StatusCode == 200 &&
    !oversizedModelResult.Summary.Contains(
        modelDiscoveryKey,
        StringComparison.Ordinal),
    "An oversized model discovery response was not rejected safely.");

var tooManyModelsBody =
    "{\"data\":[" +
    string.Join(
        ",",
        Enumerable.Range(0, ModelDiscoveryService.MaxModelCount + 1)
            .Select(index => $"{{\"id\":\"model-{index}\"}}")) +
    "]}";
var tooManyModelsResult = await new ModelDiscoveryService(
    new HttpClient(new ModelDiscoveryStubHttpMessageHandler(
        HttpStatusCode.OK,
        tooManyModelsBody)))
    .DiscoverAsync("https://models.example/v1", modelDiscoveryKey);
Check(
    !tooManyModelsResult.Success &&
    tooManyModelsResult.StatusCode == 200 &&
    !tooManyModelsResult.Summary.Contains(
        modelDiscoveryKey,
        StringComparison.Ordinal),
    "An excessive model count was not rejected safely.");

var cancellationSource = new CancellationTokenSource();
var cancellationTask = new ModelDiscoveryService(
    new HttpClient(new BlockingModelDiscoveryHttpMessageHandler()))
    .DiscoverAsync(
        "https://models.example/v1",
        modelDiscoveryKey,
        cancellationSource.Token);
cancellationSource.Cancel();
var cancellationObserved = false;
try
{
    await cancellationTask;
}
catch (OperationCanceledException)
{
    cancellationObserved = true;
}
Check(cancellationObserved, "Model discovery did not honor cancellation.");

var embeddedNavigationCases = new[]
{
    ("https://sui-xiang.com/", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://login.sui-xiang.com/path", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://captcha.qq.com/", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://verify.qcloud.com/", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://verify.tencent-cloud.com/", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://verify.tencentcs.com/", false, EmbeddedNavigationAction.AllowEmbedded),
    ("https://example.com/", true, EmbeddedNavigationAction.OpenExternal),
    ("https://example.com/", false, EmbeddedNavigationAction.Block),
    ("https://sui-xiang.com.evil.example/", true, EmbeddedNavigationAction.OpenExternal),
    ("https://sui-xiang.com.evil.example/", false, EmbeddedNavigationAction.Block),
    ("https://evilqq.com/", false, EmbeddedNavigationAction.Block),
    ("http://sui-xiang.com/", true, EmbeddedNavigationAction.Block),
    ("javascript:alert(1)", true, EmbeddedNavigationAction.Block),
    ("data:text/plain,hello", true, EmbeddedNavigationAction.Block),
    ("file:///C:/Windows/System32/drivers/etc/hosts", true, EmbeddedNavigationAction.Block),
    ("not a URI", true, EmbeddedNavigationAction.Block),
    (string.Empty, true, EmbeddedNavigationAction.Block)
};
foreach (var navigationCase in embeddedNavigationCases)
{
    Check(
        SuiXiangNavigationPolicy.Classify(
            navigationCase.Item1,
            navigationCase.Item2) == navigationCase.Item3,
        $"Embedded navigation policy misclassified {navigationCase.Item1}.");
}

Localizer.Use(AppLanguage.English);
Check(
    Localizer.Text("中文", "English") == "English",
    "English text selection failed.");
Check(
    Localizer.Format("值 {0}", "Value {0}", 7) == "Value 7",
    "English localized formatting failed.");
var invalidEnglishSse = ConnectionTestService.AnalyzeSse("data: not-json\n\n");
Check(
    invalidEnglishSse.Error == "SSE data is not valid JSON.",
    "English SSE diagnostics were not localized.");

var malformedCredential = new string('z', 20) + "\r\ninvalid";
try
{
    _ = await new ConnectionTestService().TestResponsesApiAsync(
        "https://example.invalid/v1",
        "example-model",
        malformedCredential);
    Check(false, "A malformed Bearer credential was accepted.");
}
catch (InvalidOperationException exception)
{
    Check(
        !exception.Message.Contains(malformedCredential, StringComparison.Ordinal) &&
        exception.Message.Contains("request", StringComparison.OrdinalIgnoreCase),
        "The malformed credential error was not safely redacted.");
}

var kimiChatProbeHandler = new ProtocolProbeHttpMessageHandler(
    """
    {"id":"chat-k3-probe","choices":[{"message":{"role":"assistant","content":"OK"}}]}
    """,
    "application/json");
var kimiChatProbe = new ConnectionTestService(
    new HttpClient(kimiChatProbeHandler));
var kimiChatResult = await kimiChatProbe.TestChatCompletionsApiAsync(
    AppPaths.KimiUpstreamBaseUrl,
    AppPaths.DefaultKimiModel,
    "k3-probe-key");
Check(
    kimiChatResult.Success &&
    kimiChatProbeHandler.Method == HttpMethod.Post &&
    kimiChatProbeHandler.RequestUri?.AbsolutePath == "/v1/chat/completions" &&
    kimiChatProbeHandler.AuthorizationScheme == "Bearer" &&
    kimiChatProbeHandler.AuthorizationParameter == "k3-probe-key" &&
    kimiChatProbeHandler.RequestBody?.Contains("\"model\":\"k3\"", StringComparison.Ordinal) == true,
    "The K3 compatibility probe did not use the upstream Chat Completions contract.");

var directResponsesProbeHandler = new ProtocolProbeHttpMessageHandler(
    """
    data: {"type":"response.created","response":{"id":"resp-direct-probe","status":"in_progress"}}

    data: {"type":"response.output_text.delta","delta":"OK"}

    data: {"type":"response.completed","response":{"id":"resp-direct-probe","status":"completed"}}

    data: [DONE]

    """,
    "text/event-stream");
var directResponsesProbe = new ConnectionTestService(
    new HttpClient(directResponsesProbeHandler));
var directResponsesResult = await directResponsesProbe.TestResponsesApiAsync(
    AppPaths.KimiUpstreamBaseUrl,
    "gpt-5.6-sol",
    "direct-probe-key");
Check(
    directResponsesResult.Success &&
    directResponsesProbeHandler.Method == HttpMethod.Post &&
    directResponsesProbeHandler.RequestUri?.AbsolutePath == "/v1/responses",
    "The direct SuiXiang compatibility probe did not use the Responses contract.");

var languageSettingsJson = JsonSerializer.Serialize(
    new SwitcherSettings
    {
        UiLanguage = Localizer.EnglishCode,
        UiTheme = ThemePreference.DarkCode
    });
var reloadedLanguageSettings =
    JsonSerializer.Deserialize<SwitcherSettings>(languageSettingsJson);
Check(
    reloadedLanguageSettings?.UiLanguage == Localizer.EnglishCode,
    "The selected language did not survive a settings JSON round trip.");
Check(
    reloadedLanguageSettings?.UiTheme == ThemePreference.DarkCode,
    "The selected theme did not survive a settings JSON round trip.");
var legacySettings = JsonSerializer.Deserialize<SwitcherSettings>("{}");
Check(
    legacySettings?.UiTheme == ThemePreference.LightCode,
    "Legacy settings without a theme did not default to light.");

Localizer.Use(AppLanguage.Chinese);
Check(
    Localizer.Text("中文", "English") == "中文",
    "Chinese text selection failed.");
Check(
    new SwitcherSettings().UiLanguage == Localizer.ChineseCode,
    "New settings did not default to Chinese.");
Check(
    new SwitcherSettings().UiTheme == ThemePreference.LightCode,
    "New settings did not default to the light theme.");

var settingsStoreRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-settings-test-{Guid.NewGuid():N}");
var settingsStorePath = Path.Combine(settingsStoreRoot, "settings.json");
Directory.CreateDirectory(settingsStoreRoot);
try
{
    var settingsFixture = new SwitcherSettings
    {
        UiLanguage = Localizer.EnglishCode,
        UiTheme = "invalid",
        OfficialModel = "official-preserved",
        OfficialReviewModel = "review-preserved",
        ThirdPartyBaseUrl = "https://original.example/v1",
        ThirdPartyModel = "third-preserved",
        RestartAfterSwitch = false,
        LastSuccessfulCompatibilityTestUtc =
            DateTimeOffset.Parse("2026-07-25T01:00:00Z"),
        LastTestedEndpointFingerprint = "compatibility-fingerprint",
        LastSuccessfulToolTestUtc =
            DateTimeOffset.Parse("2026-07-25T02:00:00Z"),
        LastToolTestedEndpointFingerprint = "tool-fingerprint",
        LastSuccessfulImageTestUtc =
            DateTimeOffset.Parse("2026-07-25T03:00:00Z"),
        LastImageTestedEndpointFingerprint = "image-fingerprint",
        LastGeneratedImagePath = @"C:\diagnostics\image.png"
    };
    File.WriteAllText(
        settingsStorePath,
        JsonSerializer.Serialize(settingsFixture));
    var testSettingsStore = new SettingsStore(settingsStorePath);
    var loadedThirdPartySettings = testSettingsStore.Load(
        new ConfigStatus(
            ProviderMode.ThirdParty,
            AppPaths.StableProviderId,
            "third-current",
            "third-current",
            "https://updated.example/v1",
            false));

    Check(
        loadedThirdPartySettings.UiTheme == ThemePreference.LightCode,
        "SettingsStore did not normalize an invalid theme.");
    Check(
        loadedThirdPartySettings.OfficialModel == "official-preserved" &&
        loadedThirdPartySettings.OfficialReviewModel == "review-preserved",
        "Loading third-party status changed official settings.");
    Check(
        loadedThirdPartySettings.ThirdPartyBaseUrl ==
        "https://updated.example/v1" &&
        loadedThirdPartySettings.ThirdPartyModel == "third-current",
        "Loading third-party status did not update its route settings.");
    Check(
        !loadedThirdPartySettings.RestartAfterSwitch &&
        loadedThirdPartySettings.LastTestedEndpointFingerprint ==
        "compatibility-fingerprint" &&
        loadedThirdPartySettings.LastToolTestedEndpointFingerprint ==
        "tool-fingerprint" &&
        loadedThirdPartySettings.LastImageTestedEndpointFingerprint ==
        "image-fingerprint" &&
        loadedThirdPartySettings.LastGeneratedImagePath ==
        @"C:\diagnostics\image.png",
        "Loading settings discarded unrelated preferences or diagnostics.");

    var persistedNormalizedSettings =
        JsonSerializer.Deserialize<SwitcherSettings>(
            File.ReadAllText(settingsStorePath));
    Check(
        persistedNormalizedSettings?.UiTheme == ThemePreference.LightCode,
        "The normalized theme was not persisted.");

    var loadedOfficialSettings = testSettingsStore.Load(
        new ConfigStatus(
            ProviderMode.Official,
            AppPaths.StableProviderId,
            "official-current",
            "review-current",
            null,
            true));
    Check(
        loadedOfficialSettings.OfficialModel == "official-current" &&
        loadedOfficialSettings.OfficialReviewModel == "review-current",
        "Loading official status did not update official settings.");
    Check(
        loadedOfficialSettings.ThirdPartyBaseUrl ==
        "https://updated.example/v1" &&
        loadedOfficialSettings.ThirdPartyModel == "third-current",
        "Loading official status changed third-party settings.");
}
finally
{
    Directory.Delete(settingsStoreRoot, true);
}

var migrationRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-migration-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(migrationRoot);
try
{
    var legacySettingsPath = Path.Combine(migrationRoot, "legacy-settings.json");
    File.WriteAllText(
        legacySettingsPath,
        """
        {
          "UiLanguage": "en-US",
          "UiTheme": "dark",
          "OfficialModel": "official-legacy",
          "OfficialReviewModel": "review-legacy",
          "ThirdPartyBaseUrl": "https://legacy.example/v1",
          "ThirdPartyModel": "legacy-model",
          "RestartAfterSwitch": false
        }
        """);
    var migrated = new SettingsStore(legacySettingsPath).LoadWithStatus(
        new ConfigStatus(
            ProviderMode.ThirdParty,
            AppPaths.StableProviderId,
            "legacy-model",
            "legacy-model",
            "https://legacy.example/v1",
            false,
            AppPaths.LegacySuiXiangCredentialTarget));
    Check(migrated.WasMigrated, "A v1.3 settings document was not migrated.");
    Check(
        migrated.Settings.SettingsSchemaVersion == SwitcherSettings.CurrentSchemaVersion &&
        migrated.Settings.OnboardingCompleted,
        "A migrated v1.3 user was not preserved as already onboarded.");
    Check(
        migrated.Settings.ActiveProviderProfile?.CredentialTarget ==
        AppPaths.LegacySuiXiangCredentialTarget,
        "A migrated v1.3 profile did not retain its legacy credential target.");
    Check(
        migrated.Settings.ThirdPartyBaseUrl == "https://legacy.example/v1" &&
        migrated.Settings.ThirdPartyModel == "legacy-model" &&
        migrated.Settings.UiLanguage == Localizer.EnglishCode &&
        migrated.Settings.UiTheme == ThemePreference.DarkCode,
        "Migration changed a legacy provider or interface preference.");

    var legacyOfficialSettingsPath = Path.Combine(
        migrationRoot,
        "legacy-official-settings.json");
    File.WriteAllText(
        legacyOfficialSettingsPath,
        """
        {
          "UiLanguage": "zh-CN",
          "OfficialModel": "official-before-load",
          "OfficialReviewModel": "review-before-load",
          "ThirdPartyBaseUrl": "https://saved-provider.example/v1",
          "ThirdPartyModel": "saved-provider-model",
          "RestartAfterSwitch": true
        }
        """);
    var migratedOfficial = new SettingsStore(legacyOfficialSettingsPath).LoadWithStatus(
        new ConfigStatus(
            ProviderMode.Official,
            AppPaths.StableProviderId,
            "official-current",
            "review-current",
            null,
            true));
    Check(
        migratedOfficial.WasMigrated &&
        migratedOfficial.Settings.OnboardingCompleted,
        "A v1.3 official configuration was not migrated as already onboarded.");
    Check(
        migratedOfficial.Settings.OfficialModel == "official-current" &&
        migratedOfficial.Settings.OfficialReviewModel == "review-current" &&
        migratedOfficial.Settings.ThirdPartyBaseUrl ==
        "https://saved-provider.example/v1" &&
        migratedOfficial.Settings.ThirdPartyModel == "saved-provider-model",
        "Official migration did not preserve the saved third-party route.");
    Check(
        migratedOfficial.Settings.ActiveProviderProfile?.CredentialTarget ==
        AppPaths.LegacySuiXiangCredentialTarget,
        "Official migration did not retain the legacy v1.3 credential target.");

    var freshSettingsPath = Path.Combine(migrationRoot, "fresh-settings.json");
    var fresh = new SettingsStore(freshSettingsPath).LoadWithStatus(
        new ConfigStatus(
            ProviderMode.Unknown,
            string.Empty,
            null,
            null,
            null,
            false));
    Check(fresh.IsNewInstall, "A missing settings file was not treated as a new install.");
    Check(!fresh.Settings.OnboardingCompleted, "A new install incorrectly skipped onboarding.");
    Check(
        fresh.Settings.ActiveProviderProfile is { } freshProfile &&
        CredentialTargetFactory.IsValid(freshProfile.CredentialTarget) &&
        freshProfile.CredentialTarget != AppPaths.LegacySuiXiangCredentialTarget,
        "A new install did not receive an isolated provider credential target.");

    var corruptSettingsPath = Path.Combine(migrationRoot, "corrupt-settings.json");
    File.WriteAllText(corruptSettingsPath, "{ definitely not json");
    var recovered = new SettingsStore(corruptSettingsPath).LoadWithStatus(
        new ConfigStatus(
            ProviderMode.Unknown,
            string.Empty,
            null,
            null,
            null,
            false));
    Check(
        recovered.RecoveryNotice is not null &&
        Directory.GetFiles(migrationRoot, "corrupt-settings.corrupt-*.json").Length == 1,
        "A corrupt settings file was not quarantined before recovery.");

    var repairedProfileSettingsPath = Path.Combine(
        migrationRoot,
        "repaired-profile-settings.json");
    var repairedProfileSettings = new SwitcherSettings
    {
        OnboardingCompleted = true,
        ProviderProfiles =
        [
            new ProviderProfile
            {
                Id = "not-a-guid",
                BaseUrl = "https://profile-repair.example/v1",
                Model = "profile-repair-model",
                CredentialTarget = "unmanaged:credential"
            }
        ],
        ActiveProviderProfileId = "not-a-guid"
    };
    File.WriteAllText(
        repairedProfileSettingsPath,
        JsonSerializer.Serialize(repairedProfileSettings));
    var repaired = new SettingsStore(repairedProfileSettingsPath).LoadWithStatus(
        new ConfigStatus(
            ProviderMode.Unknown,
            string.Empty,
            null,
            null,
            null,
            false));
    Check(
        repaired.Settings.ActiveProviderProfile is { } repairedProfile &&
        Guid.TryParse(repairedProfile.Id, out _) &&
        CredentialTargetFactory.IsValid(repairedProfile.CredentialTarget),
        "An invalid active profile ID or credential target was not repaired safely.");
}
finally
{
    Directory.Delete(migrationRoot, true);
}

var backupCatalogRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-backup-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(backupCatalogRoot);
try
{
    var olderBackupFolder = Path.Combine(
        backupCatalogRoot,
        "20260724-235959-001");
    var newerBackupFolder = Path.Combine(
        backupCatalogRoot,
        "20260725-010203-004");
    Directory.CreateDirectory(olderBackupFolder);
    Directory.CreateDirectory(newerBackupFolder);
    var olderBackupPath = Path.Combine(olderBackupFolder, "config.toml");
    var newerBackupPath = Path.Combine(newerBackupFolder, "config.toml");
    File.WriteAllText(olderBackupPath, "old");
    File.WriteAllText(newerBackupPath, "newer");
    File.SetLastWriteTime(
        newerBackupPath,
        new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Local));

    var backups = new BackupCatalogService(backupCatalogRoot).List();
    Check(backups.Count == 2, "The backup catalog did not list every backup.");
    Check(
        backups[0].FolderName == "20260725-010203-004",
        "Backup ordering did not use the timestamp folder.");
    Check(
        backups[0].Timestamp ==
        new DateTime(2026, 7, 25, 1, 2, 3, 4, DateTimeKind.Local),
        "The timestamp folder was not parsed correctly.");
    Check(
        backups[0].SizeBytes == 5,
        "The backup catalog reported the wrong file size.");
    Check(
        BackupCatalogService.ParseTimestamp("not-a-backup") is null,
        "An invalid backup folder was parsed as a timestamp.");
}
finally
{
    Directory.Delete(backupCatalogRoot, true);
}

bool HasRunnableDefaultWsl()
{
    try
    {
        using var probe = Process.Start(new ProcessStartInfo
        {
            FileName = "wsl.exe",
            ArgumentList = { "--exec", "true" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (probe is null || !probe.WaitForExit(10000))
        {
            probe?.Kill(entireProcessTree: true);
            return false;
        }

        return probe.ExitCode == 0;
    }
    catch (Exception exception) when (
        exception is System.ComponentModel.Win32Exception or
        InvalidOperationException)
    {
        return false;
    }
}

var service = new ConfigService();
var original = """
    model_provider = "OpenAI"
    model = "gpt-5.6-sol"
    review_model = "gpt-5.5"
    model_reasoning_effort = "max"
    approval_policy = "never"

    [model_providers.OpenAI]
    name = "OpenAI"
    wire_api = "responses"
    requires_openai_auth = true

    [desktop]
    enabled-reasoning-efforts = ["low", "medium", "high", "xhigh", "max", "ultra"]
    show-ultra-in-model-picker-slider = false

    [features]
    hooks = true
    apps = true
    plugins = true
    remote_plugin = true
    image_generation = true

    [plugins.github]
    enabled = true

    [plugin_marketplaces.personal]
    source = "https://example.invalid/codex-marketplace.json"

    [mcp_servers.sample]
    command = "sample.exe"
    """;

Check(
    !service.ParseSolUltraVisibility(original),
    "A disabled Sol Ultra visibility setting was not parsed.");
var solUltraEnabled = service.BuildSolUltraVisibilityConfig(original, true);
Check(
    service.ParseSolUltraVisibility(solUltraEnabled),
    "Sol Ultra visibility was not enabled.");
Check(
    service.ParseSolUltraAvailability(original),
    "An available Ultra reasoning effort was not detected.");
Check(
    !service.ParseSolUltraAvailability(
        original.Replace(
            ", \"ultra\"",
            string.Empty,
            StringComparison.Ordinal)),
    "Ultra was reported available when the enabled effort list omitted it.");
Check(
    service.ParseSolUltraAvailability(original) &&
    !service.ParseSolUltraVisibility(original),
    "The one-shot visibility request was incorrectly treated as the durable Ultra state.");
Check(
    solUltraEnabled.Contains(
        "enabled-reasoning-efforts = [\"low\", \"medium\", \"high\", \"xhigh\", \"max\", \"ultra\"]",
        StringComparison.Ordinal) &&
    solUltraEnabled.Contains("[mcp_servers.sample]", StringComparison.Ordinal),
    "Enabling Sol Ultra removed a sibling desktop setting or unrelated section.");
Check(
    solUltraEnabled.Split(
        ConfigService.SolUltraVisibilityKey,
        StringSplitOptions.None).Length == 2,
    "The Sol Ultra visibility assignment was duplicated.");
Check(
    service.BuildSolUltraVisibilityConfig(solUltraEnabled, true) == solUltraEnabled,
    "Enabling Sol Ultra twice was not idempotent.");
var solUltraDisabled = service.BuildSolUltraVisibilityConfig(solUltraEnabled, false);
Check(
    !service.ParseSolUltraVisibility(solUltraDisabled) &&
    solUltraDisabled.Contains(
        "show-ultra-in-model-picker-slider = false",
        StringComparison.Ordinal),
    "Sol Ultra visibility was not disabled.");

const string noDesktopConfig =
    "model = \"gpt-5.6-sol\"\r\n\r\n[features]\r\napps = true\r\n";
var addedDesktopConfig = service.BuildSolUltraVisibilityConfig(noDesktopConfig, true);
Check(
    service.ParseSolUltraVisibility(addedDesktopConfig) &&
    addedDesktopConfig.Contains("[desktop]\r\n", StringComparison.Ordinal) &&
    addedDesktopConfig.Contains("[features]\r\napps = true", StringComparison.Ordinal) &&
    !addedDesktopConfig.Replace("\r\n", string.Empty, StringComparison.Ordinal)
        .Contains('\n'),
    "A missing desktop section was not added with CRLF and unrelated settings preserved.");
Check(
    service.BuildSolUltraVisibilityConfig(addedDesktopConfig, true) == addedDesktopConfig,
    "The newly added desktop setting was not idempotent.");

var solUltraConfigRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-sol-ultra-test-{Guid.NewGuid():N}");
var solUltraConfigPath = Path.Combine(solUltraConfigRoot, "config.toml");
string? solUltraBackupFolder = null;
try
{
    Directory.CreateDirectory(solUltraConfigRoot);
    File.WriteAllText(solUltraConfigPath, original);
    solUltraBackupFolder = service.SetSolUltraVisibility(true, solUltraConfigPath);
    Check(
        solUltraBackupFolder is not null &&
        File.ReadAllText(Path.Combine(solUltraBackupFolder, "config.toml")) == original,
        "The Sol Ultra update did not create an exact pre-write backup.");
    Check(
        service.ReadSolUltraVisibility(solUltraConfigPath),
        "The Sol Ultra update did not pass read-back verification.");
    Check(
        service.SetSolUltraVisibility(true, solUltraConfigPath) is null,
        "An unchanged Sol Ultra setting created another backup or write.");

    var ultraUnavailable = original
        .Replace(
            ", \"ultra\"",
            string.Empty,
            StringComparison.Ordinal);
    File.WriteAllText(solUltraConfigPath, ultraUnavailable);
    var requestBackup = service.RequestSolUltraEnablement(solUltraConfigPath);
    Check(
        requestBackup is not null &&
        service.ReadSolUltraVisibility(solUltraConfigPath) &&
        !service.ReadSolUltraAvailability(solUltraConfigPath),
        "The one-shot Ultra enablement request was not written and verified.");
    Check(
        service.RequestSolUltraEnablement(solUltraConfigPath) is null,
        "A pending Ultra enablement request created a duplicate backup or write.");
    if (requestBackup is not null && Directory.Exists(requestBackup))
    {
        Directory.Delete(requestBackup, true);
    }

    File.WriteAllText(solUltraConfigPath, original);
    Check(
        service.RequestSolUltraEnablement(solUltraConfigPath) is null &&
        !service.ReadSolUltraVisibility(solUltraConfigPath),
        "An already available Ultra effort rewrote the one-shot request flag.");
}
finally
{
    if (Directory.Exists(solUltraConfigRoot))
    {
        Directory.Delete(solUltraConfigRoot, true);
    }
    if (solUltraBackupFolder is not null && Directory.Exists(solUltraBackupFolder))
    {
        Directory.Delete(solUltraBackupFolder, true);
    }
}

var profileCredentialTarget = CredentialTargetFactory.CreateForProfileId(
    Guid.NewGuid().ToString("N"));
Check(
    CredentialTargetFactory.IsValid(profileCredentialTarget) &&
    CredentialTargetFactory.IsValid(AppPaths.LegacySuiXiangCredentialTarget) &&
    !CredentialTargetFactory.IsValid("unmanaged:credential"),
    "Credential target validation did not enforce the managed namespace.");
var thirdParty = service.BuildThirdPartyConfig(
    original,
    "codex-auto-review",
    "https://sui-xiang.com/v1/",
    @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
    profileCredentialTarget);
var thirdPartyStatus = service.ParseStatus(thirdParty);
Check(thirdPartyStatus.Mode == ProviderMode.ThirdParty, "Third-party mode was not detected.");
Check(thirdPartyStatus.ProviderId == "OpenAI", "Stable provider ID changed.");
Check(thirdPartyStatus.Model == "codex-auto-review", "Third-party model was not written.");
Check(thirdPartyStatus.ReviewModel == "codex-auto-review", "Review model was not switched.");
Check(thirdPartyStatus.BaseUrl == "https://sui-xiang.com/v1", "Base URL was not normalized.");
Check(
    thirdPartyStatus.CredentialTarget == profileCredentialTarget &&
    thirdParty.Contains(
        $"args = [\"--credential-target\", \"{profileCredentialTarget}\"]",
        StringComparison.Ordinal),
    "The selected provider credential target was not written to config.toml.");
Check(
    thirdParty.Contains(
        "command = \"/mnt/c/Users/Test/AppData/Local/Programs/CodexProviderSwitcher/CodexProviderToken.exe\"",
        StringComparison.Ordinal),
    "Windows token broker path was not converted to WSL.");
Check(
    !thirdParty.Contains("requires_openai_auth = true", StringComparison.Ordinal),
    "Official auth remained enabled in third-party mode.");
Check(
    thirdParty.Contains("[mcp_servers.sample]", StringComparison.Ordinal),
    "Unrelated MCP configuration was removed.");
Check(
    thirdParty.Contains("[features]", StringComparison.Ordinal) &&
    thirdParty.Contains("image_generation = true", StringComparison.Ordinal),
    "Feature configuration was removed in third-party mode.");
Check(
    thirdParty.Contains("[desktop]", StringComparison.Ordinal) &&
    thirdParty.Contains(
        "show-ultra-in-model-picker-slider = false",
        StringComparison.Ordinal),
    "The Sol Ultra desktop setting was removed in third-party mode.");
Check(
    thirdParty.Contains("[plugins.github]", StringComparison.Ordinal) &&
    thirdParty.Contains("enabled = true", StringComparison.Ordinal),
    "Plugin configuration was removed in third-party mode.");
Check(
    thirdParty.Contains("[plugin_marketplaces.personal]", StringComparison.Ordinal) &&
    thirdParty.Contains(
        "source = \"https://example.invalid/codex-marketplace.json\"",
        StringComparison.Ordinal),
    "Plugin marketplace configuration was removed in third-party mode.");
Check(
    thirdParty.Split(
        "[model_providers.OpenAI]",
        StringSplitOptions.None).Length == 2,
    "Managed provider block was duplicated.");
var legacyTargetStatus = service.ParseStatus(
    thirdParty.Replace(
        $"args = [\"--credential-target\", \"{profileCredentialTarget}\"]",
        "args = []",
        StringComparison.Ordinal));
Check(
    legacyTargetStatus.CredentialTarget == AppPaths.LegacySuiXiangCredentialTarget,
    "A v1.3 empty broker argument list did not map to the legacy credential target.");
var rejectedUnmanagedCredentialTarget = false;
try
{
    _ = service.BuildThirdPartyConfig(
        original,
        "codex-auto-review",
        "https://sui-xiang.com/v1",
        @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
        "unmanaged:credential");
}
catch (ArgumentException)
{
    rejectedUnmanagedCredentialTarget = true;
}
Check(
    rejectedUnmanagedCredentialTarget,
    "Config generation accepted an unmanaged credential target.");

var official = service.BuildOfficialConfig(thirdParty, "gpt-5.6-sol", "gpt-5.5");
var officialStatus = service.ParseStatus(official);
Check(officialStatus.Mode == ProviderMode.Official, "Official mode was not detected.");
Check(officialStatus.ProviderId == "OpenAI", "Official provider ID changed.");
Check(officialStatus.Model == "gpt-5.6-sol", "Official model was not restored.");
Check(officialStatus.ReviewModel == "gpt-5.5", "Official review model was not restored.");
Check(officialStatus.BaseUrl is null, "Third-party Base URL leaked into official mode.");
Check(officialStatus.UsesOfficialAuthentication, "Official authentication was not enabled.");
Check(
    !official.Contains("[model_providers.OpenAI.auth]", StringComparison.Ordinal),
    "Third-party auth helper remained in official mode.");
Check(
    official.Contains("[features]", StringComparison.Ordinal) &&
    official.Contains("image_generation = true", StringComparison.Ordinal),
    "Feature configuration was removed after the official round trip.");
Check(
    official.Contains("[desktop]", StringComparison.Ordinal) &&
    official.Contains(
        "show-ultra-in-model-picker-slider = false",
        StringComparison.Ordinal),
    "The Sol Ultra desktop setting was removed after the official round trip.");
Check(
    official.Contains("[plugins.github]", StringComparison.Ordinal) &&
    official.Contains("enabled = true", StringComparison.Ordinal),
    "Plugin configuration was removed after the official round trip.");
Check(
    official.Contains("[plugin_marketplaces.personal]", StringComparison.Ordinal) &&
    official.Contains(
        "source = \"https://example.invalid/codex-marketplace.json\"",
        StringComparison.Ordinal),
    "Plugin marketplace configuration was removed after the official round trip.");

var officialAgain = service.BuildOfficialConfig(official, "gpt-5.6-sol", "gpt-5.5");
Check(official == officialAgain, "Official configuration rewrite is not idempotent.");

var noReview = service.BuildOfficialConfig(official, "gpt-5.6-sol", null);
var noReviewStatus = service.ParseStatus(noReview);
Check(noReviewStatus.ReviewModel is null, "Optional official review model was not removed.");

Check(
    ProviderKinds.Kimi == "kimi" &&
    AppPaths.KimiUpstreamBaseUrl == "https://sui-xiang.com/v1" &&
    AppPaths.DefaultKimiModel == "k3" &&
    AppPaths.KimiRouterBaseUrl == "http://127.0.0.1:17866/v1" &&
    AppPaths.KimiModelCatalogFileName ==
        "codex-provider-switcher-kimi-model-catalog.json" &&
    AppPaths.KimiRouterExecutableName == "CodexProviderKimiRouter.exe" &&
    AppPaths.KimiLinuxRouterDirectoryName == "linux-x64" &&
    AppPaths.KimiLinuxRouterExecutableName == "CodexProviderKimiRouter" &&
    AppPaths.KimiWslLauncherFileName == "codex-provider-kimi-launcher.sh" &&
    AppPaths.KimiAuthRefreshIntervalMilliseconds == 30000,
    "Kimi provider constants did not match the stable router contract.");

var expectedRouterHealthHandler = new ModelDiscoveryStubHttpMessageHandler(
    HttpStatusCode.OK,
    "{\"status\":\"ok\",\"service\":\"codex-provider-kimi-router\",\"upstream\":\"https://sui-xiang.com/v1\",\"pid\":1}");
using (var expectedRouterHealthClient = new HttpClient(expectedRouterHealthHandler))
using (var routerProcessService = new KimiRouterProcessService(expectedRouterHealthClient))
{
    var health = await routerProcessService.EnsureRunningAsync(
        Path.Combine(Path.GetTempPath(), "missing-kimi-router.exe"));
    Check(
        !health.Success,
        "The launcher trusted a healthy response without a verified router executable.");
}

var redirectedRouterHealthHandler = new ModelDiscoveryStubHttpMessageHandler(
    HttpStatusCode.OK,
    "{\"status\":\"ok\",\"service\":\"codex-provider-kimi-router\",\"upstream\":\"https://example.invalid/v1\",\"pid\":1}");
using (var redirectedRouterHealthClient = new HttpClient(redirectedRouterHealthHandler))
using (var routerProcessService = new KimiRouterProcessService(redirectedRouterHealthClient))
{
    var health = await routerProcessService.EnsureRunningAsync(
        Path.Combine(Path.GetTempPath(), "missing-kimi-router.exe"));
    Check(
        !health.Success,
        "The launcher trusted a same-name router that would redirect the Kimi credential.");
}

var kimiCatalogRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-kimi-catalog-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(kimiCatalogRoot);
var kimiCacheJson =
    """
    {
      "models": [
        {
          "slug": "gpt-5.6-sol",
          "display_name": "GPT-5.6-Sol",
          "description": "Sol template description",
          "default_reasoning_level": "low",
          "supported_reasoning_levels": [
            { "effort": "low", "description": "low details" },
            { "effort": "medium", "description": "medium details" },
            { "effort": "high", "description": "high details" },
            { "effort": "xhigh", "description": "xhigh details" },
            { "effort": "max", "description": "max details" },
            { "effort": "ultra", "description": "ultra details" }
          ],
          "model_messages": {
            "instructions_template": "preserve this exact instruction",
            "tool_metadata": { "kind": "preserve-me" }
          },
          "tool_mode": "code_mode_only",
          "apply_patch_tool_type": "freeform",
          "include_plugin_usage_instructions": true,
          "input_modalities": ["text", "image"],
          "supports_image_detail_original": true,
          "supports_search_tool": true,
          "web_search_tool_type": "text_and_image",
          "use_responses_lite": true,
          "multi_agent_version": "v2",
          "additional_speed_tiers": ["fast"],
          "service_tiers": [{"id":"priority","name":"Fast"}],
          "default_service_tier": "priority",
          "comp_hash": "3000",
          "context_window": 272000,
          "max_context_window": 272000
        },
        { "slug": "unrelated-model", "description": "keep out" }
      ]
    }
    """;
try
{
    var cachePath = Path.Combine(kimiCatalogRoot, "models_cache.json");
    File.WriteAllText(cachePath, kimiCacheJson);
    var catalogService = new KimiModelCatalogService();
    var kimiCatalogPath = catalogService.EnsureCatalog(
        "k3",
        kimiCatalogRoot);
    Check(
        kimiCatalogPath == Path.Combine(
            kimiCatalogRoot,
            AppPaths.KimiModelCatalogFileName) &&
        File.Exists(kimiCatalogPath),
        "The managed Kimi catalog was not atomically created at the expected path.");

    using var kimiCatalogDocument = JsonDocument.Parse(
        File.ReadAllText(kimiCatalogPath));
    var kimiCatalogDocumentRoot = kimiCatalogDocument.RootElement;
    var kimiCatalogModels = kimiCatalogDocumentRoot.GetProperty("models");
    var kimiCatalogModel = kimiCatalogModels[0];
    Check(
        kimiCatalogModels.GetArrayLength() == 1 &&
        kimiCatalogModel.GetProperty("slug").GetString() == "k3" &&
        kimiCatalogModel.GetProperty("display_name").GetString() == "k3" &&
        kimiCatalogModel.GetProperty("description").GetString()?.Contains(
            "SuiXiang",
            StringComparison.OrdinalIgnoreCase) == true &&
        kimiCatalogModel.GetProperty("default_reasoning_level").GetString() == "max" &&
        kimiCatalogModel.GetProperty("context_window").GetInt32() == 1048576 &&
        kimiCatalogModel.GetProperty("max_context_window").GetInt32() == 1048576,
        "The Kimi catalog did not override model identity and K3 context safely.");
    var kimiEfforts = kimiCatalogModel
        .GetProperty("supported_reasoning_levels")
        .EnumerateArray()
        .Select(level => level.GetProperty("effort").GetString())
        .ToArray();
    Check(
        kimiEfforts.SequenceEqual(new[] { "low", "high", "max" }) &&
        kimiCatalogModel.GetProperty("model_messages")
            .GetProperty("instructions_template").GetString() ==
            "preserve this exact instruction" &&
        kimiCatalogModel.GetProperty("model_messages")
            .GetProperty("tool_metadata").GetProperty("kind").GetString() ==
            "preserve-me",
        "The Kimi catalog did not preserve Sol instruction/tool metadata or reasoning levels.");
    Check(
        kimiCatalogModel.GetProperty("tool_mode").GetString() == "direct" &&
        kimiCatalogModel.GetProperty("apply_patch_tool_type").GetString() == "freeform" &&
        !kimiCatalogModel.GetProperty("include_plugin_usage_instructions").GetBoolean() &&
        !kimiCatalogModel.GetProperty("include_apps_usage_instructions").GetBoolean() &&
        kimiCatalogModel.GetProperty("input_modalities").ValueKind == JsonValueKind.Array &&
        kimiCatalogModel.GetProperty("input_modalities").GetArrayLength() == 1 &&
        kimiCatalogModel.GetProperty("input_modalities")[0].GetString() == "text" &&
        !kimiCatalogModel.GetProperty("supports_image_detail_original").GetBoolean() &&
        !kimiCatalogModel.GetProperty("supports_search_tool").GetBoolean() &&
        kimiCatalogModel.GetProperty("web_search_tool_type").GetString() == "text" &&
        !kimiCatalogModel.GetProperty("use_responses_lite").GetBoolean() &&
        !kimiCatalogModel.TryGetProperty("multi_agent_version", out _) &&
        !kimiCatalogModel.TryGetProperty("additional_speed_tiers", out _) &&
        !kimiCatalogModel.TryGetProperty("service_tiers", out _) &&
        !kimiCatalogModel.TryGetProperty("default_service_tier", out _) &&
        !kimiCatalogModel.TryGetProperty("comp_hash", out _),
        "The Kimi catalog advertised unsupported native tools or modalities.");

    using var kimiK3Document = JsonDocument.Parse(
        KimiModelCatalogService.BuildCatalog(kimiCacheJson, "k3"));
    Check(
        kimiK3Document.RootElement.GetProperty("models")[0]
            .GetProperty("context_window").GetInt32() == 1048576,
        "The Kimi K3 catalog did not receive its 1M context window.");
    var nonK3Rejected = false;
    try
    {
        _ = KimiModelCatalogService.BuildCatalog(kimiCacheJson, "kimi-k2.7-code");
    }
    catch (ArgumentException)
    {
        nonK3Rejected = true;
    }
    Check(nonK3Rejected, "The first Kimi route accepted a non-K3 model.");

    var invalidCatalogPreserved = Path.Combine(
        kimiCatalogRoot,
        AppPaths.KimiModelCatalogFileName);
    const string existingCatalogFixture = "{\"models\":[{\"slug\":\"existing\"}]}";
    File.WriteAllText(invalidCatalogPreserved, existingCatalogFixture);
    var invalidCatalogRejected = false;
    try
    {
        _ = catalogService.EnsureCatalog("k3", kimiCatalogRoot + "-missing-cache");
    }
    catch (FileNotFoundException)
    {
        invalidCatalogRejected = true;
    }
    Check(invalidCatalogRejected, "A missing Kimi models cache was not rejected safely.");
    var malformedCacheRejected = false;
    File.WriteAllText(cachePath, "{not-json");
    try
    {
        _ = catalogService.EnsureCatalog("k3", kimiCatalogRoot);
    }
    catch (InvalidDataException)
    {
        malformedCacheRejected = true;
    }
    Check(
        malformedCacheRejected &&
        File.ReadAllText(invalidCatalogPreserved) == existingCatalogFixture,
        "An invalid Kimi models cache did not fail before changing the managed catalog.");
}
finally
{
    if (Directory.Exists(kimiCatalogRoot))
    {
        Directory.Delete(kimiCatalogRoot, true);
    }
}

var kimiConfig = service.BuildKimiConfig(
    original,
    AppPaths.DefaultKimiModel,
    @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
    profileCredentialTarget);
var kimiStatus = service.ParseStatus(kimiConfig);
Check(
    kimiStatus.Mode == ProviderMode.ThirdParty &&
    kimiStatus.BaseUrl == AppPaths.KimiRouterBaseUrl &&
    kimiStatus.Model == AppPaths.DefaultKimiModel &&
    kimiStatus.ReviewModel == AppPaths.DefaultKimiModel &&
    kimiStatus.ModelCatalogJson == AppPaths.KimiModelCatalogFileName &&
    kimiStatus.CredentialTarget == profileCredentialTarget &&
    kimiConfig.Contains(
        $"model_catalog_json = \"{AppPaths.KimiModelCatalogFileName}\"",
        StringComparison.Ordinal) &&
    kimiConfig.Contains(
        "command = \"/bin/sh\"",
        StringComparison.Ordinal) &&
    kimiConfig.Contains(
        "/CodexProviderSwitcher/codex-provider-kimi-launcher.sh",
        StringComparison.Ordinal) &&
    kimiConfig.Contains(
        $"refresh_interval_ms = {AppPaths.KimiAuthRefreshIntervalMilliseconds}",
        StringComparison.Ordinal) &&
    !kimiConfig.Contains("--ensure-kimi-router", StringComparison.Ordinal),
    "Kimi config generation did not route through the managed loopback catalog.");
Check(
    service.BuildKimiConfig(
        kimiConfig,
        AppPaths.DefaultKimiModel,
        @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
        profileCredentialTarget) == kimiConfig,
    "Kimi config generation was not idempotent.");

var userCatalogConfig =
    "model_catalog_json = \"user-owned-catalog.json\"\n" + original;
var preservedOfficialCatalog = service.BuildOfficialConfig(
    userCatalogConfig,
    "gpt-5.6-sol",
    "gpt-5.5");
var preservedThirdPartyCatalog = service.BuildThirdPartyConfig(
    userCatalogConfig,
    "custom-model",
    "https://provider.example/v1",
    @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
    profileCredentialTarget);
Check(
    preservedOfficialCatalog.Contains(
        "model_catalog_json = \"user-owned-catalog.json\"",
        StringComparison.Ordinal) &&
    preservedThirdPartyCatalog.Contains(
        "model_catalog_json = \"user-owned-catalog.json\"",
        StringComparison.Ordinal),
    "Official or direct third-party rewrites overwrote a user-owned model catalog.");
var kimiCatalogConflictRejected = false;
try
{
    _ = service.BuildKimiConfig(
        userCatalogConfig,
        AppPaths.DefaultKimiModel,
        @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
        profileCredentialTarget);
}
catch (InvalidOperationException)
{
    kimiCatalogConflictRejected = true;
}
Check(kimiCatalogConflictRejected, "Kimi config overwrote a user-owned model catalog.");
Check(
    !service.BuildOfficialConfig(kimiConfig, "gpt-5.6-sol", "gpt-5.5")
        .Contains(
            $"model_catalog_json = \"{AppPaths.KimiModelCatalogFileName}\"",
            StringComparison.Ordinal),
    "Official config did not remove the managed Kimi catalog assignment.");

var kimiWorkflowRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-kimi-workflow-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(kimiWorkflowRoot);
var kimiWorkflowConfigPath = Path.Combine(kimiWorkflowRoot, "config.toml");
File.WriteAllText(kimiWorkflowConfigPath, original);
File.WriteAllText(Path.Combine(kimiWorkflowRoot, "models_cache.json"), kimiCacheJson);
string? kimiWorkflowBackup = null;
try
{
    var kimiWorkflow = new ProviderSwitchWorkflowService(service);
    var kimiSwitch = kimiWorkflow.SwitchToKimi(
        new KimiSwitchRequest(
            AppPaths.DefaultKimiModel,
            @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
            profileCredentialTarget),
        kimiWorkflowConfigPath,
        kimiWorkflowRoot);
    kimiWorkflowBackup = kimiSwitch.BackupFolder;
    Check(
        kimiSwitch.VerifiedStatus.ModelCatalogJson ==
            AppPaths.KimiModelCatalogFileName &&
        service.ParseStatus(File.ReadAllText(kimiWorkflowConfigPath)).BaseUrl ==
            AppPaths.KimiRouterBaseUrl,
        "Kimi workflow did not verify the routed config after its atomic write.");

    File.WriteAllText(
        kimiWorkflowConfigPath,
        userCatalogConfig);
    var beforeConflict = File.ReadAllText(kimiWorkflowConfigPath);
    var beforeConflictCatalog = File.ReadAllBytes(
        Path.Combine(kimiWorkflowRoot, AppPaths.KimiModelCatalogFileName));
    var workflowConflictRejected = false;
    try
    {
        _ = kimiWorkflow.SwitchToKimi(
            new KimiSwitchRequest(
                "K3",
                @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe",
                profileCredentialTarget),
            kimiWorkflowConfigPath,
            kimiWorkflowRoot);
    }
    catch (InvalidOperationException)
    {
        workflowConflictRejected = true;
    }
    Check(
        workflowConflictRejected &&
        File.ReadAllText(kimiWorkflowConfigPath) == beforeConflict &&
        File.ReadAllBytes(Path.Combine(
            kimiWorkflowRoot,
            AppPaths.KimiModelCatalogFileName)).SequenceEqual(beforeConflictCatalog),
        "A Kimi catalog conflict did not restore both config and the previous catalog.");
}
finally
{
    if (kimiWorkflowBackup is not null && Directory.Exists(kimiWorkflowBackup))
    {
        Directory.Delete(kimiWorkflowBackup, true);
    }
    if (Directory.Exists(kimiWorkflowRoot))
    {
        Directory.Delete(kimiWorkflowRoot, true);
    }
}

var kimiSettingsRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-kimi-settings-test-{Guid.NewGuid():N}");
Directory.CreateDirectory(kimiSettingsRoot);
try
{
    var kimiSettingsPath = Path.Combine(kimiSettingsRoot, "settings.json");
    var kimiProfile = new ProviderProfile
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = ProviderKinds.Kimi,
        BaseUrl = AppPaths.KimiUpstreamBaseUrl,
        Model = AppPaths.DefaultKimiModel,
        CredentialTarget = profileCredentialTarget
    };
    var kimiSettings = new SwitcherSettings
    {
        OnboardingCompleted = true,
        ProviderProfiles = [kimiProfile],
        ActiveProviderProfileId = kimiProfile.Id
    };
    File.WriteAllText(kimiSettingsPath, JsonSerializer.Serialize(kimiSettings));
    var kimiLoaded = new SettingsStore(kimiSettingsPath).Load(
        kimiStatus);
    Check(
        kimiLoaded.ActiveProviderProfile?.Kind == ProviderKinds.Kimi &&
        kimiLoaded.ActiveProviderProfile.BaseUrl == AppPaths.KimiUpstreamBaseUrl &&
        kimiLoaded.ActiveProviderProfile.CredentialTarget == profileCredentialTarget,
        "Settings sync replaced a Kimi upstream profile URL with the loopback route.");

    var inferredSettingsPath = Path.Combine(kimiSettingsRoot, "inferred.json");
    File.WriteAllText(
        inferredSettingsPath,
        "{\"ThirdPartyBaseUrl\":\"https://sui-xiang.com/v1\",\"ThirdPartyModel\":\"k3\"}");
    var inferred = new SettingsStore(inferredSettingsPath).Load(
        new ConfigStatus(
            ProviderMode.Unknown,
            string.Empty,
            null,
            null,
            null,
            false));
    Check(
        inferred.ActiveProviderProfile?.Kind == ProviderKinds.SuiXiang &&
        inferred.ActiveProviderProfile.BaseUrl == AppPaths.KimiUpstreamBaseUrl,
        "Legacy settings inferred the K3 bridge without an explicit Kimi profile kind.");
    Check(
        SettingsStore.IsKimiBaseUrl(AppPaths.KimiUpstreamBaseUrl) &&
        !SettingsStore.IsKimiBaseUrl("https://sui-xiang.com.evil.example/v1") &&
        !SettingsStore.IsKimiBaseUrl("http://sui-xiang.com/v1") &&
        !SettingsStore.IsKimiBaseUrl("https://sui-xiang.com:8443/v1") &&
        !SettingsStore.IsKimiBaseUrl("https://sui-xiang.com/v1/other") &&
        !SettingsStore.IsKimiBaseUrl("https://sui-xiang.com/v1?target=other") &&
        SettingsStore.IsKimiLoopbackBaseUrl(AppPaths.KimiRouterBaseUrl),
        "Kimi URL recognition accepted a deceptive host or rejected loopback routing.");
}
finally
{
    if (Directory.Exists(kimiSettingsRoot))
    {
        Directory.Delete(kimiSettingsRoot, true);
    }
}

var completedStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.created","response":{"id":"resp_self_test","status":"in_progress"}}

    data: {"type":"response.output_text.delta","delta":"PLUGIN_"}

    data: {"type":"response.output_text.delta","delta":"OK"}

    data: {"type":"response.completed","response":{"id":"resp_self_test","status":"completed"}}

    data: [DONE]

    """);
Check(completedStream.SawJsonEvent, "Completed SSE did not report any JSON events.");
Check(completedStream.Completed, "Completed SSE was not classified as completed.");
Check(!completedStream.Failed, "Completed SSE was incorrectly classified as failed.");
Check(completedStream.OutputText == "PLUGIN_OK", "Completed SSE output text was assembled incorrectly.");
Check(completedStream.ResponseId == "resp_self_test", "Completed SSE response ID was not extracted.");

var truncatedStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.output_text.delta","delta":"partial"}

    """);
Check(truncatedStream.SawJsonEvent, "Truncated SSE did not report its JSON event.");
Check(!truncatedStream.Completed, "Truncated SSE was incorrectly classified as completed.");
Check(!truncatedStream.Failed, "A valid but truncated SSE event was incorrectly classified as failed.");

var incompleteStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.incomplete","response":{"id":"resp_incomplete","status":"incomplete"}}

    """);
Check(incompleteStream.SawJsonEvent, "Incomplete SSE did not report its JSON event.");
Check(!incompleteStream.Completed, "Incomplete SSE was incorrectly classified as completed.");
Check(incompleteStream.Failed, "Explicit response.incomplete SSE was not classified as failed.");

var failedStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.failed","response":{"id":"resp_failed","status":"failed","error":{"code":"upstream_http_error","message":"The upstream returned HTTP 503."}}}

    """);
Check(failedStream.Failed, "Explicit response.failed SSE was not classified as failed.");
Check(
    failedStream.Error == "The upstream returned HTTP 503.",
    "Nested response.failed error details were not preserved.");

var topLevelFailedStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.failed","code":"upstream_timeout","message":"The upstream timed out."}

    """);
Check(
    topLevelFailedStream.Error == "The upstream timed out.",
    "Top-level response.failed error details were not preserved.");

var untypedErrorStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"error":{"message":"","code":"upstream_overloaded"}}

    """);
Check(untypedErrorStream.Failed, "An untyped top-level error object was not classified as failed.");
Check(
    untypedErrorStream.Error == "upstream_overloaded",
    "A blank error message did not fall back to the provider error code.");

var functionCallStream = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.output_item.added","output_index":0,"item":{"id":"fc_self_test","type":"function_call","call_id":"call_self_test","name":"provider_probe","arguments":""}}

    data: {"type":"response.function_call_arguments.delta","item_id":"fc_self_test","output_index":0,"delta":"{\"value\":"}

    data: {"type":"response.function_call_arguments.delta","item_id":"fc_self_test","output_index":0,"delta":"\"PLUGIN_OK\"}"}

    data: {"type":"response.function_call_arguments.done","item_id":"fc_self_test","output_index":0,"arguments":"{\"value\":\"PLUGIN_OK\"}"}

    data: {"type":"response.output_item.done","output_index":0,"item":{"id":"fc_self_test","type":"function_call","call_id":"call_self_test","name":"provider_probe","arguments":"{\"value\":\"PLUGIN_OK\"}"}}

    data: {"type":"response.completed","response":{"id":"resp_function","status":"completed","output":[{"id":"rs_self_test","type":"reasoning","summary":[]},{"id":"fc_self_test","type":"function_call","call_id":"call_self_test","name":"provider_probe","arguments":"{\"value\":\"PLUGIN_OK\"}"}]}}

    """);
Check(functionCallStream.Completed, "Function-call SSE was not classified as completed.");
Check(functionCallStream.FunctionCallId == "call_self_test", "Function call ID was not extracted.");
Check(functionCallStream.FunctionName == "provider_probe", "Function name was not extracted.");
Check(
    functionCallStream.FunctionArguments == """{"value":"PLUGIN_OK"}""",
    "Function arguments were not extracted.");
Check(
    functionCallStream.OutputItemsJson?.Contains(
        "\"type\":\"reasoning\"",
        StringComparison.Ordinal) == true,
    "Complete reasoning output items were not retained for tool-result replay.");

var streamedOutputFallback = ConnectionTestService.AnalyzeSse(
    """
    data: {"type":"response.output_item.done","output_index":0,"item":{"id":"rs_streamed","type":"reasoning","encrypted_content":"opaque"}}

    data: {"type":"response.output_item.done","output_index":1,"item":{"id":"fc_streamed","type":"function_call","call_id":"call_streamed","name":"capability_probe","arguments":"{\"value\":\"ready\"}"}}

    data: {"type":"response.completed","response":{"id":"resp_streamed","status":"completed","output":[]}}

    """);
Check(
    streamedOutputFallback.OutputItemsJson?.Contains(
        "\"call_id\":\"call_streamed\"",
        StringComparison.Ordinal) == true,
    "An empty completed.output array discarded streamed output_item.done items.");
Check(
    streamedOutputFallback.OutputItemsJson?.Contains(
        "\"encrypted_content\":\"opaque\"",
        StringComparison.Ordinal) == true,
    "Streamed reasoning content was not retained for stateless replay.");

var expectedImageBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x01, 0x02 };
var encodedImage = Convert.ToBase64String(expectedImageBytes);
var imageApiImage = ConnectionTestService.ExtractImageApiBytes(
    $$"""{"data":[{"b64_json":"{{encodedImage}}"}]}""");
Check(
    imageApiImage is not null && imageApiImage.SequenceEqual(expectedImageBytes),
    "Images API JSON was not decoded.");
Check(
    ConnectionTestService.ExtractImageApiBytes(
        """{"data":[{"b64_json":"not-base64"}]}""") is null,
    "Invalid Images API base64 was incorrectly accepted.");
var validOnePixelPng = Convert.FromBase64String(
    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
Check(
    ConnectionTestService.IsDecodableImage(validOnePixelPng),
    "A valid PNG was not fully decoded.");
Check(
    !ConnectionTestService.IsDecodableImage(
        validOnePixelPng[..(validOnePixelPng.Length / 2)]),
    "A truncated PNG was incorrectly accepted as decodable.");

Check(
    HostCapabilityDiagnosticsService.ParseChatGptLoginStatus(
        "\u001b[32mLogged in using ChatGPT\u001b[0m") == true,
    "ChatGPT login status was not detected through ANSI formatting.");
Check(
    HostCapabilityDiagnosticsService.ParseChatGptLoginStatus("Not logged in") == false,
    "Logged-out status was not detected.");
Check(
    HostCapabilityDiagnosticsService.ParseChatGptLoginStatus("Logged in using API key") == false,
    "A non-ChatGPT login method was incorrectly accepted as ChatGPT login.");
Check(
    HostCapabilityDiagnosticsService.ParseChatGptLoginStatus("unexpected output") is null,
    "Unknown login output was not classified as unknown.");

var hostFeatures = HostCapabilityDiagnosticsService.ParseFeatureList(
    """
    apps                  stable        true
    plugins               experimental  true
    remote_plugin         experimental  false
    image_generation      stable        true
    unrelated_feature     stable        true
    """);
Check(hostFeatures.AppsEnabled == true, "Apps feature state was not parsed.");
Check(hostFeatures.PluginsEnabled == true, "Plugins feature state was not parsed.");
Check(hostFeatures.RemotePluginEnabled == false, "Remote plugin feature state was not parsed.");
Check(hostFeatures.ImageGenerationEnabled == true, "Image generation feature state was not parsed.");
var partialHostFeatures = HostCapabilityDiagnosticsService.ParseFeatureList("apps true");
Check(partialHostFeatures.AppsEnabled == true, "Two-column feature output was not parsed.");
Check(
    partialHostFeatures.PluginsEnabled is null &&
    partialHostFeatures.RemotePluginEnabled is null &&
    partialHostFeatures.ImageGenerationEnabled is null,
    "Missing feature states were not left unknown.");

var cliOverrideRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-cli-override-{Guid.NewGuid():N}");
Directory.CreateDirectory(cliOverrideRoot);
try
{
    var cliOverridePath = Path.Combine(cliOverrideRoot, "codex.exe");
    File.WriteAllBytes(cliOverridePath, []);
    Check(
        HostCapabilityDiagnosticsService.FindCodexCliPath(cliOverridePath, null) ==
        Path.GetFullPath(cliOverridePath),
        "Explicit CODEX_CLI_PATH file override was not resolved.");
    Check(
        HostCapabilityDiagnosticsService.FindCodexCliPath(cliOverrideRoot, null) ==
        Path.GetFullPath(cliOverridePath),
        "CODEX_CLI_PATH directory override was not resolved.");

    var invalidOverridePath = Path.Combine(cliOverrideRoot, "codex.cmd");
    File.WriteAllBytes(invalidOverridePath, []);
    Check(
        HostCapabilityDiagnosticsService.FindCodexCliPath(invalidOverridePath, null) is null,
        "A non-executable CLI override extension was incorrectly accepted.");
}
finally
{
    Directory.Delete(cliOverrideRoot, true);
}

var temporaryCodexHome = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-session-test-{Guid.NewGuid():N}");
var previousCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
try
{
    var sessions = Path.Combine(temporaryCodexHome, "sessions", "2026", "07", "24");
    Directory.CreateDirectory(sessions);
    File.WriteAllText(
        Path.Combine(sessions, "valid.jsonl"),
        """{"type":"session_meta","payload":{"model_provider":"OpenAI"}}""" + Environment.NewLine);
    File.WriteAllText(Path.Combine(sessions, "empty.jsonl"), string.Empty);
    Environment.SetEnvironmentVariable("CODEX_HOME", temporaryCodexHome);

    var health = new SessionHealthService().Inspect();
    Check(health.TotalFiles == 2, "Session health total count is wrong.");
    Check(health.StableProviderFiles == 1, "Stable session count is wrong.");
    Check(health.EmptyPlaceholderFiles == 1, "Empty placeholder was not classified.");
    Check(health.UnreadableFiles == 0, "Empty placeholder was incorrectly marked unreadable.");
}
finally
{
    Environment.SetEnvironmentVariable("CODEX_HOME", previousCodexHome);
    Directory.Delete(temporaryCodexHome, true);
}

var lunaWorkerRoot = Path.Combine(
    Path.GetTempPath(),
    $"codex-provider-switcher-luna-worker-test-{Guid.NewGuid():N}");
var previousLunaWorkerCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
try
{
    Environment.SetEnvironmentVariable("CODEX_HOME", lunaWorkerRoot);
    var lunaWorkerService = new LunaWorkerAgentService();
    var missingLunaWorker = lunaWorkerService.Inspect();
    Check(
        missingLunaWorker.State == ManagedAgentState.Missing &&
        missingLunaWorker.Path == Path.Combine(
            lunaWorkerRoot,
            "agents",
            LunaWorkerAgentService.AgentFileName),
        "A missing Luna worker agent was not detected under CODEX_HOME.");

    var configPath = Path.Combine(lunaWorkerRoot, "config.toml");
    const string configFixture = "model = \"preserve-me\"\n";
    Directory.CreateDirectory(lunaWorkerRoot);
    File.WriteAllText(configPath, configFixture);
    var unrelatedAgentPath = Path.Combine(
        lunaWorkerRoot,
        "agents",
        "unrelated.toml");
    Directory.CreateDirectory(Path.GetDirectoryName(unrelatedAgentPath)!);
    const string unrelatedAgentFixture = "name = \"keep-me\"\n";
    File.WriteAllText(unrelatedAgentPath, unrelatedAgentFixture);

    var installedLunaWorker = lunaWorkerService.Install();
    var lunaWorkerPath = installedLunaWorker.Path;
    Check(
        installedLunaWorker.State == ManagedAgentState.Installed &&
        File.ReadAllText(lunaWorkerPath) == LunaWorkerAgentService.Template,
        "Installing the missing Luna worker did not write the exact template.");
    Check(
        File.ReadAllText(configPath) == configFixture,
        "Installing the Luna worker changed config.toml.");
    Check(
        File.ReadAllText(unrelatedAgentPath) == unrelatedAgentFixture,
        "Installing the Luna worker changed an unrelated agent file.");
    Check(
        LunaWorkerAgentService.Template.Contains(
            "name = \"luna_worker\"",
            StringComparison.Ordinal) &&
        LunaWorkerAgentService.Template.Contains(
            "description = \"Preferred agent for well-scoped",
            StringComparison.Ordinal) &&
        LunaWorkerAgentService.Template.Contains(
            "model = \"gpt-5.6-luna\"",
            StringComparison.Ordinal) &&
        LunaWorkerAgentService.Template.Contains(
            "model_reasoning_effort = \"max\"",
            StringComparison.Ordinal) &&
        !LunaWorkerAgentService.Template.Contains(
            "ultra",
            StringComparison.OrdinalIgnoreCase) &&
        LunaWorkerAgentService.Template.Contains(
            "developer_instructions = \"\"\"",
            StringComparison.Ordinal),
        "The Luna worker template is missing a required agent field.");

    var lineEndingVariant = LunaWorkerAgentService.Template
        .Replace("\n", "\r\n", StringComparison.Ordinal)
        .TrimEnd('\r', '\n');
    File.WriteAllText(lunaWorkerPath, lineEndingVariant);
    Check(
        lunaWorkerService.Inspect().State == ManagedAgentState.Installed,
        "Equivalent Luna worker line endings or final newline were not accepted.");
    var normalizedExistingContent = File.ReadAllText(lunaWorkerPath);
    var idempotentInstall = lunaWorkerService.Install();
    Check(
        idempotentInstall.State == ManagedAgentState.Installed &&
        File.ReadAllText(lunaWorkerPath) == normalizedExistingContent,
        "Reinstalling an existing Luna worker was not idempotent.");

    var officialLunaRoute = new ConfigStatus(
        ProviderMode.Official,
        AppPaths.StableProviderId,
        LunaWorkerAgentService.AgentModel,
        null,
        null,
        true);
    var suiXiangLunaRoute = new ConfigStatus(
        ProviderMode.ThirdParty,
        AppPaths.StableProviderId,
        LunaWorkerAgentService.AgentModel,
        null,
        "https://sui-xiang.com/v1",
        false);
    var customLunaRoute = new ConfigStatus(
        ProviderMode.ThirdParty,
        AppPaths.StableProviderId,
        LunaWorkerAgentService.AgentModel,
        null,
        "https://api.example.com/v1",
        false);
    var deceptiveLunaRoute = customLunaRoute with
    {
        BaseUrl = "https://sui-xiang.com.evil.example/v1"
    };
    Check(
        lunaWorkerService.Reconcile(customLunaRoute).State ==
        ManagedAgentState.Installed &&
        lunaWorkerService.Reconcile(deceptiveLunaRoute).State ==
        ManagedAgentState.Installed &&
        !LunaWorkerAgentService.IsSuiXiangRoute(deceptiveLunaRoute),
        "A custom provider or deceptive host was incorrectly classified as SuiXiang.");

    const string disabledPathConflict = "name = \"user-owned-disabled-file\"\n";
    File.WriteAllText(AppPaths.DisabledLunaWorkerAgentPath, disabledPathConflict);
    Check(
        lunaWorkerService.Reconcile(suiXiangLunaRoute).State ==
        ManagedAgentState.Conflict &&
        File.ReadAllText(lunaWorkerPath) == normalizedExistingContent &&
        File.ReadAllText(AppPaths.DisabledLunaWorkerAgentPath) ==
        disabledPathConflict,
        "A disabled-path collision overwrote a file or hid the active Luna worker state.");
    File.Delete(AppPaths.DisabledLunaWorkerAgentPath);

    var disabledLunaWorker =
        lunaWorkerService.Reconcile(suiXiangLunaRoute);
    Check(
        disabledLunaWorker.State == ManagedAgentState.Disabled &&
        disabledLunaWorker.Path == AppPaths.DisabledLunaWorkerAgentPath &&
        !File.Exists(lunaWorkerPath) &&
        File.ReadAllText(AppPaths.DisabledLunaWorkerAgentPath) ==
        normalizedExistingContent,
        "Switching to SuiXiang did not park the managed Luna worker.");
    Check(
        lunaWorkerService.Reconcile(suiXiangLunaRoute).State ==
        ManagedAgentState.Disabled,
        "Parking the managed Luna worker on SuiXiang was not idempotent.");
    Check(
        lunaWorkerService.Install().State == ManagedAgentState.Disabled &&
        !File.Exists(lunaWorkerPath),
        "Installing while the managed Luna worker was parked re-enabled it.");

    var restoredOnCustomProvider =
        lunaWorkerService.Reconcile(customLunaRoute);
    Check(
        restoredOnCustomProvider.State == ManagedAgentState.Installed &&
        restoredOnCustomProvider.Path == lunaWorkerPath &&
        File.Exists(lunaWorkerPath) &&
        !File.Exists(AppPaths.DisabledLunaWorkerAgentPath),
        "Leaving SuiXiang for a custom provider did not restore the managed Luna worker.");
    Check(
        lunaWorkerService.Reconcile(suiXiangLunaRoute).State ==
        ManagedAgentState.Disabled,
        "Returning to SuiXiang did not park the managed Luna worker again.");
    var restoredLunaWorker = lunaWorkerService.Reconcile(officialLunaRoute);
    Check(
        restoredLunaWorker.State == ManagedAgentState.Installed &&
        lunaWorkerService.Reconcile(officialLunaRoute).State ==
        ManagedAgentState.Installed,
        "Restoring the managed Luna worker was not idempotent.");

    const string conflictingLunaWorker = "name = \"user-owned-luna\"\n";
    File.WriteAllText(lunaWorkerPath, conflictingLunaWorker);
    Check(
        lunaWorkerService.Inspect().State == ManagedAgentState.Conflict,
        "A user-owned Luna worker file was not detected as a conflict.");
    var conflictInstall = lunaWorkerService.Install();
    Check(
        conflictInstall.State == ManagedAgentState.Conflict,
        "Installing a conflicting Luna worker did not return the conflict state.");
    Check(
        File.ReadAllText(lunaWorkerPath) == conflictingLunaWorker,
        "A conflicting Luna worker file was modified during install.");
    Check(
        lunaWorkerService.Reconcile(suiXiangLunaRoute).State ==
        ManagedAgentState.Conflict &&
        File.ReadAllText(lunaWorkerPath) == conflictingLunaWorker,
        "A user-owned Luna worker was modified when entering a third-party route.");
}
finally
{
    Environment.SetEnvironmentVariable("CODEX_HOME", previousLunaWorkerCodexHome);
    if (Directory.Exists(lunaWorkerRoot))
    {
        Directory.Delete(lunaWorkerRoot, true);
    }
}

var testTarget = CredentialTargetFactory.CreateForProfileId(Guid.NewGuid().ToString("N"));
var testSecret = $"test-{Guid.NewGuid():N}";
try
{
    CredentialVault.Write(testTarget, testSecret);
    Check(CredentialVault.Read(testTarget) == testSecret, "Windows Credential Manager round trip failed.");

    if (args.Length >= 1 && File.Exists(args[0]))
    {
        using var broker = Process.Start(new ProcessStartInfo
        {
            FileName = args[0],
            ArgumentList = { "--credential-target", testTarget },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (broker is null)
        {
            failures.Add("Token broker did not start.");
        }
        else
        {
            var output = broker.StandardOutput.ReadToEnd();
            broker.WaitForExit();
            Check(broker.ExitCode == 0, "Token broker returned a non-zero exit code.");
            Check(output == testSecret, "Token broker returned the wrong credential.");
        }

        using var invalidBroker = Process.Start(new ProcessStartInfo
        {
            FileName = args[0],
            ArgumentList = { "--credential-target", "unmanaged:credential" },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (invalidBroker is null)
        {
            failures.Add("Token broker invalid-target check did not start.");
        }
        else
        {
            invalidBroker.WaitForExit();
            Check(
                invalidBroker.ExitCode == 3,
                "Token broker accepted an unmanaged credential target.");
        }

        var missingTarget = CredentialTargetFactory.CreateForProfileId(
            Guid.NewGuid().ToString("N"));
        CredentialVault.Delete(missingTarget);
        using var missingBroker = Process.Start(new ProcessStartInfo
        {
            FileName = args[0],
            ArgumentList = { "--credential-target", missingTarget },
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        if (missingBroker is null)
        {
            failures.Add("Token broker missing-credential check did not start.");
        }
        else
        {
            var missingOutput = missingBroker.StandardOutput.ReadToEnd();
            missingBroker.WaitForExit();
            Check(
                missingBroker.ExitCode == 2,
                "Token broker did not report a missing managed credential.");
            Check(
                string.IsNullOrEmpty(missingOutput),
                "Token broker wrote output for a missing credential.");
        }

        if (HasRunnableDefaultWsl())
        {
            var brokerWslPath = ConfigService.ToWslPath(args[0]);
            var wslStartInfo = new ProcessStartInfo
            {
                FileName = "wsl.exe",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            wslStartInfo.ArgumentList.Add("--");
            wslStartInfo.ArgumentList.Add(brokerWslPath);
            wslStartInfo.ArgumentList.Add("--credential-target");
            wslStartInfo.ArgumentList.Add(testTarget);

            using var wslBroker = Process.Start(wslStartInfo);
            if (wslBroker is null)
            {
                failures.Add("WSL token broker did not start.");
            }
            else
            {
                var wslOutput = wslBroker.StandardOutput.ReadToEnd();
                wslBroker.WaitForExit();
                Check(wslBroker.ExitCode == 0, "WSL token broker returned a non-zero exit code.");
                Check(wslOutput == testSecret, "WSL token broker returned the wrong credential.");
            }
        }
        else
        {
            Console.WriteLine(
                "WSL token-broker integration test skipped: no runnable default distribution.");
        }
    }
}
finally
{
    CredentialVault.Delete(testTarget);
}

if (args.Length >= 3 && File.Exists(args[1]) && File.Exists(args[2]))
{
    var liveConfig = File.ReadAllText(args[1]);
    var liveThirdParty = service.BuildThirdPartyConfig(
        liveConfig,
        "codex-auto-review",
        "https://sui-xiang.com/v1",
        args[0]);
    var liveOfficial = service.BuildOfficialConfig(
        liveThirdParty,
        "gpt-5.6-sol",
        "gpt-5.5");

    Check(
        service.ParseStatus(liveThirdParty).Mode == ProviderMode.ThirdParty,
        "The real config fixture did not switch to third-party mode.");
    Check(
        service.ParseStatus(liveOfficial).Mode == ProviderMode.Official,
        "The real config fixture did not switch back to official mode.");

    foreach (var line in liveConfig.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) ||
            !trimmed.EndsWith("]", StringComparison.Ordinal) ||
            trimmed.Contains("model_providers.OpenAI", StringComparison.Ordinal))
        {
            continue;
        }

        Check(
            liveThirdParty.Contains(trimmed, StringComparison.Ordinal),
            $"An unrelated TOML section disappeared: {trimmed}");
        Check(
            liveOfficial.Contains(trimmed, StringComparison.Ordinal),
            $"An unrelated TOML section disappeared after round trip: {trimmed}");
    }

    var validationRoot = Path.Combine(
        Path.GetTempPath(),
        $"codex-provider-switcher-validation-{Guid.NewGuid():N}");
    Directory.CreateDirectory(validationRoot);
    try
    {
        foreach (var fixture in new[]
                 {
                     ("third-party", liveThirdParty),
                     ("official", liveOfficial)
                 })
        {
            File.WriteAllText(Path.Combine(validationRoot, "config.toml"), fixture.Item2);
            var validationStartInfo = new ProcessStartInfo
            {
                FileName = args[2],
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            validationStartInfo.ArgumentList.Add("features");
            validationStartInfo.ArgumentList.Add("list");
            validationStartInfo.Environment["CODEX_HOME"] = validationRoot;

            using var validation = Process.Start(validationStartInfo);
            if (validation is null)
            {
                failures.Add($"Codex config validator did not start for {fixture.Item1}.");
                continue;
            }

            var standardOutput = validation.StandardOutput.ReadToEnd();
            var standardError = validation.StandardError.ReadToEnd();
            validation.WaitForExit();
            Check(
                validation.ExitCode == 0,
                $"Codex rejected the {fixture.Item1} config fixture: " +
                $"{standardOutput} {standardError}".Trim());
        }
    }
    finally
    {
        Directory.Delete(validationRoot, true);
    }
}

var liveCapabilitiesIndex = Array.IndexOf(args, "--live-capabilities");
if (liveCapabilitiesIndex >= 0)
{
    if (args.Length < liveCapabilitiesIndex + 4)
    {
        failures.Add(
            "Usage: --live-capabilities <base-url> <responses-model> <image-model>");
    }
    else
    {
        var liveBaseUrl = args[liveCapabilitiesIndex + 1];
        var liveResponsesModel = args[liveCapabilitiesIndex + 2];
        var liveImageModel = args[liveCapabilitiesIndex + 3];
        var liveKey = CredentialVault.Read(AppPaths.CredentialTarget);
        if (string.IsNullOrWhiteSpace(liveKey))
        {
            failures.Add(
                $"No credential is stored under {AppPaths.CredentialTarget}.");
        }
        else
        {
            var probe = new ConnectionTestService();
            var textResult = await probe.TestResponsesApiAsync(
                liveBaseUrl,
                liveResponsesModel,
                liveKey);
            Console.WriteLine($"Live text probe: {textResult.Summary}");
            Check(textResult.Success, "Live Responses text probe failed.");

            var toolResult = await probe.TestFunctionCallingAsync(
                liveBaseUrl,
                liveResponsesModel,
                liveKey);
            Console.WriteLine($"Live tool probe: {toolResult.Summary}");
            Check(toolResult.Success, "Live function-call round trip failed.");

            if (!args.Contains("--skip-live-image", StringComparer.Ordinal))
            {
                var imageResult = await probe.TestImageGenerationAsync(
                    liveBaseUrl,
                    liveImageModel,
                    liveKey);
                Console.WriteLine($"Live image probe: {imageResult.Summary}");
                Check(imageResult.Success, "Live image generation probe failed.");
            }
        }
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"Self-test failed ({failures.Count}):");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine("All self-tests passed.");
return 0;

sealed class ModelDiscoveryStubHttpMessageHandler(
    HttpStatusCode statusCode,
    string responseBody) : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }

    public HttpMethod? Method { get; private set; }

    public string? AuthorizationScheme { get; private set; }

    public string? AuthorizationParameter { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        Method = request.Method;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                responseBody,
                Encoding.UTF8,
                "application/json"),
            RequestMessage = request
        });
    }
}

sealed class BlockingModelDiscoveryHttpMessageHandler : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The blocking test handler was not canceled.");
    }
}

sealed class StubHttpMessageHandler(
    HttpStatusCode statusCode,
    string responseBody) : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }

    public string Accept { get; private set; } = string.Empty;

    public string ApiVersion { get; private set; } = string.Empty;

    public bool HasAuthorization { get; private set; }

    public string UserAgent { get; private set; } = string.Empty;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        Accept = string.Join(",", request.Headers.Accept.Select(value => value.MediaType));
        ApiVersion = request.Headers.TryGetValues(
            "X-GitHub-Api-Version",
            out var versions)
            ? string.Join(",", versions)
            : string.Empty;
        HasAuthorization = request.Headers.Authorization is not null;
        UserAgent = request.Headers.UserAgent.ToString();
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(
                responseBody,
                Encoding.UTF8,
                "application/json"),
            RequestMessage = request
        });
    }
}

sealed class ProtocolProbeHttpMessageHandler(
    string responseBody,
    string mediaType) : HttpMessageHandler
{
    public Uri? RequestUri { get; private set; }

    public HttpMethod? Method { get; private set; }

    public string? AuthorizationScheme { get; private set; }

    public string? AuthorizationParameter { get; private set; }

    public string? RequestBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestUri = request.RequestUri;
        Method = request.Method;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        RequestBody = request.Content is null
            ? null
            : await request.Content.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responseBody, Encoding.UTF8, mediaType),
            RequestMessage = request
        };
    }
}
