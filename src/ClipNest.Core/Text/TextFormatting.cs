using System;
using System.Globalization;
using System.Text;

namespace ClipNest.Core.Text;

public static class TextStats
{
    public static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var lines = 1;
        foreach (var c in text)
        {
            if (c == '\n')
            {
                lines++;
            }
        }

        return lines;
    }
}

public static class SearchText
{
    /// <summary>
    /// cleans a search box value. returns null when there is nothing to search.
    /// </summary>
    public static string? NormalizeQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return query.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// builds the lowercase text that search runs against.
    /// pinned titles are included so images can be found by title.
    /// </summary>
    public static string BuildIndexText(string? pinTitle, string? text, int maxTextChars)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(pinTitle))
        {
            builder.Append(pinTitle.Trim()).Append('\n');
        }

        if (!string.IsNullOrEmpty(text))
        {
            builder.Append(text.AsSpan(0, Math.Min(text.Length, maxTextChars)));
        }

        return builder.ToString().ToLowerInvariant();
    }

    public static int CountCharacters(string value)
    {
        var count = 0;
        foreach (var _ in value.EnumerateRunes())
        {
            count++;
        }

        return count;
    }
}

public static class PreviewFormatter
{
    /// <summary>
    /// makes a tidy preview for the list. keeps line breaks but trims noise.
    /// </summary>
    public static string ForList(string? text, int maxChars = 400)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var slice = text.Length > maxChars * 2 ? text[..(maxChars * 2)] : text;
        slice = slice.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");

        // skip blank lines at the start
        slice = slice.TrimStart('\n', ' ');
        if (slice.Length > maxChars)
        {
            slice = slice[..maxChars] + "…";
        }

        return slice.TrimEnd();
    }

    /// <summary>
    /// the first non empty line, used as a one line title.
    /// </summary>
    public static string FirstLine(string? text, int maxChars = 120)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().Replace("\t", " ");
            if (line.Length == 0)
            {
                continue;
            }

            return line.Length > maxChars ? line[..maxChars] + "…" : line;
        }

        return "";
    }
}

public static class RelativeTime
{
    public static string Format(DateTimeOffset then, DateTimeOffset now)
    {
        var age = now - then;
        if (age < TimeSpan.FromSeconds(45))
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(60))
        {
            return $"{Math.Max(1, (int)Math.Round(age.TotalMinutes))} min ago";
        }

        if (age < TimeSpan.FromHours(24))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        var localThen = then.ToLocalTime();
        var localNow = now.ToLocalTime();
        if (localNow.Date - localThen.Date == TimeSpan.FromDays(1))
        {
            return "yesterday";
        }

        if (age < TimeSpan.FromDays(7))
        {
            return $"{(int)age.TotalDays} days ago";
        }

        return localThen.Year == localNow.Year
            ? localThen.ToString("MMM d", CultureInfo.CurrentCulture)
            : localThen.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }
}

public static class ByteSize
{
    public static string Format(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = { "KB", "MB", "GB", "TB" };
        var unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value >= 100
            ? $"{value.ToString("0", CultureInfo.InvariantCulture)} {units[unit]}"
            : $"{value.ToString("0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}
