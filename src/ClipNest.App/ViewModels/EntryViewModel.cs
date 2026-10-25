using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using ClipNest.App.Platform;
using ClipNest.Core.Diagnostics;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using ClipNest.Core.Text;

namespace ClipNest.App.ViewModels;

/// <summary>
/// one row in the history list.
/// </summary>
internal sealed class EntryViewModel : ObservableObject
{
    private ImageSource? _thumbnail;
    private bool _thumbnailMissing;
    private string _timeText = "";

    public EntryViewModel(ClipEntry entry, DateTimeOffset now)
    {
        Entry = entry;
        Preview = entry.Kind == ClipKind.Text ? PreviewFormatter.ForList(entry.TextPreview) : "";
        RefreshTime(now);
    }

    public ClipEntry Entry { get; }

    public long Id => Entry.Id;

    public bool IsText => Entry.Kind == ClipKind.Text;

    public bool IsImage => Entry.Kind == ClipKind.Image;

    public bool IsPinned => Entry.IsPinned;

    // the list groups rows by this value
    public string SectionTitle => IsPinned ? "Pinned" : "Recent";

    public bool HasPinTitle => IsPinned && !string.IsNullOrWhiteSpace(Entry.PinTitle);

    public string PinTitle => Entry.PinTitle ?? "";

    public string Preview { get; }

    public bool ShowPreviewText => IsText;

    public string KindLabel => IsText ? "Text" : "Image";

    // glyphs from the segoe fluent icons font
    public string KindGlyph => IsText ? "" : "";

    public string PinGlyph => IsPinned ? "" : "";

    public string PinActionName => IsPinned ? "Unpin" : "Pin";

    public string Details
    {
        get
        {
            if (IsImage)
            {
                return $"{Entry.ImageWidth.ToString(CultureInfo.CurrentCulture)} × {Entry.ImageHeight.ToString(CultureInfo.CurrentCulture)}";
            }

            var chars = Entry.TextLength == 1 ? "1 char" : $"{Entry.TextLength.ToString("N0", CultureInfo.CurrentCulture)} chars";
            return Entry.LineCount > 1
                ? $"{chars} · {Entry.LineCount.ToString("N0", CultureInfo.CurrentCulture)} lines"
                : chars;
        }
    }

    public string SourceText => string.IsNullOrEmpty(Entry.SourceApp)
        ? ""
        : Path.GetFileNameWithoutExtension(Entry.SourceApp);

    public string TimeText
    {
        get => _timeText;
        private set
        {
            if (SetProperty(ref _timeText, value))
            {
                OnPropertyChanged(nameof(Meta));
                OnPropertyChanged(nameof(AccessibleName));
            }
        }
    }

    public string Meta
    {
        get
        {
            var meta = $"{KindLabel} · {Details} · {TimeText}";
            return SourceText.Length > 0 ? $"{meta} · {SourceText}" : meta;
        }
    }

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set => SetProperty(ref _thumbnail, value);
    }

    public bool ThumbnailMissing
    {
        get => _thumbnailMissing;
        private set => SetProperty(ref _thumbnailMissing, value);
    }

    /// <summary>
    /// what a screen reader says for this row.
    /// </summary>
    public string AccessibleName
    {
        get
        {
            var start = IsPinned ? "Pinned " + KindLabel.ToLowerInvariant() : KindLabel;
            var title = HasPinTitle ? $", {PinTitle}" : "";
            var body = IsText ? ", " + PreviewFormatter.FirstLine(Entry.TextPreview, 80) : $", {Details}";
            return $"{start}{title}{body}, {TimeText}";
        }
    }

    public void RefreshTime(DateTimeOffset now) => TimeText = RelativeTime.Format(Entry.LastUsedAt, now);

    /// <summary>
    /// loads the small preview picture off the ui thread.
    /// </summary>
    public async Task LoadThumbnailAsync(ImageStore images)
    {
        if (!IsImage || Thumbnail is not null)
        {
            return;
        }

        var path = images.GetFullPath(Entry.ThumbnailFile) ?? images.GetFullPath(Entry.ImageFile);
        if (path is null)
        {
            ThumbnailMissing = true;
            return;
        }

        try
        {
            var bitmap = await Task.Run(() => File.Exists(path) ? ImageCodec.LoadThumbnail(path) : null);
            if (bitmap is null)
            {
                ThumbnailMissing = true;
                return;
            }

            Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                   or System.Runtime.InteropServices.COMException)
        {
            AppLog.Error("a thumbnail could not be loaded", ex);
            ThumbnailMissing = true;
        }
    }
}
