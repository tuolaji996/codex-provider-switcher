using System.IO;
using System.Windows;
using System.Windows.Media;
using CodexProviderSwitcher.Core;
using Microsoft.Win32;

namespace CodexProviderSwitcher;

internal static class ThemeManager
{
    private static readonly IReadOnlyDictionary<string, string> LightPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#F5F6F8",
            ["NavigationBrush"] = "#FFFFFF",
            ["SurfaceBrush"] = "#FFFFFF",
            ["SurfaceAltBrush"] = "#F0F2F5",
            ["SurfaceHoverBrush"] = "#E9EDF2",
            ["BorderBrush"] = "#D9DEE5",
            ["TextBrush"] = "#1B2027",
            ["MutedTextBrush"] = "#66717E",
            ["SubtleTextBrush"] = "#66717E",
            ["InputBrush"] = "#FFFFFF",
            ["AccentBrush"] = "#16845D",
            ["AccentHoverBrush"] = "#106C4B",
            ["AccentForegroundBrush"] = "#FFFFFF",
            ["OfficialStatusBrush"] = "#2563C7",
            ["ThirdPartyStatusBrush"] = "#16845D",
            ["WarningBrush"] = "#A65A00",
            ["WarningStatusBrush"] = "#A65A00",
            ["WarningSurfaceBrush"] = "#FFF4E5",
            ["DangerBrush"] = "#C73535",
            ["DangerHoverBrush"] = "#A82D2D",
            ["StatusBadgeForegroundBrush"] = "#FFFFFF",
            ["NeutralStatusBrush"] = "#7C8794",
            ["StatusBarBrush"] = "#F9FAFB",
            ["SelectionBrush"] = "#DDEFE8"
        };

    private static readonly IReadOnlyDictionary<string, string> DarkPalette =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["WindowBrush"] = "#171A1F",
            ["NavigationBrush"] = "#111418",
            ["SurfaceBrush"] = "#1E2228",
            ["SurfaceAltBrush"] = "#242A31",
            ["SurfaceHoverBrush"] = "#2A313A",
            ["BorderBrush"] = "#343C46",
            ["TextBrush"] = "#F2F5F7",
            ["MutedTextBrush"] = "#A7B0BA",
            ["SubtleTextBrush"] = "#7F8A96",
            ["InputBrush"] = "#171B20",
            ["AccentBrush"] = "#31C88C",
            ["AccentHoverBrush"] = "#43D69C",
            ["AccentForegroundBrush"] = "#07140F",
            ["OfficialStatusBrush"] = "#3568C8",
            ["ThirdPartyStatusBrush"] = "#16845D",
            ["WarningBrush"] = "#E5A33D",
            ["WarningStatusBrush"] = "#9A5A00",
            ["WarningSurfaceBrush"] = "#342817",
            ["DangerBrush"] = "#F06B6B",
            ["DangerHoverBrush"] = "#FF8080",
            ["StatusBadgeForegroundBrush"] = "#FFFFFF",
            ["NeutralStatusBrush"] = "#7F8A96",
            ["StatusBarBrush"] = "#14171B",
            ["SelectionBrush"] = "#203A31"
        };

    public static UiThemePreference ResolvedTheme { get; private set; } =
        UiThemePreference.Light;

    public static void Apply(string? preferenceCode)
    {
        var preference = ThemePreference.Parse(preferenceCode);
        ResolvedTheme = preference == UiThemePreference.System
            ? ReadSystemTheme()
            : preference;

        var palette = ResolvedTheme == UiThemePreference.Dark
            ? DarkPalette
            : LightPalette;
        foreach (var (key, color) in palette)
        {
            Application.Current.Resources[key] =
                new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }

    private static UiThemePreference ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0
                ? UiThemePreference.Dark
                : UiThemePreference.Light;
        }
        catch (Exception exception) when (
            exception is UnauthorizedAccessException or
            IOException or
            System.Security.SecurityException)
        {
            return UiThemePreference.Light;
        }
    }
}
