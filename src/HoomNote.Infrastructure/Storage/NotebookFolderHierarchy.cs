namespace HoomNote.Infrastructure.Storage;

public static class NotebookFolderHierarchy
{
    public static IReadOnlyList<Guid> RepairInvalidParents(List<NotebookFolderPreference> folders)
    {
        var repaired = new List<Guid>();
        var folderIds = folders.Select(folder => folder.Id).ToHashSet();

        for (var index = 0; index < folders.Count; index++)
        {
            var folder = folders[index];
            if (folder.ParentId is not { } parentId ||
                parentId != folder.Id && folderIds.Contains(parentId))
                continue;

            folders[index] = folder with { ParentId = null };
            repaired.Add(folder.Id);
        }

        // A parent graph has at most one outgoing edge per folder. Walking each chain
        // and detaching the first repeated node repairs cycles without touching valid links.
        var byId = folders.ToDictionary(folder => folder.Id);
        for (var folderIndex = 0; folderIndex < folders.Count; folderIndex++)
        {
            var folder = folders[folderIndex];
            var path = new HashSet<Guid>();
            var current = folder;
            while (current.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent))
            {
                if (!path.Add(current.Id))
                {
                    var index = folders.FindIndex(item => item.Id == current.Id);
                    if (index >= 0)
                    {
                        folders[index] = current with { ParentId = null };
                        byId[current.Id] = folders[index];
                        repaired.Add(current.Id);
                    }
                    break;
                }
                current = parent;
            }
        }

        return repaired.Distinct().ToArray();
    }

    public static bool WouldCreateCycle(
        IReadOnlyCollection<NotebookFolderPreference> folders,
        Guid sourceFolderId,
        Guid? targetParentId)
    {
        if (targetParentId is null) return false;
        if (targetParentId == sourceFolderId) return true;

        var byId = folders.ToDictionary(folder => folder.Id);
        var visited = new HashSet<Guid>();
        var currentId = targetParentId.Value;
        while (visited.Add(currentId) && byId.TryGetValue(currentId, out var current))
        {
            if (current.Id == sourceFolderId) return true;
            if (current.ParentId is not { } parentId) return false;
            currentId = parentId;
        }
        return false;
    }

    public static int GetDepth(
        IReadOnlyCollection<NotebookFolderPreference> folders,
        Guid folderId)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var visited = new HashSet<Guid>();
        var depth = 0;
        var currentId = folderId;
        while (visited.Add(currentId) &&
               byId.TryGetValue(currentId, out var current) &&
               current.ParentId is { } parentId)
        {
            depth++;
            currentId = parentId;
        }
        return depth;
    }
}
