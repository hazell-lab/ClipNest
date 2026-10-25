using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using ClipNest.Core.Capture;
using ClipNest.Core.Settings;

namespace ClipNest.App.ViewModels;

public sealed record ThemeOption(AppTheme Mode, string Name);

/// <summary>
/// an editable copy of the settings shown in the settings window.
/// </summary>
internal sealed class SettingsViewModel : ObservableObject
{
    private string _hotkeyText;
    private string? _hotkeyStatus;
    private bool _hotkeyStatusIsError;
    private bool _captureEnabled;
    private bool _captureImages;
    private bool _launchAtStartup;
    private ThemeOption _theme;
    private string _maxHistoryItems;
    private string _maxStorageMb;
    private string _retentionDays;
    private bool _autoPaste;
    private string _newExcludedApp = "";
    private string? _selectedExcludedApp;
    private string _storageText = "Measuring…";
    private string? _saveMessage;

    public SettingsViewModel(AppSettings settings, string dataFolder)
    {
        DataFolder = dataFolder;
        _hotkeyText = settings.Hotkey;
        _captureEnabled = settings.CaptureEnabled;
        _captureImages = settings.CaptureImages;
        _launchAtStartup = settings.LaunchAtStartup;
        _theme = Themes.First(t => t.Mode == settings.Theme);
        _maxHistoryItems = settings.MaxHistoryItems.ToString(CultureInfo.CurrentCulture);
        _maxStorageMb = settings.MaxStorageMb.ToString(CultureInfo.CurrentCulture);
        _retentionDays = settings.RetentionDays.ToString(CultureInfo.CurrentCulture);
        _autoPaste = settings.AutoPaste;
        ExcludedApps = new ObservableCollection<string>(settings.ExcludedApps);
    }

    public IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption(AppTheme.Dark, "Dark"),
        new ThemeOption(AppTheme.Light, "Light"),
        new ThemeOption(AppTheme.System, "Same as Windows"),
    };

    public string DataFolder { get; }

    public string HotkeyText
    {
        get => _hotkeyText;
        set => SetProperty(ref _hotkeyText, value);
    }

    public string? HotkeyStatus
    {
        get => _hotkeyStatus;
        set
        {
            if (SetProperty(ref _hotkeyStatus, value))
            {
                OnPropertyChanged(nameof(HasHotkeyStatus));
            }
        }
    }

    public bool HasHotkeyStatus => !string.IsNullOrEmpty(_hotkeyStatus);

    public bool HotkeyStatusIsError
    {
        get => _hotkeyStatusIsError;
        set => SetProperty(ref _hotkeyStatusIsError, value);
    }

    public bool CaptureEnabled
    {
        get => _captureEnabled;
        set => SetProperty(ref _captureEnabled, value);
    }

    public bool CaptureImages
    {
        get => _captureImages;
        set => SetProperty(ref _captureImages, value);
    }

    public bool LaunchAtStartup
    {
        get => _launchAtStartup;
        set => SetProperty(ref _launchAtStartup, value);
    }

    public ThemeOption Theme
    {
        get => _theme;
        set => SetProperty(ref _theme, value);
    }

    public string MaxHistoryItems
    {
        get => _maxHistoryItems;
        set => SetProperty(ref _maxHistoryItems, value);
    }

    public string MaxStorageMb
    {
        get => _maxStorageMb;
        set => SetProperty(ref _maxStorageMb, value);
    }

    public string RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    public bool AutoPaste
    {
        get => _autoPaste;
        set => SetProperty(ref _autoPaste, value);
    }

    public ObservableCollection<string> ExcludedApps { get; }

    public string NewExcludedApp
    {
        get => _newExcludedApp;
        set => SetProperty(ref _newExcludedApp, value);
    }

    public string? SelectedExcludedApp
    {
        get => _selectedExcludedApp;
        set => SetProperty(ref _selectedExcludedApp, value);
    }

    public string StorageText
    {
        get => _storageText;
        set => SetProperty(ref _storageText, value);
    }

    public string? SaveMessage
    {
        get => _saveMessage;
        set
        {
            if (SetProperty(ref _saveMessage, value))
            {
                OnPropertyChanged(nameof(HasSaveMessage));
            }
        }
    }

    public bool HasSaveMessage => !string.IsNullOrEmpty(_saveMessage);

    public bool AddExcludedApp()
    {
        var name = NewExcludedApp.Trim();
        if (name.Length == 0)
        {
            return false;
        }

        if (!AppExclusionList.IsExcluded(ExcludedApps, name))
        {
            ExcludedApps.Add(name);
        }

        NewExcludedApp = "";
        return true;
    }

    public void RemoveSelectedExcludedApp()
    {
        if (SelectedExcludedApp is { } selected)
        {
            ExcludedApps.Remove(selected);
        }
    }

    /// <summary>
    /// builds settings from the form. text boxes that are not numbers keep
    /// their old values, and everything is moved into its allowed range.
    /// </summary>
    public AppSettings ToSettings(AppSettings current)
    {
        var result = current.Clone();
        result.Hotkey = HotkeyText;
        result.CaptureEnabled = CaptureEnabled;
        result.CaptureImages = CaptureImages;
        result.LaunchAtStartup = LaunchAtStartup;
        result.Theme = Theme.Mode;
        result.MaxHistoryItems = ParseOr(MaxHistoryItems, current.MaxHistoryItems);
        result.MaxStorageMb = ParseOr(MaxStorageMb, current.MaxStorageMb);
        result.RetentionDays = ParseOr(RetentionDays, current.RetentionDays);
        result.AutoPaste = AutoPaste;
        result.ExcludedApps = ExcludedApps.ToList();
        return result.Normalized();
    }

    /// <summary>
    /// shows the cleaned values after saving, so clamped numbers are visible.
    /// </summary>
    public void ShowSaved(AppSettings saved)
    {
        MaxHistoryItems = saved.MaxHistoryItems.ToString(CultureInfo.CurrentCulture);
        MaxStorageMb = saved.MaxStorageMb.ToString(CultureInfo.CurrentCulture);
        RetentionDays = saved.RetentionDays.ToString(CultureInfo.CurrentCulture);
        HotkeyText = saved.Hotkey;
    }

    private static int ParseOr(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var value)
            ? value
            : fallback;
}
