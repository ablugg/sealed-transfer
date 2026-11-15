using System.Buffers;
using SealedTransfer.Core.Transfers;

namespace SealedTransfer.Infrastructure.Storage;

/// <summary>
/// Stores encrypted chunks on local disk. Used in development; Azure Blob Storage replaces it in a
/// later milestone behind the same <see cref="IBlobStore"/> interface.
/// </summary>
public sealed class FileSystemBlobStore : IBlobStore
{
    private const int BufferBytes = 81920;

    private readonly string _root;

    public FileSystemBlobStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _root = Path.GetFullPath(rootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<long> WriteAsync(
        Guid transferId,
        int index,
        Stream content,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(transferId);
        Directory.CreateDirectory(directory);

        var finalPath = PathFor(transferId, index);
        var tempPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
        long total = 0;

        try
        {
            await using (var file = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                BufferBytes,
                FileOptions.Asynchronous))
            {
                int read;
                while ((read = await content.ReadAsync(buffer.AsMemory(0, BufferBytes), cancellationToken)) > 0)
                {
                    total += read;
                    if (total > maxBytes)
                    {
                        throw new BlobTooLargeException(maxBytes);
                    }

                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
            }

            // Write to a temporary file first so a half-uploaded chunk is never visible under its real name.
            File.Move(tempPath, finalPath, overwrite: true);
            return total;
        }
        catch
        {
            File.Delete(tempPath);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Task<Stream?> OpenReadAsync(Guid transferId, int index, CancellationToken cancellationToken)
    {
        var path = PathFor(transferId, index);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Task.FromResult<Stream?>(stream);
    }

    public Task DeleteAllAsync(Guid transferId, CancellationToken cancellationToken)
    {
        var directory = DirectoryFor(transferId);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    // Paths are built only from a Guid and an int, so a caller can never steer a write outside the root.
    private string DirectoryFor(Guid transferId) => Path.Combine(_root, transferId.ToString("N"));

    private string PathFor(Guid transferId, int index) =>
        Path.Combine(DirectoryFor(transferId), index.ToString("D6") + ".bin");
}
