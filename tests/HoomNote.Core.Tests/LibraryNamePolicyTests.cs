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

    [Fact]
    public void NotebookTitles_SortNumbersFirstThenLettersCaseInsensitively()
    {
        var titles = new[]
        {
            "zoology", "Biology", "10 Labs", "2 Labs", "anatomy", "biology",
            "# Archive", "01 Intro"
        };

        var sorted = titles.OrderBy(title => title, NotebookTitleComparer.Instance).ToArray();

        Assert.Equal(
            ["01 Intro", "2 Labs", "10 Labs", "anatomy", "Biology", "biology", "zoology", "# Archive"],
            sorted);
    }

    [Fact]
    public void NotebookTitles_UseNaturalNumbersWithinAlphabeticTitles()
    {
        var titles = new[] { "Unit 12", "unit 2", "Unit 1", "Unit 02" };

        var sorted = titles.OrderBy(title => title, NotebookTitleComparer.Instance).ToArray();

        Assert.Equal(["Unit 1", "unit 2", "Unit 02", "Unit 12"], sorted);
    }
}
