using RailRouteHelper.Core;

namespace RailRouteHelper.Runtime.Tests;

internal static class RuntimeTestSnapshots
{
    public static OperationalSnapshot Empty(ulong gameTicks) => new(
        new GameVersion(3, 0, 0),
        DateTimeOffset.UnixEpoch.AddSeconds((long)gameTicks),
        gameTicks,
        [],
        [],
        [],
        []);

    public static OperationalSnapshot WithRouteTargets(
        IReadOnlyList<string> targets)
    {
        var stations = new[]
        {
            new StationSnapshot(
                "station",
                "Central",
                null,
                [
                    new PlatformSnapshot(2, "platform-2"),
                    new PlatformSnapshot(3, "platform-3"),
                ]),
        };
        var clearance = new RouteClearanceObservation(
            "signal-entry",
            "Entry",
            NetworkNodeKind.Signal,
            1,
            targets,
            RouteClearanceInterpretation.Allocated,
            RouteClearanceOrigin.Unknown);
        return Empty(10) with
        {
            Stations = stations,
            RouteClearances = [clearance],
        };
    }
}
