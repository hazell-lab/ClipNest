using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using ClipNest.App.Platform;
using ClipNest.App.Services;
using ClipNest.Core;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.Settings;

namespace ClipNest.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppController? _controller;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // a second launch just asks the running copy to open its panel
        _instance = SingleInstance.TryAcquire();
        if (_instance is null)
        {
            Shutdown();
            return;
        }

        var paths = AppPaths.ForCurrentUser();
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"ClipNest could not create its data folder:\n{paths.Root}\n\n{ex.Message}",
                "ClipNest",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return;
        }

        AppLog.Initialize(paths.Logs);
        AppLog.Info("starting");

        var store = new SettingsStore(paths.Settings);
        var loaded = store.Load();
        var notices = new List<string>();
        if (loaded.Status == SettingsLoadStatus.RecoveredFromDamage)
        {
            notices.Add("Your settings file was damaged, so defaults are being used. A copy of the old file was kept.");
        }
        else if (loaded.Status == SettingsLoadStatus.UnreadableUsingDefaults)
        {
            notices.Add("Your settings file could not be read, so defaults are being used for now.");
        }

        ThemeManager.Apply(loaded.Settings.Theme);

        _controller = new AppController(paths, store, loaded.Settings);
        _instance.ListenForActivation(() => Dispatcher.BeginInvoke(new Action(() => _controller?.ShowPanel())));

        var background = e.Args.Any(a => string.Equals(a, StartupRegistration.BackgroundArgument, StringComparison.OrdinalIgnoreCase));
        try
        {
            await _controller.StartAsync(background, notices);
        }
        catch (Exception ex)
        {
            AppLog.Error("startup failed", ex);
            MessageBox.Show("ClipNest could not start.\n\n" + ex.Message, "ClipNest", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // release the clipboard listener, hotkey, tray icon and database
        _controller?.Dispose();
        _instance?.Dispose();
        AppLog.Info("stopped");
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // keep running when something unexpected happens, and note it in the log
        AppLog.Error("unexpected error", e.Exception);
        e.Handled = true;
    }
}
