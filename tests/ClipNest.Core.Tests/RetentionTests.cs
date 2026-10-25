using System;
using System.Linq;
using System.Threading.Tasks;
using ClipNest.Core.Models;
using ClipNest.Core.Storage;
using Xunit;

namespace ClipNest.Core.Tests;

public class RetentionTests
{
    private static RetentionLimits Limits(int maxItems = 10_000, long maxBytes = long.MaxValue, int days = 0) =>
        new(maxItems, maxBytes, days);

    [Fact]
    public async Task Old_unpinned_entries_are_removed_and_old_pinned_ones_kept()
    {
        using var env = await TestEnvironment.CreateAsync();
        var oldUnpinned = await env.AddTextAsync("old note");
        var oldPinned = await env.AddTextAsync("old pinned");
        await env.History.SetPinnedAsync(oldPinned, true);

        env.Time.Advance(TimeSpan.FromDays(40));
        var fresh = await env.AddTextAsync("fresh note");

        var removed = await env.History.ApplyRetentionAsync(Limits(days: 30));

        Assert.Equal(1, removed);
        Assert.Null(await env.History.GetAsync(oldUnpinned));
        Assert.NotNull(await env.History.GetAsync(oldPinned));
        Assert.NotNull(await env.History.GetAsync(fresh));
    }

    [Fact]
    public async Task Zero_days_means_entries_never_expire_by_age()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddTextAsync("ancient");
        env.Time.Advance(TimeSpan.FromDays(5000));

        Assert.Equal(0, await env.History.ApplyRetentionAsync(Limits(days: 0)));
    }

    [Fact]
    public async Task Item_limit_keeps_the_newest_unpinned_and_all_pinned()
    {
        using var env = await TestEnvironment.CreateAsync();
        var ids = new long[6];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = await env.AddTextAsync($"item {i}");
        }

        await env.History.SetPinnedAsync(ids[0], true);
        await env.History.SetPinnedAsync(ids[1], true);

        await env.History.ApplyRetentionAsync(Limits(maxItems: 2));

        var recent = (await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 100)).Select(e => e.Id);
        var pinned = (await env.History.GetPinnedAsync(null, HistoryFilter.All)).Select(e => e.Id);
        Assert.Equal(new[] { ids[5], ids[4] }, recent);
        Assert.Equal(new[] { ids[0], ids[1] }.OrderBy(x => x), pinned.OrderBy(x => x));
    }

    [Fact]
    public async Task Storage_limit_removes_oldest_unpinned_until_it_fits()
    {
        using var env = await TestEnvironment.CreateAsync();
        var big = new string('a', 1000);
        var a = await env.AddTextAsync("1" + big);
        var b = await env.AddTextAsync("2" + big);
        var pinned = await env.AddTextAsync("3" + big);
        var c = await env.AddTextAsync("4" + big);
        await env.History.SetPinnedAsync(pinned, true);

        // room for about two entries
        await env.History.ApplyRetentionAsync(Limits(maxBytes: 2100));

        Assert.Null(await env.History.GetAsync(a));
        Assert.Null(await env.History.GetAsync(b));
        Assert.NotNull(await env.History.GetAsync(pinned));
        Assert.NotNull(await env.History.GetAsync(c));
    }

    [Fact]
    public async Task Storage_limit_never_removes_pinned_entries_even_if_still_over()
    {
        using var env = await TestEnvironment.CreateAsync();
        var pinned = await env.AddTextAsync(new string('p', 5000));
        await env.History.SetPinnedAsync(pinned, true);
        var loose = await env.AddTextAsync("small");

        await env.History.ApplyRetentionAsync(Limits(maxBytes: 100));

        Assert.NotNull(await env.History.GetAsync(pinned));
        Assert.Null(await env.History.GetAsync(loose));
    }

    [Fact]
    public async Task Retention_deletes_image_files_of_removed_entries()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddImageAsync(1);
        var keptPinned = await env.AddImageAsync(2);
        await env.History.SetPinnedAsync(keptPinned, true);
        Assert.Equal(4, env.ImageFileCount());

        env.Time.Advance(TimeSpan.FromDays(10));
        await env.History.ApplyRetentionAsync(Limits(days: 1));

        Assert.Equal(2, env.ImageFileCount());
        var kept = await env.History.GetAsync(keptPinned);
        Assert.True(env.Images.Exists(kept!.ImageFile));
        Assert.True(env.Images.Exists(kept.ThumbnailFile));
    }

    [Fact]
    public async Task Copying_an_entry_refreshes_its_age()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddTextAsync("still useful");
        env.Time.Advance(TimeSpan.FromDays(20));
        await env.History.PrepareCopyAsync(id);
        env.Time.Advance(TimeSpan.FromDays(20));

        await env.History.ApplyRetentionAsync(Limits(days: 30));

        Assert.NotNull(await env.History.GetAsync(id));
    }
}
