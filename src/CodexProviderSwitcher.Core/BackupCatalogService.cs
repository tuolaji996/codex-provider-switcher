using System.Globalization;

namespace CodexProviderSwitcher.Core;

public sealed record BackupEntry(
    DateTime Timestamp,
    long SizeBytes,
    string FolderName,
    string ConfigPath);

public sealed class BackupCatalogService
{
    private readonly string _backupsRoot;

    public BackupCatalogService(string? backupsRoot = null)
    {
        _backupsRoot = backupsRoot ?? AppPaths.BackupsRoot;
    }

    public IReadOnlyList<BackupEntry> List()
    {
        Directory.CreateDirectory(_backupsRoot);
        return Directory
            .EnumerateFiles(
                _backupsRoot,
                "config.toml",
                SearchOption.AllDirectories)
            .Select(path => new FileInfo(path))
            .Select(file => new BackupEntry(
                ParseTimestamp(file.Directory?.Name) ??
                file.Directory?.CreationTime ??
                file.LastWriteTime,
                file.Length,
                file.Directory?.Name ?? string.Empty,
                file.FullName))
            .OrderByDescending(entry => entry.Timestamp)
            .ToList();
    }

    public static DateTime? ParseTimestamp(string? folderName) =>
        DateTime.TryParseExact(
            folderName,
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal,
            out var timestamp)
            ? timestamp
            : null;
}
