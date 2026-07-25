namespace CodexProviderSwitcher.Core;

public enum UiThemePreference
{
    Light,
    Dark,
    System
}

public static class ThemePreference
{
    public const string LightCode = "light";
    public const string DarkCode = "dark";
    public const string SystemCode = "system";

    public static UiThemePreference Parse(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return UiThemePreference.Light;
        }

        return code.Trim().ToLowerInvariant() switch
        {
            DarkCode => UiThemePreference.Dark,
            SystemCode => UiThemePreference.System,
            _ => UiThemePreference.Light
        };
    }

    public static string NormalizeCode(string? code) => ToCode(Parse(code));

    public static string ToCode(UiThemePreference preference) =>
        preference switch
        {
            UiThemePreference.Dark => DarkCode,
            UiThemePreference.System => SystemCode,
            _ => LightCode
        };
}
