namespace HoomNote.Core.Editing;

// Keep a local copy only while the system clipboard has not been replaced.
public sealed record ClipboardSnapshot(uint SequenceNumber, string? ObjectsJson, string? Text)
{
    public bool IsCurrent(uint sequenceNumber) =>
        SequenceNumber != 0 && SequenceNumber == sequenceNumber &&
        (!string.IsNullOrWhiteSpace(ObjectsJson) || !string.IsNullOrWhiteSpace(Text));
}
