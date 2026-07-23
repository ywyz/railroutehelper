using System.Buffers;
using MessagePack;
using MessagePack.Formatters;

namespace RailRouteHelper.SaveFiles;

public sealed class SaveValueFormatter : IMessagePackFormatter<SaveValue?>
{
    private const int MaximumCollectionItems = 1_000_000;

    public void Serialize(
        ref MessagePackWriter writer,
        SaveValue? value,
        MessagePackSerializerOptions options) =>
        throw new NotSupportedException("SaveValue is a read-only representation.");

    public SaveValue? Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return SaveNil.Instance;
        }

        return reader.NextMessagePackType switch
        {
            MessagePackType.Boolean => new SaveBoolean(reader.ReadBoolean()),
            MessagePackType.Integer => ReadInteger(ref reader),
            MessagePackType.Float => new SaveFloat(reader.ReadDouble()),
            MessagePackType.String => new SaveString(reader.ReadString()!),
            MessagePackType.Binary => new SaveBinary(
                reader.ReadBytes()?.ToArray() ?? Array.Empty<byte>()),
            MessagePackType.Array => ReadArray(ref reader, options),
            MessagePackType.Map => ReadMap(ref reader, options),
            MessagePackType.Extension => ReadExtension(ref reader),
            _ => throw new MessagePackSerializationException(
                $"Unsupported MessagePack type {reader.NextMessagePackType}."),
        };
    }

    private static SaveValue ReadInteger(ref MessagePackReader reader)
    {
        var code = reader.NextCode;
        var isUnsigned = code <= 0x7f
            || code is MessagePackCode.UInt8
                or MessagePackCode.UInt16
                or MessagePackCode.UInt32
                or MessagePackCode.UInt64;

        return isUnsigned
            ? new SaveUnsignedInteger(reader.ReadUInt64())
            : new SaveSignedInteger(reader.ReadInt64());
    }

    private static SaveArray ReadArray(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        options.Security.DepthStep(ref reader);
        try
        {
            var count = ReadCollectionCount(reader.ReadArrayHeader());
            var items = new SaveValue[count];
            for (var index = 0; index < count; index++)
            {
                items[index] = ReadValue(ref reader, options);
            }

            return new SaveArray(items);
        }
        finally
        {
            reader.Depth--;
        }
    }

    private static SaveMap ReadMap(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        options.Security.DepthStep(ref reader);
        try
        {
            var count = ReadCollectionCount(reader.ReadMapHeader());
            var entries = new SaveMapEntry[count];
            for (var index = 0; index < count; index++)
            {
                var key = ReadValue(ref reader, options);
                var value = ReadValue(ref reader, options);
                entries[index] = new SaveMapEntry(key, value);
            }

            return new SaveMap(entries);
        }
        finally
        {
            reader.Depth--;
        }
    }

    private static SaveExtension ReadExtension(ref MessagePackReader reader)
    {
        var extension = reader.ReadExtensionFormat();
        return new SaveExtension(extension.TypeCode, extension.Data.ToArray());
    }

    private static int ReadCollectionCount(int count)
    {
        if (count > MaximumCollectionItems)
        {
            throw new MessagePackSerializationException(
                $"A save collection declares {count} items; "
                + $"the safety limit is {MaximumCollectionItems}.");
        }

        return count;
    }

    private static SaveValue ReadValue(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options) =>
        options.Resolver
            .GetFormatterWithVerify<SaveValue?>()
            .Deserialize(ref reader, options)
        ?? throw new MessagePackSerializationException(
            "The save value formatter returned null.");
}
