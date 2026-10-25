using System;
using System.ComponentModel;
using ClipNest.Core.Diagnostics;

namespace ClipNest.App.Platform;

/// <summary>
/// tells us when the clipboard changes, using the windows clipboard listener
/// instead of checking over and over.
/// </summary>
internal sealed class ClipboardListener : IDisposable
{
    private readonly MessageWindow _window;
    private bool _registered;

    public ClipboardListener(MessageWindow window)
    {
        _window = window;
        _window.AddHook(WndProc);
        _registered = NativeMethods.AddClipboardFormatListener(window.Handle);
        if (!_registered)
        {
            var error = new Win32Exception();
            AppLog.Error("clipboard listener could not be registered", error);
            throw error;
        }
    }

    public event EventHandler? ClipboardChanged;

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.RemoveClipboardFormatListener(_window.Handle);
            _registered = false;
        }

        _window.RemoveHook(WndProc);
    }
}
