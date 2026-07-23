using HoomNote.Core.Documents;

namespace HoomNote.Core.Editing;

public interface IDocumentCommand
{
    string Description { get; }
    void Execute(HoomNoteDocument document);
    void Undo(HoomNoteDocument document);
}

public sealed class AddObjectCommand(Guid pageId, CanvasObject canvasObject) : IDocumentCommand
{
    public string Description => $"Add {canvasObject.GetType().Name}";

    public void Execute(HoomNoteDocument document)
    {
        var page = FindPage(document);
        // AddObjectCommand owns a newly generated object id. Execute is called either once or
        // after its matching Undo, so scanning a large imported page for duplicates only adds
        // latency to every pen-up without improving correctness.
        page.Objects.Add(canvasObject);
        page.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void Undo(HoomNoteDocument document)
    {
        var page = FindPage(document);
        page.Objects.RemoveAll(item => item.Id == canvasObject.Id);
        page.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private NotePage FindPage(HoomNoteDocument document) =>
        document.Pages.FirstOrDefault(page => page.Id == pageId)
        ?? throw new InvalidOperationException("The target page no longer exists.");
}

public sealed class ReplaceObjectsCommand(
    Guid pageId,
    IReadOnlyList<CanvasObject> before,
    IReadOnlyList<CanvasObject> after,
    string description) : IDocumentCommand
{
    public string Description => description;

    public void Execute(HoomNoteDocument document) => Replace(document, before, after);
    public void Undo(HoomNoteDocument document) => Replace(document, after, before);

    private void Replace(
        HoomNoteDocument document,
        IReadOnlyList<CanvasObject> remove,
        IReadOnlyList<CanvasObject> add)
    {
        var page = document.Pages.FirstOrDefault(page => page.Id == pageId)
            ?? throw new InvalidOperationException("The target page no longer exists.");
        var ids = remove.Select(item => item.Id).ToHashSet();
        page.Objects.RemoveAll(item => ids.Contains(item.Id));
        page.Objects.AddRange(add);
        page.Objects.Sort((left, right) => left.ZIndex.CompareTo(right.ZIndex));
        page.UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class DeletePageCommand(Guid pageId) : IDocumentCommand
{
    private NotePage? _removedPage;
    private int _pageIndex = -1;
    private bool _captured;
    private readonly List<(Guid SectionId, int Index)> _sectionLocations = [];

    public string Description => "Delete page";

    public void Execute(HoomNoteDocument document)
    {
        var index = document.Pages.FindIndex(page => page.Id == pageId);
        if (index < 0) return;
        if (!_captured)
        {
            _removedPage = document.Pages[index];
            _pageIndex = index;
            foreach (var section in document.Sections)
            {
                var sectionIndex = section.PageIds.IndexOf(pageId);
                if (sectionIndex >= 0) _sectionLocations.Add((section.Id, sectionIndex));
            }
            _captured = true;
        }

        document.Pages.RemoveAt(index);
        foreach (var section in document.Sections) section.PageIds.RemoveAll(id => id == pageId);
    }

    public void Undo(HoomNoteDocument document)
    {
        if (_removedPage is null || document.Pages.Any(page => page.Id == pageId)) return;
        document.Pages.Insert(Math.Clamp(_pageIndex, 0, document.Pages.Count), _removedPage);
        foreach (var (sectionId, index) in _sectionLocations)
        {
            var section = document.Sections.FirstOrDefault(item => item.Id == sectionId);
            if (section is null || section.PageIds.Contains(pageId)) continue;
            section.PageIds.Insert(Math.Clamp(index, 0, section.PageIds.Count), pageId);
        }
    }
}

// A bounded history is essential for vector documents: segment erasure can legitimately retain
// both the pre-split and post-split point arrays. Three hundred steps is still a deep interactive
// undo stack without allowing an edited imported notebook to pin hundreds of MB indefinitely.
public sealed class CommandHistory(int capacity = 120)
{
    private readonly Stack<IDocumentCommand> _undo = new();
    private readonly Stack<IDocumentCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event EventHandler? Changed;

    public void Execute(IDocumentCommand command, HoomNoteDocument document)
    {
        command.Execute(document);
        _undo.Push(command);
        _redo.Clear();
        TrimToCapacity();
        document.UpdatedAt = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo(HoomNoteDocument document)
    {
        if (!_undo.TryPop(out var command)) return;
        command.Undo(document);
        _redo.Push(command);
        document.UpdatedAt = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo(HoomNoteDocument document)
    {
        if (!_redo.TryPop(out var command)) return;
        command.Execute(document);
        _undo.Push(command);
        document.UpdatedAt = DateTimeOffset.UtcNow;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimToCapacity()
    {
        if (_undo.Count <= capacity) return;
        var retained = _undo.Take(capacity).Reverse().ToArray();
        _undo.Clear();
        foreach (var command in retained) _undo.Push(command);
    }
}
