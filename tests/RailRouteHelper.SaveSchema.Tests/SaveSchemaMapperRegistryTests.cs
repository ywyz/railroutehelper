using RailRouteHelper.Core;
using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveSchema.Tests;

public sealed class SaveSchemaMapperRegistryTests
{
    [Fact]
    public void Default_registry_maps_the_observed_2_3_schema()
    {
        var registry = SaveSchemaMapperRegistry.CreateDefault();
        var result = registry.Map(CreateDocument("2.3.24"));

        Assert.Equal("rail-route-save/2.3-observed/v1", result.SchemaId);
        Assert.Equal(new GameVersion(2, 3, 24), result.Snapshot.GameVersion);
        Assert.Equal((ulong)123_000, result.Snapshot.GameTimeTicks);

        var station = Assert.Single(result.Snapshot.Stations);
        Assert.Equal("station-a", station.Id);
        Assert.Equal("Central", station.Name);
        Assert.Equal(new GridPoint(10.5, 20), station.GridPosition);
        Assert.Equal(
            new PlatformSnapshot(1, "Node:Track:A-B:0"),
            Assert.Single(station.Platforms));

        var track = Assert.Single(result.Snapshot.TrackSegments);
        Assert.Equal("Node:Track:A-B:0", track.Id);
        Assert.Equal(["Node:Switch:A", "Node:Semaphore:B"], track.EndpointNodeIds);
        Assert.Equal(
            [new GridPoint(10, 20), new GridPoint(11, 20)],
            track.EndpointGridPoints);
        Assert.Equal("station-a", track.StationId);
        Assert.Equal(1, track.PlatformNumber);
        Assert.Equal(1, track.RawAllocationCode);

        var train = Assert.Single(result.Snapshot.Trains);
        Assert.Equal("train-active", train.Id);
        Assert.Equal("G100", train.ReportingNumber);
        Assert.Equal(["Node:Track:A-B:0"], train.OccupiedNodeIds);
        Assert.Equal("Node:Semaphore:B", train.HeadingTowardNodeId);
        Assert.Equal((long)100_000, train.NotMovingSinceTicks);
        Assert.Equal((ulong)1, train.CurrentStopIndex);
        Assert.Equal([3], train.RawStopReasonCodes);
        var stop = Assert.Single(train.ScheduledStops);
        Assert.Equal("Central", stop.StationName);
        Assert.Equal("Node:Track:A-B:0", stop.TrackNodeId);

        Assert.Collection(
            result.Snapshot.RouteClearances,
            clearance =>
            {
                Assert.Equal("Node:Track:A-B:0", clearance.NodeId);
                Assert.Equal(
                    RouteClearanceInterpretation.Allocated,
                    clearance.Interpretation);
                Assert.Equal(RouteClearanceOrigin.Unknown, clearance.Origin);
            },
            clearance =>
            {
                Assert.Equal("Node:Semaphore:B", clearance.NodeId);
                Assert.Equal(
                    RouteClearanceInterpretation.TrainOccupied,
                    clearance.Interpretation);
                Assert.Equal(NetworkNodeKind.Signal, clearance.NodeKind);
            });
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "route-clearance-origin-unknown");
    }

    [Fact]
    public void Default_registry_lists_only_corpus_validated_versions()
    {
        var versions = SaveSchemaMapperRegistry.CreateDefault()
            .SupportedGameVersions
            .Select(version => version.ToString());

        Assert.Equal(
            ["2.3.17", "2.3.18", "2.3.22", "2.3.23", "2.3.24"],
            versions);
    }

    [Fact]
    public void Unknown_game_version_is_rejected_instead_of_guessed()
    {
        var document = new SaveDocument(
            "synthetic.mp.lz4",
            0,
            DateTimeOffset.UnixEpoch,
            Map(("gameVersion", Text("2.3.25"))));

        var exception = Assert.Throws<UnsupportedGameVersionException>(
            () => SaveSchemaMapperRegistry.CreateDefault().Map(document));

        Assert.Equal("2.3.25", exception.GameVersion);
    }

    [Fact]
    public void Unrecognized_allocation_code_is_retained_and_reported()
    {
        var result = SaveSchemaMapperRegistry.CreateDefault().Map(
            CreateDocument("2.3.23", trackAllocationCode: 3));

        var clearance = Assert.Single(
            result.Snapshot.RouteClearances,
            clearance => clearance.NodeId == "Node:Track:A-B:0");
        Assert.Equal(3, clearance.RawAllocationCode);
        Assert.Equal(
            RouteClearanceInterpretation.UnknownAllocated,
            clearance.Interpretation);
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Code == "unknown-allocation-code"
                && diagnostic.Message.Contains('3', StringComparison.Ordinal));
    }

    [Fact]
    public void Unplaced_station_with_nil_grid_position_is_retained()
    {
        var result = SaveSchemaMapperRegistry.CreateDefault().Map(
            CreateDocument("2.3.24", stationHasPosition: false));

        Assert.Null(Assert.Single(result.Snapshot.Stations).GridPosition);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "station-position-unset");
    }

    [Fact]
    public void Signed_negative_not_moving_time_is_preserved()
    {
        var result = SaveSchemaMapperRegistry.CreateDefault().Map(
            CreateDocument("2.3.24", notMovingSinceTicks: -143_800_000));

        Assert.Equal(
            -143_800_000,
            Assert.Single(result.Snapshot.Trains).NotMovingSinceTicks);
    }

    private static SaveDocument CreateDocument(
        string version,
        int trackAllocationCode = 1,
        bool stationHasPosition = true,
        long notMovingSinceTicks = 100_000)
    {
        var station = Map(
            ("stationData", Map(
                ("uuid", Text("station-a")),
                ("name", Text("Central")),
                ("gridPoint", stationHasPosition
                    ? Array(Number(10.5), Number(20))
                    : SaveNil.Instance),
                ("platformsData", Array(
                    Map(
                        ("platformNum", Unsigned(1)),
                        ("trackRef", Text("Node:Track:A-B:0"))))))),
            ("name", Text("Central")));
        var track = Map(
            ("Name", Text("Node:Track:A-B:0")),
            ("FriendlyName", Text("Central platform 1")),
            ("modelObjectData", Union(
                8,
                Map(
                    ("endPoints", Array(
                        DirectReference("Node:Switch:A"),
                        DirectReference("Node:Semaphore:B"))),
                    ("endPointGridPoints", Array(
                        Array(Number(10), Number(20)),
                        Array(Number(11), Number(20))))))),
            ("InternalState", Union(
                6,
                Map(
                    ("active", Boolean(true)),
                    ("allocationState", Signed(trackAllocationCode))))));
        var signal = Map(
            ("Name", Text("Node:Semaphore:B")),
            ("FriendlyName", Text("Signal B")),
            ("InternalState", Union(
                1,
                Map(
                    ("active", Boolean(true)),
                    ("allocationState", Unsigned(2))))));
        var activeTrain = Map(
            ("uuid", Text("train-active")),
            ("reportingNumber", Text("G100")),
            ("disposed", Boolean(false)),
            ("initialized", Boolean(true)),
            ("currentSpeed", Number(0)),
            ("targetSpeed", Number(0)),
            ("occupiedNodes", Array(UnionReference("Node:Track:A-B:0"))),
            ("headsTowards", UnionReference("Node:Semaphore:B")),
            ("notMovingSince", Signed(notMovingSinceTicks)),
            ("currentStopIndex", Unsigned(1)),
            ("stopReasons", Array(Unsigned(3))),
            ("scheduledVisits", Array(
                Map(
                    ("from", Unsigned(200_000)),
                    ("to", Unsigned(220_000)),
                    ("stationReference", Map(("name", Text("Central")))),
                    ("track", UnionReference("Node:Track:A-B:0")),
                    ("departed", Boolean(false)),
                    ("exited", Boolean(false)),
                    ("terminus", Boolean(false))))));
        var disposedTrain = Map(
            ("disposed", Boolean(true)));
        var pendingTrain = Map(
            ("disposed", Boolean(false)),
            ("initialized", Boolean(false)));
        var root = Map(
            ("gameVersion", Text(version)),
            ("savedStationRepository", Map(
                ("savedStations", Array(station)))),
            ("savedNodeRepository", Map(
                ("nodes", Array(track, signal)))),
            ("savedTrainRepository", Map(
                ("savedTrains", Array(
                    activeTrain,
                    disposedTrain,
                    pendingTrain)))),
            ("savedTimeController", Map(
                ("currentTimeOfDay", Unsigned(123_000)))));

        return new SaveDocument(
            "synthetic.mp.lz4",
            1,
            DateTimeOffset.UnixEpoch,
            root);
    }

    private static SaveMap Map(
        params (string Key, SaveValue Value)[] entries) =>
        new(
            entries.Select(
                    entry => new SaveMapEntry(Text(entry.Key), entry.Value))
                .ToArray());

    private static SaveArray Array(params SaveValue[] items) => new(items);

    private static SaveArray Union(ulong tag, SaveMap value) =>
        Array(Unsigned(tag), value);

    private static SaveArray UnionReference(string name) =>
        Union(1, Map(("NameReference", Text(name))));

    private static SaveMap DirectReference(string name) =>
        Map(("NameReference", Text(name)));

    private static SaveString Text(string value) => new(value);

    private static SaveFloat Number(double value) => new(value);

    private static SaveBoolean Boolean(bool value) => new(value);

    private static SaveUnsignedInteger Unsigned(ulong value) => new(value);

    private static SaveSignedInteger Signed(long value) => new(value);
}
