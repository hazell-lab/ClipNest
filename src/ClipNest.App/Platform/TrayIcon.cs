using System;
using System.Runtime.InteropServices;
using ClipNest.Core.Diagnostics;

namespace ClipNest.App.Platform;

/// <summary>
/// the notification area icon with its menu, built on the shell api.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const int IconId = 1;
    private const int CallbackMessage = NativeMethods.WM_APP + 1;

    private const int CommandOpen = 1;
    private const int CommandToggleCapture = 2;
    private const int CommandSettings = 3;
    private const int CommandOpenDataFolder = 4;
    private const int CommandQuit = 5;

    private readonly MessageWindow _window;
    private readonly int _taskbarCreatedMessage;
    private IntPtr _activeIcon;
    private IntPtr _pausedIcon;
    private bool _added;
    private bool _capturing = true;
    private string _hotkeyText = "";

    public TrayIcon(MessageWindow window)
    {
        _window = window;
        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessageW("TaskbarCreated");
        _window.AddHook(WndProc);
        LoadIcons();
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleCaptureRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? OpenDataFolderRequested;
    public event EventHandler? QuitRequested;

    public void Show()
    {
        var data = CreateData(NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        _added = NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_ADD, ref data);
        if (!_added)
        {
            AppLog.Warn("tray icon could not be added");
            return;
        }

        // version 4 gives us modern click and keyboard messages
        data.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_SETVERSION, ref data);
    }

    public void UpdateState(bool capturing, string hotkeyText)
    {
        _capturing = capturing;
        _hotkeyText = hotkeyText;
        if (!_added)
        {
            return;
        }

        var data = CreateData(NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP);
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    public void ShowNotification(string title, string message, bool warning = false)
    {
        if (!_added)
        {
            return;
        }

        var data = CreateData(NativeMethods.NIF_INFO);
        data.szInfoTitle = Truncate(title, 63);
        data.szInfo = Truncate(message, 255);
        data.dwInfoFlags = warning ? NativeMethods.NIIF_WARNING : NativeMethods.NIIF_INFO;
        NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref data);
    }

    private NativeMethods.NOTIFYICONDATAW CreateData(uint flags)
    {
        var tip = _capturing ? "ClipNest - capturing clipboard" : "ClipNest - capture paused";
        return new NativeMethods.NOTIFYICONDATAW
        {
            cbSize = Marshal.SizeOf<NativeMethods.NOTIFYICONDATAW>(),
            hWnd = _window.Handle,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _capturing ? _activeIcon : _pausedIcon,
            szTip = tip,
            szInfo = "",
            szInfoTitle = "",
        };
    }

    private void LoadIcons()
    {
        var dpi = NativeMethods.GetDpiForWindow(_window.Handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var size = NativeMethods.GetSystemMetricsForDpi(NativeMethods.SM_CXSMICON, dpi);
        if (size <= 0)
        {
            size = 16;
        }

        _activeIcon = IconLoader.LoadFromResource("Assets/TrayActive.ico", size);
        _pausedIcon = IconLoader.LoadFromResource("Assets/TrayPaused.ico", size);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMessage && _taskbarCreatedMessage != 0)
        {
            // explorer restarted, so the icon has to be added again
            _added = false;
            Show();
            return IntPtr.Zero;
        }

        if (msg != CallbackMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var eventId = (int)(lParam.ToInt64() & 0xFFFF);
        switch (eventId)
        {
            case NativeMethods.NIN_SELECT:
            case NativeMethods.NIN_KEYSELECT:
                OpenRequested?.Invoke(this, EventArgs.Empty);
                break;

            case NativeMethods.WM_CONTEXTMENU:
                var x = (short)(wParam.ToInt64() & 0xFFFF);
                var y = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                ShowMenu(x, y);
                break;
        }

        return IntPtr.Zero;
    }

    private void ShowMenu(int x, int y)
    {
        var menu = NativeMethods.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var openText = string.IsNullOrEmpty(_hotkeyText) ? "Open clipboard history" : $"Open clipboard history\t{_hotkeyText}";
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CommandOpen, openText);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CommandToggleCapture, _capturing ? "Pause capture" : "Resume capture");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CommandSettings, "Settings…");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CommandOpenDataFolder, "Open data folder");
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, UIntPtr.Zero, null);
            NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, (UIntPtr)CommandQuit, "Quit ClipNest");
            NativeMethods.SetMenuDefaultItem(menu, CommandOpen, 0);

            // the menu only closes properly when our window is in front
            NativeMethods.SetForegroundWindow(_window.Handle);
            var command = NativeMethods.TrackPopupMenuEx(
                menu,
                NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_BOTTOMALIGN,
                x,
                y,
                _window.Handle,
                IntPtr.Zero);
            NativeMethods.PostMessageW(_window.Handle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case CommandOpen:
                    OpenRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandToggleCapture:
                    ToggleCaptureRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandSettings:
                    SettingsRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandOpenDataFolder:
                    OpenDataFolderRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case CommandQuit:
                    QuitRequested?.Invoke(this, EventArgs.Empty);
                    break;
            }
        }
        finally
        {
            NativeMethods.DestroyMenu(menu);
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public void Dispose()
    {
        if (_added)
        {
            var data = CreateData(0);
            NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_DELETE, ref data);
            _added = false;
        }

        _window.RemoveHook(WndProc);
        if (_activeIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_activeIcon);
            _activeIcon = IntPtr.Zero;
        }

        if (_pausedIcon != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_pausedIcon);
            _pausedIcon = IntPtr.Zero;
        }
    }
}
