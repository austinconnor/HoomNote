using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using HoomNote.Infrastructure.Storage;

namespace HoomNote.Infrastructure.Tests;

public sealed class HomeLibraryOrderingTests
{
    [Fact]
    public void RootLayout_ListsFoldersAlphabeticallyAndRecentNotebooksByUse()
    {
        var school = new NotebookFolderPreference { Name = "School" };
        var archive = new NotebookFolderPreference { Name = "Archive" };
        var orphan = new NotebookFolderPreference { Name = "Recovered", ParentId = Guid.NewGuid() };
        var now = DateTimeOffset.UtcNow;
        var older = Summary("Older", now.AddDays(-4));
        var newest = Summary("Newest", now);
        var middle = Summary("Middle", now.AddDays(-1));

        var layout = HomeLibraryOrdering.Build(
            [older, newest, middle],
            [school, orphan, archive],
            new Dictionary<string, string>(),
            requestedFolderId: null,
            recentLimit: 2);

        Assert.Equal(["Archive", "Recovered", "School"],
            layout.ChildFolders.Select(folder => folder.Name));
        Assert.Equal([newest.Id, middle.Id],
            layout.RecentDocuments.Select(document => document.Id));
        Assert.Equal(older.Id, Assert.Single(layout.RemainingDocuments).Id);
    }

    [Fact]
    public void FolderLayout_OnlyIncludesDirectChildrenAndDirectNotebooks()
    {
        var parent = new NotebookFolderPreference { Name = "Parent" };
        var child = new NotebookFolderPreference { Name = "Child", ParentId = parent.Id };
        var nested = new NotebookFolderPreference { Name = "Nested", ParentId = child.Id };
        var direct = Summary("Direct", DateTimeOffset.UtcNow);
        var descendant = Summary("Descendant", DateTimeOffset.UtcNow.AddMinutes(1));
        var elsewhere = Summary("Elsewhere", DateTimeOffset.UtcNow.AddMinutes(2));
        var assignments = new Dictionary<string, string>
        {
            [direct.Id.ToString("D")] = parent.Id.ToString("D"),
            [descendant.Id.ToString("D")] = child.Id.ToString("D")
        };

        var layout = HomeLibraryOrdering.Build(
            [direct, descendant, elsewhere],
            [parent, child, nested],
            assignments,
            parent.Id);

        Assert.Equal(parent.Id, layout.CurrentFolderId);
        Assert.Equal(child.Id, Assert.Single(layout.ChildFolders).Id);
        Assert.Equal(direct.Id, Assert.Single(layout.RecentDocuments).Id);
        Assert.Empty(layout.RemainingDocuments);
    }

    private static DocumentSummary Summary(string title, DateTimeOffset updatedAt) =>
        new(Guid.NewGuid(), title, DocumentKind.PagedNotebook, 1, updatedAt);
}
