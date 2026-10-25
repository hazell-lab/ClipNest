using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipNest.Core.Capture;
using ClipNest.Core.History;
using ClipNest.Core.Models;

namespace ClipNest.App.Platform;

/// <summary>
/// turns clipboard image bytes into png files and back. runs on background threads.
/// </summary>
internal static class ImageCodec
{
    public static CapturedImage? Decode(RawClipboardImage raw, out SkipReason failure)
    {
        failure = SkipReason.None;
        try
        {
            var fileBytes = raw.IsPng ? raw.Bytes : DibFormat.ToBmpFile(raw.Bytes);
            if (fileBytes is null)
            {
                failure = SkipReason.UnsupportedFormat;
                return null;
            }

            using var stream = new MemoryStream(fileBytes, writable: false);

            // delay creation reads only the header, so huge images are rejected before decoding
            var decoder = BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];

            var width = frame.PixelWidth;
            var height = frame.PixelHeight;
            if (width <= 0 || height <= 0)
            {
                failure = SkipReason.ReadFailed;
                return null;
            }

            if (width > CaptureLimits.MaxImageDimension
                || height > CaptureLimits.MaxImageDimension
                || (long)width * height > CaptureLimits.MaxImagePixels)
            {
                failure = SkipReason.TooLarge;
                return null;
            }

            BitmapSource source = frame.Format == PixelFormats.Bgra32
                ? frame
                : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

            var stride = width * 4;
            var pixels = new byte[(long)stride * height];
            source.CopyPixels(pixels, stride, 0);
            var hash = ContentHasher.ForImagePixels(width, height, pixels);

            // store at 96 dpi so other apps paste it at its real pixel size
            var normalized = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            normalized.Freeze();

            var png = EncodePng(normalized);
            var thumbnail = EncodePng(MakeThumbnail(normalized));
            return new CapturedImage(png, thumbnail, width, height, hash);
        }
        catch (Exception ex) when (ex is NotSupportedException or FormatException or ArgumentException
                                   or InvalidOperationException or IOException or OverflowException
                                   or System.Runtime.InteropServices.COMException)
        {
            Core.Diagnostics.AppLog.Error("clipboard image could not be decoded", ex);
            failure = SkipReason.ReadFailed;
            return null;
        }
        catch (OutOfMemoryException ex)
        {
            Core.Diagnostics.AppLog.Error("clipboard image was too large to decode", ex);
            failure = SkipReason.TooLarge;
            return null;
        }
    }

    /// <summary>
    /// reads a saved png and builds the two clipboard formats we write:
    /// a windows bitmap for older apps and the png itself for newer ones.
    /// </summary>
    public static (byte[] Dib, byte[] Png) PrepareForClipboard(string pngPath)
    {
        var png = File.ReadAllBytes(pngPath);
        using var stream = new MemoryStream(png, writable: false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        if (frame.Format != PixelFormats.Bgra32)
        {
            frame = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        }

        var width = frame.PixelWidth;
        var height = frame.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[(long)stride * height];
        frame.CopyPixels(pixels, stride, 0);
        return (DibFormat.FromBgraPixels(width, height, pixels), png);
    }

    public static BitmapSource? LoadThumbnail(string path)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource MakeThumbnail(BitmapSource source)
    {
        var longest = Math.Max(source.PixelWidth, source.PixelHeight);
        if (longest <= CaptureLimits.ThumbnailMaxSize)
        {
            return source;
        }

        var scale = (double)CaptureLimits.ThumbnailMaxSize / longest;
        var thumbnail = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        thumbnail.Freeze();
        return thumbnail;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
