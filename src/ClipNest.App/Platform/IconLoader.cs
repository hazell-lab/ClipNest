using System;
using System.IO;
using System.Windows;
using ClipNest.Core.Diagnostics;

namespace ClipNest.App.Platform;

/// <summary>
/// loads an icon from an .ico file packed inside the app and turns it into a
/// windows icon handle of the wanted size.
/// </summary>
internal static class IconLoader
{
    public static IntPtr LoadFromResource(string relativeUri, int size)
    {
        try
        {
            var info = Application.GetResourceStream(new Uri(relativeUri, UriKind.Relative));
            if (info is null)
            {
                return IntPtr.Zero;
            }

            using var stream = info.Stream;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return FromIcoBytes(memory.ToArray(), size);
        }
        catch (Exception ex) when (ex is IOException or UriFormatException)
        {
            AppLog.Error("tray icon could not be loaded", ex);
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// picks the closest image in the .ico file and creates an icon from it.
    /// </summary>
    public static IntPtr FromIcoBytes(byte[] ico, int size)
    {
        // an .ico file starts with a 6 byte header and then 16 bytes per image
        if (ico.Length < 6 || BitConverter.ToUInt16(ico, 2) != 1)
        {
            return IntPtr.Zero;
        }

        var count = BitConverter.ToUInt16(ico, 4);
        var bestIndex = -1;
        var bestWidth = 0;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + (i * 16);
            if (entry + 16 > ico.Length)
            {
                break;
            }

            // a stored width of zero means 256 pixels
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var better = bestIndex < 0
                || (width >= size && (bestWidth < size || width < bestWidth))
                || (width < size && bestWidth < size && width > bestWidth);
            if (better)
            {
                bestIndex = i;
                bestWidth = width;
            }
        }

        if (bestIndex < 0)
        {
            return IntPtr.Zero;
        }

        var header = 6 + (bestIndex * 16);
        var length = BitConverter.ToInt32(ico, header + 8);
        var offset = BitConverter.ToInt32(ico, header + 12);
        if (offset < 0 || length <= 0 || offset + length > ico.Length)
        {
            return IntPtr.Zero;
        }

        var image = new byte[length];
        Buffer.BlockCopy(ico, offset, image, 0, length);

        // 0x00030000 is the icon format version windows expects here
        return NativeMethods.CreateIconFromResourceEx(image, (uint)length, true, 0x00030000, size, size, 0);
    }
}
