using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using ClipNest.Core.Capture;

namespace ClipNest.App.Platform;

/// <summary>
/// raw image bytes taken from the clipboard, either a png file or a windows bitmap.
/// </summary>
internal sealed record RawClipboardImage(byte[] Bytes, bool IsPng);

internal sealed record ClipboardReadResult(
    ClipboardFormatsInfo Formats,
    CaptureDecision Decision,
    string? Text,
    RawClipboardImage? Image,
    SkipReason Failure);

/// <summary>
/// reads and writes the clipboard with the win32 api.
/// every method must be called on the ui thread, which owns the message window.
/// </summary>
internal sealed class ClipboardAccess
{
    // waits between attempts when another app is holding the clipboard
    private static readonly int[] RetryDelaysMs = { 10, 20, 40, 60, 80, 120, 160, 200, 250 };

    private readonly IntPtr _owner;
    private readonly uint _pngFormat;
    private readonly uint _excludeFormat;
    private readonly uint _historyFormat;
    private readonly uint _viewerIgnoreFormat;
    private readonly uint _selfCopyFormat;

    public ClipboardAccess(IntPtr ownerWindow)
    {
        _owner = ownerWindow;
        _pngFormat = NativeMethods.RegisterClipboardFormatW("PNG");

        // documented windows formats that ask clipboard tools to skip the data
        _excludeFormat = NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _historyFormat = NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");

        // an older marker that several password managers still set
        _viewerIgnoreFormat = NativeMethods.RegisterClipboardFormatW("Clipboard Viewer Ignore");

        // our own marker, added to everything this app copies
        _selfCopyFormat = NativeMethods.RegisterClipboardFormatW("ClipNest.SelfCopy");
    }

    public static uint CurrentSequenceNumber => NativeMethods.GetClipboardSequenceNumber();

    /// <summary>
    /// looks at the clipboard formats, asks the policy what to do, and reads
    /// the content only when it should be saved.
    /// </summary>
    public async Task<ClipboardReadResult> ReadAsync(Func<ClipboardFormatsInfo, CaptureDecision> decide)
    {
        var empty = new ClipboardFormatsInfo();
        if (!await OpenWithRetriesAsync())
        {
            return new ClipboardReadResult(empty, CaptureDecision.Skip(SkipReason.ClipboardBusy), null, null, SkipReason.ClipboardBusy);
        }

        try
        {
            var formats = InspectFormats();
            var decision = decide(formats);
            switch (decision.Action)
            {
                case CaptureAction.CaptureText:
                    var text = ReadUnicodeText(out var textTooLarge);
                    if (textTooLarge)
                    {
                        return new ClipboardReadResult(formats, decision, null, null, SkipReason.TooLarge);
                    }

                    return new ClipboardReadResult(formats, decision, text, null, text is null ? SkipReason.ReadFailed : SkipReason.None);

                case CaptureAction.CaptureImage:
                    var image = ReadImage(out var imageTooLarge);
                    if (imageTooLarge)
                    {
                        return new ClipboardReadResult(formats, decision, null, null, SkipReason.TooLarge);
                    }

                    return new ClipboardReadResult(formats, decision, null, image, image is null ? SkipReason.ReadFailed : SkipReason.None);

                default:
                    return new ClipboardReadResult(formats, decision, null, null, SkipReason.None);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    public Task<bool> WriteTextAsync(string text)
    {
        // plain unicode text only, so no formatting is ever copied
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return WriteAsync(new List<(uint, byte[])> { (NativeMethods.CF_UNICODETEXT, bytes) });
    }

    public Task<bool> WriteImageAsync(byte[] dib, byte[] png)
    {
        return WriteAsync(new List<(uint, byte[])>
        {
            (NativeMethods.CF_DIB, dib),
            (_pngFormat, png),
        });
    }

    private async Task<bool> WriteAsync(List<(uint Format, byte[] Data)> items)
    {
        // mark our own copies so the listener can ignore them
        items.Add((_selfCopyFormat, new byte[] { 1, 0, 0, 0 }));

        if (!await OpenWithRetriesAsync())
        {
            return false;
        }

        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            foreach (var (format, data) in items)
            {
                if (!SetData(format, data))
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private async Task<bool> OpenWithRetriesAsync()
    {
        if (NativeMethods.OpenClipboard(_owner))
        {
            return true;
        }

        // another app may be using the clipboard for a moment, so try a few more times
        foreach (var delay in RetryDelaysMs)
        {
            await Task.Delay(delay);
            if (NativeMethods.OpenClipboard(_owner))
            {
                return true;
            }
        }

        return false;
    }

    private ClipboardFormatsInfo InspectFormats()
    {
        bool hasText = false, hasImage = false, hasFiles = false, hasOther = false;
        bool exclude = false, optOut = false, viewerIgnore = false, self = false;

        uint format = 0;
        while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
        {
            if (format is NativeMethods.CF_UNICODETEXT or NativeMethods.CF_TEXT or NativeMethods.CF_OEMTEXT)
            {
                hasText = true;
            }
            else if (format is NativeMethods.CF_DIB or NativeMethods.CF_DIBV5 or NativeMethods.CF_BITMAP || format == _pngFormat)
            {
                hasImage = true;
            }
            else if (format == NativeMethods.CF_HDROP)
            {
                hasFiles = true;
            }
            else if (format == _excludeFormat)
            {
                exclude = true;
            }
            else if (format == _viewerIgnoreFormat)
            {
                viewerIgnore = true;
            }
            else if (format == _selfCopyFormat)
            {
                self = true;
            }
            else if (format == _historyFormat)
            {
                // a value of zero means the app does not allow history
                optOut = ReadDword(_historyFormat) == 0;
            }
            else if (format != NativeMethods.CF_LOCALE)
            {
                hasOther = true;
            }
        }

        return new ClipboardFormatsInfo
        {
            HasText = hasText,
            HasImage = hasImage,
            HasFileList = hasFiles,
            HasOtherFormats = hasOther,
            HasExcludeMarker = exclude,
            HasHistoryOptOut = optOut,
            HasViewerIgnoreMarker = viewerIgnore,
            HasSelfCopyMarker = self,
        };
    }

    private static string? ReadUnicodeText(out bool tooLarge)
    {
        tooLarge = false;
        var handle = NativeMethods.GetClipboardData(NativeMethods.CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)NativeMethods.GlobalSize(handle).ToUInt64();
        var maxChars = size / 2;
        if (maxChars > CaptureLimits.MaxTextChars + 1)
        {
            tooLarge = true;
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[maxChars];
            Marshal.Copy(pointer, buffer, 0, (int)maxChars);

            // the text ends at the first null character
            var length = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, length < 0 ? buffer.Length : length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private RawClipboardImage? ReadImage(out bool tooLarge)
    {
        tooLarge = false;

        // png keeps transparency, so it is preferred when an app offers it
        if (_pngFormat != 0)
        {
            var png = ReadBytes(_pngFormat, out tooLarge);
            if (tooLarge)
            {
                return null;
            }

            if (png is { Length: > 8 })
            {
                return new RawClipboardImage(png, IsPng: true);
            }
        }

        // windows creates this bitmap format for us from other bitmap formats
        var dib = ReadBytes(NativeMethods.CF_DIB, out tooLarge);
        return dib is { Length: > 40 } ? new RawClipboardImage(dib, IsPng: false) : null;
    }

    private static byte[]? ReadBytes(uint format, out bool tooLarge)
    {
        tooLarge = false;
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var size = (long)NativeMethods.GlobalSize(handle).ToUInt64();
        if (size <= 0)
        {
            return null;
        }

        if (size > CaptureLimits.MaxRawClipboardImageBytes)
        {
            tooLarge = true;
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bytes = new byte[size];
            Marshal.Copy(pointer, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static int? ReadDword(uint format)
    {
        var handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero || NativeMethods.GlobalSize(handle).ToUInt64() < 4)
        {
            return null;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.ReadInt32(pointer);
        }
        finally
        {
            NativeMethods.GlobalUnlock(handle);
        }
    }

    private static bool SetData(uint format, byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (UIntPtr)data.Length);
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return false;
        }

        Marshal.Copy(data, 0, pointer, data.Length);
        NativeMethods.GlobalUnlock(handle);

        // after a successful call windows owns the memory, otherwise we must free it
        if (NativeMethods.SetClipboardData(format, handle) == IntPtr.Zero)
        {
            NativeMethods.GlobalFree(handle);
            return false;
        }

        return true;
    }
}
