using System.Diagnostics;
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
    approval_policy = "never"

    [model_providers.OpenAI]
    name = "OpenAI"
    wire_api = "responses"
    requires_openai_auth = true

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

var thirdParty = service.BuildThirdPartyConfig(
    original,
    "codex-auto-review",
    "https://sui-xiang.com/v1/",
    @"C:\Users\Test\AppData\Local\Programs\CodexProviderSwitcher\CodexProviderToken.exe");
var thirdPartyStatus = service.ParseStatus(thirdParty);
Check(thirdPartyStatus.Mode == ProviderMode.ThirdParty, "Third-party mode was not detected.");
Check(thirdPartyStatus.ProviderId == "OpenAI", "Stable provider ID changed.");
Check(thirdPartyStatus.Model == "codex-auto-review", "Third-party model was not written.");
Check(thirdPartyStatus.ReviewModel == "codex-auto-review", "Review model was not switched.");
Check(thirdPartyStatus.BaseUrl == "https://sui-xiang.com/v1", "Base URL was not normalized.");
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

var testTarget = $"CodexProviderSwitcher:self-test:{Guid.NewGuid():N}";
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
