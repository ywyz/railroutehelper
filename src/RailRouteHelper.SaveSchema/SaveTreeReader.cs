using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveSchema;

internal static class SaveTreeReader
{
    public static SaveValue Require(SaveMap map, string key, string path)
    {
        foreach (var entry in map.Entries)
        {
            if (entry.Key is SaveString text
                && string.Equals(text.Value, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        throw new InvalidSaveSchemaException(path, "present");
    }

    public static SaveValue? Optional(SaveMap map, string key)
    {
        foreach (var entry in map.Entries)
        {
            if (entry.Key is SaveString text
                && string.Equals(text.Value, key, StringComparison.Ordinal))
            {
                return entry.Value;
            }
        }

        return null;
    }

    public static SaveMap RequireMap(SaveValue value, string path) =>
        value as SaveMap
        ?? throw new InvalidSaveSchemaException(path, "a map");

    public static SaveArray RequireArray(SaveValue value, string path) =>
        value as SaveArray
        ?? throw new InvalidSaveSchemaException(path, "an array");

    public static string RequireString(SaveValue value, string path) =>
        value is SaveString text
            ? text.Value
            : throw new InvalidSaveSchemaException(path, "a string");

    public static bool RequireBoolean(SaveValue value, string path) =>
        value is SaveBoolean boolean
            ? boolean.Value
            : throw new InvalidSaveSchemaException(path, "a boolean");

    public static double RequireNumber(SaveValue value, string path) =>
        value switch
        {
            SaveFloat number => number.Value,
            SaveSignedInteger integer => integer.Value,
            SaveUnsignedInteger integer => integer.Value,
            _ => throw new InvalidSaveSchemaException(path, "a number"),
        };

    public static ulong RequireUnsignedInteger(SaveValue value, string path) =>
        value switch
        {
            SaveUnsignedInteger integer => integer.Value,
            SaveSignedInteger { Value: >= 0 } integer => checked((ulong)integer.Value),
            _ => throw new InvalidSaveSchemaException(
                path,
                "a non-negative integer"),
        };

    public static int RequireInt32(SaveValue value, string path)
    {
        try
        {
            return value switch
            {
                SaveUnsignedInteger integer => checked((int)integer.Value),
                SaveSignedInteger integer => checked((int)integer.Value),
                _ => throw new InvalidSaveSchemaException(path, "an integer"),
            };
        }
        catch (OverflowException)
        {
            throw new InvalidSaveSchemaException(path, "a 32-bit integer");
        }
    }

    public static long RequireInt64(SaveValue value, string path)
    {
        try
        {
            return value switch
            {
                SaveUnsignedInteger integer => checked((long)integer.Value),
                SaveSignedInteger integer => integer.Value,
                _ => throw new InvalidSaveSchemaException(path, "an integer"),
            };
        }
        catch (OverflowException)
        {
            throw new InvalidSaveSchemaException(path, "a 64-bit integer");
        }
    }

    public static ulong? OptionalUnsignedInteger(SaveValue value, string path) =>
        value is SaveNil
            ? null
            : RequireUnsignedInteger(value, path);

    public static long? OptionalInt64(SaveValue value, string path) =>
        value is SaveNil
            ? null
            : RequireInt64(value, path);

    public static SaveMap RequireUnionMap(SaveValue value, string path)
    {
        var union = RequireArray(value, path);
        if (union.Items.Count < 2)
        {
            throw new InvalidSaveSchemaException(
                path,
                "a tagged two-element array");
        }

        return RequireMap(union.Items[1], $"{path}[1]");
    }

    public static string? OptionalUnionReference(
        SaveValue value,
        string path)
    {
        if (value is SaveNil)
        {
            return null;
        }

        var reference = RequireUnionMap(value, path);
        var name = Require(reference, "NameReference", $"{path}[1].NameReference");
        return name is SaveNil ? null : RequireString(name, $"{path}[1].NameReference");
    }

    public static string RequireDirectReference(SaveValue value, string path)
    {
        var reference = RequireMap(value, path);
        return RequireString(
            Require(reference, "NameReference", $"{path}.NameReference"),
            $"{path}.NameReference");
    }
}
