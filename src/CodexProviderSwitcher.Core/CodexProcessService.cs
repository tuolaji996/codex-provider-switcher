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

        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"shell:AppsFolder\\{AppPaths.CodexAppId}",
            UseShellExecute = true
        });
    }

}
