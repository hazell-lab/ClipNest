using System;
using System.Runtime.InteropServices;
using ClipNest.Core.Settings;

namespace ClipNest.App.Platform;

/// <summary>
/// one system wide keyboard shortcut that opens the history panel.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x434E;
    private readonly MessageWindow _window;
    private bool _registered;

    public GlobalHotkey(MessageWindow window)
    {
        _window = window;
        _window.AddHook(WndProc);
    }

    public event EventHandler? Pressed;

    public HotkeyGesture? Current { get; private set; }

    public bool IsRegistered => _registered;

    /// <summary>
    /// tries to take the shortcut. on failure the old one is not restored,
    /// so the caller decides what to do next.
    /// </summary>
    public bool TryRegister(HotkeyGesture gesture, out string? error)
    {
        Unregister();
        error = null;

        var virtualKey = gesture.VirtualKey;
        if (virtualKey == 0)
        {
            error = "That key cannot be used for a shortcut.";
            return false;
        }

        var modifiers = (uint)gesture.Modifiers | NativeMethods.MOD_NOREPEAT;
        if (!NativeMethods.RegisterHotKey(_window.Handle, HotkeyId, modifiers, (uint)virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"{gesture} is already used by another app. Pick a different shortcut in Settings."
                : $"Windows did not accept {gesture} as a shortcut (error {code}).";
            return false;
        }

        _registered = true;
        Current = gesture;
        return true;
    }

    public void Unregister()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_window.Handle, HotkeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        Unregister();
        _window.RemoveHook(WndProc);
    }
}
