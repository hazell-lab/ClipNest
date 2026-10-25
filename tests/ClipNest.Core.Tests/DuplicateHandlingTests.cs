using System;
using System.Linq;
using System.Threading.Tasks;
using ClipNest.Core.Capture;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using Xunit;

namespace ClipNest.Core.Tests;

public class DuplicateHandlingTests
{
    [Fact]
    public async Task Consecutive_same_text_is_stored_once()
    {
        using var env = await TestEnvironment.CreateAsync();

        var first = await env.History.CaptureTextAsync("hello world", "notepad");
        var second = await env.History.CaptureTextAsync("hello world", "notepad");

        Assert.Equal(CaptureOutcome.Added, first.Outcome);
        Assert.Equal(CaptureOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(first.EntryId, second.EntryId);
        Assert.Single(await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50));
    }

    [Fact]
    public async Task Repeated_older_text_moves_to_top_instead_of_duplicating()
    {
        using var env = await TestEnvironment.CreateAsync();
        var a = await env.AddTextAsync("alpha");
        await env.AddTextAsync("beta");

        env.Time.Advance(TimeSpan.FromSeconds(5));
        var again = await env.History.CaptureTextAsync("alpha", null);

        Assert.Equal(CaptureOutcome.MovedToTop, again.Outcome);
        Assert.Equal(a, again.EntryId);
        var recent = await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50);
        Assert.Equal(2, recent.Count);
        Assert.Equal(a, recent[0].Id);
    }

    [Fact]
    public async Task Text_that_differs_only_in_whitespace_is_kept_separately()
    {
        using var env = await TestEnvironment.CreateAsync();
        await env.AddTextAsync("value");
        await env.AddTextAsync("value ");
        await env.AddTextAsync("Value");

        var recent = await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50);
        Assert.Equal(3, recent.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public async Task Blank_text_is_skipped(string text)
    {
        using var env = await TestEnvironment.CreateAsync();
        var result = await env.History.CaptureTextAsync(text, null);

        Assert.Equal(CaptureOutcome.Skipped, result.Outcome);
        Assert.Equal(SkipReason.Empty, result.Reason);
    }

    [Fact]
    public async Task Text_over_the_size_limit_is_skipped_not_cut()
    {
        using var env = await TestEnvironment.CreateAsync();
        var huge = new string('x', CaptureLimits.MaxTextChars + 1);

        var result = await env.History.CaptureTextAsync(huge, null);

        Assert.Equal(SkipReason.TooLarge, result.Reason);
        Assert.Empty(await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50));
    }

    [Fact]
    public async Task Same_image_twice_keeps_one_entry_and_one_set_of_files()
    {
        using var env = await TestEnvironment.CreateAsync();
        var first = await env.History.CaptureImageAsync(TestEnvironment.MakeImage(7), "mspaint");
        var second = await env.History.CaptureImageAsync(TestEnvironment.MakeImage(7), "mspaint");

        Assert.Equal(CaptureOutcome.Added, first.Outcome);
        Assert.Equal(CaptureOutcome.DuplicateIgnored, second.Outcome);
        Assert.Equal(2, env.ImageFileCount());
    }

    [Fact]
    public async Task Oversized_image_is_skipped()
    {
        using var env = await TestEnvironment.CreateAsync();
        var image = TestEnvironment.MakeImage(1) with { Width = 20_000, Height = 10 };

        var result = await env.History.CaptureImageAsync(image, null);

        Assert.Equal(SkipReason.TooLarge, result.Reason);
        Assert.Equal(0, env.ImageFileCount());
    }

    [Fact]
    public async Task Copying_pinned_content_again_does_not_create_a_duplicate()
    {
        using var env = await TestEnvironment.CreateAsync();
        var pinned = await env.AddTextAsync("snippet");
        await env.History.SetPinnedAsync(pinned, true);
        await env.AddTextAsync("other");

        env.Time.Advance(TimeSpan.FromSeconds(3));
        var result = await env.History.CaptureTextAsync("snippet", null);

        Assert.Equal(pinned, result.EntryId);
        var pins = await env.History.GetPinnedAsync(null, HistoryFilter.All);
        var recent = await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50);
        Assert.Single(pins);
        Assert.Single(recent);
        Assert.DoesNotContain(recent, e => e.Id == pinned);
    }

    [Fact]
    public async Task Text_preview_and_stats_are_stored()
    {
        using var env = await TestEnvironment.CreateAsync();
        var text = "line one\nline two\n" + new string('z', 3000);
        var id = await env.AddTextAsync(text);

        var entry = await env.History.GetAsync(id);
        Assert.NotNull(entry);
        Assert.Equal(3, entry!.LineCount);
        Assert.Equal(text.Length, entry.TextLength);
        Assert.Equal(1000, entry.TextPreview!.Length);
        Assert.Equal(text, await env.History.GetFullTextAsync(id));
        Assert.Equal("notepad", entry.SourceApp);
    }

    [Fact]
    public async Task Concurrent_captures_of_the_same_text_store_one_entry()
    {
        using var env = await TestEnvironment.CreateAsync();
        var tasks = Enumerable.Range(0, 10).Select(_ => env.History.CaptureTextAsync("same", null));
        await Task.WhenAll(tasks);

        Assert.Single(await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50));
    }
}
