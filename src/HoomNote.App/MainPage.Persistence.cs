using HoomNote.Core.Documents;
using HoomNote_App.Services;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace HoomNote_App;

public sealed partial class MainPage
{
    private async void OnSaveTimerTick(DispatcherQueueTimer sender, object args)
    {
        if (_isPointerDown)
        {
            sender.Start();
            return;
        }
        try
        {
            await SaveNowAsync();
        }
        catch (Exception exception)
        {
            ShowError("Autosave failed.", exception);
        }
    }

    private async Task SaveNowAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null || !_hasUnsavedChanges) return;
        await _saveGate.WaitAsync(cancellationToken);
        try
        {
            var document = _document;
            if (document is null || !_hasUnsavedChanges) return;
            // The UI owns and mutates the live document graph. Never let the repository worker
            // enumerate those lists after the dispatcher resumes.
            var persistenceSnapshot = CreatePersistenceSnapshot(document);
            var dirtyPageIds = _pendingSavePageIds.ToArray();
            var editVersion = _editVersion;
            var fullSaveVersion = _fullSaveVersion;
            var appendSnapshot = _pendingInkAppends.ToArray();
            var appendIds = appendSnapshot.Select(item => item.Stroke.Id).ToHashSet();
            StatusText.Text = "Saving…";
            var performedFullSave = _requiresFullSave;
            if (!performedFullSave && appendSnapshot.Length > 0)
                performedFullSave = !await RunRepositoryAsync(repository =>
                    repository.SaveInkAppendsAsync(persistenceSnapshot, appendSnapshot, cancellationToken),
                    cancellationToken);
            if (performedFullSave)
                await RunRepositoryAsync(repository =>
                    repository.SavePagesAsync(persistenceSnapshot, dirtyPageIds, cancellationToken), cancellationToken);

            _pendingInkAppends.RemoveAll(item => appendIds.Contains(item.Stroke.Id));
            if (performedFullSave && fullSaveVersion == _fullSaveVersion) _requiresFullSave = false;
            if (editVersion == _editVersion)
            {
                _hasUnsavedChanges = false;
                _pendingSavePageIds.Clear();
            }
            StatusText.Text = $"Saved {DateTime.Now:t}";
            ScheduleBackupIfDue();
        }
        finally { _saveGate.Release(); }
    }

    private static HoomNoteDocument CreatePersistenceSnapshot(HoomNoteDocument document) => document with
    {
        SchemaVersion = HoomNoteDocument.CurrentSchemaVersion,
        Tags = [.. document.Tags],
        Sections = document.Sections.Select(section => section with { PageIds = [.. section.PageIds] }).ToList(),
        Pages = document.Pages.Select(page => page with
        {
            Objects = [.. page.Objects],
            RecognizedRegions = [.. page.RecognizedRegions]
        }).ToList(),
        Settings = document.Settings with { }
    };

    private void ScheduleBackupIfDue()
    {
        if (_repository is null || _backupDirectory is null ||
            DateTime.UtcNow - _lastBackupUtc < TimeSpan.FromHours(6)) return;
        _lastBackupUtc = DateTime.UtcNow;
        var backupDirectory = _backupDirectory;
        _ = RunRepositoryAsync(repository => repository.CreateBackupAsync(backupDirectory)).ContinueWith(task =>
        {
            if (task.IsFaulted && task.Exception is { } exception)
                DiagnosticsLog.Error("backup.create_failed", exception.GetBaseException());
        }, TaskScheduler.Default);
    }
}
