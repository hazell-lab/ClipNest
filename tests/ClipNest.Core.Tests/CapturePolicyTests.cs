using ClipNest.Core.Capture;
using Xunit;

namespace ClipNest.Core.Tests;

public class CapturePolicyTests
{
    private static CaptureDecision Decide(ClipboardFormatsInfo formats, bool enabled = true, bool images = true, bool excluded = false) =>
        CapturePolicy.Decide(formats, enabled, images, excluded);

    [Fact]
    public void Paused_capture_skips_everything()
    {
        Assert.Equal(SkipReason.Paused, Decide(new() { HasText = true }, enabled: false).Reason);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Privacy_markers_are_honored(bool exclude, bool optOut, bool viewerIgnore)
    {
        var decision = Decide(new()
        {
            HasText = true,
            HasExcludeMarker = exclude,
            HasHistoryOptOut = optOut,
            HasViewerIgnoreMarker = viewerIgnore,
        });

        Assert.Equal(CaptureAction.Skip, decision.Action);
        Assert.Equal(SkipReason.PrivacyMarker, decision.Reason);
    }

    [Fact]
    public void Excluded_apps_are_skipped()
    {
        Assert.Equal(SkipReason.ExcludedApp, Decide(new() { HasText = true }, excluded: true).Reason);
    }

    [Fact]
    public void Text_is_preferred_when_an_image_is_also_offered()
    {
        Assert.Equal(CaptureAction.CaptureText, Decide(new() { HasText = true, HasImage = true }).Action);
    }

    [Fact]
    public void Images_are_captured_unless_turned_off()
    {
        Assert.Equal(CaptureAction.CaptureImage, Decide(new() { HasImage = true }).Action);
        Assert.Equal(SkipReason.ImagesDisabled, Decide(new() { HasImage = true }, images: false).Reason);
    }

    [Fact]
    public void Files_and_other_formats_are_unsupported()
    {
        Assert.Equal(SkipReason.UnsupportedFormat, Decide(new() { HasFileList = true }).Reason);
        Assert.Equal(SkipReason.UnsupportedFormat, Decide(new() { HasOtherFormats = true }).Reason);
        Assert.Equal(SkipReason.Empty, Decide(new()).Reason);
        Assert.Contains("files", CapturePolicy.DescribeSkip(SkipReason.UnsupportedFormat, hadFileList: true));
    }

    [Theory]
    [InlineData("KeePass.exe", "keepass", true)]
    [InlineData("keepass", "KeePass.EXE", true)]
    [InlineData(@"C:\Tools\KeePass.exe", "KeePass", true)]
    [InlineData("KeePass.exe", "KeePassXC", false)]
    [InlineData("KeePass.exe", null, false)]
    [InlineData("KeePass.exe", "", false)]
    public void Exclusion_names_compare_loosely(string listed, string? process, bool expected)
    {
        Assert.Equal(expected, AppExclusionList.IsExcluded(new[] { listed }, process));
    }
}
