using HoomNote.Core.Documents;
using HoomNote.Core.Services;

namespace HoomNote.Infrastructure.Storage;

public sealed record HomeLibraryLayout(
    Guid? CurrentFolderId,
    IReadOnlyList<NotebookFolderPreference> ChildFolders,
    IReadOnlyList<DocumentSummary> RecentDocuments,
    IReadOnlyList<DocumentSummary> RemainingDocuments);

public static class HomeLibraryOrdering
{
    public static HomeLibraryLayout Build(
        IReadOnlyCollection<DocumentSummary> documents,
        IReadOnlyCollection<NotebookFolderPreference> folders,
        IReadOnlyDictionary<string, string> documentFolders,
        Guid? requestedFolderId,
        int recentLimit = 6)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentLimit);
        var folderIds = folders.Select(folder => folder.Id).ToHashSet();
        Guid? currentFolderId = requestedFolderId is { } folderId && folderIds.Contains(folderId)
            ? folderId
            : null;

        var childFolders = folders
            .Where(folder => currentFolderId is { } currentId
                ? folder.ParentId == currentId
                : folder.ParentId is null || !folderIds.Contains(folder.ParentId.Value))
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(folder => folder.Id)
            .ToArray();

        var scopedDocuments = currentFolderId is null
            ? documents
            : documents.Where(document =>
                AssignedFolderId(document.Id, documentFolders) == currentFolderId).ToArray();
        var recent = scopedDocuments
            .OrderByDescending(document => document.UpdatedAt)
            .ThenBy(document => document.Title, NotebookTitleComparer.Instance)
            .Take(recentLimit)
            .ToArray();
        var recentIds = recent.Select(document => document.Id).ToHashSet();
        var remaining = scopedDocuments
            .Where(document => !recentIds.Contains(document.Id))
            .OrderBy(document => document.Title, NotebookTitleComparer.Instance)
            .ThenBy(document => document.Id)
            .ToArray();

        return new HomeLibraryLayout(currentFolderId, childFolders, recent, remaining);
    }

    private static Guid? AssignedFolderId(
        Guid documentId,
        IReadOnlyDictionary<string, string> documentFolders) =>
        documentFolders.TryGetValue(documentId.ToString("D"), out var rawFolderId) &&
        Guid.TryParse(rawFolderId, out var folderId)
            ? folderId
            : null;
}
