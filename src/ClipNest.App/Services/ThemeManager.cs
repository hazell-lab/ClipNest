using System;
using System.Windows;
using ClipNest.App.Platform;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.Settings;
using Microsoft.Win32;

namespace ClipNest.App.Services;

/// <summary>
/// swaps the color dictionary between dark and light.
/// all controls use dynamic resources, so they update right away.
/// </summary>
internal static class ThemeManager
{
    private const string DarkUri = "pack://application:,,,/ClipNest;component/Themes/Dark.xaml";
    private const string LightUri = "pack://application:,,,/ClipNest;component/Themes/Light.xaml";

    private static AppTheme _mode = AppTheme.Dark;

    public static bool IsDark { get; private set; } = true;

    public static event EventHandler? ThemeChanged;

    public static void Apply(AppTheme mode)
    {
        _mode = mode;
        var dark = mode switch
        {
            AppTheme.Light => false,
            AppTheme.System => !SystemUsesLightTheme(),
            _ => true,
        };

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var colors = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkUri : LightUri, UriKind.Absolute),
        };

        // the colors always live in the first slot
        if (dictionaries.Count > 0)
        {
            dictionaries[0] = colors;
        }
        else
        {
            dictionaries.Add(colors);
        }

        IsDark = dark;
        foreach (Window window in Application.Current.Windows)
        {
            WindowHelpers.ApplyTitleBarTheme(window, dark);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// call when windows reports a settings change, so "same as windows" follows along.
    /// </summary>
    public static void OnSystemSettingChanged()
    {
        if (_mode == AppTheme.System)
        {
            Apply(AppTheme.System);
        }
    }

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            AppLog.Warn("could not read the windows theme setting");
            return false;
        }
    }
}
