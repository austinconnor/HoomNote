namespace HoomNote.Core.Documents;

public static class LibraryNamePolicy
{
    public const int MaxLength = 128;

    public static string? Normalize(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length <= MaxLength) return normalized;
        return normalized[..MaxLength].TrimEnd();
    }
}
