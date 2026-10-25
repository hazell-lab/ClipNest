using System;

namespace ClipNest.Core.Models;

public enum ClipKind
{
    Text = 0,
    Image = 1,
}

public enum HistoryFilter
{
    All,
    Text,
    Images,
    Pinned,
}

/// <summary>
/// one saved clipboard item. full text is not kept here, only a short preview.
/// </summary>
public sealed record ClipEntry
{
    public long Id { get; init; }
    public ClipKind Kind { get; init; }

    // the first part of the text, used for list previews
    public string? TextPreview { get; init; }
    public int TextLength { get; init; }
    public int LineCount { get; init; }

    public string ContentHash { get; init; } = "";
    public string? ImageFile { get; init; }
    public string? ThumbnailFile { get; init; }
    public int ImageWidth { get; init; }
    public int ImageHeight { get; init; }

    // rough size on disk for this entry in bytes
    public long ByteSize { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastUsedAt { get; init; }

    public bool IsPinned { get; init; }
    public string? PinTitle { get; init; }
    public DateTimeOffset? PinnedAt { get; init; }

    public string? SourceApp { get; init; }
    public string? CodeLanguage { get; init; }
}

/// <summary>
/// an image read from the clipboard, already turned into png bytes.
/// </summary>
public sealed record CapturedImage(
    byte[] PngBytes,
    byte[] ThumbnailPngBytes,
    int Width,
    int Height,
    string PixelHash);

/// <summary>
/// what the app needs to put an entry back on the clipboard.
/// </summary>
public sealed record CopyPayload
{
    public required ClipEntry Entry { get; init; }
    public string? Text { get; init; }
    public string? ImagePath { get; init; }
    public bool ImageFileMissing { get; init; }
}
