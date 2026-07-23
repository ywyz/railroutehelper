using MessagePack;
using MessagePack.Resolvers;

namespace RailRouteHelper.Monitoring.Tests;

internal static class SyntheticSaveFixture
{
    private static readonly MessagePackSerializerOptions SerializerOptions =
        ContractlessStandardResolver.Options.WithCompression(
            MessagePackCompression.Lz4BlockArray);

    public static async Task WriteManualAsync(
        string path,
        ulong gameTimeTicks,
        bool routeEstablished)
    {
        var sourceTrack = Track(
            "Node:Track:manual-source",
            "Node:Sink:manual-origin",
            "Node:Semaphore:manual-entry",
            allocationState: 2);
        var entrySignal = Node(
            "Node:Semaphore:manual-entry",
            routeEstablished ? 1 : 0,
            routeEstablished ? "Node:Track:manual-platform-2" : null);
        var approachTrack = Track(
            "Node:Track:manual-approach",
            "Node:Semaphore:manual-entry",
            "Node:Semaphore:manual-platform-2",
            routeEstablished ? 1 : 0);
        var platformSignal = Node(
            "Node:Semaphore:manual-platform-2",
            routeEstablished ? 1 : 0,
            routeEstablished ? "Node:Track:manual-platform-2" : null);
        var platformTrack = Track(
            "Node:Track:manual-platform-2",
            "Node:Semaphore:manual-platform-2",
            "Node:Semaphore:manual-platform-2-exit",
            routeEstablished ? 1 : 0);
        var station = Map(
            ("stationData", Map(
                ("uuid", "manual-station"),
                ("name", "Manual Station"),
                ("gridPoint", Array(4d, 0d)),
                ("platformsData", Array(
                    Map(
                        ("platformNum", 2UL),
                        ("trackRef", "Node:Track:manual-platform-2")))))),
            ("name", "Manual Station"));
        var train = Map(
            ("uuid", "train-manual"),
            ("reportingNumber", "T-MANUAL"),
            ("disposed", false),
            ("initialized", true),
            ("currentSpeed", 22.22222328186035),
            ("targetSpeed", 22.22222328186035),
            ("occupiedNodes", Array(
                (object?)UnionReference("Node:Track:manual-source"))),
            ("headsTowards", UnionReference("Node:Semaphore:manual-entry")),
            ("notMovingSince", 0L),
            ("currentStopIndex", 1UL),
            ("stopReasons", Array()),
            ("scheduledVisits", Array(
                ScheduledStop(
                    "Manual Origin",
                    "Node:Track:manual-source",
                    50,
                    50),
                ScheduledStop(
                    "Manual Station",
                    "Node:Track:manual-platform-2",
                    100,
                    200),
                ScheduledStop(
                    "Manual Exit",
                    "Node:Track:manual-exit",
                    300,
                    300))));
        var root = Map(
            ("gameVersion", "2.3.24"),
            ("savedStationRepository", Map(
                ("savedStations", Array(station)))),
            ("savedNodeRepository", Map(
                ("nodes", Array(
                    sourceTrack,
                    entrySignal,
                    approachTrack,
                    platformSignal,
                    platformTrack)))),
            ("savedTrainRepository", Map(
                ("savedTrains", Array(train)))),
            ("savedTimeController", Map(
                ("currentTimeOfDay", gameTimeTicks))));
        var encoded = MessagePackSerializer.Serialize(root, SerializerOptions);

        await File.WriteAllBytesAsync(path, encoded);
    }

    public static async Task WriteAutomaticAsync(
        string path,
        ulong gameTimeTicks,
        AutomaticRouteTarget routeTarget)
    {
        var platform2Selected = routeTarget is AutomaticRouteTarget.Platform2;
        var platform5Selected = routeTarget is AutomaticRouteTarget.Platform5;
        var released = routeTarget is AutomaticRouteTarget.Released;
        var platformTrack = platform5Selected
            ? "Node:Track:auto-platform-5"
            : "Node:Track:auto-platform-2";
        var occupiedTrack = released
            ? "Node:Track:auto-platform-2"
            : "Node:Track:auto-source";
        var train = Map(
            ("uuid", "train-automatic"),
            ("reportingNumber", platform5Selected ? "T-AUTO-5" : "T-AUTO-2"),
            ("disposed", false),
            ("initialized", true),
            ("currentSpeed", released ? 0d : 22.22222328186035),
            ("targetSpeed", 22.22222328186035),
            ("occupiedNodes", Array(
                (object?)UnionReference(occupiedTrack))),
            ("headsTowards", UnionReference(
                released
                    ? "Node:Semaphore:auto-platform-2-exit"
                    : "Node:Semaphore:auto-entry")),
            ("notMovingSince", released ? 250L : 0L),
            ("currentStopIndex", released ? 2UL : 1UL),
            ("stopReasons", Array()),
            ("scheduledVisits", Array(
                ScheduledStop(
                    "Automatic Origin",
                    "Node:Track:auto-source",
                    50,
                    50),
                ScheduledStop(
                    "Automatic Station",
                    platformTrack,
                    100,
                    200),
                ScheduledStop(
                    "Automatic Exit",
                    "Node:Track:auto-exit",
                    300,
                    300))));
        var station = Map(
            ("stationData", Map(
                ("uuid", "automatic-station"),
                ("name", "Automatic Station"),
                ("gridPoint", Array(4d, 3d)),
                ("platformsData", Array(
                    Map(
                        ("platformNum", 2UL),
                        ("trackRef", "Node:Track:auto-platform-2")),
                    Map(
                        ("platformNum", 5UL),
                        ("trackRef", "Node:Track:auto-platform-5")))))),
            ("name", "Automatic Station"));
        var entryTarget = routeTarget switch
        {
            AutomaticRouteTarget.Platform2 =>
                "Node:Track:auto-platform-2",
            AutomaticRouteTarget.Platform5 =>
                "Node:Track:auto-platform-5",
            _ => null,
        };
        var selectedBranch = routeTarget switch
        {
            AutomaticRouteTarget.Platform2 =>
                "Node:Track:auto-platform-2-branch",
            AutomaticRouteTarget.Platform5 =>
                "Node:Track:auto-platform-5-branch",
            _ => null,
        };
        var nodes = new object?[]
        {
            Track(
                "Node:Track:auto-source",
                "Node:DepartureSensor:auto-origin",
                "Node:Semaphore:auto-entry",
                released ? 0 : 2),
            Node(
                "Node:Semaphore:auto-entry",
                released ? 0 : 1,
                entryTarget),
            Track(
                "Node:Track:auto-shared",
                "Node:Semaphore:auto-entry",
                "Node:Switch:auto-throat",
                released ? 0 : 1),
            Node(
                "Node:Switch:auto-throat",
                released ? 0 : 1,
                selectedBranch),
            Track(
                "Node:Track:auto-platform-2-branch",
                "Node:Switch:auto-throat",
                "Node:Semaphore:auto-platform-2",
                platform2Selected ? 1 : 0),
            Node(
                "Node:Semaphore:auto-platform-2",
                platform2Selected ? 1 : 0,
                platform2Selected
                    ? "Node:Track:auto-platform-2"
                    : null),
            Track(
                "Node:Track:auto-platform-2",
                "Node:Semaphore:auto-platform-2",
                "Node:Semaphore:auto-platform-2-exit",
                released ? 2 : platform2Selected ? 1 : 0),
            Track(
                "Node:Track:auto-platform-5-branch",
                "Node:Switch:auto-throat",
                "Node:Semaphore:auto-platform-5",
                platform5Selected ? 1 : 0),
            Node(
                "Node:Semaphore:auto-platform-5",
                platform5Selected ? 1 : 0,
                platform5Selected
                    ? "Node:Track:auto-platform-5"
                    : null),
            Track(
                "Node:Track:auto-platform-5",
                "Node:Semaphore:auto-platform-5",
                "Node:Semaphore:auto-platform-5-exit",
                platform5Selected ? 1 : 0),
        };
        var root = Map(
            ("gameVersion", "2.3.24"),
            ("savedStationRepository", Map(
                ("savedStations", Array(station)))),
            ("savedNodeRepository", Map(("nodes", nodes))),
            ("savedTrainRepository", Map(
                ("savedTrains", Array(train)))),
            ("savedTimeController", Map(
                ("currentTimeOfDay", gameTimeTicks))));
        var encoded = MessagePackSerializer.Serialize(root, SerializerOptions);

        await File.WriteAllBytesAsync(path, encoded);
    }

    private static Dictionary<string, object?> Track(
        string name,
        string firstEndpoint,
        string secondEndpoint,
        int allocationState) =>
        Map(
            ("Name", name),
            ("FriendlyName", name),
            ("modelObjectData", Union(
                8UL,
                Map(
                    ("endPoints", Array(
                        DirectReference(firstEndpoint),
                        DirectReference(secondEndpoint))),
                    ("endPointGridPoints", Array(
                        Array(0d, 0d),
                        Array(1d, 0d)))))),
            ("InternalState", Union(
                6UL,
                Map(
                    ("active", true),
                    ("allocationState", allocationState)))));

    private static Dictionary<string, object?> Node(
        string name,
        int allocationState,
        string? connectedNodeId) =>
        Map(
            ("Name", name),
            ("FriendlyName", name),
            ("InternalState", Union(
                1UL,
                Map(
                    ("active", true),
                    ("allocationState", allocationState),
                    ("Connected", connectedNodeId is null
                        ? Array(null, null)
                        : Array(null, connectedNodeId))))));

    private static Dictionary<string, object?> ScheduledStop(
        string stationName,
        string trackNodeId,
        ulong from,
        ulong to) =>
        Map(
            ("from", from),
            ("to", to),
            ("stationReference", Map(("name", stationName))),
            ("track", UnionReference(trackNodeId)),
            ("departed", false),
            ("exited", false),
            ("terminus", false));

    private static Dictionary<string, object?> DirectReference(string name) =>
        Map(("NameReference", name));

    private static object?[] UnionReference(string name) =>
        Union(1UL, DirectReference(name));

    private static object?[] Union(ulong tag, object? value) =>
        Array(tag, value);

    private static object?[] Array(params object?[] items) => items;

    private static Dictionary<string, object?> Map(
        params (string Key, object? Value)[] entries) =>
        entries.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
}

internal enum AutomaticRouteTarget
{
    Platform2,
    Platform5,
    Released,
}
