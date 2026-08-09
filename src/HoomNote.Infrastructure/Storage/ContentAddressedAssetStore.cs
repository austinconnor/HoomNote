using System.Security.Cryptography;
using HoomNote.Core.Services;

namespace HoomNote.Infrastructure.Storage;

public sealed class ContentAddressedAssetStore : IAssetStore
{
    private readonly string _rootPath;

    public ContentAddressedAssetStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_rootPath);
        foreach (var temporary in Directory.EnumerateFiles(_rootPath, ".*.tmp"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(temporary) < DateTime.UtcNow.AddDays(-1)) File.Delete(temporary);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<string> AddAsync(Stream source, string extension, CancellationToken cancellationToken = default)
    {
        extension = NormalizeExtension(extension);
        var temporaryPath = Path.Combine(_rootPath, $".{Guid.NewGuid():N}.tmp");
        string hash;
        try
        {
            await using (var destination = new FileStream(
                             temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            using (var incrementalHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    incrementalHash.AppendData(buffer, 0, read);
                }
                await destination.FlushAsync(cancellationToken);
                hash = Convert.ToHexString(incrementalHash.GetHashAndReset()).ToLowerInvariant();
            }

            var finalPath = Path.Combine(_rootPath, hash + extension);
            try { File.Move(temporaryPath, finalPath, overwrite: false); }
            catch (IOException) when (File.Exists(finalPath))
            {
                File.Delete(temporaryPath);
            }
            return hash + extension;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public Task<Stream> OpenReadAsync(string assetHash, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Stream stream = new FileStream(GetPath(assetHash), FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult(stream);
    }

    public string GetPath(string assetHash)
    {
        if (assetHash.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            assetHash.Contains(Path.DirectorySeparatorChar) || assetHash.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Invalid asset identifier.", nameof(assetHash));
        return Path.Combine(_rootPath, assetHash);
    }

    public Task<int> CollectGarbageAsync(IReadOnlySet<string> referencedAssets,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_rootPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name.StartsWith('.') || referencedAssets.Contains(name)) continue;
            try
            {
                File.Delete(path);
                deleted++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return deleted;
    }, cancellationToken);

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)) return ".bin";
        if (!extension.StartsWith('.')) extension = "." + extension;
        return extension.Length <= 12 && extension.Skip(1).All(char.IsLetterOrDigit) ? extension : ".bin";
    }
}
