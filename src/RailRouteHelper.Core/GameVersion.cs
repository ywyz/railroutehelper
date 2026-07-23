using System.Globalization;

namespace RailRouteHelper.Core;

public readonly record struct GameVersion(
    int Major,
    int Minor,
    int Patch) : IComparable<GameVersion>
{
    public static GameVersion Parse(string value)
    {
        if (!TryParse(value, out var version))
        {
            throw new FormatException(
                $"'{value}' is not a three-component numeric game version.");
        }

        return version;
    }

    public static bool TryParse(string? value, out GameVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var components = value.Split('.');
        if (components.Length != 3
            || !TryParseComponent(components[0], out var major)
            || !TryParseComponent(components[1], out var minor)
            || !TryParseComponent(components[2], out var patch))
        {
            return false;
        }

        version = new GameVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(GameVersion other)
    {
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0)
        {
            return majorComparison;
        }

        var minorComparison = Minor.CompareTo(other.Minor);
        return minorComparison != 0
            ? minorComparison
            : Patch.CompareTo(other.Patch);
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Major}.{Minor}.{Patch}");

    private static bool TryParseComponent(string value, out int component) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out component)
        && component >= 0;
}
