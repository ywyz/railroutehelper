using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

namespace RailRouteHelper.Runtime;

public static class ReadOnlyMemberPath
{
    private const BindingFlags InstanceMembers =
        BindingFlags.Instance
        | BindingFlags.Public
        | BindingFlags.NonPublic;

    private static readonly ConcurrentDictionary<MemberKey, MemberInfo>
        MemberCache = new();

    public static object? Read(object target, string path)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        object? current = target;
        foreach (var segment in path.Split('.'))
        {
            if (current is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException(
                    "Member paths may not contain empty segments.",
                    nameof(path));
            }

            var member = MemberCache.GetOrAdd(
                new MemberKey(current.GetType(), segment),
                static key => FindReadableMember(key.Type, key.Name));
            current = member switch
            {
                FieldInfo field => field.GetValue(current),
                PropertyInfo property => property.GetValue(current),
                _ => throw new UnreachableException(),
            };
        }

        return current;
    }

    public static T ReadRequired<T>(object target, string path)
    {
        var value = Read(target, path)
            ?? throw new InvalidDataException(
                $"Member path '{path}' returned null.");
        if (value is T typed)
        {
            return typed;
        }

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        try
        {
            if (targetType.IsEnum)
            {
                return (T)Enum.ToObject(targetType, value);
            }

            return (T)Convert.ChangeType(
                value,
                targetType,
                CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (
            error is InvalidCastException
            or FormatException
            or OverflowException
            or ArgumentException)
        {
            throw new InvalidDataException(
                $"Member path '{path}' cannot be converted to "
                + $"'{typeof(T).FullName}'.",
                error);
        }
    }

    private static MemberInfo FindReadableMember(Type type, string name)
    {
        var field = type.GetField(name, InstanceMembers);
        if (field is not null && !field.IsStatic)
        {
            return field;
        }

        var property = type.GetProperty(name, InstanceMembers);
        if (property is not null
            && property.GetMethod is { IsStatic: false }
            && property.GetIndexParameters().Length == 0)
        {
            return property;
        }

        throw new MissingMemberException(type.FullName, name);
    }

    private readonly record struct MemberKey(Type Type, string Name);
}
