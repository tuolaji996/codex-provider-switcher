using System.Text.Json;

namespace CodexProviderSwitcher.Core;

public sealed class SessionHealthService
{
    public Task<SessionHealth> InspectAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(cancellationToken), cancellationToken);

    public SessionHealth Inspect(CancellationToken cancellationToken = default)
    {
        var total = 0;
        var stable = 0;
        var other = 0;
        var unreadable = 0;
        var empty = 0;

        foreach (var folderName in new[] { "sessions", "archived_sessions" })
        {
            var root = Path.Combine(AppPaths.CodexHome, folderName);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var path in Directory.EnumerateFiles(
                         root,
                         "*.jsonl",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                total++;
                try
                {
                    if (new FileInfo(path).Length == 0)
                    {
                        empty++;
                        continue;
                    }

                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var firstLine = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(firstLine))
                    {
                        unreadable++;
                        continue;
                    }

                    using var document = JsonDocument.Parse(firstLine);
                    var provider = document.RootElement
                        .GetProperty("payload")
                        .GetProperty("model_provider")
                        .GetString();
                    if (provider == AppPaths.StableProviderId)
                    {
                        stable++;
                    }
                    else
                    {
                        other++;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException)
                {
                    unreadable++;
                }
            }
        }

        return new SessionHealth(total, stable, other, unreadable, empty);
    }
}
