using System;
using System.IO;
using System.Linq;
using ClipNest.Core.Settings;
using Xunit;

namespace ClipNest.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "clipnest-settings-tests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_folder, "settings.json");

    public SettingsTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Missing_file_creates_defaults_on_disk()
    {
        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(SettingsLoadStatus.CreatedDefaults, result.Status);
        Assert.True(File.Exists(FilePath));
        Assert.Equal("Ctrl+Shift+V", result.Settings.Hotkey);
        Assert.True(result.Settings.CaptureEnabled);
        Assert.False(result.Settings.AutoPaste);
        Assert.Equal(AppTheme.Dark, result.Settings.Theme);
    }

    [Fact]
    public void Saved_settings_load_back_unchanged()
    {
        var store = new SettingsStore(FilePath);
        var settings = new AppSettings
        {
            Hotkey = "Ctrl+Alt+H",
            CaptureEnabled = false,
            CaptureImages = false,
            LaunchAtStartup = true,
            Theme = AppTheme.Light,
            MaxHistoryItems = 1234,
            MaxStorageMb = 99,
            RetentionDays = 0,
            AutoPaste = true,
            ExcludedApps = { "Secret.exe" },
        };

        store.Save(settings);
        var loaded = new SettingsStore(FilePath).Load();

        Assert.Equal(SettingsLoadStatus.Loaded, loaded.Status);
        var s = loaded.Settings;
        Assert.Equal("Ctrl+Alt+H", s.Hotkey);
        Assert.False(s.CaptureEnabled);
        Assert.False(s.CaptureImages);
        Assert.True(s.LaunchAtStartup);
        Assert.Equal(AppTheme.Light, s.Theme);
        Assert.Equal(1234, s.MaxHistoryItems);
        Assert.Equal(99, s.MaxStorageMb);
        Assert.Equal(0, s.RetentionDays);
        Assert.True(s.AutoPaste);
        Assert.Contains("Secret.exe", s.ExcludedApps);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"Theme\": \"Purple\"}")]
    [InlineData("{\"MaxHistoryItems\": \"lots\"}")]
    public void Damaged_file_is_backed_up_and_defaults_are_used(string content)
    {
        File.WriteAllText(FilePath, content);

        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(SettingsLoadStatus.RecoveredFromDamage, result.Status);
        Assert.NotNull(result.BackupPath);
        Assert.Equal(content, File.ReadAllText(result.BackupPath!));
        Assert.Equal("Ctrl+Shift+V", result.Settings.Hotkey);

        // the fixed file must load cleanly next time
        Assert.Equal(SettingsLoadStatus.Loaded, new SettingsStore(FilePath).Load().Status);
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        File.WriteAllText(FilePath, """
            {
              "Hotkey": "Shift+Q",
              "MaxHistoryItems": 1,
              "MaxStorageMb": 999999,
              "RetentionDays": -5,
              "ExcludedApps": ["  KeePass.exe ", "keepass", "", "Other.EXE"],
              "SomethingUnknown": true
            }
            """);

        var s = new SettingsStore(FilePath).Load().Settings;

        Assert.Equal("Ctrl+Shift+V", s.Hotkey);
        Assert.Equal(AppSettings.MinHistoryItems, s.MaxHistoryItems);
        Assert.Equal(AppSettings.MaxStorageMbLimit, s.MaxStorageMb);
        Assert.Equal(0, s.RetentionDays);
        Assert.Equal(new[] { "KeePass.exe", "Other.EXE" }, s.ExcludedApps.ToArray());
    }

    [Fact]
    public void Theme_is_saved_as_readable_text()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { Theme = AppTheme.System });

        Assert.Contains("\"System\"", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("Ctrl+Shift+V", "Ctrl+Shift+V", 0x56)]
    [InlineData("shift + ctrl + v", "Ctrl+Shift+V", 0x56)]
    [InlineData("Control+Alt+F12", "Ctrl+Alt+F12", 0x7B)]
    [InlineData("Win+Shift+space", "Shift+Win+Space", 0x20)]
    [InlineData("Ctrl+1", "Ctrl+1", 0x31)]
    [InlineData("Alt+PageDown", "Alt+PageDown", 0x22)]
    public void Hotkeys_parse_to_a_standard_form(string text, string expected, int virtualKey)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(expected, gesture!.ToString());
        Assert.Equal(virtualKey, gesture.VirtualKey);
    }

    [Theory]
    [InlineData((string?)null)]
    [InlineData("")]
    [InlineData("V")]
    [InlineData("Shift+V")]
    [InlineData("Ctrl+Ctrl+V")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Banana")]
    [InlineData("Hyper+V")]
    public void Invalid_hotkeys_are_rejected(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
        Assert.Equal(HotkeyGesture.Default, HotkeyGesture.ParseOrDefault(text));
    }
}
