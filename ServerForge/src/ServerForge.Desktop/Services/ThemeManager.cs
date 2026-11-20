using System.Windows;
using System.Windows.Media;

namespace ServerForge.Desktop.Services;

/// <summary>
/// Synchronizes Windows 11 WPF ThemeMode with ServerForge's dynamic Fluent palette brushes
/// so both Dark and Light themes maintain accessible contrast across all 13 native screens.
/// </summary>
public static class ThemeManager
{
    public static void ApplyTheme(bool isDark)
    {
        var app = System.Windows.Application.Current;
        if (app is null)
            return;

#pragma warning disable WPF0001
        app.ThemeMode = isDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        if (isDark)
        {
            SetBrush(app, "CanvasBackgroundBrush", "#0F141C");
            SetBrush(app, "SidebarBackgroundBrush", "#141A24");
            SetBrush(app, "CardSurfaceBrush", "#18202C");
            SetBrush(app, "CardBorderBrush", "#263244");
            SetBrush(app, "TextPrimaryBrush", "#F3F6FA");
            SetBrush(app, "TextSecondaryBrush", "#9BA7B8");
            SetBrush(app, "AccentBrush", "#3B82F6");
            SetBrush(app, "AccentHoverBrush", "#2563EB");
            SetBrush(app, "SuccessBrush", "#16A34A");
            SetBrush(app, "WarningBrush", "#D97706");
            SetBrush(app, "DangerBrush", "#DC2626");
        }
        else
        {
            SetBrush(app, "CanvasBackgroundBrush", "#F4F6F9");
            SetBrush(app, "SidebarBackgroundBrush", "#EAEFF5");
            SetBrush(app, "CardSurfaceBrush", "#FFFFFF");
            SetBrush(app, "CardBorderBrush", "#D0D9E4");
            SetBrush(app, "TextPrimaryBrush", "#0F172A");
            SetBrush(app, "TextSecondaryBrush", "#475569");
            SetBrush(app, "AccentBrush", "#2563EB");
            SetBrush(app, "AccentHoverBrush", "#1D4ED8");
            SetBrush(app, "SuccessBrush", "#15803D");
            SetBrush(app, "WarningBrush", "#B45309");
            SetBrush(app, "DangerBrush", "#B91C1C");
        }
    }

    private static void SetBrush(System.Windows.Application app, string key, string hexColor)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hexColor);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }
}
