using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClipNest.App.Platform;

internal sealed record PasteResult(bool Success, string? Message)
{
    public static PasteResult Ok { get; } = new(true, null);

    public static PasteResult Failed(string message) => new(false, message);
}

/// <summary>
/// remembers which window was active before the panel opened, and can
/// paste into it by sending ctrl+v. pasting is optional and off by default.
/// </summary>
internal sealed class PasteService
{
    public IntPtr Target { get; private set; }

    /// <summary>
    /// call this right before the panel opens.
    /// </summary>
    public void RememberForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || WindowHelpers.IsOwnWindow(hwnd))
        {
            return;
        }

        // the taskbar is in front after a tray click, it is not a useful target
        Target = WindowHelpers.IsShellWindow(hwnd) ? IntPtr.Zero : hwnd;
    }

    /// <summary>
    /// gives focus back to the window the user was using.
    /// </summary>
    public bool RestoreFocus()
    {
        return Target != IntPtr.Zero && NativeMethods.IsWindow(Target) && NativeMethods.SetForegroundWindow(Target);
    }

    public async Task<PasteResult> PasteIntoTargetAsync()
    {
        var target = Target;
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target))
        {
            return PasteResult.Failed("There is no previous window to paste into.");
        }

        NativeMethods.GetWindowThreadProcessId(target, out var processId);

        // windows blocks fake key presses sent to apps running as administrator
        if (!WindowHelpers.IsCurrentProcessElevated() && WindowHelpers.IsProcessElevated(processId))
        {
            return PasteResult.Failed("The target app runs as administrator, so Windows blocks automatic paste.");
        }

        NativeMethods.SetForegroundWindow(target);
        var focused = false;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(30);
            if (NativeMethods.GetForegroundWindow() == target)
            {
                focused = true;
                break;
            }
        }

        if (!focused)
        {
            return PasteResult.Failed("Windows did not let ClipNest switch back to the previous window.");
        }

        // wait for the user to let go of shift, alt or win so they do not mix with ctrl+v
        for (var attempt = 0; attempt < 20 && AnyModifierHeld(); attempt++)
        {
            await Task.Delay(25);
        }

        var inputs = new[]
        {
            Key(NativeMethods.VK_CONTROL, up: false),
            Key(NativeMethods.VK_V, up: false),
            Key(NativeMethods.VK_V, up: true),
            Key(NativeMethods.VK_CONTROL, up: true),
        };

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
        return sent == inputs.Length
            ? PasteResult.Ok
            : PasteResult.Failed("Windows blocked the paste key press.");
    }

    private static bool AnyModifierHeld()
    {
        static bool Down(ushort key) => (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
        return Down(NativeMethods.VK_SHIFT) || Down(NativeMethods.VK_MENU)
            || Down(NativeMethods.VK_LWIN) || Down(NativeMethods.VK_RWIN);
    }

    private static NativeMethods.INPUT Key(ushort virtualKey, bool up) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        u = new NativeMethods.InputUnion
        {
            ki = new NativeMethods.KEYBDINPUT
            {
                wVk = virtualKey,
                dwFlags = up ? NativeMethods.KEYEVENTF_KEYUP : 0,
            },
        },
    };
}
