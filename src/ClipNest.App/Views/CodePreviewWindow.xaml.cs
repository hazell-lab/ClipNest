using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ClipNest.App.Platform;
using ClipNest.App.Services;
using ClipNest.Core.Code;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using ClipNest.Core.Text;

namespace ClipNest.App.Views;

public sealed record LanguageOption(string? Id, string Name);

/// <summary>
/// shows a text entry with syntax colors. the text is only displayed, never run.
/// </summary>
public partial class CodePreviewWindow : Window
{
    private const int MaxDisplayChars = 100_000;
    private const double LineHeightPx = 19;
    private const double CodeFontSize = 13;

    private readonly ClipEntry _entry;
    private readonly AppController _controller;
    private string _text = "";
    private string _detected = CodeLanguages.PlainText;
    private bool _ready;
    private int _renderVersion;

    internal CodePreviewWindow(ClipEntry entry, AppController controller)
    {
        InitializeComponent();
        _entry = entry;
        _controller = controller;
        HeaderText.Text = entry.IsPinned && !string.IsNullOrWhiteSpace(entry.PinTitle)
            ? entry.PinTitle
            : PreviewFormatter.FirstLine(entry.TextPreview, 80);

        SourceInitialized += (_, _) => WindowHelpers.ApplyTitleBarTheme(this, ThemeManager.IsDark);
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            _text = await _controller.History.GetFullTextAsync(_entry.Id) ?? "";
        }
        catch (HistoryStorageException ex)
        {
            StatusText.Text = ex.Message;
            return;
        }

        var sample = _text;
        _detected = await Task.Run(() => LanguageDetector.Detect(sample));

        var options = new List<LanguageOption>
        {
            new(null, $"Auto-detect ({CodeLanguages.DisplayNameFor(_detected)})"),
        };
        options.AddRange(CodeLanguages.All.Select(l => new LanguageOption(l.Id, l.DisplayName)));
        LanguageBox.ItemsSource = options;

        var saved = CodeLanguages.IsKnown(_entry.CodeLanguage) ? _entry.CodeLanguage : null;
        LanguageBox.SelectedItem = options.FirstOrDefault(o => o.Id == saved) ?? options[0];
        _ready = true;
        await RenderAsync();
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not LanguageOption option)
        {
            return;
        }

        try
        {
            // remember the manual choice for next time, auto is stored as no choice
            await _controller.History.SetCodeLanguageAsync(_entry.Id, option.Id);
        }
        catch (HistoryStorageException ex)
        {
            AppLog.Error("code language could not be saved", ex);
        }

        await RenderAsync();
    }

    private void OnWrapChanged(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            ApplyWrap();
        }
    }

    private async void OnCopy(object sender, RoutedEventArgs e)
    {
        CopyButton.IsEnabled = false;
        var ok = await _controller.CopyToClipboardAsync(_entry.Id);
        StatusText.Text = ok ? "Copied to clipboard as plain text." : "Could not copy right now.";
        CopyButton.IsEnabled = true;
    }

    private async Task RenderAsync()
    {
        var version = ++_renderVersion;
        var language = (LanguageBox.SelectedItem as LanguageOption)?.Id ?? _detected;
        var truncated = _text.Length > MaxDisplayChars;
        var display = (truncated ? _text[..MaxDisplayChars] : _text).Replace("\r\n", "\n").Replace('\r', '\n');

        var tokens = await Task.Run(() => SyntaxTokenizer.Tokenize(display, language));
        if (version != _renderVersion)
        {
            return;
        }

        var paragraph = new Paragraph { Margin = new Thickness(0) };
        var position = 0;
        foreach (var token in tokens)
        {
            if (token.Start > position)
            {
                AddText(paragraph, display.Substring(position, token.Start - position), null);
            }

            AddText(paragraph, display.Substring(token.Start, token.Length), BrushKeyFor(token.Kind));
            position = token.Start + token.Length;
        }

        if (position < display.Length)
        {
            AddText(paragraph, display[position..], null);
        }

        var document = new FlowDocument(paragraph)
        {
            FontFamily = (FontFamily)FindResource("CodeFont"),
            FontSize = CodeFontSize,
            LineHeight = LineHeightPx,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left,
        };
        document.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimaryBrush");
        CodeView.Document = document;

        var lines = display.Split('\n');
        var numbers = new StringBuilder();
        for (var i = 1; i <= lines.Length; i++)
        {
            numbers.Append(i.ToString(CultureInfo.InvariantCulture));
            if (i < lines.Length)
            {
                numbers.Append('\n');
            }
        }

        LineNumbers.Text = numbers.ToString();
        _longestLine = lines.Length == 0 ? 0 : lines.Max(l => l.Replace("\t", "    ").Length);
        ApplyWrap();

        TruncatedNotice.Visibility = truncated ? Visibility.Visible : Visibility.Collapsed;
        TruncatedText.Text = truncated
            ? $"Showing the first {MaxDisplayChars:N0} of {_text.Length:N0} characters. Copy still copies the full text."
            : "";

        var shownName = CodeLanguages.DisplayNameFor(language);
        StatusText.Text = tokens.Count == 0 && language != CodeLanguages.PlainText
            ? $"{lines.Length:N0} lines · {shownName} (no colors found)"
            : $"{lines.Length:N0} lines · {shownName}"
              + ((LanguageBox.SelectedItem as LanguageOption)?.Id is null ? " · detected automatically, may be wrong" : "");
    }

    private double _longestLine;

    private void ApplyWrap()
    {
        if (CodeView.Document is null)
        {
            return;
        }

        if (WrapToggle.IsChecked == true)
        {
            // line numbers would not match wrapped lines, so hide them
            CodeScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            CodeView.Document.PageWidth = double.NaN;
            LineNumbers.Visibility = Visibility.Collapsed;
        }
        else
        {
            CodeScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;

            // monospace characters are about 0.6 of the font size wide
            CodeView.Document.PageWidth = Math.Max(200, (_longestLine * CodeFontSize * 0.62) + 40);
            LineNumbers.Visibility = Visibility.Visible;
        }
    }

    private static void AddText(Paragraph paragraph, string text, string? brushKey)
    {
        // explicit line breaks keep the line numbers lined up
        var parts = text.Split('\n');
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                paragraph.Inlines.Add(new LineBreak());
            }

            if (parts[i].Length == 0)
            {
                continue;
            }

            var run = new Run(parts[i]);
            if (brushKey is not null)
            {
                run.SetResourceReference(TextElement.ForegroundProperty, brushKey);
            }

            paragraph.Inlines.Add(run);
        }
    }

    private static string BrushKeyFor(TokenKind kind) => kind switch
    {
        TokenKind.Keyword => "CodeKeywordBrush",
        TokenKind.Type => "CodeTypeBrush",
        TokenKind.String => "CodeStringBrush",
        TokenKind.Comment => "CodeCommentBrush",
        TokenKind.Number => "CodeNumberBrush",
        TokenKind.Preprocessor => "CodePreprocessorBrush",
        TokenKind.Tag => "CodeTagBrush",
        TokenKind.AttributeName => "CodeAttributeBrush",
        TokenKind.Property => "CodePropertyBrush",
        TokenKind.Variable => "CodeVariableBrush",
        _ => "TextPrimaryBrush",
    };
}
