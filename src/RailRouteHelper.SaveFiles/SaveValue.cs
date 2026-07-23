using MessagePack;

namespace RailRouteHelper.SaveFiles;

[MessagePackFormatter(typeof(SaveValueFormatter))]
public abstract record SaveValue;

public sealed record SaveNil : SaveValue
{
    public static SaveNil Instance { get; } = new();

    private SaveNil()
    {
    }
}

public sealed record SaveBoolean(bool Value) : SaveValue;

public sealed record SaveSignedInteger(long Value) : SaveValue;

public sealed record SaveUnsignedInteger(ulong Value) : SaveValue;

public sealed record SaveFloat(double Value) : SaveValue;

public sealed record SaveString(string Value) : SaveValue;

public sealed record SaveBinary(ReadOnlyMemory<byte> Value) : SaveValue;

public sealed record SaveArray(IReadOnlyList<SaveValue> Items) : SaveValue;

public sealed record SaveMapEntry(SaveValue Key, SaveValue Value);

public sealed record SaveMap(IReadOnlyList<SaveMapEntry> Entries) : SaveValue
{
    public SaveValue this[string key]
    {
        get
        {
            foreach (var entry in Entries)
            {
                if (entry.Key is SaveString text
                    && string.Equals(text.Value, key, StringComparison.Ordinal))
                {
                    return entry.Value;
                }
            }

            throw new KeyNotFoundException($"The save map has no '{key}' string key.");
        }
    }
}

public sealed record SaveExtension(
    sbyte TypeCode,
    ReadOnlyMemory<byte> Data) : SaveValue;

