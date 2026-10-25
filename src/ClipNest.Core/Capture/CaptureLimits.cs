namespace ClipNest.Core.Capture;

/// <summary>
/// size limits that keep memory and disk use sensible.
/// </summary>
public static class CaptureLimits
{
    // texts longer than this are skipped instead of cut, so copies stay exact
    public const int MaxTextChars = 1_000_000;

    public const int MaxImageDimension = 12_000;
    public const long MaxImagePixels = 40_000_000;
    public const long MaxEncodedImageBytes = 64L * 1024 * 1024;

    // raw clipboard image data bigger than this is not even read
    public const long MaxRawClipboardImageBytes = 256L * 1024 * 1024;

    public const int ThumbnailMaxSize = 160;

    // only the start of long texts goes into the search index
    public const int SearchIndexChars = 32_768;

    public const int PreviewChars = 1_000;
}
