using System.Text;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed partial class ConfigService
{
    public const string EnabledReasoningEffortsKey = "enabled-reasoning-efforts";
    public const string SolUltraVisibilityKey = "show-ultra-in-model-picker-slider";
    public const string ModelCatalogJsonKey = "model_catalog_json";

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
        var modelCatalogJson = ReadTopLevelString(text, ModelCatalogJsonKey);
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
            credentialTarget,
            modelCatalogJson);
    }

    public bool ReadSolUltraVisibility(string? path = null)
    {
        path ??= AppPaths.ConfigPath;
        return File.Exists(path) &&
               ParseSolUltraVisibility(File.ReadAllText(path));
    }

    public bool ParseSolUltraVisibility(string text)
    {
        var desktopBlock = ReadSection(text, "desktop");
        return desktopBlock is not null &&
               ReadBooleanValue(desktopBlock, SolUltraVisibilityKey);
    }

    public bool ReadSolUltraAvailability(string? path = null)
    {
        path ??= AppPaths.ConfigPath;
        return File.Exists(path) &&
               ParseSolUltraAvailability(File.ReadAllText(path));
    }

    public bool ParseSolUltraAvailability(string text)
    {
        var desktopBlock = ReadSection(text, "desktop");
        return desktopBlock is not null &&
               ReadStringArrayContains(
                   desktopBlock,
                   EnabledReasoningEffortsKey,
                   "ultra");
    }

    public string BuildSolUltraVisibilityConfig(string original, bool enabled) =>
        UpsertSectionAssignment(
            original,
            "desktop",
            SolUltraVisibilityKey,
            enabled ? "true" : "false");

    public string? SetSolUltraVisibility(bool enabled, string? configPath = null)
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

        var original = File.ReadAllText(configPath);
        if (ParseSolUltraVisibility(original) == enabled)
        {
            return null;
        }

        var backupFolder = CreateBackup(configPath);
        var wroteConfig = false;
        try
        {
            WriteConfig(BuildSolUltraVisibilityConfig(original, enabled), configPath);
            wroteConfig = true;
            if (ReadSolUltraVisibility(configPath) != enabled)
            {
                throw new InvalidOperationException(
                    "Post-write verification failed for the Sol Ultra setting.");
            }

            return backupFolder;
        }
        catch (Exception exception) when (wroteConfig)
        {
            try
            {
                WriteConfig(original, configPath);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The Sol Ultra update failed and the original configuration could not be restored.",
                    exception,
                    rollbackException);
            }

            throw new InvalidOperationException(
                "The Sol Ultra update failed and the original configuration was restored.",
                exception);
        }
    }

    public string? RequestSolUltraEnablement(string? configPath = null)
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

        var original = File.ReadAllText(configPath);
        if (ParseSolUltraAvailability(original) ||
            ParseSolUltraVisibility(original))
        {
            return null;
        }

        return SetSolUltraVisibility(true, configPath);
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

    public string BuildKimiConfig(
        string original,
        string model,
        string tokenBrokerWindowsPath,
        string credentialTarget)
    {
        var brokerDirectory = Path.GetDirectoryName(tokenBrokerWindowsPath)
            ?? throw new ArgumentException(
                "The token broker path has no parent directory.",
                nameof(tokenBrokerWindowsPath));
        var launcherWindowsPath = Path.Combine(
            brokerDirectory,
            AppPaths.KimiWslLauncherFileName);
        var launcherWslPath = ToWslPath(launcherWindowsPath);
        credentialTarget = CredentialTargetFactory.RequireValid(credentialTarget);
        var managedBlock = $"""
            {ManagedComment}
            [model_providers.{AppPaths.StableProviderId}]
            name = "OpenAI"
            base_url = "{EscapeToml(AppPaths.KimiRouterBaseUrl)}"
            wire_api = "responses"

            [model_providers.{AppPaths.StableProviderId}.auth]
            command = "/bin/sh"
            args = ["{EscapeToml(launcherWslPath)}", "--credential-target", "{EscapeToml(credentialTarget)}"]
            timeout_ms = 20000
            refresh_interval_ms = {AppPaths.KimiAuthRefreshIntervalMilliseconds}
            """;

        return Rewrite(
            original,
            model,
            model,
            managedBlock,
            AppPaths.KimiModelCatalogFileName);
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
        string managedBlock,
        string? modelCatalogJson = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

        lines.RemoveAll(line => line.Trim().Equals(ManagedComment, StringComparison.Ordinal));
        RemoveManagedProviderSections(lines);
        if (modelCatalogJson is null)
        {
            RemoveManagedModelCatalogAssignments(lines);
        }
        else
        {
            EnsureManagedModelCatalogAssignment(lines, modelCatalogJson);
        }

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

    private static void RemoveManagedModelCatalogAssignments(List<string> lines)
    {
        for (var index = 0; index < lines.Count;)
        {
            if (ParseSectionName(lines[index]) is not null)
            {
                break;
            }

            if (IsAssignment(lines[index], ModelCatalogJsonKey) &&
                string.Equals(
                    ReadStringFromAssignment(lines[index]),
                    AppPaths.KimiModelCatalogFileName,
                    StringComparison.Ordinal))
            {
                lines.RemoveAt(index);
                continue;
            }

            index++;
        }
    }

    private static void EnsureManagedModelCatalogAssignment(
        List<string> lines,
        string expectedValue)
    {
        if (!string.Equals(
                expectedValue,
                AppPaths.KimiModelCatalogFileName,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Kimi model catalog path is not managed by this application.",
                nameof(expectedValue));
        }

        for (var index = 0; index < lines.Count; index++)
        {
            if (ParseSectionName(lines[index]) is not null)
            {
                break;
            }

            if (!IsAssignment(lines[index], ModelCatalogJsonKey))
            {
                continue;
            }

            if (!string.Equals(
                    ReadStringFromAssignment(lines[index]),
                    expectedValue,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    Localizer.Text(
                        "已存在用户自定义的 model_catalog_json；未改动配置。",
                        "A user-owned model_catalog_json already exists; the configuration was not changed."));
            }
        }

        RemoveManagedModelCatalogAssignments(lines);
        UpsertTopLevel(
            lines,
            ModelCatalogJsonKey,
            $"\"{EscapeToml(expectedValue)}\"");
    }

    private static string UpsertSectionAssignment(
        string original,
        string sectionName,
        string key,
        string tomlValue)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        var sectionIndex = lines.FindIndex(line =>
            string.Equals(ParseSectionName(line), sectionName, StringComparison.Ordinal));

        if (sectionIndex >= 0)
        {
            var sectionEnd = sectionIndex + 1;
            while (sectionEnd < lines.Count && ParseSectionName(lines[sectionEnd]) is null)
            {
                if (IsAssignment(lines[sectionEnd], key))
                {
                    lines[sectionEnd] = $"{key} = {tomlValue}";
                    return string.Join(newline, lines);
                }

                sectionEnd++;
            }

            var insertionIndex = sectionEnd;
            while (insertionIndex > sectionIndex + 1 &&
                   string.IsNullOrWhiteSpace(lines[insertionIndex - 1]))
            {
                insertionIndex--;
            }

            lines.Insert(insertionIndex, $"{key} = {tomlValue}");
            return string.Join(newline, lines);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add($"[{sectionName}]");
        lines.Add($"{key} = {tomlValue}");
        lines.Add(string.Empty);
        return string.Join(newline, lines);
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

    private static bool ReadStringArrayContains(
        string block,
        string key,
        string expected)
    {
        var line = block
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .FirstOrDefault(candidate => IsAssignment(candidate, key));
        if (line is null)
        {
            return false;
        }

        var equals = line.IndexOf('=');
        var value = equals < 0 ? string.Empty : line[(equals + 1)..].Trim();
        var close = value.IndexOf(']');
        if (!value.StartsWith("[", StringComparison.Ordinal) || close < 0)
        {
            return false;
        }

        var array = value[..(close + 1)];
        return TomlArrayStringRegex()
            .Matches(array)
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .Any(item => item.Equals(expected, StringComparison.OrdinalIgnoreCase));
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
        if (line is null)
        {
            return AppPaths.LegacySuiXiangCredentialTarget;
        }

        var equals = line.IndexOf('=');
        if (equals < 0)
        {
            return null;
        }

        var value = line[(equals + 1)..].Trim();
        if (!value.StartsWith("[", StringComparison.Ordinal) ||
            !value.EndsWith("]", StringComparison.Ordinal))
        {
            return null;
        }

        var arguments = TomlArrayStringRegex()
            .Matches(value)
            .Select(match => Regex.Unescape(match.Groups[1].Value))
            .ToArray();
        if (arguments.Length == 0)
        {
            return value == "[]" ? AppPaths.LegacySuiXiangCredentialTarget : null;
        }

        string? target = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (!string.Equals(arguments[index], "--credential-target", StringComparison.Ordinal))
            {
                continue;
            }

            if (target is not null || index + 1 >= arguments.Length)
            {
                return null;
            }

            target = arguments[++index];
        }

        return target is not null && CredentialTargetFactory.IsValid(target)
            ? target
            : null;
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

    [GeneratedRegex("\"((?:\\\\.|[^\"])*)\"")]
    private static partial Regex TomlArrayStringRegex();

}
