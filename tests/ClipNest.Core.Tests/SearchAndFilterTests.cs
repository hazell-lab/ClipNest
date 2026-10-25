using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using ClipNest.Core.Models;
using Xunit;

namespace ClipNest.Core.Tests;

public class SearchAndFilterTests
{
    private static async Task<List<long>> RecentIds(TestEnvironment env, string? search, HistoryFilter filter = HistoryFilter.All) =>
        (await env.History.GetRecentAsync(search, filter, 0, 1000)).Select(e => e.Id).ToList();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Search_finds_substrings_ignoring_case(bool fullText)
    {
        using var env = await TestEnvironment.CreateAsync(fullText);
        var hit = await env.AddTextAsync("The Quick Brown Fox");
        await env.AddTextAsync("lazy dog");

        Assert.Equal(new[] { hit }, await RecentIds(env, "quick brown"));
        Assert.Equal(new[] { hit }, await RecentIds(env, "BROWN"));
        Assert.Equal(new[] { hit }, await RecentIds(env, "  fox  "));
        Assert.Empty(await RecentIds(env, "cat"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Short_queries_still_work(bool fullText)
    {
        using var env = await TestEnvironment.CreateAsync(fullText);
        var hit = await env.AddTextAsync("x = 42");
        await env.AddTextAsync("nothing here");

        Assert.Equal(new[] { hit }, await RecentIds(env, "42"));
        Assert.Equal(new[] { hit }, await RecentIds(env, "x"));
    }

    [Theory]
    [InlineData(true, "say \"hi\"")]
    [InlineData(false, "say \"hi\"")]
    [InlineData(true, "100%")]
    [InlineData(false, "100%")]
    [InlineData(true, "a_b")]
    [InlineData(true, "foo OR bar")]
    [InlineData(true, "c++ (x)*")]
    [InlineData(true, "naïve café")]
    [InlineData(false, "naïve café")]
    public async Task Special_characters_are_matched_literally(bool fullText, string needle)
    {
        using var env = await TestEnvironment.CreateAsync(fullText);
        var hit = await env.AddTextAsync("before " + needle + " after");
        await env.AddTextAsync("foo bar a b 100 hi say c x naive cafe");

        Assert.Equal(new[] { hit }, await RecentIds(env, needle));
    }

    [Fact]
    public async Task Both_search_paths_return_the_same_results()
    {
        using var fast = await TestEnvironment.CreateAsync(allowFullTextSearch: true);
        using var slow = await TestEnvironment.CreateAsync(allowFullTextSearch: false);
        var words = new[] { "alpha", "beta", "gamma", "delta", "Alphabet soup", "BETAMAX", "gam" };
        foreach (var word in words)
        {
            await fast.AddTextAsync(word);
            await slow.AddTextAsync(word);
        }

        foreach (var query in new[] { "alp", "beta", "am", "a", "soup", "ma", "zzz", "gam" })
        {
            var fastTexts = (await fast.History.GetRecentAsync(query, HistoryFilter.All, 0, 100)).Select(e => e.TextPreview);
            var slowTexts = (await slow.History.GetRecentAsync(query, HistoryFilter.All, 0, 100)).Select(e => e.TextPreview);
            Assert.Equal(slowTexts, fastTexts);
        }
    }

    [Fact]
    public async Task Filters_split_text_images_and_pinned()
    {
        using var env = await TestEnvironment.CreateAsync();
        var text = await env.AddTextAsync("plain text");
        var image = await env.AddImageAsync(1);
        var pinnedText = await env.AddTextAsync("pinned text");
        var pinnedImage = await env.AddImageAsync(2);
        await env.History.SetPinnedAsync(pinnedText, true);
        await env.History.SetPinnedAsync(pinnedImage, true);

        Assert.Equal(new[] { image, text }, await RecentIds(env, null, HistoryFilter.All));
        Assert.Equal(new[] { text }, await RecentIds(env, null, HistoryFilter.Text));
        Assert.Equal(new[] { image }, await RecentIds(env, null, HistoryFilter.Images));
        Assert.Empty(await RecentIds(env, null, HistoryFilter.Pinned));

        var pinnedAll = (await env.History.GetPinnedAsync(null, HistoryFilter.Pinned)).Select(e => e.Id).ToList();
        var pinnedTextOnly = (await env.History.GetPinnedAsync(null, HistoryFilter.Text)).Select(e => e.Id).ToList();
        var pinnedImagesOnly = (await env.History.GetPinnedAsync(null, HistoryFilter.Images)).Select(e => e.Id).ToList();
        Assert.Equal(2, pinnedAll.Count);
        Assert.Equal(new[] { pinnedText }, pinnedTextOnly);
        Assert.Equal(new[] { pinnedImage }, pinnedImagesOnly);
    }

    [Fact]
    public async Task Images_are_only_found_by_their_pinned_title()
    {
        using var env = await TestEnvironment.CreateAsync();
        var image = await env.AddImageAsync(5);

        // there is no ocr, so image contents never match
        Assert.Empty(await RecentIds(env, "png"));

        await env.History.SetPinnedAsync(image, true);
        await env.History.RenamePinAsync(image, "Team Logo");

        var found = await env.History.GetPinnedAsync("logo", HistoryFilter.All);
        Assert.Equal(image, Assert.Single(found).Id);
        Assert.Equal("Team Logo", found[0].PinTitle);
    }

    [Fact]
    public async Task Pin_titles_are_searchable_and_removed_when_unpinned()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddTextAsync("ssh user@server -p 2222");
        await env.History.SetPinnedAsync(id, true);
        await env.History.RenamePinAsync(id, "Prod login");

        Assert.Single(await env.History.GetPinnedAsync("prod", HistoryFilter.All));

        await env.History.SetPinnedAsync(id, false);
        var entry = await env.History.GetAsync(id);
        Assert.Null(entry!.PinTitle);
        Assert.Empty(await RecentIds(env, "prod"));
        Assert.Single(await RecentIds(env, "2222"));
    }

    [Fact]
    public async Task Paging_returns_every_item_once_in_order()
    {
        using var env = await TestEnvironment.CreateAsync();
        for (var i = 0; i < 250; i++)
        {
            await env.AddTextAsync($"item {i}");
        }

        var seen = new List<long>();
        for (var offset = 0; ; offset += 100)
        {
            var page = await env.History.GetRecentAsync(null, HistoryFilter.All, offset, 100);
            seen.AddRange(page.Select(e => e.Id));
            if (page.Count < 100)
            {
                break;
            }
        }

        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct().Count());
        Assert.Equal(seen.OrderByDescending(id => id), seen);
    }

    [Fact]
    public async Task Search_stays_fast_with_a_large_history()
    {
        using var env = await TestEnvironment.CreateAsync();
        var random = new Random(42);
        for (var i = 0; i < 5000; i++)
        {
            var filler = new string((char)('a' + random.Next(26)), 200);
            await env.History.CaptureTextAsync($"entry {i} {filler} token{i % 50}", null);
            env.Time.Advance(TimeSpan.FromMilliseconds(10));
        }

        var watch = Stopwatch.StartNew();
        var results = await env.History.GetRecentAsync("token7", HistoryFilter.All, 0, 1000);
        watch.Stop();

        // token7 also matches token70..token79 style values, which do not exist with mod 50
        Assert.Equal(100, results.Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"search took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Long_text_is_searchable_near_its_start()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddTextAsync("needle-start " + new string('q', 100_000) + " needle-end");

        Assert.Single(await RecentIds(env, "needle-start"));

        // only the first part of very long text is indexed
        Assert.Empty(await RecentIds(env, "needle-end"));
        Assert.True(id > 0);
    }
}
