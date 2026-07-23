using HoomNote.Core.Documents;
using HoomNote.Core.Services;

namespace HoomNote.Infrastructure.Storage;

public sealed class BackupService(IPackageService packageService, string backupDirectory, int retainedBackups = 7)
{
    public async Task CreateAsync(HoomNoteDocument document, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(backupDirectory);
        var safeTitle = string.Concat(document.Title.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(backupDirectory,
            $"{safeTitle}-{document.Id:N}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.hoomnote");
        await packageService.ExportAsync(document, destination, cancellationToken);

        var oldBackups = new DirectoryInfo(backupDirectory)
            .EnumerateFiles($"*-{document.Id:N}-*.hoomnote")
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(retainedBackups);
        foreach (var backup in oldBackups) backup.Delete();
    }
}

