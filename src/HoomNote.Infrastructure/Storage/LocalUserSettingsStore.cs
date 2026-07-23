using System.Text.Json;
using HoomNote.Infrastructure.Serialization;

namespace HoomNote.Infrastructure.Storage;

public sealed record UserPreferences
{
    public const int CurrentVersion = 5;

    public int Version { get; init; } = CurrentVersion;
    public List<string> SavedInkColors { get; init; } = ["#111111"];
    public string PenColor { get; init; } = "#111111";
    public string HighlighterColor { get; init; } = "#FFCE56";
    public bool HighlighterStraightLine { get; init; }
    public bool TabsCollapsed { get; init; }
    public List<ToolbarPresetPreference> ToolbarPresets { get; init; } = [];
    public List<NotebookFolderPreference> NotebookFolders { get; init; } = [];
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
    public async Task<UserPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath)) return new UserPreferences();
        try
        {
            await using var input = new FileStream(settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var loaded = await JsonSerializer.DeserializeAsync<UserPreferences>(input, HoomNoteJson.Options, cancellationToken)
                         ?? new UserPreferences();
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
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + ".tmp";
        await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                         16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(output, preferences, HoomNoteJson.Options, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}
