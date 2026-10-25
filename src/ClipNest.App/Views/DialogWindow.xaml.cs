using System.Windows;
using System.Windows.Input;

namespace ClipNest.App.Views;

/// <summary>
/// a small themed dialog for yes or no questions and short text input.
/// </summary>
public partial class DialogWindow : Window
{
    private DialogWindow(Window? owner, string title, string message, string confirmText, bool danger, bool withInput, string? initialValue)
    {
        InitializeComponent();
        if (owner is not null && owner.IsVisible)
        {
            Owner = owner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        ConfirmButton.Content = confirmText;
        if (danger)
        {
            ConfirmButton.Style = (Style)FindResource("DangerButton");
        }

        if (withInput)
        {
            InputBox.Visibility = Visibility.Visible;
            InputBox.Text = initialValue ?? "";
        }

        Loaded += (_, _) =>
        {
            if (withInput)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
            else
            {
                // the safe choice gets focus first for destructive questions
                (danger ? CancelButton : ConfirmButton).Focus();
            }
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        };
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText, bool danger)
    {
        var dialog = new DialogWindow(owner, title, message, confirmText, danger, withInput: false, initialValue: null);
        return dialog.ShowDialog() == true;
    }

    /// <summary>
    /// returns the typed text, or null when the user cancels.
    /// </summary>
    public static string? Prompt(Window? owner, string title, string message, string? initialValue)
    {
        var dialog = new DialogWindow(owner, title, message, "Save", danger: false, withInput: true, initialValue);
        return dialog.ShowDialog() == true ? dialog.InputBox.Text : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
