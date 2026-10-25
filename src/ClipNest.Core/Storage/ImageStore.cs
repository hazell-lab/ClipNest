using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using ClipNest.Core.Diagnostics;

namespace ClipNest.Core.Storage;

/// <summary>
/// keeps captured images and their thumbnails as png files in one folder.
/// </summary>
public sealed partial class ImageStore
{
    public ImageStore(string folder)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        Folder = folder;
    }

    public string Folder { get; }

    /// <summary>
    /// saves both files and returns their names (not full paths).
    /// </summary>
    public (string ImageFile, string ThumbnailFile) Save(byte[] pngBytes, byte[] thumbnailBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);
        ArgumentNullException.ThrowIfNull(thumbnailBytes);
        Directory.CreateDirectory(Folder);

        var id = Guid.NewGuid().ToString("N");
        var imageFile = id + ".png";
        var thumbFile = id + ".thumb.png";
        try
        {
            WriteAtomically(Path.Combine(Folder, imageFile), pngBytes);
            WriteAtomically(Path.Combine(Folder, thumbFile), thumbnailBytes);
        }
        catch
        {
            Delete(imageFile, thumbFile);
            throw;
        }

        return (imageFile, thumbFile);
    }

    /// <summary>
    /// turns a stored file name into a full path. returns null for anything
    /// that does not look like one of our own files.
    /// </summary>
    public string? GetFullPath(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || !OwnFileName().IsMatch(fileName))
        {
            return null;
        }

        return Path.Combine(Folder, fileName);
    }

    public bool Exists(string? fileName)
    {
        var path = GetFullPath(fileName);
        return path is not null && File.Exists(path);
    }

    /// <summary>
    /// deletes files by name. missing files are fine.
    /// </summary>
    public int Delete(params string?[] fileNames)
    {
        var deleted = 0;
        foreach (var name in fileNames)
        {
            var path = GetFullPath(name);
            if (path is null)
            {
                continue;
            }

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("could not delete an image file", ex);
            }
        }

        return deleted;
    }

    public int Delete(IEnumerable<string> fileNames)
    {
        var list = new List<string?>(fileNames);
        return Delete(list.ToArray());
    }

    /// <summary>
    /// removes our files that no entry points to, plus old temp files.
    /// </summary>
    public int DeleteOrphans(IReadOnlySet<string> referencedFiles)
    {
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        var removed = 0;
        foreach (var path in Directory.EnumerateFiles(Folder))
        {
            var name = Path.GetFileName(path);
            var isTemp = name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                && OwnFileName().IsMatch(name[..^4]);
            var isOrphan = OwnFileName().IsMatch(name) && !referencedFiles.Contains(name);
            if (!isTemp && !isOrphan)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppLog.Error("could not delete an unused image file", ex);
            }
        }

        return removed;
    }

    public int DeleteAllOwnFiles() => DeleteOrphans(new HashSet<string>());

    public long MeasureBytes()
    {
        if (!Directory.Exists(Folder))
        {
            return 0;
        }

        long total = 0;
        foreach (var path in Directory.EnumerateFiles(Folder))
        {
            try
            {
                total += new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // the file vanished while counting
            }
        }

        return total;
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("^[0-9a-f]{32}(\\.thumb)?\\.png$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnFileName();
}
