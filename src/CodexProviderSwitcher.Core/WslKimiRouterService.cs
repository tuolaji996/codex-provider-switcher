using System.Diagnostics;
using System.Text;

namespace CodexProviderSwitcher.Core;

public sealed record WslKimiRouterEnsureResult(
    bool Success,
    string Summary,
    bool Started);

/// <summary>
/// Invokes the bundled WSL launcher from the Windows GUI. Codex itself runs in
/// WSL, so a Windows-only listener on 127.0.0.1 cannot serve its Responses
/// traffic. The launcher owns the Linux router lifecycle and validates its
/// loopback health endpoint inside the same network namespace as Codex.
/// </summary>
public sealed class WslKimiRouterService
{
    private static readonly TimeSpan EnsureTimeout = TimeSpan.FromSeconds(20);
    private const int MaxDiagnosticChars = 2048;

    public async Task<WslKimiRouterEnsureResult> EnsureRunningAsync(
        string launcherWindowsPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherWindowsPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(launcherWindowsPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            return Failure(F(
                "WSL K3 启动器路径无效：{0}",
                "The WSL K3 launcher path is invalid: {0}",
                exception.Message));
        }

        if (!File.Exists(fullPath))
        {
            return Failure(F(
                "未找到随想 K3 WSL 启动器：{0}",
                "The SuiXiang K3 WSL launcher was not found: {0}",
                fullPath));
        }

        var ensure = await RunLauncherAsync(
            fullPath,
            "--ensure-only",
            cancellationToken).ConfigureAwait(false);
        if (!ensure.Success)
        {
            return Failure(ensure.Summary);
        }

        var health = await RunLauncherAsync(
            fullPath,
            "--health",
            cancellationToken).ConfigureAwait(false);
        return health.Success
            ? new WslKimiRouterEnsureResult(
                true,
                T(
                    "WSL 中的随想 K3 路由器已通过 loopback 健康检查。",
                    "The SuiXiang K3 router passed its WSL loopback health check."),
                Started: true)
            : Failure(health.Summary);
    }

    private static async Task<LauncherResult> RunLauncherAsync(
        string launcherWindowsPath,
        string mode,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EnsureTimeout);

        Process? process = null;
        try
        {
            process = Process.Start(BuildStartInfo(launcherWindowsPath, mode));
            if (process is null)
            {
                return LauncherResult.Failed(T(
                    "无法启动 WSL 来运行随想 K3 路由器。",
                    "Could not start WSL to run the SuiXiang K3 router."));
            }

            var outputTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errorTask = ReadBoundedAsync(process.StandardError, timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            var error = await errorTask.ConfigureAwait(false);
            if (process.ExitCode == 0)
            {
                return LauncherResult.Succeeded;
            }

            return LauncherResult.Failed(F(
                "WSL 随想 K3 路由器未就绪（退出代码 {0}）：{1}",
                "The WSL SuiXiang K3 router was not ready (exit code {0}): {1}",
                process.ExitCode,
                CompactDiagnostic(string.IsNullOrWhiteSpace(error) ? output : error)));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryTerminate(process);
            return LauncherResult.Failed(T(
                "等待 WSL 随想 K3 路由器超时。请确认已安装并可以运行 WSL。",
                "Timed out waiting for the WSL SuiXiang K3 router. Confirm WSL is installed and runnable."));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return LauncherResult.Failed(F(
                "无法运行 WSL 随想 K3 路由器：{0}",
                "Could not run the WSL SuiXiang K3 router: {0}",
                exception.Message));
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(string launcherWindowsPath, string mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--exec");
        startInfo.ArgumentList.Add("/bin/sh");
        startInfo.ArgumentList.Add(ConfigService.ToWslPath(launcherWindowsPath));
        startInfo.ArgumentList.Add(mode);
        return startInfo;
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[512];
        var builder = new StringBuilder();
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return builder.ToString();
            }

            if (builder.Length < MaxDiagnosticChars)
            {
                builder.Append(buffer, 0, Math.Min(count, MaxDiagnosticChars - builder.Length));
            }
        }
    }

    private static string CompactDiagnostic(string? value)
    {
        var compact = string.IsNullOrWhiteSpace(value)
            ? T("未提供错误详情。", "No error details were provided.")
            : value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= MaxDiagnosticChars
            ? compact
            : compact[..MaxDiagnosticChars];
    }

    private static void TryTerminate(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(milliseconds: 2_000);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The timed-out WSL command can already have exited. Do not hide
            // the actionable timeout with cleanup noise.
        }
    }

    private static WslKimiRouterEnsureResult Failure(string summary) =>
        new(false, summary, Started: false);

    private static string T(string chinese, string english) =>
        Localizer.Text(chinese, english);

    private static string F(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        Localizer.Format(chineseFormat, englishFormat, arguments);

    private sealed record LauncherResult(bool Success, string Summary)
    {
        public static LauncherResult Succeeded { get; } = new(true, string.Empty);

        public static LauncherResult Failed(string summary) => new(false, summary);
    }
}
