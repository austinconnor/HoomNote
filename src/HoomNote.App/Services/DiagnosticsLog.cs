using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace HoomNote_App.Services;

/// <summary>
/// Small, local-only, bounded diagnostics journal. Events deliberately contain operational
/// metadata only: never note text, recognized text, search terms, or imported document content.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const long MaxDirectoryBytes = 20L * 1024 * 1024;
    private const int MaxFiles = 8;
    private static readonly object Gate = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..12];
    private static string? _logPath;
    private static string? _activeSessionPath;
    private static bool _initialized;
    private static bool _shutdown;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoomNote", "logs");

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized) return;
            try
            {
                Directory.CreateDirectory(LogDirectory);
                _logPath = Path.Combine(LogDirectory, $"hoomnote-{DateTime.UtcNow:yyyyMMdd}.log");
                _activeSessionPath = Path.Combine(LogDirectory, "active-session.txt");
                PruneLogs();
                var previousSession = File.Exists(_activeSessionPath)
                    ? File.ReadAllText(_activeSessionPath).Trim()
                    : null;
                File.WriteAllText(_activeSessionPath,
                    $"pid={Environment.ProcessId} session={SessionId} started_utc={DateTimeOffset.UtcNow:O}");
                _initialized = true;
                Append("info", "diagnostics.started",
                    ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"),
                    ("os", Environment.OSVersion.VersionString),
                    ("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture),
                    ("previous_session_unclean", !string.IsNullOrWhiteSpace(previousSession)),
                    ("previous_session", previousSession ?? string.Empty));
            }
            catch
            {
                // Diagnostics must never prevent the application from starting.
                _initialized = false;
            }
        }
    }

    public static void Info(string eventName, params (string Key, object? Value)[] fields) =>
        Write("info", eventName, fields);

    public static void Warning(string eventName, params (string Key, object? Value)[] fields) =>
        Write("warning", eventName, fields);

    public static void Error(string eventName, Exception exception,
        params (string Key, object? Value)[] fields) => WriteException("error", eventName, exception, fields);

    public static void Critical(string eventName, Exception exception,
        params (string Key, object? Value)[] fields) => WriteException("critical", eventName, exception, fields);

    public static void Shutdown(string reason = "process_exit")
    {
        lock (Gate)
        {
            if (!_initialized || _shutdown) return;
            Append("info", "diagnostics.stopped", ("reason", reason));
            _shutdown = true;
            try
            {
                if (_activeSessionPath is not null) File.Delete(_activeSessionPath);
            }
            catch { }
        }
    }

    private static void Write(string level, string eventName, params (string Key, object? Value)[] fields)
    {
        lock (Gate)
        {
            if (!_initialized || _shutdown) return;
            try { Append(level, eventName, fields); }
            catch { }
        }
    }

    private static void WriteException(string level, string eventName, Exception exception,
        IReadOnlyList<(string Key, object? Value)> fields)
    {
        var combined = fields.Concat(new (string Key, object? Value)[]
        {
            ("exception_type", exception.GetType().FullName),
            ("exception_message", exception.Message),
            ("stack", exception.ToString())
        }).ToArray();
        Write(level, eventName, combined);
    }

    private static void Append(string level, string eventName, params (string Key, object? Value)[] fields)
    {
        if (_logPath is null) return;
        RotateIfNeeded();
        var builder = new StringBuilder(256);
        builder.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" level=").Append(Sanitize(level))
            .Append(" event=").Append(Sanitize(eventName))
            .Append(" session=").Append(SessionId)
            .Append(" pid=").Append(Environment.ProcessId)
            .Append(" tid=").Append(Environment.CurrentManagedThreadId)
            .Append(" managed_mb=").Append(GC.GetTotalMemory(false) / (1024 * 1024));
        foreach (var (key, value) in fields)
            builder.Append(' ').Append(Sanitize(key)).Append("=\"").Append(Sanitize(value)).Append('"');
        File.AppendAllText(_logPath, builder.AppendLine().ToString(), Encoding.UTF8);
    }

    private static string Sanitize(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static void RotateIfNeeded()
    {
        if (_logPath is null || !File.Exists(_logPath) || new FileInfo(_logPath).Length < MaxFileBytes) return;
        var rotated = Path.Combine(LogDirectory,
            $"hoomnote-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SessionId}.log");
        File.Move(_logPath, rotated, overwrite: true);
        PruneLogs();
    }

    private static void PruneLogs()
    {
        var files = Directory.EnumerateFiles(LogDirectory, "hoomnote-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();
        long retainedBytes = 0;
        for (var index = 0; index < files.Length; index++)
        {
            retainedBytes += files[index].Length;
            if (index < MaxFiles && retainedBytes <= MaxDirectoryBytes) continue;
            try { files[index].Delete(); }
            catch { }
        }
    }
}
