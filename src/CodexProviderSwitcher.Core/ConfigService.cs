using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public sealed partial class ConfigService
{
    public const string EnabledReasoningEffortsKey = "enabled-reasoning-efforts";
    public const string SolUltraVisibilityKey = "show-ultra-in-model-picker-slider";
    public const string ModelCatalogJsonKey = "model_catalog_json";
    public const string ModelContextWindowKey = "model_context_window";
    public const string ModelAutoCompactTokenLimitKey =
        "model_auto_compact_token_limit";
    public const long RecommendedSolContextWindow = 1_000_000;
    public const long RecommendedSolAutoCompactTokenLimit = 900_000;
    public const string SolContextWindowManagedComment =
        "# Managed by Codex Provider Switcher: GPT-5.6 Sol 1M context window.";

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

    public SolContextWindowStatus ReadSolContextWindowStatus(string? path = null)
    {
        path ??= AppPaths.ConfigPath;
        return File.Exists(path)
            ? ParseSolContextWindowStatus(File.ReadAllText(path))
            : new SolContextWindowStatus(
                SolContextWindowMode.Default,
                null,
                null,
                false);
    }

    public SolContextWindowStatus ParseSolContextWindowStatus(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var assignments = ScanSolContextWindowAssignments(
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'));
        var contextWindow = assignments.ContextWindows.Count == 0
            ? null
            : assignments.ContextWindows[0];
        var autoCompactTokenLimit = assignments.AutoCompactTokenLimits.Count == 0
            ? null
            : assignments.AutoCompactTokenLimits[0];

        if (assignments.ContextWindows.Count == 0 &&
            assignments.AutoCompactTokenLimits.Count == 0)
        {
            return new SolContextWindowStatus(
                SolContextWindowMode.Default,
                null,
                null,
                assignments.Managed);
        }

        var mode = assignments.ContextWindows.Count == 1 &&
                   assignments.AutoCompactTokenLimits.Count == 1 &&
                   contextWindow == RecommendedSolContextWindow &&
                   autoCompactTokenLimit == RecommendedSolAutoCompactTokenLimit
            ? SolContextWindowMode.Recommended
            : SolContextWindowMode.Custom;

        return new SolContextWindowStatus(
            mode,
            contextWindow,
            autoCompactTokenLimit,
            assignments.Managed);
    }

    public string BuildSolContextWindowConfig(string original, bool enabled) =>
        BuildSolContextWindowConfig(original, enabled, replaceCustom: false);

    public string? SetSolContextWindow(
        bool enabled,
        string? path = null,
        bool replaceCustom = false)
    {
        path ??= AppPaths.ConfigPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                Localizer.Text(
                    "\u672a\u627e\u5230 Codex config.toml\u3002",
                    "Codex config.toml was not found."),
                path);
        }

        var original = File.ReadAllText(path);
        var updated = BuildSolContextWindowConfig(
            original,
            enabled,
            replaceCustom);
        if (string.Equals(updated, original, StringComparison.Ordinal))
        {
            return null;
        }

        var backupFolder = CreateBackup(path);
        var wroteConfig = false;
        try
        {
            WriteConfig(updated, path);
            wroteConfig = true;
            var readBack = File.ReadAllText(path);
            var verifiedStatus = ParseSolContextWindowStatus(readBack);
            var verified = string.Equals(readBack, updated, StringComparison.Ordinal) &&
                           (enabled
                               ? verifiedStatus.IsRecommended && verifiedStatus.Managed
                               : verifiedStatus.Mode == SolContextWindowMode.Default &&
                                 !verifiedStatus.Managed);
            if (!verified)
            {
                throw new InvalidOperationException(
                    "Post-write verification failed for the Sol context window settings.");
            }

            return backupFolder;
        }
        catch (Exception exception) when (wroteConfig)
        {
            try
            {
                WriteConfig(original, path);
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    "The Sol context window update failed and the original configuration could not be restored.",
                    exception,
                    rollbackException);
            }

            throw new InvalidOperationException(
                "The Sol context window update failed and the original configuration was restored.",
                exception);
        }
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
        ProviderAvailabilityPolicy.RequireAvailableThirdPartyRoute(baseUrl, model);
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
        ProviderAvailabilityPolicy.RequireKimiRouteEnabled();
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

    private string BuildSolContextWindowConfig(
        string original,
        bool enabled,
        bool replaceCustom)
    {
        ArgumentNullException.ThrowIfNull(original);
        var status = ParseSolContextWindowStatus(original);

        if (enabled)
        {
            var model = ReadTopLevelString(original, "model");
            if (!IsSolModel(model))
            {
                throw new InvalidOperationException(
                    Localizer.Text(
                        "1M \u4e0a\u4e0b\u6587\u4ec5\u80fd\u5728\u5f53\u524d\u6a21\u578b\u4e3a gpt-5.6-sol \u65f6\u542f\u7528\u3002",
                        "The 1M context window can only be enabled when the current model is gpt-5.6-sol."));
            }

            if (status.Mode == SolContextWindowMode.Recommended)
            {
                return original;
            }

            if (status.Mode == SolContextWindowMode.Custom && !replaceCustom)
            {
                throw CreateCustomSolContextWindowException();
            }

            return RewriteSolContextWindowAssignments(original, enabled: true);
        }

        if (status.Mode == SolContextWindowMode.Default)
        {
            return status.Managed
                ? RewriteSolContextWindowAssignments(original, enabled: false)
                : original;
        }

        if (status.Mode == SolContextWindowMode.Custom && !replaceCustom)
        {
            throw CreateCustomSolContextWindowException();
        }

        return RewriteSolContextWindowAssignments(original, enabled: false);
    }

    private static InvalidOperationException CreateCustomSolContextWindowException() =>
        new(Localizer.Text(
            "\u5df2\u5b58\u5728\u7528\u6237\u81ea\u5b9a\u4e49\u7684\u4e0a\u4e0b\u6587\u8bbe\u7f6e\uff1b\u672a\u6539\u52a8\u914d\u7f6e\u3002",
            "User-defined context window settings already exist; the configuration was not changed."));

    private static string RewriteSolContextWindowAssignments(
        string original,
        bool enabled)
    {
        var newline = original.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = original
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .ToList();
        RemoveSolContextWindowAssignments(lines);

        if (enabled)
        {
            var insertionIndex = lines.FindIndex(line => ParseSectionName(line) is not null);
            if (insertionIndex < 0)
            {
                insertionIndex = lines.Count;
                while (insertionIndex > 0 &&
                       string.IsNullOrWhiteSpace(lines[insertionIndex - 1]))
                {
                    insertionIndex--;
                }
            }

            lines.InsertRange(
                insertionIndex,
                new[]
                {
                    SolContextWindowManagedComment,
                    $"{ModelContextWindowKey} = {RecommendedSolContextWindow}",
                    $"{ModelAutoCompactTokenLimitKey} = {RecommendedSolAutoCompactTokenLimit}"
                });
        }

        return string.Join(newline, lines);
    }

    private static void RemoveSolContextWindowAssignments(List<string> lines)
    {
        for (var index = 0; index < lines.Count;)
        {
            if (ParseSectionName(lines[index]) is not null)
            {
                break;
            }

            if (lines[index].Trim().Equals(
                    SolContextWindowManagedComment,
                    StringComparison.Ordinal) ||
                IsAssignment(lines[index], ModelContextWindowKey) ||
                IsAssignment(lines[index], ModelAutoCompactTokenLimitKey))
            {
                lines.RemoveAt(index);
                continue;
            }

            index++;
        }
    }

    private static void RemoveManagedSolContextWindowForNonSol(
        List<string> lines,
        string targetModel)
    {
        if (IsSolModel(targetModel))
        {
            return;
        }

        var assignments = ScanSolContextWindowAssignments(lines);
        if (assignments.Managed &&
            assignments.ContextWindows.Count == 1 &&
            assignments.ContextWindows[0] == RecommendedSolContextWindow &&
            assignments.AutoCompactTokenLimits.Count == 1 &&
            assignments.AutoCompactTokenLimits[0] == RecommendedSolAutoCompactTokenLimit)
        {
            RemoveSolContextWindowAssignments(lines);
        }
    }

    private static SolContextWindowAssignments ScanSolContextWindowAssignments(
        IEnumerable<string> lines)
    {
        var contextWindows = new List<long?>();
        var autoCompactTokenLimits = new List<long?>();
        var managed = false;

        foreach (var line in lines)
        {
            if (ParseSectionName(line) is not null)
            {
                break;
            }

            if (line.Trim().Equals(
                    SolContextWindowManagedComment,
                    StringComparison.Ordinal))
            {
                managed = true;
            }
            else if (IsAssignment(line, ModelContextWindowKey))
            {
                contextWindows.Add(ReadLongFromAssignment(line));
            }
            else if (IsAssignment(line, ModelAutoCompactTokenLimitKey))
            {
                autoCompactTokenLimits.Add(ReadLongFromAssignment(line));
            }
        }

        return new SolContextWindowAssignments(
            contextWindows,
            autoCompactTokenLimits,
            managed);
    }

    private static long? ReadLongFromAssignment(string line)
    {
        var equals = line.IndexOf('=');
        if (equals < 0)
        {
            return null;
        }

        var value = line[(equals + 1)..];
        var comment = value.IndexOf('#');
        if (comment >= 0)
        {
            value = value[..comment];
        }

        value = value.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return long.TryParse(
            value,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
    }

    public static bool IsSolModel(string? model) =>
        string.Equals(
            model?.Trim(),
            AppPaths.DefaultOfficialModel,
            StringComparison.OrdinalIgnoreCase);

    private sealed record SolContextWindowAssignments(
        IReadOnlyList<long?> ContextWindows,
        IReadOnlyList<long?> AutoCompactTokenLimits,
        bool Managed);

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

        RemoveManagedSolContextWindowForNonSol(lines, model);

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
        if (!trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return null;
        }

        var isArrayTable = trimmed.StartsWith("[[", StringComparison.Ordinal);
        var contentStart = isArrayTable ? 2 : 1;
        var inBasicString = false;
        var inLiteralString = false;
        var escaped = false;

        for (var index = contentStart; index < trimmed.Length; index++)
        {
            var character = trimmed[index];
            if (inBasicString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inBasicString = false;
                }

                continue;
            }

            if (inLiteralString)
            {
                if (character == '\'')
                {
                    inLiteralString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inBasicString = true;
                continue;
            }

            if (character == '\'')
            {
                inLiteralString = true;
                continue;
            }

            if (character != ']' ||
                (isArrayTable &&
                 (index + 1 >= trimmed.Length || trimmed[index + 1] != ']')))
            {
                continue;
            }

            var closingLength = isArrayTable ? 2 : 1;
            var trailing = trimmed[(index + closingLength)..].TrimStart();
            if (trailing.Length > 0 && trailing[0] != '#')
            {
                return null;
            }

            var name = trimmed[contentStart..index].Trim();
            return name.Length == 0 ? null : name;
        }

        return null;
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
