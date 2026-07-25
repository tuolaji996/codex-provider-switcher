using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed record HostFeatureStates(
    bool? AppsEnabled,
    bool? PluginsEnabled,
    bool? RemotePluginEnabled,
    bool? ImageGenerationEnabled)
{
    public bool IsComplete =>
        AppsEnabled.HasValue &&
        PluginsEnabled.HasValue &&
        RemotePluginEnabled.HasValue &&
        ImageGenerationEnabled.HasValue;
}

public sealed record HostCapabilityDiagnostics(
    string? CodexCliPath,
    bool? ChatGptLoggedIn,
    HostFeatureStates Features,
    string Summary)
{
    public bool CliAvailable => !string.IsNullOrWhiteSpace(CodexCliPath);

    public bool IsComplete =>
        CliAvailable &&
        ChatGptLoggedIn.HasValue &&
        Features.IsComplete;
}

public sealed partial class HostCapabilityDiagnosticsService
{
    public const string OfficialConnectionsUri = "codex://settings/connections";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(12);
    private const int MaxCapturedCharacters = 64 * 1024;

    public async Task<HostCapabilityDiagnostics> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var cliPath = FindCodexCliPath();
        if (cliPath is null)
        {
            return new HostCapabilityDiagnostics(
                null,
                null,
                EmptyFeatureStates(),
                Localizer.Text(
                    "未找到原生 Windows Codex CLI。",
                    "Native Windows Codex CLI was not found."));
        }

        var loginResult = await RunCliCommandAsync(
            cliPath,
            ["login", "status"],
            cancellationToken);
        var featureResult = await RunCliCommandAsync(
            cliPath,
            ["features", "list"],
            cancellationToken);

        var chatGptLoggedIn = loginResult.CommandStarted
            ? ParseChatGptLoginStatus(loginResult.CapturedOutput)
            : null;
        var features = featureResult.CommandStarted
            ? ParseFeatureList(featureResult.CapturedOutput)
            : EmptyFeatureStates();

        return new HostCapabilityDiagnostics(
            cliPath,
            chatGptLoggedIn,
            features,
            BuildSummary(chatGptLoggedIn, features));
    }

    public static string? FindCodexCliPath()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
        }

        return FindCodexCliPath(
            Environment.GetEnvironmentVariable("CODEX_CLI_PATH"),
            localAppData);
    }

    public static string? FindCodexCliPath(
        string? configuredPath,
        string? localAppData)
    {
        var overridePath = ResolveExecutablePath(configuredPath);
        if (overridePath is not null)
        {
            return overridePath;
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        try
        {
            var binRoot = Path.Combine(
                Environment.ExpandEnvironmentVariables(localAppData.Trim().Trim('"')),
                "OpenAI",
                "Codex",
                "bin");
            if (!Directory.Exists(binRoot))
            {
                return null;
            }

            return Directory
                .EnumerateDirectories(binRoot)
                .Select(directory => Path.Combine(directory, "codex.exe"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenBy(file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(file => file.FullName)
                .FirstOrDefault();
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    public static bool? ParseChatGptLoginStatus(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var normalized = StripAnsi(output);
        if (ChatGptLoginPattern().IsMatch(normalized))
        {
            return true;
        }

        if (NotLoggedInPattern().IsMatch(normalized) ||
            OtherLoginMethodPattern().IsMatch(normalized))
        {
            return false;
        }

        return null;
    }

    public static HostFeatureStates ParseFeatureList(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return EmptyFeatureStates();
        }

        var values = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in StripAnsi(output).Split(
                     ['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var columns = line.Split(
                [' ', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (columns.Length < 2 ||
                !bool.TryParse(columns[^1], out var enabled))
            {
                continue;
            }

            values[columns[0]] = enabled;
        }

        return new HostFeatureStates(
            ReadFeature(values, "apps"),
            ReadFeature(values, "plugins"),
            ReadFeature(values, "remote_plugin"),
            ReadFeature(values, "image_generation"));
    }

    public static bool OpenOfficialConnectionsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = OfficialConnectionsUri,
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool OpenOfficialCodexApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppId}",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ResolveExecutablePath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(
                configuredPath.Trim().Trim('"'));
            if (Directory.Exists(expanded))
            {
                expanded = Path.Combine(expanded, "codex.exe");
            }

            if (!File.Exists(expanded) ||
                !string.Equals(
                    Path.GetExtension(expanded),
                    ".exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static async Task<CliCommandResult> RunCliCommandAsync(
        string cliPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cliPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(CommandTimeout);

        try
        {
            if (!process.Start())
            {
                return CliCommandResult.NotStarted;
            }

            var standardOutput = CaptureAndDrainAsync(
                process.StandardOutput,
                timeout.Token);
            var standardError = CaptureAndDrainAsync(
                process.StandardError,
                timeout.Token);

            await process.WaitForExitAsync(timeout.Token);
            var output = await standardOutput;
            var error = await standardError;
            var captured = string.Join(
                Environment.NewLine,
                new[] { output, error }.Where(value => !string.IsNullOrWhiteSpace(value)));

            return new CliCommandResult(true, process.ExitCode, captured);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return new CliCommandResult(true, null, string.Empty);
        }
        catch (OperationCanceledException)
        {
            TryTerminate(process);
            throw;
        }
        catch (Exception exception) when (
            exception is Win32Exception or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException)
        {
            TryTerminate(process);
            return CliCommandResult.NotStarted;
        }
    }

    private static async Task<string> CaptureAndDrainAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var captured = new StringBuilder();
        var buffer = new char[2048];

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return captured.ToString();
            }

            var remaining = MaxCapturedCharacters - captured.Length;
            if (remaining > 0)
            {
                captured.Append(buffer, 0, Math.Min(count, remaining));
            }
        }
    }

    private static void TryTerminate(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            process.Kill(true);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            // The process may have exited while the timeout was being handled.
        }
    }

    private static bool? ReadFeature(
        IReadOnlyDictionary<string, bool> values,
        string name) =>
        values.TryGetValue(name, out var enabled) ? enabled : null;

    private static HostFeatureStates EmptyFeatureStates() =>
        new(null, null, null, null);

    private static string BuildSummary(
        bool? chatGptLoggedIn,
        HostFeatureStates features)
    {
        var separator = Localizer.Text("；", "; ");
        return string.Join(
                   separator,
                   Localizer.Format(
                       "ChatGPT 登录：{0}",
                       "ChatGPT sign-in: {0}",
                       FormatLoginState(chatGptLoggedIn)),
                   Localizer.Format(
                       "Apps：{0}",
                       "Apps: {0}",
                       FormatState(features.AppsEnabled)),
                   Localizer.Format(
                       "插件：{0}",
                       "Plugins: {0}",
                       FormatState(features.PluginsEnabled)),
                   Localizer.Format(
                       "Remote：{0}",
                       "Remote: {0}",
                       FormatState(features.RemotePluginEnabled)),
                   Localizer.Format(
                       "图片生成：{0}",
                       "Image generation: {0}",
                       FormatState(features.ImageGenerationEnabled))) +
               Localizer.Text("。", ".");
    }

    private static string FormatLoginState(bool? state) =>
        state switch
        {
            true => Localizer.Text("已登录", "Signed in"),
            false => Localizer.Text("未检测到", "Not detected"),
            null => Localizer.Text("未知", "Unknown")
        };

    private static string FormatState(bool? state) =>
        state switch
        {
            true => Localizer.Text("已启用", "Enabled"),
            false => Localizer.Text("未启用", "Disabled"),
            null => Localizer.Text("未知", "Unknown")
        };

    private static string StripAnsi(string value) =>
        AnsiEscapePattern().Replace(value, string.Empty);

    private sealed record CliCommandResult(
        bool CommandStarted,
        int? ExitCode,
        string CapturedOutput)
    {
        public static CliCommandResult NotStarted { get; } =
            new(false, null, string.Empty);
    }

    [GeneratedRegex(
        @"^\s*Logged in (?:using|with) ChatGPT\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ChatGptLoginPattern();

    [GeneratedRegex(
        @"^\s*Not logged in\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NotLoggedInPattern();

    [GeneratedRegex(
        @"^\s*Logged in (?:using|with)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex OtherLoginMethodPattern();

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscapePattern();
}
