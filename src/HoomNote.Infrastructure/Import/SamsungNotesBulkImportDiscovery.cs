namespace HoomNote.Infrastructure.Import;

public sealed record SamsungNoteImportSource(string SourcePath, string RelativeFolder);

public static class SamsungNotesBulkImportDiscovery
{
    public static IReadOnlyList<SamsungNoteImportSource> DiscoverFolder(string rootPath)
    {
        var root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Samsung Notes import folder was not found: {root}");

        return EnumerateSdocxFiles(root)
            .Select(path => new SamsungNoteImportSource(
                path,
                NormalizeRelativeFolder(Path.GetRelativePath(
                    root, Path.GetDirectoryName(path) ?? root))))
            .OrderBy(item => item.RelativeFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => Path.GetFileName(item.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<SamsungNoteImportSource> DiscoverInstalledLibrary(string localAppDataPath)
    {
        var localAppData = Path.GetFullPath(localAppDataPath);
        var roots = new List<string>();
        var packages = Path.Combine(localAppData, "Packages");
        if (Directory.Exists(packages))
        {
            try
            {
                roots.AddRange(Directory.EnumerateDirectories(packages)
                    .Where(path => Path.GetFileName(path)
                        .Contains("SamsungNotes", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.Combine(path, "LocalState"))
                    .Where(Directory.Exists));
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(localAppData, "Samsung", "Samsung Notes"),
                     Path.Combine(localAppData, "SamsungNotes")
                 })
        {
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }

        return roots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(root => DiscoverFolder(root)
                .Select(item => item with
                {
                    RelativeFolder = CombineRelativeFolder(
                        Path.GetFileName(root),
                        item.RelativeFolder)
                }))
            .GroupBy(item => item.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.RelativeFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => Path.GetFileName(item.SourcePath), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> EnumerateSdocxFiles(string root)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = Path.GetFullPath(pending.Pop());
            if (!visited.Add(directory)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(directory).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }

            foreach (var file in files)
                if (Path.GetExtension(file).Equals(".sdocx", StringComparison.OrdinalIgnoreCase))
                    yield return Path.GetFullPath(file);

            IEnumerable<string> children;
            try { children = Directory.EnumerateDirectories(directory).ToArray(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var child in children)
            {
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
    }

    private static string NormalizeRelativeFolder(string path) =>
        path == "." ? string.Empty : path
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);

    private static string CombineRelativeFolder(string first, string second) =>
        string.IsNullOrWhiteSpace(second) ? first : Path.Combine(first, second);
}
