using System.Globalization;
using System.Reflection;
using System.Text;
using System.Threading.Channels;

namespace HoomNote_App.Services;

/// <summary>
/// Local-only, bounded diagnostics journal. Producers only format and enqueue; filesystem I/O is
/// serialized on one background consumer so input and rendering threads never open or flush files.
/// </summary>
public static class DiagnosticsLog
{
    private const long MaxFileBytes = 5L * 1024 * 1024;
    private const long MaxDirectoryBytes = 20L * 1024 * 1024;
    private const int MaxFiles = 8;
    private static readonly object Gate = new();
    private static readonly string SessionId = Guid.NewGuid().ToString("N")[..12];
    private static Channel<string>? _channel;
    private static Task? _writerTask;
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
                _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(4096)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });
                _initialized = true;
                _writerTask = Task.Run(() => WriterLoopAsync(_channel.Reader));
                Enqueue("info", "diagnostics.started",
                    ("version", Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown"),
                    ("os", Environment.OSVersion.VersionString),
                    ("arch", System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture),
                    ("previous_session_unclean", !string.IsNullOrWhiteSpace(previousSession)),
                    ("previous_session", previousSession ?? string.Empty));
            }
            catch
            {
                _initialized = false;
            }
        }
    }

    public static void Info(string eventName, params (string Key, object? Value)[] fields) =>
        Enqueue("info", eventName, fields);

    public static void Warning(string eventName, params (string Key, object? Value)[] fields) =>
        Enqueue("warning", eventName, fields);

    public static void Error(string eventName, Exception exception,
        params (string Key, object? Value)[] fields) => EnqueueException("error", eventName, exception, fields);

    public static void Critical(string eventName, Exception exception,
        params (string Key, object? Value)[] fields) => EnqueueException("critical", eventName, exception, fields);

    public static void Shutdown(string reason = "process_exit")
    {
        Task? writerTask;
        lock (Gate)
        {
            if (!_initialized || _shutdown) return;
            Enqueue("info", "diagnostics.stopped", ("reason", reason));
            _shutdown = true;
            _channel?.Writer.TryComplete();
            writerTask = _writerTask;
        }
        try { writerTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch { }
        try
        {
            if (_activeSessionPath is not null) File.Delete(_activeSessionPath);
        }
        catch { }
    }

    private static void Enqueue(string level, string eventName, params (string Key, object? Value)[] fields)
    {
        var channel = _channel;
        if (!_initialized || _shutdown || channel is null) return;
        try { channel.Writer.TryWrite(Format(level, eventName, fields)); }
        catch { }
    }

    private static void EnqueueException(string level, string eventName, Exception exception,
        IReadOnlyList<(string Key, object? Value)> fields)
    {
        var combined = fields.Concat(new (string Key, object? Value)[]
        {
            ("exception_type", exception.GetType().FullName),
            ("exception_message", exception.Message),
            ("stack", exception.ToString())
        }).ToArray();
        Enqueue(level, eventName, combined);
    }

    private static string Format(string level, string eventName, IReadOnlyList<(string Key, object? Value)> fields)
    {
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
        return builder.AppendLine().ToString();
    }

    private static async Task WriterLoopAsync(ChannelReader<string> reader)
    {
        StreamWriter? writer = null;
        try
        {
            while (await reader.WaitToReadAsync())
            {
                writer ??= OpenWriter();
                while (reader.TryRead(out var line))
                {
                    if (writer.BaseStream.Length + Encoding.UTF8.GetByteCount(line) >= MaxFileBytes)
                    {
                        await writer.FlushAsync();
                        writer.Dispose();
                        RotateLog();
                        writer = OpenWriter();
                    }
                    await writer.WriteAsync(line);
                }
                await writer.FlushAsync();
            }
        }
        catch
        {
            // Logging must never bring down the application.
        }
        finally
        {
            if (writer is not null)
            {
                try { await writer.FlushAsync(); }
                catch { }
                writer.Dispose();
            }
        }
    }

    private static StreamWriter OpenWriter()
    {
        var stream = new FileStream(_logPath!, FileMode.Append, FileAccess.Write, FileShare.Read,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new StreamWriter(stream, new UTF8Encoding(false), 32 * 1024, leaveOpen: false);
    }

    private static void RotateLog()
    {
        if (_logPath is null || !File.Exists(_logPath)) return;
        var rotated = Path.Combine(LogDirectory,
            $"hoomnote-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{SessionId}.log");
        File.Move(_logPath, rotated, overwrite: true);
        PruneLogs();
    }

    private static string Sanitize(object? value)
    {
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        return text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
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
