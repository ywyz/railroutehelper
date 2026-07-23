using System.Buffers;
using MessagePack;

namespace RailRouteHelper.SaveFiles;

public sealed class MessagePackLz4SaveFileAdapter : ISaveFileAdapter
{
    public const long DefaultMaximumSourceBytes = 64L * 1024 * 1024;

    private static readonly MessagePackSerializerOptions SerializerOptions =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.Lz4BlockArray)
            .WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly long maximumSourceBytes;

    public MessagePackLz4SaveFileAdapter(
        long maximumSourceBytes = DefaultMaximumSourceBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSourceBytes);
        this.maximumSourceBytes = maximumSourceBytes;
    }

    public async ValueTask<SaveDocument> ReadAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The save file does not exist.", fullPath);
        }

        if (file.Length > maximumSourceBytes)
        {
            throw new InvalidDataException(
                $"The save file is {file.Length} bytes, exceeding the "
                + $"{maximumSourceBytes}-byte read limit.");
        }

        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var encoded = await ReadWithLimitAsync(
            stream,
            maximumSourceBytes,
            cancellationToken);
        SaveValue root;
        try
        {
            root = MessagePackSerializer.Deserialize<SaveValue>(
                encoded,
                SerializerOptions,
                cancellationToken);
        }
        catch (MessagePackSerializationException error)
        {
            throw new InvalidDataException(
                "The save file is not a valid MessagePack/LZ4 container.",
                error);
        }

        return new SaveDocument(
            fullPath,
            encoded.Length,
            new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
            root);
    }

    private static async ValueTask<byte[]> ReadWithLimitAsync(
        Stream source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var initialCapacity = (int)Math.Min(source.Length, maximumBytes);
        using var destination = new MemoryStream(initialCapacity);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(
                       buffer.AsMemory(),
                       cancellationToken)) > 0)
            {
                if (destination.Length + bytesRead > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"The save file grew beyond the {maximumBytes}-byte read limit.");
                }

                await destination.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
            }

            return destination.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
