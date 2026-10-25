using System;
using System.Collections.Generic;
using System.Linq;

namespace ClipNest.Core.Capture;

public enum SkipReason
{
    None,
    Paused,
    SelfCopy,
    PrivacyMarker,
    ExcludedApp,
    ImagesDisabled,
    UnsupportedFormat,
    TooLarge,
    Empty,
    ClipboardBusy,
    ReadFailed,
}

public enum CaptureAction
{
    Skip,
    CaptureText,
    CaptureImage,
}

/// <summary>
/// what we learned about the clipboard before reading any content.
/// </summary>
public sealed record ClipboardFormatsInfo
{
    public bool HasText { get; init; }
    public bool HasImage { get; init; }
    public bool HasFileList { get; init; }
    public bool HasOtherFormats { get; init; }

    // set when the source app asked clipboard tools to ignore this data
    public bool HasExcludeMarker { get; init; }

    // set when the source app said this data must not go into history
    public bool HasHistoryOptOut { get; init; }

    // older marker that some password tools still use
    public bool HasViewerIgnoreMarker { get; init; }

    // set on data that this app wrote itself
    public bool HasSelfCopyMarker { get; init; }
}

public readonly record struct CaptureDecision(CaptureAction Action, SkipReason Reason)
{
    public static CaptureDecision Skip(SkipReason reason) => new(CaptureAction.Skip, reason);
}

public static class CapturePolicy
{
    /// <summary>
    /// decides what to do with a clipboard change. the order of checks matters.
    /// </summary>
    public static CaptureDecision Decide(
        ClipboardFormatsInfo formats,
        bool captureEnabled,
        bool captureImages,
        bool sourceExcluded)
    {
        ArgumentNullException.ThrowIfNull(formats);

        if (!captureEnabled)
        {
            return CaptureDecision.Skip(SkipReason.Paused);
        }

        if (formats.HasSelfCopyMarker)
        {
            return CaptureDecision.Skip(SkipReason.SelfCopy);
        }

        // privacy markers always win over everything else
        if (formats.HasExcludeMarker || formats.HasHistoryOptOut || formats.HasViewerIgnoreMarker)
        {
            return CaptureDecision.Skip(SkipReason.PrivacyMarker);
        }

        if (sourceExcluded)
        {
            return CaptureDecision.Skip(SkipReason.ExcludedApp);
        }

        // prefer text when an app offers both text and a picture
        if (formats.HasText)
        {
            return new CaptureDecision(CaptureAction.CaptureText, SkipReason.None);
        }

        if (formats.HasImage)
        {
            return captureImages
                ? new CaptureDecision(CaptureAction.CaptureImage, SkipReason.None)
                : CaptureDecision.Skip(SkipReason.ImagesDisabled);
        }

        if (formats.HasFileList || formats.HasOtherFormats)
        {
            return CaptureDecision.Skip(SkipReason.UnsupportedFormat);
        }

        return CaptureDecision.Skip(SkipReason.Empty);
    }

    /// <summary>
    /// short text shown to the user when a copy was not saved.
    /// returns null for skips that need no message.
    /// </summary>
    public static string? DescribeSkip(SkipReason reason, bool hadFileList = false) => reason switch
    {
        SkipReason.PrivacyMarker => "Last copy was not saved because the app marked it as private.",
        SkipReason.ExcludedApp => "Last copy was not saved because its app is excluded.",
        SkipReason.ImagesDisabled => "Last copy was an image. Image capture is turned off in settings.",
        SkipReason.UnsupportedFormat => hadFileList
            ? "Last copy was a list of files. Files are not supported yet."
            : "Last copy used a format that is not supported yet (only plain text and images are).",
        SkipReason.TooLarge => "Last copy was too large to save.",
        SkipReason.ClipboardBusy => "Last copy could not be read because the clipboard was busy.",
        SkipReason.ReadFailed => "Last copy could not be read.",
        _ => null,
    };
}

/// <summary>
/// helpers for the list of apps whose copies should not be saved.
/// </summary>
public static class AppExclusionList
{
    public static readonly IReadOnlyList<string> Defaults = new[]
    {
        "KeePass.exe",
        "KeePassXC.exe",
        "1Password.exe",
        "Bitwarden.exe",
    };

    /// <summary>
    /// trims a process name, drops ".exe" and lowercases it, so names compare easily.
    /// </summary>
    public static string Normalize(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return "";
        }

        var name = processName.Trim();

        // drop any folder part the user may have pasted
        var slash = Math.Max(name.LastIndexOf('\\'), name.LastIndexOf('/'));
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Trim().ToLowerInvariant();
    }

    public static bool IsExcluded(IEnumerable<string>? excluded, string? processName)
    {
        if (excluded is null)
        {
            return false;
        }

        var target = Normalize(processName);
        if (target.Length == 0)
        {
            return false;
        }

        return excluded.Any(item => Normalize(item) == target);
    }

    /// <summary>
    /// trims names and removes empty or repeated ones.
    /// </summary>
    public static List<string> Clean(IEnumerable<string>? names)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (names is null)
        {
            return result;
        }

        foreach (var raw in names)
        {
            var key = Normalize(raw);
            if (key.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            result.Add(raw.Trim());
        }

        return result;
    }
}
