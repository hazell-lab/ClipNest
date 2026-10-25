using System;

namespace ClipNest.Core.Capture;

/// <summary>
/// converts between windows clipboard bitmaps and normal .bmp file bytes.
/// </summary>
public static class DibFormat
{
    private const int BitmapFileHeaderSize = 14;

    /// <summary>
    /// clipboard bitmaps have no file header, so we add one to decode them as a .bmp file.
    /// </summary>
    public static byte[]? ToBmpFile(byte[] dib)
    {
        if (dib.Length < 12)
        {
            return null;
        }

        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 12 || headerSize > dib.Length)
        {
            return null;
        }

        int bitCount;
        uint compression = 0;
        uint colorsUsed = 0;
        if (headerSize == 12)
        {
            // very old header style
            bitCount = BitConverter.ToUInt16(dib, 10);
        }
        else
        {
            if (dib.Length < 40)
            {
                return null;
            }

            bitCount = BitConverter.ToUInt16(dib, 14);
            compression = BitConverter.ToUInt32(dib, 16);
            colorsUsed = BitConverter.ToUInt32(dib, 32);
        }

        // color masks follow a short header when the bitfields modes are used
        var masks = headerSize == 40 && compression == 3 ? 12 : headerSize == 40 && compression == 6 ? 16 : 0;
        var colors = colorsUsed != 0 ? (int)Math.Min(colorsUsed, 256) : bitCount <= 8 ? 1 << bitCount : 0;
        var paletteBytes = colors * (headerSize == 12 ? 3 : 4);
        var pixelOffset = BitmapFileHeaderSize + headerSize + masks + paletteBytes;

        var file = new byte[BitmapFileHeaderSize + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BitConverter.GetBytes(file.Length).CopyTo(file, 2);
        BitConverter.GetBytes(pixelOffset).CopyTo(file, 10);
        Buffer.BlockCopy(dib, 0, file, BitmapFileHeaderSize, dib.Length);
        return file;
    }

    /// <summary>
    /// builds a 32 bit bottom up windows bitmap from top down pixel rows.
    /// </summary>
    public static byte[] FromBgraPixels(int width, int height, byte[] bgraPixels)
    {
        const int headerSize = 40;
        var stride = width * 4;
        var dib = new byte[headerSize + ((long)stride * height)];
        BitConverter.GetBytes(headerSize).CopyTo(dib, 0);
        BitConverter.GetBytes(width).CopyTo(dib, 4);
        BitConverter.GetBytes(height).CopyTo(dib, 8);
        BitConverter.GetBytes((ushort)1).CopyTo(dib, 12);
        BitConverter.GetBytes((ushort)32).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * height).CopyTo(dib, 20);
        BitConverter.GetBytes(3780).CopyTo(dib, 24);
        BitConverter.GetBytes(3780).CopyTo(dib, 28);

        for (var row = 0; row < height; row++)
        {
            // bitmaps store the bottom row first
            Buffer.BlockCopy(bgraPixels, row * stride, dib, headerSize + ((height - 1 - row) * stride), stride);
        }

        return dib;
    }
}
