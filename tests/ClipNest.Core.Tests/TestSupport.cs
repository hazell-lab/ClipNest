using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using Microsoft.Data.Sqlite;

namespace ClipNest.Core.Tests;

/// <summary>
/// a clock that only moves when a test tells it to.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset? start = null)
    {
        _now = start ?? new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>
/// a throwaway data folder with a ready history service.
/// </summary>
public sealed class TestEnvironment : IDisposable
{
    private TestEnvironment(string root, bool allowFullTextSearch)
    {
        Root = root;
        Time = new ManualTimeProvider();
        Paths = new AppPaths(root);
        Paths.EnsureCreated();
        Database = new ClipDatabase(Paths.Database, Time, allowFullTextSearch);
        Images = new ImageStore(Paths.Images);
        Guard = new SelfCopyGuard(Time);
        History = new HistoryService(Database, Images, Guard, Time);
    }

    public string Root { get; }
    public ManualTimeProvider Time { get; }
    public AppPaths Paths { get; }
    public ClipDatabase Database { get; }
    public ImageStore Images { get; }
    public SelfCopyGuard Guard { get; }
    public HistoryService History { get; }

    public static async Task<TestEnvironment> CreateAsync(bool allowFullTextSearch = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        var env = new TestEnvironment(root, allowFullTextSearch);
        await env.History.InitializeAsync();
        return env;
    }

    /// <summary>
    /// adds text and moves the clock forward so every entry has its own time.
    /// </summary>
    public async Task<long> AddTextAsync(string text)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        var result = await History.CaptureTextAsync(text, "notepad");
        return result.EntryId ?? throw new InvalidOperationException("text was not stored: " + result.Outcome);
    }

    public async Task<long> AddImageAsync(byte seed, int width = 4, int height = 3)
    {
        Time.Advance(TimeSpan.FromSeconds(1));
        var result = await History.CaptureImageAsync(MakeImage(seed, width, height), "mspaint");
        return result.EntryId ?? throw new InvalidOperationException("image was not stored: " + result.Outcome);
    }

    public static CapturedImage MakeImage(byte seed, int width = 4, int height = 3)
    {
        var pixels = Enumerable.Range(0, width * height * 4).Select(i => (byte)(i + seed)).ToArray();

        // fake png bytes are fine here, the core never decodes them
        var png = new byte[200 + seed];
        png[0] = 0x89;
        var thumb = new byte[50];
        return new CapturedImage(png, thumb, width, height, ContentHasher.ForImagePixels(width, height, pixels));
    }

    public int ImageFileCount() =>
        Directory.Exists(Paths.Images) ? Directory.GetFiles(Paths.Images).Length : 0;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // a leftover temp folder is harmless
        }
    }
}
