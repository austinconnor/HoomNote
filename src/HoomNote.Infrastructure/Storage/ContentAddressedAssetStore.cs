using System.Security.Cryptography;
using HoomNote.Core.Services;

namespace HoomNote.Infrastructure.Storage;

public sealed class ContentAddressedAssetStore(string rootPath) : IAssetStore
{
    public async Task<string> AddAsync(Stream source, string extension, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(rootPath);
        extension = NormalizeExtension(extension);
        var temporaryPath = Path.Combine(rootPath, $".{Guid.NewGuid():N}.tmp");
        string hash;

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

        var finalPath = Path.Combine(rootPath, hash + extension);
        if (!File.Exists(finalPath)) File.Move(temporaryPath, finalPath);
        else File.Delete(temporaryPath);
        return hash + extension;
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
        return Path.Combine(rootPath, assetHash);
    }

    private static string NormalizeExtension(string extension)
    {
        extension = extension.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(extension)) return ".bin";
        if (!extension.StartsWith('.')) extension = "." + extension;
        return extension.Length <= 12 && extension.Skip(1).All(char.IsLetterOrDigit) ? extension : ".bin";
    }
}

