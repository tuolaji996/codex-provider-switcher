using System.Text.RegularExpressions;

namespace CodexProviderSwitcher.Core;

public static partial class CredentialTargetFactory
{
    private const string ProfilePrefix = "CodexProviderSwitcher:provider:";

    public static string CreateForProfileId(string profileId)
    {
        if (!Guid.TryParse(profileId, out var parsed))
        {
            throw new ArgumentException(
                "A provider profile ID must be a GUID.",
                nameof(profileId));
        }

        return $"{ProfilePrefix}{parsed:N}";
    }

    public static bool IsValid(string? target) =>
        !string.IsNullOrWhiteSpace(target) &&
        (target.Equals(
             AppPaths.LegacySuiXiangCredentialTarget,
             StringComparison.Ordinal) ||
         ProfileTargetRegex().IsMatch(target));

    public static string RequireValid(string target)
    {
        if (!IsValid(target))
        {
            throw new ArgumentException(
                "The credential target is not managed by Codex Provider Switcher.",
                nameof(target));
        }

        return target;
    }

    [GeneratedRegex(
        "^CodexProviderSwitcher:provider:[0-9a-f]{32}$",
        RegexOptions.CultureInvariant)]
    private static partial Regex ProfileTargetRegex();
}
