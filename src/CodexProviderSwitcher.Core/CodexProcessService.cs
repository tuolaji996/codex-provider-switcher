using System.Diagnostics;
using System.Globalization;

namespace CodexProviderSwitcher.Core;

public sealed class CodexProcessService
{
    private const string CodexProcessName = "ChatGPT";
    private static readonly string CodexLogsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages",
        "OpenAI.Codex_2p2nqsd0c76g0",
        "LocalCache",
        "Local",
        "Codex",
        "Logs");
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StartupPollInterval = TimeSpan.FromMilliseconds(200);

    private readonly Func<int> _getCodexProcessCount;
    private readonly Func<ProcessStartInfo, bool> _startProcess;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Func<DateTime, bool> _isAppServerReadySince;
    private readonly TimeSpan _startupTimeout;
    private readonly TimeSpan _startupPollInterval;

    public CodexProcessService()
        : this(
            GetCodexProcessCount,
            StartProcess,
            Task.Delay,
            IsAppServerReadySince,
            StartupTimeout,
            StartupPollInterval)
    {
    }

    internal CodexProcessService(
        Func<int> getCodexProcessCount,
        Func<ProcessStartInfo, bool> startProcess,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        Func<DateTime, bool> isAppServerReadySince,
        TimeSpan startupTimeout,
        TimeSpan startupPollInterval)
    {
        _getCodexProcessCount = getCodexProcessCount ??
            throw new ArgumentNullException(nameof(getCodexProcessCount));
        _startProcess = startProcess ??
            throw new ArgumentNullException(nameof(startProcess));
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
        _isAppServerReadySince = isAppServerReadySince ??
            throw new ArgumentNullException(nameof(isAppServerReadySince));
        _startupTimeout = RequirePositive(startupTimeout, nameof(startupTimeout));
        _startupPollInterval = RequirePositive(
            startupPollInterval,
            nameof(startupPollInterval));
    }

    internal Task WaitUntilStartedForTestAsync(
        CancellationToken cancellationToken = default) =>
        WaitUntilStartedAsync(DateTime.UtcNow, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var processes = Process.GetProcessesByName(CodexProcessName);
        foreach (var process in processes)
        {
            try
            {
                process.CloseMainWindow();
            }
            catch (InvalidOperationException)
            {
                // Process exited between enumeration and shutdown.
            }
            finally
            {
                process.Dispose();
            }
        }

        await WaitUntilStoppedAsync(TimeSpan.FromSeconds(4), cancellationToken);

        processes = Process.GetProcessesByName(CodexProcessName);
        foreach (var process in processes)
        {
            try
            {
                process.Kill(true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Process already exited.
            }
            finally
            {
                process.Dispose();
            }
        }

        await WaitUntilStoppedAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (Process.GetProcessesByName(CodexProcessName) is { Length: > 0 } remaining)
        {
            foreach (var process in remaining)
            {
                process.Dispose();
            }

            throw new InvalidOperationException(
                "Codex did not stop before the configuration update.");
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var launchStartedUtc = DateTime.UtcNow;
        var startInfo = new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppId}",
            UseShellExecute = true
        };
        if (!_startProcess(startInfo))
        {
            throw new InvalidOperationException(
                "Codex could not be launched. Start Codex manually and try the provider switch again.");
        }

        await WaitUntilStartedAsync(launchStartedUtc, cancellationToken);
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    private static async Task WaitUntilStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName(CodexProcessName);
            if (processes.Length == 0)
            {
                return;
            }

            foreach (var process in processes)
            {
                process.Dispose();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
        }
    }

    private async Task WaitUntilStartedAsync(
        DateTime launchStartedUtc,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + _startupTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_getCodexProcessCount() > 0)
            {
                if (_isAppServerReadySince(launchStartedUtc))
                {
                    return;
                }
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await _delayAsync(
                remaining < _startupPollInterval ? remaining : _startupPollInterval,
                cancellationToken);
        }

        throw new InvalidOperationException(
            $"Codex did not start within {_startupTimeout.TotalSeconds:0} seconds after the provider configuration was saved. " +
            "Start Codex manually, wait for it to finish loading, then retry the switch if needed.");
    }

    private static int GetCodexProcessCount()
    {
        var processes = Process.GetProcessesByName(CodexProcessName);
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private static bool StartProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo);
        return process is not null;
    }

    private static bool IsAppServerReadySince(DateTime launchStartedUtc)
    {
        if (!Directory.Exists(CodexLogsRoot))
        {
            return false;
        }

        var searchStartUtc = launchStartedUtc - TimeSpan.FromSeconds(1);
        try
        {
            foreach (var path in Directory.EnumerateFiles(
                         CodexLogsRoot,
                         "codex-desktop-*.log",
                         SearchOption.AllDirectories))
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < searchStartUtc)
                {
                    continue;
                }

                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                    {
                        if (IsAppServerReadyMarker(line, searchStartUtc))
                        {
                            return true;
                        }
                    }
                }
                catch (IOException)
                {
                    // The desktop can rotate a new log while it is starting.
                }
                catch (UnauthorizedAccessException)
                {
                    // Fall back to the bounded process-stability wait below.
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool IsAppServerReadyMarker(string line, DateTime searchStartUtc)
    {
        if (!line.Contains(
                "Current reported app-server version:",
                StringComparison.Ordinal))
        {
            return false;
        }

        var timestampEnd = line.IndexOf(' ');
        if (timestampEnd <= 0 ||
            !DateTimeOffset.TryParse(
                line[..timestampEnd],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            return false;
        }

        return timestamp.UtcDateTime >= searchStartUtc;
    }

    private static TimeSpan RequirePositive(TimeSpan value, string parameterName) =>
        value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                "The duration must be positive.");

}
