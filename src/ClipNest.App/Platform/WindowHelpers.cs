using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ClipNest.App.Platform;

/// <summary>
/// small helpers about windows and the processes that own them.
/// </summary>
internal static class WindowHelpers
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static readonly string[] ShellClasses =
    {
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "TopLevelWindowForOverflowXamlIsland",
        "Progman",
        "WorkerW",
    };

    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    /// <summary>
    /// true for the taskbar and desktop, which are not useful paste targets.
    /// </summary>
    public static bool IsShellWindow(IntPtr hwnd)
    {
        var builder = new StringBuilder(256);
        if (GetClassNameW(hwnd, builder, builder.Capacity) == 0)
        {
            return false;
        }

        var name = builder.ToString();
        foreach (var shell in ShellClasses)
        {
            if (string.Equals(shell, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // the process ended or cannot be inspected
            return null;
        }
    }

    /// <summary>
    /// best guess of which app put data on the clipboard. the clipboard owner
    /// is often a hidden window, so the foreground app is used as a fallback.
    /// </summary>
    public static string? GuessClipboardSourceApp()
    {
        var owner = NativeMethods.GetClipboardOwner();
        var name = GetProcessName(owner);
        if (name is not null)
        {
            return name;
        }

        return GetProcessName(NativeMethods.GetForegroundWindow());
    }

    /// <summary>
    /// returns true when the process runs as administrator, or when we are not
    /// allowed to look, which usually means the same thing.
    /// </summary>
    public static bool IsProcessElevated(uint processId)
    {
        var process = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return true;
        }

        try
        {
            if (!NativeMethods.OpenProcessToken(process, NativeMethods.TOKEN_QUERY, out var token))
            {
                return true;
            }

            try
            {
                return NativeMethods.GetTokenInformation(token, NativeMethods.TokenElevation, out var elevated, sizeof(int), out _)
                    && elevated != 0;
            }
            finally
            {
                NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            NativeMethods.CloseHandle(process);
        }
    }

    public static bool IsCurrentProcessElevated() => IsProcessElevated((uint)Environment.ProcessId);

    /// <summary>
    /// asks windows 11 to draw a dark or light title bar for one of our windows.
    /// </summary>
    public static void ApplyTitleBarTheme(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var value = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
    }

    /// <summary>
    /// places the panel near the mouse, on the monitor the mouse is on,
    /// and keeps it fully inside that monitor's work area.
    /// </summary>
    public static void PlaceNearCursor(Window window)
    {
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfoW(monitor, ref info))
        {
            return;
        }

        // window sizes are in device independent units, the monitor uses real pixels
        double scale = 1.0;
        if (NativeMethods.GetDpiForMonitor(monitor, NativeMethods.MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        var width = (int)Math.Round(window.Width * scale);
        var height = (int)Math.Round(window.Height * scale);
        var work = info.rcWork;

        var x = cursor.X - (width / 2);
        var y = cursor.Y - (int)(40 * scale);
        x = Math.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));
        y = Math.Clamp(y, work.Top, Math.Max(work.Top, work.Bottom - height));

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }
}
