using System;
using System.IO;
using System.Security;
using ClipNest.Core.Diagnostics;
using Microsoft.Win32;

namespace ClipNest.App.Platform;

/// <summary>
/// turns "start with windows" on or off for the current user only.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipNest";
    public const string BackgroundArgument = "--background";

    public static bool TryApply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)
                || string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
            {
                error = "Start with Windows needs the built ClipNest.exe, not 'dotnet run'.";
                return false;
            }

            key.SetValue(ValueName, $"\"{exe}\" {BackgroundArgument}");
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.Error("startup setting could not be changed", ex);
            error = "Windows did not allow changing the startup setting.";
            return false;
        }
    }
}
