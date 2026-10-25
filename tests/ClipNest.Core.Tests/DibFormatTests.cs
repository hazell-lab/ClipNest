using System;
using ClipNest.Core.Capture;
using Xunit;

namespace ClipNest.Core.Tests;

public class DibFormatTests
{
    // a 2 by 2 picture: red, green on top, blue, white below, stored as blue green red alpha
    private static readonly byte[] TopDownPixels =
    {
        0, 0, 255, 255,   0, 255, 0, 255,
        255, 0, 0, 255,   255, 255, 255, 255,
    };

    [Fact]
    public void Built_bitmap_has_a_valid_header_and_bottom_row_first()
    {
        var dib = DibFormat.FromBgraPixels(2, 2, TopDownPixels);

        Assert.Equal(40 + 16, dib.Length);
        Assert.Equal(40, BitConverter.ToInt32(dib, 0));
        Assert.Equal(2, BitConverter.ToInt32(dib, 4));
        Assert.Equal(2, BitConverter.ToInt32(dib, 8));
        Assert.Equal(32, BitConverter.ToUInt16(dib, 14));

        // the first stored row is the bottom row: blue then white
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, dib[40..44]);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, dib[48..52]);
    }

    [Fact]
    public void File_header_points_at_the_pixels()
    {
        var dib = DibFormat.FromBgraPixels(2, 2, TopDownPixels);
        var file = DibFormat.ToBmpFile(dib);

        Assert.NotNull(file);
        Assert.Equal((byte)'B', file![0]);
        Assert.Equal((byte)'M', file[1]);
        Assert.Equal(file.Length, BitConverter.ToInt32(file, 2));
        Assert.Equal(14 + 40, BitConverter.ToInt32(file, 10));
    }

    [Fact]
    public void Palette_and_bitfield_bitmaps_get_the_right_pixel_offset()
    {
        // an 8 bit bitmap with a full palette
        var paletted = new byte[40 + (256 * 4) + 4];
        BitConverter.GetBytes(40).CopyTo(paletted, 0);
        BitConverter.GetBytes((ushort)8).CopyTo(paletted, 14);
        Assert.Equal(14 + 40 + 1024, BitConverter.ToInt32(DibFormat.ToBmpFile(paletted)!, 10));

        // a 32 bit bitmap with three color masks after the header
        var masked = new byte[40 + 12 + 4];
        BitConverter.GetBytes(40).CopyTo(masked, 0);
        BitConverter.GetBytes((ushort)32).CopyTo(masked, 14);
        BitConverter.GetBytes(3u).CopyTo(masked, 16);
        Assert.Equal(14 + 40 + 12, BitConverter.ToInt32(DibFormat.ToBmpFile(masked)!, 10));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Too_short_data_is_rejected(int length)
    {
        Assert.Null(DibFormat.ToBmpFile(new byte[length]));
    }

    [Fact]
    public void Header_size_larger_than_data_is_rejected()
    {
        var bad = new byte[50];
        BitConverter.GetBytes(4000).CopyTo(bad, 0);
        Assert.Null(DibFormat.ToBmpFile(bad));
    }
}
