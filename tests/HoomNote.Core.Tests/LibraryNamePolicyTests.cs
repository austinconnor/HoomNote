using HoomNote.Core.Documents;

namespace HoomNote.Core.Tests;

public sealed class LibraryNamePolicyTests
{
    [Fact]
    public void Normalize_TrimsNameWithoutChangingContent()
    {
        Assert.Equal("Semester notes", LibraryNamePolicy.Normalize("  Semester notes  "));
    }

    [Fact]
    public void Normalize_RejectsBlankName()
    {
        Assert.Null(LibraryNamePolicy.Normalize("   "));
    }

    [Fact]
    public void Normalize_PreservesExactly128Characters()
    {
        var name = new string('n', LibraryNamePolicy.MaxLength);

        Assert.Equal(name, LibraryNamePolicy.Normalize(name));
    }

    [Fact]
    public void Normalize_TruncatesNamesBeyond128Characters()
    {
        var name = new string('n', LibraryNamePolicy.MaxLength + 20);

        var normalized = LibraryNamePolicy.Normalize(name);

        Assert.NotNull(normalized);
        Assert.Equal(LibraryNamePolicy.MaxLength, normalized.Length);
    }
}
