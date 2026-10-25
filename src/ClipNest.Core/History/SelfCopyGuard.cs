using System;
using System.Collections.Generic;

namespace ClipNest.Core.History;

/// <summary>
/// remembers content the app just put on the clipboard,
/// so the next clipboard change is not saved again as a new entry.
/// </summary>
public sealed class SelfCopyGuard
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(5);

    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _expected = new(StringComparer.Ordinal);

    public SelfCopyGuard(TimeProvider? time = null, TimeSpan? window = null)
    {
        _time = time ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    public void Expect(string contentHash)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        lock (_gate)
        {
            RemoveExpired();
            _expected[contentHash] = _time.GetUtcNow() + _window;
        }
    }

    public bool IsSelfCopy(string contentHash)
    {
        if (string.IsNullOrEmpty(contentHash))
        {
            return false;
        }

        lock (_gate)
        {
            RemoveExpired();

            // keep the mark until it expires, since one copy can fire several change events
            return _expected.ContainsKey(contentHash);
        }
    }

    private void RemoveExpired()
    {
        var now = _time.GetUtcNow();
        List<string>? old = null;
        foreach (var pair in _expected)
        {
            if (pair.Value <= now)
            {
                (old ??= new List<string>()).Add(pair.Key);
            }
        }

        if (old is null)
        {
            return;
        }

        foreach (var key in old)
        {
            _expected.Remove(key);
        }
    }
}
