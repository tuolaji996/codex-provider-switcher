using System.Diagnostics;

namespace CodexProviderSwitcher.Core;

public sealed class CodexProcessService
{
    public async Task StopAsync(CancellationToken cancellationToken = default)
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

        await WaitUntilStoppedAsync(TimeSpan.FromSeconds(4), cancellationToken);

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

        await WaitUntilStoppedAsync(TimeSpan.FromSeconds(5), cancellationToken);
        if (Process.GetProcessesByName("ChatGPT") is { Length: > 0 } remaining)
        {
            foreach (var process in remaining)
            {
                process.Dispose();
            }

            throw new InvalidOperationException(
                "Codex did not stop before the configuration update.");
        }
    }

    public Task StartAsync()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppId}",
            UseShellExecute = true
        });
        return Task.CompletedTask;
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync();
    }

    private static async Task WaitUntilStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var processes = Process.GetProcessesByName("ChatGPT");
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
}
