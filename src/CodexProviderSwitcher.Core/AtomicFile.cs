using System.Text;

namespace CodexProviderSwitcher.Core;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                Localizer.Format(
                    "无法确定 {0} 的父目录。",
                    "Cannot determine the parent directory for {0}.",
                    path));
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static void WriteAllBytes(string path, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                Localizer.Format(
                    "无法确定 {0} 的父目录。",
                    "Cannot determine the parent directory for {0}.",
                    path));
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllBytes(temporaryPath, content);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
