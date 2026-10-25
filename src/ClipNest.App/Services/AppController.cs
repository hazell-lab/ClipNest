using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipNest.App.Platform;
using ClipNest.App.ViewModels;
using ClipNest.App.Views;
using ClipNest.Core;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using ClipNest.Core.Settings;
using ClipNest.Core.Storage;

namespace ClipNest.App.Services;

/// <summary>
/// the center of the app. it owns the tray icon, hotkey, clipboard
/// listener and windows, and connects them to the history service.
/// </summary>
internal sealed class AppController : IPanelHost, IDisposable
{
    private static readonly TimeSpan RetentionThrottle = TimeSpan.FromSeconds(30);

    private readonly AppPaths _paths;
    private readonly SettingsStore _settingsStore;
    private readonly HistoryService _history;
    private readonly PasteService _paste = new();
    private readonly DispatcherTimer _retentionTimer;

    private AppSettings _settings;
    private MessageWindow? _messageWindow;
    private ClipboardAccess? _clipboard;
    private ClipboardListener? _listener;
    private CaptureCoordinator? _capture;
    private GlobalHotkey? _hotkey;
    private TrayIcon? _tray;
    private HistoryPanel? _panel;
    private HistoryViewModel? _panelModel;
    private SettingsWindow? _settingsWindow;
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;
    private (string Message, DateTimeOffset At)? _lastSkip;
    private bool _disposed;

    public AppController(AppPaths paths, SettingsStore settingsStore, AppSettings settings)
    {
        _paths = paths;
        _settingsStore = settingsStore;
        _settings = settings;
        _history = new HistoryService(
            new ClipDatabase(paths.Database),
            new ImageStore(paths.Images),
            new SelfCopyGuard());

        _retentionTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        _retentionTimer.Tick += async (_, _) => await RunRetentionAsync(force: true);
    }

    public AppSettings Settings => _settings;

    public string DataFolder => _paths.Root;

    public HistoryService History => _history;

    public async Task StartAsync(bool launchedInBackground, IReadOnlyList<string> startupNotices)
    {
        _messageWindow = new MessageWindow();
        _messageWindow.AddHook(SettingsChangeHook);
        _clipboard = new ClipboardAccess(_messageWindow.Handle);

        _tray = new TrayIcon(_messageWindow);
        _tray.OpenRequested += (_, _) => ShowPanel();
        _tray.ToggleCaptureRequested += (_, _) => ToggleCapture();
        _tray.SettingsRequested += (_, _) => OpenSettings();
        _tray.OpenDataFolderRequested += (_, _) => OpenDataFolder();
        _tray.QuitRequested += (_, _) => Quit();
        _tray.Show();

        _hotkey = new GlobalHotkey(_messageWindow);
        _hotkey.Pressed += (_, _) => TogglePanel();
        var hotkeyOk = TryRegisterHotkey(out var hotkeyError);

        try
        {
            var opened = await _history.InitializeAsync();
            if (opened.Status == DatabaseOpenStatus.RecoveredFromCorruption)
            {
                _tray.ShowNotification(
                    "History was reset",
                    "The history file was damaged, so a new one was started. The old file was kept next to it.",
                    warning: true);
            }
        }
        catch (HistoryStorageException ex)
        {
            _tray.ShowNotification("Clipboard history is unavailable", ex.Message, warning: true);
        }

        try
        {
            _listener = new ClipboardListener(_messageWindow);
            _capture = new CaptureCoordinator(_listener, _clipboard, _history, () => _settings);
            _capture.CaptureSaved += OnCaptureSaved;
            _capture.CaptureSkipped += OnCaptureSkipped;
        }
        catch (Win32Exception ex)
        {
            AppLog.Error("clipboard capture could not start", ex);
            _tray.ShowNotification("Capture could not start", "Windows did not allow ClipNest to watch the clipboard.", warning: true);
        }

        UpdateCaptureState();

        // keep the startup entry pointing at the current exe location
        if (_settings.LaunchAtStartup)
        {
            StartupRegistration.TryApply(true, out _);
        }

        _retentionTimer.Start();
        _ = RunRetentionAsync(force: true);

        foreach (var notice in startupNotices)
        {
            _tray.ShowNotification("ClipNest", notice, warning: true);
        }

        if (!hotkeyOk && hotkeyError is not null)
        {
            _tray.ShowNotification("Shortcut not available", hotkeyError + " You can still open history from the tray icon.", warning: true);
        }
        else if (!launchedInBackground && startupNotices.Count == 0)
        {
            _tray.ShowNotification("ClipNest is running", $"Press {_settings.Hotkey} to open your clipboard history.");
        }
    }

    // panel

    public void TogglePanel()
    {
        if (_panel is { IsVisible: true, IsActive: true })
        {
            HidePanel(restoreFocus: true);
            return;
        }

        ShowPanel();
    }

    public async void ShowPanel()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            EnsurePanel();
            if (_panel!.IsVisible && _panel.IsActive)
            {
                _panel.FocusSearch();
                return;
            }

            _paste.RememberForegroundWindow();
            _panelModel!.ResetForOpen();
            _panelModel.IsCapturing = _settings.CaptureEnabled;
            if (_lastSkip is { } skip && DateTimeOffset.UtcNow - skip.At < TimeSpan.FromMinutes(2))
            {
                _panelModel.ShowNotice(skip.Message, warning: true);
            }

            _lastSkip = null;
            _panel.ShowNearCursor();
            await _panelModel.OnPanelShownAsync();
            _panel.FocusSearch();
        }
        catch (Exception ex)
        {
            AppLog.Error("the history panel could not be shown", ex);
        }
    }

    public void HidePanel(bool restoreFocus)
    {
        if (_panel is { IsVisible: true })
        {
            _panel.Hide();
        }

        if (restoreFocus)
        {
            _paste.RestoreFocus();
        }
    }

    private void EnsurePanel()
    {
        if (_panel is not null)
        {
            return;
        }

        _panelModel = new HistoryViewModel(_history, this);
        _panel = new HistoryPanel(_panelModel);
    }

    public async Task CopyEntryAsync(EntryViewModel entry, bool allowAutoPaste)
    {
        var copied = await CopyToClipboardAsync(entry.Id);
        if (!copied)
        {
            return;
        }

        if (allowAutoPaste && _settings.AutoPaste)
        {
            HidePanel(restoreFocus: false);
            var result = await _paste.PasteIntoTargetAsync();
            if (!result.Success)
            {
                // copying already worked, so the user can still paste by hand
                _tray?.ShowNotification("Copied, but not pasted", (result.Message ?? "Automatic paste failed.") + " Press Ctrl+V to paste.");
            }

            return;
        }

        HidePanel(restoreFocus: true);
    }

    /// <summary>
    /// puts an entry back on the clipboard. used by the panel and the code preview.
    /// </summary>
    public async Task<bool> CopyToClipboardAsync(long entryId)
    {
        if (_clipboard is null)
        {
            return false;
        }

        try
        {
            var payload = await _history.PrepareCopyAsync(entryId);
            if (payload is null)
            {
                Notify("That item no longer exists.");
                return false;
            }

            bool written;
            if (payload.Entry.Kind == ClipKind.Text)
            {
                written = await _clipboard.WriteTextAsync(payload.Text ?? "");
            }
            else
            {
                if (payload.ImageFileMissing || payload.ImagePath is null)
                {
                    Notify("The image file for this item is missing. You can delete the item.");
                    return false;
                }

                var path = payload.ImagePath;
                var data = await Task.Run(() => ImageCodec.PrepareForClipboard(path));
                written = await _clipboard.WriteImageAsync(data.Dib, data.Png);
            }

            if (!written)
            {
                Notify("The clipboard is busy right now. Please try again.");
            }

            return written;
        }
        catch (HistoryStorageException ex)
        {
            Notify(ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException or COMException or UnauthorizedAccessException)
        {
            AppLog.Error("an item could not be copied", ex);
            Notify("The item could not be copied.");
            return false;
        }
    }

    public void OpenCodePreview(EntryViewModel entry)
    {
        HidePanel(restoreFocus: false);
        var window = new CodePreviewWindow(entry.Entry, this);
        window.Show();
        window.Activate();
    }

    public bool Confirm(string title, string message, string confirmText, bool danger)
    {
        return RunDialog(owner => DialogWindow.Confirm(owner, title, message, confirmText, danger));
    }

    public string? Prompt(string title, string message, string? initialValue)
    {
        string? result = null;
        RunDialog(owner =>
        {
            result = DialogWindow.Prompt(owner, title, message, initialValue);
            return result is not null;
        });
        return result;
    }

    private bool RunDialog(Func<Window?, bool> show)
    {
        Window? owner = _settingsWindow is { IsActive: true } ? _settingsWindow : _panel is { IsVisible: true } ? _panel : null;

        // the panel hides when it loses focus, but not while its own dialog is open
        if (_panel is not null)
        {
            _panel.SuppressAutoHide = true;
        }

        try
        {
            return show(owner);
        }
        finally
        {
            if (_panel is not null)
            {
                _panel.SuppressAutoHide = false;
                if (_panel.IsVisible)
                {
                    _panel.Activate();
                }
            }
        }
    }

    // settings

    public void OpenSettings()
    {
        HidePanel(restoreFocus: false);
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        var model = new SettingsViewModel(_settings, _paths.Root);
        _settingsWindow = new SettingsWindow(model, this);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ResumeHotkey();
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    /// <summary>
    /// saves the settings from the form and applies them right away.
    /// </summary>
    public async Task ApplySettingsAsync(SettingsViewModel model)
    {
        var previous = _settings;
        var next = model.ToSettings(previous);
        _settings = next;

        var problems = new List<string>();
        if (!_settingsStore.TrySave(next))
        {
            problems.Add("Settings could not be written to disk.");
        }

        if (previous.Theme != next.Theme)
        {
            ThemeManager.Apply(next.Theme);
        }

        if (previous.Hotkey != next.Hotkey || _hotkey is { IsRegistered: false })
        {
            if (TryRegisterHotkey(out var error))
            {
                model.HotkeyStatus = $"{next.Hotkey} opens your clipboard history.";
                model.HotkeyStatusIsError = false;
            }
            else
            {
                model.HotkeyStatus = error;
                model.HotkeyStatusIsError = true;
            }
        }

        if (previous.LaunchAtStartup != next.LaunchAtStartup
            && !StartupRegistration.TryApply(next.LaunchAtStartup, out var startupError))
        {
            problems.Add(startupError ?? "The startup setting could not be changed.");
        }

        model.ShowSaved(next);
        UpdateCaptureState();
        await RunRetentionAsync(force: true);
        model.SaveMessage = problems.Count == 0 ? "Settings saved." : string.Join(" ", problems);
    }

    public void SuspendHotkey() => _hotkey?.Unregister();

    public void ResumeHotkey()
    {
        if (_hotkey is { IsRegistered: false })
        {
            TryRegisterHotkey(out _);
        }
    }

    public async Task<string> DescribeStorageAsync()
    {
        try
        {
            var report = await _history.GetStorageReportAsync();
            return $"{report.EntryCount:N0} items ({report.PinnedCount:N0} pinned) · about {Core.Text.ByteSize.Format(report.TotalBytes)} "
                + $"(database {Core.Text.ByteSize.Format(report.DatabaseBytes)}, images {Core.Text.ByteSize.Format(report.ImageBytes)})";
        }
        catch (HistoryStorageException ex)
        {
            return ex.Message;
        }
    }

    public async Task<string> ClearUnpinnedFromSettingsAsync()
    {
        if (!Confirm("Clear history?", "All unpinned items and their images will be deleted from this PC. Pinned items are kept.", "Clear history", danger: true))
        {
            return "Nothing was deleted.";
        }

        try
        {
            var removed = await _history.ClearUnpinnedAsync();
            return $"{removed:N0} items deleted.";
        }
        catch (HistoryStorageException ex)
        {
            return ex.Message;
        }
    }

    public async Task<string> DeleteAllDataAsync()
    {
        if (!Confirm(
                "Delete all clipboard data?",
                "Every saved item, including pinned items and all stored images, will be deleted from this PC. Your settings are kept.",
                "Delete everything",
                danger: true))
        {
            return "Nothing was deleted.";
        }

        try
        {
            await _history.DeleteEverythingAsync();
            return "All clipboard data was deleted.";
        }
        catch (HistoryStorageException ex)
        {
            return ex.Message;
        }
    }

    public void ToggleCapture()
    {
        _settings = _settings.Clone();
        _settings.CaptureEnabled = !_settings.CaptureEnabled;
        _settingsStore.TrySave(_settings);
        UpdateCaptureState();
        _settingsWindow?.OnCaptureToggledElsewhere(_settings.CaptureEnabled);
    }

    public void Quit()
    {
        _panel?.CloseForExit();
        Application.Current.Shutdown();
    }

    public void OpenDataFolder()
    {
        try
        {
            _paths.EnsureCreated();
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_paths.Root}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            AppLog.Error("data folder could not be opened", ex);
        }
    }

    // helpers

    private bool TryRegisterHotkey(out string? error)
    {
        error = null;
        if (_hotkey is null)
        {
            return false;
        }

        var gesture = HotkeyGesture.ParseOrDefault(_settings.Hotkey);
        var ok = _hotkey.TryRegister(gesture, out error);
        _tray?.UpdateState(_settings.CaptureEnabled, ok ? gesture.ToString() : "");
        return ok;
    }

    private void UpdateCaptureState()
    {
        _tray?.UpdateState(_settings.CaptureEnabled, _hotkey is { IsRegistered: true } ? _settings.Hotkey : "");
        if (_panelModel is not null)
        {
            _panelModel.IsCapturing = _settings.CaptureEnabled;
        }
    }

    private void OnCaptureSaved(object? sender, EventArgs e)
    {
        if (_panelModel is null)
        {
            return;
        }

        // a hidden panel reloads when it opens, so only a visible one needs a refresh
        if (_panel is { IsVisible: true })
        {
            _ = _panelModel.ReloadAsync();
            _ = _panelModel.RefreshFooterAsync();
        }

        _ = RunRetentionAsync(force: false);
    }

    private void OnCaptureSkipped(object? sender, string message)
    {
        if (_panel is { IsVisible: true } && _panelModel is not null)
        {
            _panelModel.ShowNotice(message, warning: true);
        }
        else
        {
            _lastSkip = (message, DateTimeOffset.UtcNow);
        }
    }

    private void Notify(string message)
    {
        if (_panel is { IsVisible: true } && _panelModel is not null)
        {
            _panelModel.ShowNotice(message, warning: true);
        }
        else
        {
            _tray?.ShowNotification("ClipNest", message, warning: true);
        }
    }

    private async Task RunRetentionAsync(bool force)
    {
        if (!_history.IsReady)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (!force && now - _lastRetention < RetentionThrottle)
        {
            return;
        }

        _lastRetention = now;
        try
        {
            var limits = new RetentionLimits(_settings.MaxHistoryItems, _settings.MaxStorageBytes, _settings.RetentionDays);
            var removed = await _history.ApplyRetentionAsync(limits);
            if (removed > 0)
            {
                AppLog.Info($"cleanup removed {removed} old items");
            }
        }
        catch (HistoryStorageException ex)
        {
            AppLog.Error("cleanup failed", ex);
        }
    }

    private IntPtr SettingsChangeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_SETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // windows sends this name when the light or dark app mode changes
            var area = Marshal.PtrToStringUni(lParam);
            if (area == "ImmersiveColorSet")
            {
                ThemeManager.OnSystemSettingChanged();
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _retentionTimer.Stop();

        if (_capture is not null)
        {
            _capture.CaptureSaved -= OnCaptureSaved;
            _capture.CaptureSkipped -= OnCaptureSkipped;
            _capture.Dispose();
        }

        _listener?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _panel?.CloseForExit();
        _settingsWindow?.Close();
        if (_messageWindow is not null)
        {
            _messageWindow.RemoveHook(SettingsChangeHook);
            _messageWindow.Dispose();
        }

        ClipDatabase.ReleaseAllConnections();
    }
}
