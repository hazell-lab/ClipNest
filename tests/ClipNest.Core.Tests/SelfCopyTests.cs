using System;
using System.Threading.Tasks;
using ClipNest.Core.Capture;
using ClipNest.Core.History;
using ClipNest.Core.Models;
using Xunit;

namespace ClipNest.Core.Tests;

public class SelfCopyTests
{
    [Fact]
    public async Task Copying_an_entry_back_is_not_captured_as_a_new_entry()
    {
        using var env = await TestEnvironment.CreateAsync();
        var a = await env.AddTextAsync("first item");
        var b = await env.AddTextAsync("second item");

        var payload = await env.History.PrepareCopyAsync(a);
        Assert.NotNull(payload);
        Assert.Equal("first item", payload!.Text);

        // this is the clipboard change caused by the app itself
        var result = await env.History.CaptureTextAsync("first item", "clipnest");

        Assert.Equal(CaptureOutcome.IgnoredSelfCopy, result.Outcome);
        var recent = await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50);
        Assert.Equal(2, recent.Count);
        Assert.Equal(a, recent[0].Id);
        Assert.Equal(b, recent[1].Id);
    }

    [Fact]
    public async Task Copying_an_image_back_is_not_captured_again()
    {
        using var env = await TestEnvironment.CreateAsync();
        var image = await env.AddImageAsync(3);
        await env.AddTextAsync("text after image");
        var filesBefore = env.ImageFileCount();

        var payload = await env.History.PrepareCopyAsync(image);
        var result = await env.History.CaptureImageAsync(TestEnvironment.MakeImage(3), null);

        Assert.NotNull(payload!.ImagePath);
        Assert.Equal(CaptureOutcome.IgnoredSelfCopy, result.Outcome);
        Assert.Equal(filesBefore, env.ImageFileCount());
        Assert.Equal(2, (await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50)).Count);
    }

    [Fact]
    public async Task After_the_guard_expires_the_copied_entry_is_still_not_duplicated()
    {
        using var env = await TestEnvironment.CreateAsync();
        var a = await env.AddTextAsync("alpha");
        await env.AddTextAsync("beta");

        await env.History.PrepareCopyAsync(a);
        env.Time.Advance(TimeSpan.FromMinutes(1));
        var result = await env.History.CaptureTextAsync("alpha", null);

        // the copied entry was moved to the top, so this is a consecutive repeat
        Assert.Equal(CaptureOutcome.DuplicateIgnored, result.Outcome);
        Assert.Equal(2, (await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50)).Count);
    }

    [Fact]
    public async Task Copying_a_pinned_entry_back_is_not_captured_again()
    {
        using var env = await TestEnvironment.CreateAsync();
        var pinned = await env.AddTextAsync("pinned snippet");
        await env.History.SetPinnedAsync(pinned, true);
        await env.AddTextAsync("newer");

        await env.History.PrepareCopyAsync(pinned);
        var result = await env.History.CaptureTextAsync("pinned snippet", null);

        Assert.Equal(CaptureOutcome.IgnoredSelfCopy, result.Outcome);
        Assert.Single(await env.History.GetPinnedAsync(null, HistoryFilter.All));
        Assert.Single(await env.History.GetRecentAsync(null, HistoryFilter.All, 0, 50));
    }

    [Fact]
    public void Guard_forgets_content_after_its_window()
    {
        var time = new ManualTimeProvider();
        var guard = new SelfCopyGuard(time, TimeSpan.FromSeconds(5));
        guard.Expect("t:abc");

        Assert.True(guard.IsSelfCopy("t:abc"));
        Assert.True(guard.IsSelfCopy("t:abc"));
        Assert.False(guard.IsSelfCopy("t:other"));

        time.Advance(TimeSpan.FromSeconds(6));
        Assert.False(guard.IsSelfCopy("t:abc"));
    }

    [Fact]
    public void Clipboard_data_with_the_app_marker_is_skipped()
    {
        var decision = CapturePolicy.Decide(
            new ClipboardFormatsInfo { HasText = true, HasSelfCopyMarker = true },
            captureEnabled: true,
            captureImages: true,
            sourceExcluded: false);

        Assert.Equal(CaptureAction.Skip, decision.Action);
        Assert.Equal(SkipReason.SelfCopy, decision.Reason);
    }

    [Fact]
    public async Task Copying_a_deleted_entry_returns_nothing()
    {
        using var env = await TestEnvironment.CreateAsync();
        var id = await env.AddTextAsync("gone soon");
        await env.History.DeleteAsync(id);

        Assert.Null(await env.History.PrepareCopyAsync(id));
    }
}
