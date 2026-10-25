using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ClipNest.App.Platform;
using ClipNest.App.Services;
using ClipNest.App.ViewModels;
using ClipNest.Core.Settings;

namespace ClipNest.App.Views;

/// <summary>
/// the settings window. closing it keeps the app running in the tray.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _model;
    private readonly AppController _controller;

    internal SettingsWindow(SettingsViewModel model, AppController controller)
    {
        InitializeComponent();
        _model = model;
        _controller = controller;
        DataContext = model;

        SourceInitialized += (_, _) => WindowHelpers.ApplyTitleBarTheme(this, ThemeManager.IsDark);
        Loaded += async (_, _) => _model.StorageText = await _controller.DescribeStorageAsync();
    }

    internal void OnCaptureToggledElsewhere(bool enabled) => _model.CaptureEnabled = enabled;

    private void OnHotkeyFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // stop the current shortcut while recording, so pressing it does not open the panel
        _controller.SuspendHotkey();
        _model.HotkeyStatus = "Press the new shortcut now.";
        _model.HotkeyStatusIsError = false;
    }

    private void OnHotkeyBlur(object sender, KeyboardFocusChangedEventArgs e) => _controller.ResumeHotkey();

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab)
        {
            return;
        }

        e.Handled = true;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        if (key == Key.Escape)
        {
            _model.HotkeyStatus = null;
            Keyboard.ClearFocus();
            return;
        }

        var name = KeyName(key);
        var builder = new StringBuilder();
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            builder.Append("Ctrl+");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            builder.Append("Alt+");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            builder.Append("Shift+");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            builder.Append("Win+");
        }

        if (name is not null && HotkeyGesture.TryParse(builder + name, out var gesture))
        {
            _model.HotkeyText = gesture.ToString();
            _model.HotkeyStatus = $"Press Save to use {gesture}.";
            _model.HotkeyStatusIsError = false;
        }
        else
        {
            _model.HotkeyStatus = "Use Ctrl, Alt or Win together with a letter, number, F-key or arrow key.";
            _model.HotkeyStatusIsError = true;
        }
    }

    private static string? KeyName(Key key)
    {
        if (key >= Key.A && key <= Key.Z)
        {
            return ((char)('A' + (key - Key.A))).ToString();
        }

        if (key >= Key.D0 && key <= Key.D9)
        {
            return ((char)('0' + (key - Key.D0))).ToString();
        }

        if (key >= Key.F1 && key <= Key.F24)
        {
            return "F" + (key - Key.F1 + 1);
        }

        return key switch
        {
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            _ => null,
        };
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        _model.HotkeyText = HotkeyGesture.DefaultText;
        _model.HotkeyStatus = $"Press Save to use {HotkeyGesture.DefaultText}.";
        _model.HotkeyStatusIsError = false;
    }

    private void OnExcludedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            _model.AddExcludedApp();
            e.Handled = true;
        }
    }

    private void OnAddExcluded(object sender, RoutedEventArgs e) => _model.AddExcludedApp();

    private void OnRemoveExcluded(object sender, RoutedEventArgs e) => _model.RemoveSelectedExcludedApp();

    private void OnNumberInput(object sender, TextCompositionEventArgs e)
    {
        // only digits are allowed in number boxes
        e.Handled = !e.Text.All(char.IsAsciiDigit);
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await _controller.ApplySettingsAsync(_model);
        _model.StorageText = await _controller.DescribeStorageAsync();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnOpenFolder(object sender, RoutedEventArgs e) => _controller.OpenDataFolder();

    private async void OnClearHistory(object sender, RoutedEventArgs e)
    {
        _model.SaveMessage = await _controller.ClearUnpinnedFromSettingsAsync();
        _model.StorageText = await _controller.DescribeStorageAsync();
    }

    private async void OnDeleteAll(object sender, RoutedEventArgs e)
    {
        _model.SaveMessage = await _controller.DeleteAllDataAsync();
        _model.StorageText = await _controller.DescribeStorageAsync();
    }
}
