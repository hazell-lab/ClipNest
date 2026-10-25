using System;
using System.IO;
using System.Threading.Tasks;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ClipNest.Core.Tests;

public class StorageCleanupTests
{
    [Fact]
    public async Task Deleting_an_image_entry_removes_its_files()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddImageAsync(9);
        var entry = await env.History.GetAsync(id);
        Assert.True(env.Images.Exists(entry!.ImageFile));
        Assert.True(env.Images.Exists(entry.ThumbnailFile));

        Assert.True(await env.History.DeleteAsync(id));

        Assert.False(env.Images.Exists(entry.ImageFile));
        Assert.False(env.Images.Exists(entry.ThumbnailFile));
        Assert.Equal(0, env.ImageFileCount());
    }

    [Fact]
    public async Task Clearing_history_keeps_pinned_images_and_their_files()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddImageAsync(1);
        await env.AddTextAsync("loose text");
        var pinned = await env.AddImageAsync(2);
        await env.History.SetPinnedAsync(pinned, true);

        var cleared = await env.History.ClearUnpinnedAsync();

        Assert.Equal(2, cleared);
        Assert.Equal(2, env.ImageFileCount());
        Assert.Empty(await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 10));
        Assert.Single(await env.History.GetPinnedAsync(null, HistoryFilter.All));
    }

    [Fact]
    public async Task Pinned_entries_can_be_deleted_explicitly()
    {
        using var env = await TestEnvironment.CreateAsync();
        var pinned = await env.AddImageAsync(4);
        await env.History.SetPinnedAsync(pinned, true);

        Assert.True(await env.History.DeleteAsync(pinned));

        Assert.Empty(await env.History.GetPinnedAsync(null, HistoryFilter.All));
        Assert.Equal(0, env.ImageFileCount());
    }

    [Fact]
    public async Task Delete_everything_removes_all_entries_and_files()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddImageAsync(1);
        var pinned = await env.AddTextAsync("pinned");
        await env.History.SetPinnedAsync(pinned, true);

        await env.History.DeleteEverythingAsync();

        var report = await env.History.GetStorageReportAsync();
        Assert.Equal(0, report.EntryCount);
        Assert.Equal(0, env.ImageFileCount());
    }

    [Fact]
    public async Task Unused_image_files_are_removed_on_start_but_other_files_are_left_alone()
    {
        using var env = await TestEnvironment.CreateAsync();
        var kept = await env.AddImageAsync(1);
        var orphan = Path.Combine(env.Paths.Images, Guid.NewGuid().ToString("N") + ".png");
        var leftoverTemp = Path.Combine(env.Paths.Images, Guid.NewGuid().ToString("N") + ".thumb.png.tmp");
        var userFile = Path.Combine(env.Paths.Images, "my-notes.txt");
        File.WriteAllBytes(orphan, new byte[] { 1 });
        File.WriteAllBytes(leftoverTemp, new byte[] { 1 });
        File.WriteAllText(userFile, "keep me");

        // a second service over the same folder acts like an app restart
        var restarted = new HistoryService(
            new ClipDatabase(env.Paths.Database, env.Time),
            new ImageStore(env.Paths.Images),
            new SelfCopyGuard(env.Time),
            env.Time);
        await restarted.InitializeAsync();

        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(leftoverTemp));
        Assert.True(File.Exists(userFile));
        var entry = await restarted.GetAsync(kept);
        Assert.True(env.Images.Exists(entry!.ImageFile));
    }

    [Fact]
    public async Task Missing_image_file_is_reported_without_crashing()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddImageAsync(6);
        var entry = await env.History.GetAsync(id);
        File.Delete(env.Images.GetFullPath(entry!.ImageFile)!);

        var payload = await env.History.PrepareCopyAsync(id);

        Assert.NotNull(payload);
        Assert.True(payload!.ImageFileMissing);
        Assert.True(await env.History.DeleteAsync(id));
    }

    [Fact]
    public void Image_store_rejects_names_that_are_not_its_own()
    {
        var store = new ImageStore(Path.Combine(Path.GetTempPath(), "clipnest-images"));

        Assert.Null(store.GetFullPath("..\\..\\windows\\system32\\evil.png"));
        Assert.Null(store.GetFullPath("photo.png"));
        Assert.Null(store.GetFullPath(null));
        Assert.NotNull(store.GetFullPath(Guid.NewGuid().ToString("N") + ".thumb.png"));
        Assert.Equal(0, store.Delete("../outside.png"));
    }

    [Fact]
    public async Task Storage_report_counts_entries_and_bytes()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddTextAsync("a");
        var pinned = await env.AddImageAsync(1);
        await env.History.SetPinnedAsync(pinned, true);

        var report = await env.History.GetStorageReportAsync();

        Assert.Equal(2, report.EntryCount);
        Assert.Equal(1, report.PinnedCount);
        Assert.True(report.DatabaseBytes > 0);
        Assert.True(report.ImageBytes > 0);
    }

    [Fact]
    public async Task Damaged_database_is_moved_aside_and_replaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var paths = new AppPaths(root);
        File.WriteAllText(paths.Database, "this is definitely not a sqlite database file, just some text");
        var time = new ManualTimeProvider();

        try
        {
            var history = new HistoryService(new ClipDatabase(paths.Database, time), new ImageStore(paths.Images), new SelfCopyGuard(time), time);
            var result = await history.InitializeAsync();

            Assert.Equal(DatabaseOpenStatus.RecoveredFromCorruption, result.Status);
            Assert.NotNull(result.BackupPath);
            Assert.True(File.Exists(result.BackupPath));

            var added = await history.CaptureTextAsync("works again", null);
            Assert.Equal(CaptureOutcome.Added, added.Outcome);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Using_storage_before_it_is_ready_gives_a_clear_error()
    {
        var root = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        var history = new HistoryService(
            new ClipDatabase(Path.Combine(root, "history.db")),
            new ImageStore(Path.Combine(root, "images")),
            new SelfCopyGuard());

        await Assert.ThrowsAsync<HistoryStorageException>(() => history.GetRecentAsync(null, HistoryFilter.All, 0, 10));
    }
}
