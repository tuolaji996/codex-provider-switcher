using System.Diagnostics;

namespace CodexProviderSwitcher.Core;

public sealed class CodexProcessService
{
    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        var processes = Process.GetProcessesByName("ChatGPT");
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

        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

        processes = Process.GetProcessesByName("ChatGPT");
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

        await StopWslAppServerAsync(cancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppId}",
            UseShellExecute = true
        });
    }

    private static async Task StopWslAppServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "wsl.exe",
                Arguments = "-d Ubuntu -- sh -lc \"pkill -f '[c]odex.*app-server' || true\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            });
            if (process is null)
            {
                return;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            // Closing the Windows app normally stops its WSL app-server. This is a best-effort cleanup.
        }
    }
}
