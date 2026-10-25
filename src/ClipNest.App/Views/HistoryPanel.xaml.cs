using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ClipNest.App.Platform;
using ClipNest.App.ViewModels;

namespace ClipNest.App.Views;

/// <summary>
/// the floating clipboard history panel.
/// </summary>
public partial class HistoryPanel : Window
{
    private readonly HistoryViewModel _model;
    private readonly TranslateTransform _slide = new();
    private bool _closingForExit;
    private bool _closed;

    internal HistoryPanel(HistoryViewModel model)
    {
        InitializeComponent();
        _model = model;
        DataContext = model;
        Chrome.RenderTransform = _slide;
        Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// true while a dialog from the panel is open, so losing focus does not hide it.
    /// </summary>
    internal bool SuppressAutoHide { get; set; }

    internal void ShowNearCursor()
    {
        // follow the windows "animation effects" setting for reduced motion
        var animate = SystemParameters.ClientAreaAnimation;

        BeginAnimation(OpacityProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = animate ? 0 : 1;
        _slide.Y = 0;

        WindowHelpers.PlaceNearCursor(this);
        Show();

        // place again now that the window knows the scale of its monitor
        WindowHelpers.PlaceNearCursor(this);
        Activate();

        if (animate)
        {
            var duration = TimeSpan.FromMilliseconds(130);
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
            _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(8, 0, duration) { EasingFunction = ease });
        }
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    internal void CloseForExit()
    {
        _closingForExit = true;
        if (!_closed)
        {
            Close();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // closing the panel only hides it, the app keeps running in the tray
        if (!_closingForExit)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void OnPanelDeactivated(object? sender, EventArgs e)
    {
        if (!SuppressAutoHide && IsVisible)
        {
            Hide();
        }
    }

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var inSearch = SearchBox.IsKeyboardFocusWithin;
        var inList = HistoryList.IsKeyboardFocusWithin;
        var onButton = Keyboard.FocusedElement is ButtonBase;
        var selected = _model.Selected;

        switch (e.Key)
        {
            case Key.Escape:
                _model.CloseCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Down when inSearch:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up when inSearch:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.PageDown when inSearch:
                MoveSelection(8);
                e.Handled = true;
                break;

            case Key.PageUp when inSearch:
                MoveSelection(-8);
                e.Handled = true;
                break;

            case Key.Enter when (inSearch || inList) && !onButton && selected is not null:
                _model.CopyCommand.Execute(selected);
                e.Handled = true;
                break;

            case Key.Delete when inList && !onButton && selected is not null:
                _model.DeleteCommand.Execute(selected);
                e.Handled = true;
                break;

            case Key.P when ctrl && selected is not null:
                _model.TogglePinCommand.Execute(selected);
                e.Handled = true;
                break;

            case Key.F2 when selected is not null:
                _model.RenameCommand.Execute(selected);
                e.Handled = true;
                break;

            case Key.E when ctrl && selected is not null:
                _model.PreviewCommand.Execute(selected);
                e.Handled = true;
                break;

            case Key.F when ctrl:
                FocusSearch();
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        _model.MoveSelection(delta);
        if (_model.Selected is { } item)
        {
            HistoryList.ScrollIntoView(item);
        }
    }

    private void OnListScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // load older items when the user nears the end of the list
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - Math.Max(e.ViewportHeight * 0.5, 1))
        {
            _ = _model.LoadMoreAsync();
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && FindParent<ButtonBase>(source) is null
            && FindParent<ListBoxItem>(source) is not null
            && _model.Selected is { } selected)
        {
            _model.CopyCommand.Execute(selected);
            e.Handled = true;
        }
    }

    private void OnListTextInput(object sender, TextCompositionEventArgs e)
    {
        // typing while the list has focus goes to the search box
        if (string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0]))
        {
            return;
        }

        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private static T? FindParent<T>(DependencyObject? child)
        where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match)
            {
                return match;
            }

            child = child is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }

        return null;
    }
}
