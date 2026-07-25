using System.Globalization;

namespace CodexProviderSwitcher.Core;

public enum AppLanguage
{
    Chinese,
    English
}

public static class Localizer
{
    public const string ChineseCode = "zh-CN";
    public const string EnglishCode = "en-US";

    public static AppLanguage Current { get; private set; } = AppLanguage.Chinese;

    public static void Use(string? code) => Use(Parse(code));

    public static void Use(AppLanguage language)
    {
        Current = language;
        var culture = CultureInfo.GetCultureInfo(ToCode(language));
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    public static AppLanguage Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return AppLanguage.Chinese;
        }

        var normalized = code.Trim();
        return normalized.Equals("ENG", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? AppLanguage.English
            : AppLanguage.Chinese;
    }

    public static string NormalizeCode(string? code) => ToCode(Parse(code));

    public static string ToCode(AppLanguage language) =>
        language == AppLanguage.English ? EnglishCode : ChineseCode;

    public static string Text(string chinese, string english) =>
        Current == AppLanguage.English ? english : chinese;

    public static string Format(
        string chineseFormat,
        string englishFormat,
        params object?[] arguments) =>
        string.Format(
            CultureInfo.GetCultureInfo(ToCode(Current)),
            Text(chineseFormat, englishFormat),
            arguments);
}
