using System.Text;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed partial class ConfigService
{
    private const string ManagedComment =
        "# Managed by Codex Provider Switcher. Keep this provider ID stable so all chats share one history.";

    public ConfigStatus ReadStatus(string? path = null)
    {
        path ??= AppPaths.ConfigPath;
        if (!File.Exists(path))
        {
            return new ConfigStatus(
                ProviderMode.Unknown,
                string.Empty,
                null,
                null,
                null,
                false);
        }

        return ParseStatus(File.ReadAllText(path));
    }

    public ConfigStatus ParseStatus(string text)
    {
        var provider = ReadTopLevelString(text, "model_provider") ?? string.Empty;
        var model = ReadTopLevelString(text, "model");
        var reviewModel = ReadTopLevelString(text, "review_model");
        var block = ReadSection(text, $"model_providers.{AppPaths.StableProviderId}");
        var baseUrl = block is null ? null : ReadStringValue(block, "base_url");
        var officialAuth = block is not null && ReadBooleanValue(block, "requires_openai_auth");
        var authBlock = ReadSection(
            text,
            $"model_providers.{AppPaths.StableProviderId}.auth");
        var credentialTarget =
            !officialAuth && !string.IsNullOrWhiteSpace(baseUrl)
                ? ReadCredentialTarget(authBlock)
                : null;

        var mode = provider == AppPaths.StableProviderId
            ? officialAuth && string.IsNullOrWhiteSpace(baseUrl)
                ? ProviderMode.Official
                : !string.IsNullOrWhiteSpace(baseUrl)
                    ? ProviderMode.ThirdParty
                    : ProviderMode.Unknown
            : ProviderMode.Unknown;

        return new ConfigStatus(
            mode,
            provider,
            model,
            reviewModel,
            baseUrl,
            officialAuth,
            credentialTarget);
    }

    public string BuildOfficialConfig(
        string original,
        string officialModel,
        string? officialReviewModel)
    {
        var managedBlock = $"""
            {ManagedComment}
            [model_providers.{AppPaths.StableProviderId}]
            name = "OpenAI"
            wire_api = "responses"
            requires_openai_auth = true
            """;

        return Rewrite(
            original,
            officialModel,
            officialReviewModel,
            managedBlock);
    }

    public string BuildThirdPartyConfig(
        string original,
        string model,
        string baseUrl,
        string tokenBrokerWindowsPath)
        => BuildThirdPartyConfig(
            original,
            model,
            baseUrl,
            tokenBrokerWindowsPath,
            AppPaths.LegacySuiXiangCredentialTarget);

    public string BuildThirdPartyConfig(
        string original,
        string model,
        string baseUrl,
        string tokenBrokerWindowsPath,
        string credentialTarget)
    {
        var normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        var brokerWslPath = ToWslPath(tokenBrokerWindowsPath);
        credentialTarget = CredentialTargetFactory.RequireValid(credentialTarget);
        var managedBlock = $"""
            {ManagedComment}
            [model_providers.{AppPaths.StableProviderId}]
            name = "OpenAI"
            base_url = "{EscapeToml(normalizedBaseUrl)}"
            wire_api = "responses"

            [model_providers.{AppPaths.StableProviderId}.auth]
            command = "{EscapeToml(brokerWslPath)}"
            args = ["--credential-target", "{EscapeToml(credentialTarget)}"]
            timeout_ms = 5000
            refresh_interval_ms = 0
            """;

        return Rewrite(original, model, model, managedBlock);
    }

    public string CreateBackup(string? configPath = null)
    {
        configPath ??= AppPaths.ConfigPath;
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException(
                Localizer.Text(
                    "未找到 Codex config.toml。",
                    "Codex config.toml was not found."),
                configPath);
        }

        var folder = Path.Combine(
            AppPaths.BackupsRoot,
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
        Directory.CreateDirectory(folder);
        var destination = Path.Combine(folder, "config.toml");
        File.Copy(configPath, destination, false);
        return folder;
    }

    public void WriteConfig(string content, string? configPath = null)
    {
        configPath ??= AppPaths.ConfigPath;
        AtomicFile.WriteAllText(configPath, content);
    }

    public static string NormalizeBaseUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException(
                Localizer.Text(
                    "Base URL 必须是完整的 http:// 或 https:// 地址。",
                    "Base URL must be a complete http:// or https:// address."),
                nameof(value));
        }

        return uri.ToString().TrimEnd('/');
    }

    public static string ToWslPath(string windowsPath)
    {
        var fullPath = Path.GetFullPath(windowsPath);
        if (fullPath.Length >= 3 && fullPath[1] == ':' &&
            (fullPath[2] == '\\' || fullPath[2] == '/'))
        {
            var drive = char.ToLowerInvariant(fullPath[0]);
            var rest = fullPath[3..].Replace('\\', '/');
            return $"/mnt/{drive}/{rest}";
        }

        return fullPath.Replace('\\', '/');
    }

    private static string Rewrite(
        string original,
        string model,
        string? reviewModel,
        string managedBlock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        lines.RemoveAll(line => line.Trim().Equals(ManagedComment, StringComparison.Ordinal));
        RemoveManagedProviderSections(lines);

        UpsertTopLevel(lines, "model_provider", $"\"{AppPaths.StableProviderId}\"");
        UpsertTopLevel(lines, "model", $"\"{EscapeToml(model.Trim())}\"");
        if (string.IsNullOrWhiteSpace(reviewModel))
        {
            RemoveTopLevel(lines, "review_model");
        }
        else
        {
            UpsertTopLevel(lines, "review_model", $"\"{EscapeToml(reviewModel.Trim())}\"");
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        lines.Add(string.Empty);
        lines.AddRange(managedBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        lines.Add(string.Empty);
        return string.Join(newline, lines);
    }

    private static void RemoveManagedProviderSections(List<string> lines)
    {
        var index = 0;
        while (index < lines.Count)
        {
            var section = ParseSectionName(lines[index]);
            if (section is null ||
                !(section.Equals(
                      $"model_providers.{AppPaths.StableProviderId}",
                      StringComparison.Ordinal) ||
                  section.StartsWith(
                      $"model_providers.{AppPaths.StableProviderId}.",
                      StringComparison.Ordinal)))
            {
                index++;
                continue;
            }

            var end = index + 1;
            while (end < lines.Count && ParseSectionName(lines[end]) is null)
            {
                end++;
            }

            lines.RemoveRange(index, end - index);
            while (index > 0 && index < lines.Count &&
                   string.IsNullOrWhiteSpace(lines[index - 1]) &&
                   string.IsNullOrWhiteSpace(lines[index]))
            {
                lines.RemoveAt(index);
            }
        }
    }

    private static void UpsertTopLevel(List<string> lines, string key, string tomlValue)
    {
        var sectionStarted = false;
        for (var index = 0; index < lines.Count; index++)
        {
            if (ParseSectionName(lines[index]) is not null)
            {
                sectionStarted = true;
            }

            if (!sectionStarted && IsAssignment(lines[index], key))
            {
                lines[index] = $"{key} = {tomlValue}";
                return;
            }
        }

        var insertionIndex = lines.FindIndex(line => ParseSectionName(line) is not null);
        if (insertionIndex < 0)
        {
            insertionIndex = lines.Count;
        }

        lines.Insert(insertionIndex, $"{key} = {tomlValue}");
    }

    private static void RemoveTopLevel(List<string> lines, string key)
    {
        for (var index = 0; index < lines.Count;)
        {
            if (ParseSectionName(lines[index]) is not null)
            {
                break;
            }

            if (IsAssignment(lines[index], key))
            {
                lines.RemoveAt(index);
                continue;
            }

            index++;
        }
    }

    private static string? ReadTopLevelString(string text, string key)
    {
        var sectionStarted = false;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (ParseSectionName(line) is not null)
            {
                sectionStarted = true;
            }

            if (!sectionStarted && IsAssignment(line, key))
            {
                return ReadStringFromAssignment(line);
            }
        }

        return null;
    }

    private static string? ReadSection(string text, string sectionName)
    {
        var builder = new StringBuilder();
        var inSection = false;
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var section = ParseSectionName(line);
            if (section is not null)
            {
                if (inSection)
                {
                    break;
                }

                inSection = section.Equals(sectionName, StringComparison.Ordinal);
                continue;
            }

            if (inSection)
            {
                builder.AppendLine(line);
            }
        }

        return inSection || builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? ReadStringValue(string block, string key)
    {
        var line = block
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(candidate => IsAssignment(candidate, key));
        return line is null ? null : ReadStringFromAssignment(line);
    }

    private static bool ReadBooleanValue(string block, string key)
    {
        var line = block
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(candidate => IsAssignment(candidate, key));
        if (line is null)
        {
            return false;
        }

        var value = line[(line.IndexOf('=') + 1)..].Trim();
        return bool.TryParse(value, out var parsed) && parsed;
    }

    private static string? ReadCredentialTarget(string? authBlock)
    {
        if (string.IsNullOrWhiteSpace(authBlock))
        {
            return AppPaths.LegacySuiXiangCredentialTarget;
        }

        var line = authBlock
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(candidate => IsAssignment(candidate, "args"));
        if (line is null || line.Contains("[]", StringComparison.Ordinal))
        {
            return AppPaths.LegacySuiXiangCredentialTarget;
        }

        var match = CredentialTargetArgsRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        var target = Regex.Unescape(match.Groups[1].Value);
        return CredentialTargetFactory.IsValid(target) ? target : null;
    }

    private static string? ReadStringFromAssignment(string line)
    {
        var equals = line.IndexOf('=');
        if (equals < 0)
        {
            return null;
        }

        var value = line[(equals + 1)..].Trim();
        var match = TomlStringRegex().Match(value);
        if (!match.Success)
        {
            return null;
        }

        return Regex.Unescape(match.Groups[1].Value);
    }

    private static bool IsAssignment(string line, string key)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith(key, StringComparison.Ordinal) &&
               trimmed.Length > key.Length &&
               (char.IsWhiteSpace(trimmed[key.Length]) || trimmed[key.Length] == '=');
    }

    private static string? ParseSectionName(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("[", StringComparison.Ordinal) ||
            !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.Trim('[', ']').Trim();
    }

    private static string EscapeToml(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    [GeneratedRegex("^\"((?:\\\\.|[^\"])*)\"")]
    private static partial Regex TomlStringRegex();

    [GeneratedRegex(
        "^\\s*args\\s*=\\s*\\[\\s*\"--credential-target\"\\s*,\\s*\"((?:\\\\.|[^\"])*)\"\\s*\\]\\s*$")]
    private static partial Regex CredentialTargetArgsRegex();
}
