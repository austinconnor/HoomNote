using System.Collections.Concurrent;
using System.Text.Json;
using HoomNote.Infrastructure.Serialization;

namespace HoomNote.Infrastructure.Storage;

public sealed record UserPreferences
{
    public const int CurrentVersion = 11;

    public int Version { get; init; } = CurrentVersion;
    public List<string> SavedInkColors { get; init; } = ["#111111"];
    public string PenColor { get; init; } = "#111111";
    public string HighlighterColor { get; init; } = "#FFFF00";
    public bool HighlighterStraightLine { get; init; }
    public double TemporaryGridSize { get; init; } = 32;
    public double StyleBrushSize { get; init; } = 36;
    public double EraserSize { get; init; } = 12;
    public bool ScaleStrokeWidthsOnTransform { get; init; }
    public double MinimumZoomPercent { get; init; } = 8;
    public double MaximumZoomPercent { get; init; } = 800;
    public bool TabsCollapsed { get; init; }
    public List<ToolbarPresetPreference> ToolbarPresets { get; init; } = [];
    public List<NotebookFolderPreference> NotebookFolders { get; init; } = [];
    public List<string> ExpandedFolderIds { get; init; } = [];
    public Dictionary<string, string> DocumentFolders { get; init; } = [];
    public Dictionary<string, string> DocumentColors { get; init; } = [];
    public List<string> NotebookOrder { get; init; } = [];
    public string DefaultPageTemplate { get; init; } = "Lined";
    public string DefaultPageColor { get; init; } = "#FFFDF8";
}

public sealed record ToolbarPresetPreference
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Tool { get; init; } = "Pen";
    public string Color { get; init; } = "#111111";
    public double Width { get; init; } = 2.4;
    public double PressureSensitivity { get; init; } = 85;
    public double Opacity { get; init; } = 1;
    public double Smoothing { get; init; } = 0.78;
    public bool StraightLine { get; init; }
}

public sealed record NotebookFolderPreference
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid? ParentId { get; init; }
    public string Name { get; init; } = "Folder";
    public string Color { get; init; } = "#667085";
}

public sealed class LocalUserSettingsStore(string settingsPath)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileGates =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath)) return new UserPreferences();
        try
        {
            await using var input = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var loaded = await JsonSerializer.DeserializeAsync<UserPreferences>(input, HoomNoteJson.Options, cancellationToken)
                         ?? new UserPreferences();
            if (loaded.Version < 8 &&
                string.Equals(loaded.HighlighterColor, "#FFCE56", StringComparison.OrdinalIgnoreCase))
                loaded = loaded with { HighlighterColor = "#FFFF00" };
            return loaded with { Version = UserPreferences.CurrentVersion };
        }
        catch (JsonException)
        {
            return new UserPreferences();
        }
    }

    public async Task SaveAsync(UserPreferences preferences, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(settingsPath);
        // Capture mutable lists and dictionaries before the first await. MainPage keeps these
        // collections live for fast UI updates, but an async serializer must never observe a
        // half-applied folder move or a later window's mutation.
        var snapshot = Snapshot(preferences);
        var fileGate = FileGates.GetOrAdd(fullPath, _ => new SemaphoreSlim(1, 1));
        await fileGate.WaitAsync(cancellationToken);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(output, snapshot, HoomNoteJson.Options, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            fileGate.Release();
        }
    }

    public static UserPreferences Snapshot(UserPreferences preferences) => preferences with
    {
        SavedInkColors = [.. preferences.SavedInkColors],
        ToolbarPresets = [.. preferences.ToolbarPresets],
        NotebookFolders = [.. preferences.NotebookFolders],
        ExpandedFolderIds = [.. preferences.ExpandedFolderIds],
        DocumentFolders = new Dictionary<string, string>(
            preferences.DocumentFolders, StringComparer.OrdinalIgnoreCase),
        DocumentColors = new Dictionary<string, string>(
            preferences.DocumentColors, StringComparer.OrdinalIgnoreCase),
        NotebookOrder = [.. preferences.NotebookOrder]
    };
}
