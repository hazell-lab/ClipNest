using System;
using System.Collections.Generic;
using ClipNest.Core.Capture;

namespace ClipNest.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

/// <summary>
/// all user settings. saved as json in the app data folder.
/// </summary>
public sealed class AppSettings
{
    public const int MinHistoryItems = 10;
    public const int MaxHistoryItemsLimit = 20_000;
    public const int MinStorageMb = 16;
    public const int MaxStorageMbLimit = 10_240;
    public const int MaxRetentionDays = 3_650;

    public int SchemaVersion { get; set; } = 1;

    public string Hotkey { get; set; } = HotkeyGesture.DefaultText;

    public bool CaptureEnabled { get; set; } = true;

    public bool CaptureImages { get; set; } = true;

    public bool LaunchAtStartup { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // limit for unpinned items, pinned items are never removed automatically
    public int MaxHistoryItems { get; set; } = 500;

    public int MaxStorageMb { get; set; } = 256;

    // zero means keep items until other limits remove them
    public int RetentionDays { get; set; } = 30;

    public bool AutoPaste { get; set; }

    public List<string> ExcludedApps { get; set; } = new(AppExclusionList.Defaults);

    public long MaxStorageBytes => MaxStorageMb * 1024L * 1024L;

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.ExcludedApps = new List<string>(ExcludedApps ?? new List<string>());
        return copy;
    }

    /// <summary>
    /// returns a copy with every value moved into its allowed range.
    /// </summary>
    public AppSettings Normalized()
    {
        var copy = Clone();
        copy.SchemaVersion = 1;
        copy.Hotkey = HotkeyGesture.ParseOrDefault(copy.Hotkey).ToString();
        copy.MaxHistoryItems = Math.Clamp(copy.MaxHistoryItems, MinHistoryItems, MaxHistoryItemsLimit);
        copy.MaxStorageMb = Math.Clamp(copy.MaxStorageMb, MinStorageMb, MaxStorageMbLimit);
        copy.RetentionDays = Math.Clamp(copy.RetentionDays, 0, MaxRetentionDays);
        if (!Enum.IsDefined(copy.Theme))
        {
            copy.Theme = AppTheme.Dark;
        }

        copy.ExcludedApps = AppExclusionList.Clean(copy.ExcludedApps);
        return copy;
    }
}
