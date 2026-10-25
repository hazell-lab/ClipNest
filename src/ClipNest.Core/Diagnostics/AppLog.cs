using System;
using System.Globalization;
using System.IO;

namespace ClipNest.Core.Diagnostics;

/// <summary>
/// a tiny error log. it must never receive clipboard content,
/// only short messages written by this app and exception types.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static string? _filePath;

    public static string? FilePath => _filePath;

    public static void Initialize(string logFolder)
    {
        try
        {
            Directory.CreateDirectory(logFolder);
            _filePath = Path.Combine(logFolder, "app.log");
        }
        catch (Exception)
        {
            // logging is optional, so a broken folder is ignored
            _filePath = null;
        }
    }

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string context, Exception? exception = null) => Write("ERROR", context, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        var path = _filePath;
        if (path is null)
        {
            return;
        }

        // only the exception type and message are kept, never data values
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {level} {message}");
        if (exception is not null)
        {
            line += $" | {exception.GetType().Name}: {exception.Message}";
        }

        lock (Gate)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Move(path, path + ".1", overwrite: true);
                }

                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception)
            {
                // never crash because of logging
            }
        }
    }
}
