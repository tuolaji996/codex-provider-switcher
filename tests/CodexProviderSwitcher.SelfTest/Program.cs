using System.Diagnostics;
using CodexProviderSwitcher.Core;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
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

var officialAgain = service.BuildOfficialConfig(official, "gpt-5.6-sol", "gpt-5.5");
Check(official == officialAgain, "Official configuration rewrite is not idempotent.");

var noReview = service.BuildOfficialConfig(official, "gpt-5.6-sol", null);
var noReviewStatus = service.ParseStatus(noReview);
Check(noReviewStatus.ReviewModel is null, "Optional official review model was not removed.");

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

        var brokerWslPath = ConfigService.ToWslPath(args[0]);
        var wslStartInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        wslStartInfo.ArgumentList.Add("-d");
        wslStartInfo.ArgumentList.Add("Ubuntu");
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
