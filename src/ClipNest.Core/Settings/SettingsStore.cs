using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClipNest.Core.Diagnostics;

namespace ClipNest.Core.Settings;

public enum SettingsLoadStatus
{
    Loaded,
    CreatedDefaults,
    RecoveredFromDamage,
    UnreadableUsingDefaults,
}

public sealed record SettingsLoadResult(AppSettings Settings, SettingsLoadStatus Status, string? BackupPath = null);

/// <summary>
/// reads and writes settings.json safely.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly TimeProvider _time;

    public SettingsStore(string filePath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        FilePath = filePath;
        _time = time ?? TimeProvider.System;
    }

    public string FilePath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new AppSettings().Normalized();
            TrySave(fresh);
            return new SettingsLoadResult(fresh, SettingsLoadStatus.CreatedDefaults);
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the file exists but cannot be read right now, so leave it alone
            AppLog.Error("settings file could not be read", ex);
            return new SettingsLoadResult(new AppSettings().Normalized(), SettingsLoadStatus.UnreadableUsingDefaults);
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                ?? throw new JsonException("settings file is empty");
            return new SettingsLoadResult(loaded.Normalized(), SettingsLoadStatus.Loaded);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            AppLog.Error("settings file is damaged, using defaults", ex);
            var backup = BackupDamagedFile();
            var defaults = new AppSettings().Normalized();
            TrySave(defaults);
            return new SettingsLoadResult(defaults, SettingsLoadStatus.RecoveredFromDamage, backup);
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var folder = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(settings.Normalized(), JsonOptions);

        // write a temp file first so a crash never leaves a half written file
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }

    public bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("settings could not be saved", ex);
            return false;
        }
    }

    private string? BackupDamagedFile()
    {
        try
        {
            var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.GetDirectoryName(FilePath) ?? ".";
            var backup = Path.Combine(folder, $"settings.damaged-{stamp}.json");
            File.Copy(FilePath, backup, overwrite: true);
            return backup;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("damaged settings could not be backed up", ex);
            return null;
        }
    }
}
