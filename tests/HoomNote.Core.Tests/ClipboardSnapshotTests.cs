using HoomNote.Core.Editing;

namespace HoomNote.Core.Tests;

public sealed class ClipboardSnapshotTests
{
    [Theory]
    [InlineData("[]", null)]
    [InlineData(null, "Copied PDF text")]
    public void LocalCopyRemainsAvailableUntilAnotherCopyReplacesIt(string? json, string? text)
    {
        var snapshot = new ClipboardSnapshot(42, json, text);
        Assert.True(snapshot.IsCurrent(42));
        Assert.True(snapshot.IsCurrent(42)); // Repeated paste keeps the same copy.
        Assert.False(snapshot.IsCurrent(43)); // External copy wins even if the local write failed.
        Assert.False(snapshot.IsCurrent(0));
    }

    [Fact]
    public void UnknownClipboardOwnershipAndEmptyCopiesCannotEnableFallback()
    {
        Assert.False(new ClipboardSnapshot(0, "[]", null).IsCurrent(0));
        Assert.False(new ClipboardSnapshot(42, null, " ").IsCurrent(42));
    }
}
