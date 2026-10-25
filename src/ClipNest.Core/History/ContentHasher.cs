using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace ClipNest.Core.History;

/// <summary>
/// makes short ids for clipboard content so repeats can be spotted.
/// </summary>
public static class ContentHasher
{
    public static string ForText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = MemoryMarshal.AsBytes(text.AsSpan());
        return "t:" + Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>
    /// hashes raw pixels so the same picture matches even if encoded differently.
    /// </summary>
    public static string ForImagePixels(int width, int height, ReadOnlySpan<byte> pixels)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(size, width);
        BinaryPrimitives.WriteInt32LittleEndian(size[4..], height);
        hash.AppendData(size);
        hash.AppendData(pixels);
        return "i:" + Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
