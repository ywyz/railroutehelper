using RailRouteHelper.Core;

namespace RailRouteHelper.Operations.Tests;

public sealed class ComplexTopologyTests
{
    [Fact]
    public void Allocated_loop_terminates_and_is_order_invariant()
    {
        const int loopSize = 24;
        var baseline = AnalyzeLoop(loopSize, seed: 0);

        Assert.Equal("LOOP", baseline.ReportingNumber);
        Assert.Equal(
            TrainRouteReachability.Reachable,
            baseline.Reachability);
        Assert.Equal(
            TrainOperationalStatus.ApproachingStation,
            baseline.Status);
        Assert.Equal(Track(loopSize / 2), baseline.NextDestination?.TrackNodeId);

        for (var seed = 1; seed <= 64; seed++)
        {
            var candidate = AnalyzeLoop(loopSize, seed);

            Assert.Equal(baseline.ReportingNumber, candidate.ReportingNumber);
            Assert.Equal(baseline.Reachability, candidate.Reachability);
            Assert.Equal(baseline.Status, candidate.Status);
            Assert.Equal(
                baseline.NextDestination,
                candidate.NextDestination);
            Assert.Equal(
                baseline.Evidence.Select(item => item.Code),
                candidate.Evidence.Select(item => item.Code));
        }
    }

    [Theory]
    [InlineData(BranchSelection.None)]
    [InlineData(BranchSelection.Both)]
    [InlineData(BranchSelection.Unrelated)]
    public void Multiple_viable_forward_branches_remain_ambiguous(
        BranchSelection selection)
    {
        var train = Assert.Single(
            new OperationsAnalyzer().Analyze(
                CreateBranchSnapshot(selection)).Trains);

        Assert.Equal(TrainRouteReachability.Unknown, train.Reachability);
        Assert.Equal(TrainOperationalStatus.Unknown, train.Status);
        Assert.Null(train.ClearedThroughNodeId);
        Assert.Null(train.FirstUnclearedNodeId);
        Assert.Contains(
            train.Evidence,
            evidence =>
                evidence.Code == "forward-branch-ambiguous"
                && evidence.Certainty == EvidenceCertainty.Inferred);
    }

    [Fact]
    public void One_selected_forward_branch_is_deterministic()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var train = Assert.Single(
                new OperationsAnalyzer().Analyze(
                    CreateBranchSnapshot(
                        BranchSelection.First,
                        seed)).Trains);

            Assert.Equal(TrainRouteReachability.Reachable, train.Reachability);
            Assert.Equal("Node:Track:branch-a-platform", train.ClearedThroughNodeId);
            Assert.DoesNotContain(
                train.Evidence,
                evidence => evidence.Code == "forward-branch-ambiguous");
        }
    }

    [Fact]
    public void Multiple_trains_and_allocated_routes_do_not_cross_talk()
    {
        var baseline = AnalyzeParallelRoutes(seed: 0);
        Assert.Equal(["A100", "B200"], baseline.Keys);

        Assert.Equal(
            "Node:Track:a-platform",
            baseline["A100"].NextDestination?.TrackNodeId);
        Assert.Equal(
            "Node:Track:b-platform",
            baseline["B200"].NextDestination?.TrackNodeId);
        Assert.All(
            baseline.Values,
            assessment => Assert.Equal(
                TrainRouteReachability.Reachable,
                assessment.Reachability));

        for (var seed = 1; seed <= 64; seed++)
        {
            var candidate = AnalyzeParallelRoutes(seed);

            foreach (var reportingNumber in baseline.Keys)
            {
                Assert.Equal(
                    baseline[reportingNumber].NextDestination,
                    candidate[reportingNumber].NextDestination);
                Assert.Equal(
                    baseline[reportingNumber].Reachability,
                    candidate[reportingNumber].Reachability);
                Assert.Equal(
                    baseline[reportingNumber].ClearedThroughNodeId,
                    candidate[reportingNumber].ClearedThroughNodeId);
            }
        }
    }

    [Fact]
    public void Complex_turnout_route_events_are_order_invariant()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var before = CreateRouteEventSnapshot(RoutePhase.Before, seed);
            var established = CreateRouteEventSnapshot(
                RoutePhase.Established,
                seed + 101);
            var retargeted = CreateRouteEventSnapshot(
                RoutePhase.Retargeted,
                seed + 202);
            var released = CreateRouteEventSnapshot(
                RoutePhase.Released,
                seed + 303);
            var analyzer = new OperationsAnalyzer();

            AssertRouteChange(
                analyzer.Analyze(established, before),
                RouteChangeKind.Established,
                previousPlatform: null,
                currentPlatform: 2);
            AssertRouteChange(
                analyzer.Analyze(retargeted, established),
                RouteChangeKind.Retargeted,
                previousPlatform: 2,
                currentPlatform: 5);
            AssertRouteChange(
                analyzer.Analyze(released, retargeted),
                RouteChangeKind.Released,
                previousPlatform: 5,
                currentPlatform: null);
        }
    }

    private static TrainOperationsAssessment AnalyzeLoop(int size, int seed)
    {
        var random = new Random(seed);
        var tracks = Enumerable.Range(0, size)
            .Select(
                index => Segment(
                    Track(index),
                    Junction(index),
                    Junction((index + 1) % size)))
            .Select(
                track => seed % 2 == 0
                    ? track
                    : track with
                    {
                        EndpointNodeIds = track.EndpointNodeIds.Reverse().ToArray(),
                    })
            .OrderBy(_ => random.Next())
            .ToArray();
        var clearances = Enumerable.Range(0, size)
            .SelectMany(
                index => new[]
                {
                    Clearance(
                        Track(index),
                        NetworkNodeKind.Track,
                        index == 0
                            ? RouteClearanceInterpretation.TrainOccupied
                            : RouteClearanceInterpretation.Allocated),
                    Clearance(
                        Junction(index),
                        NetworkNodeKind.Switch,
                        RouteClearanceInterpretation.Allocated),
                })
            .OrderBy(_ => random.Next())
            .ToArray();
        var destinationTrack = Track(size / 2);
        var snapshot = Snapshot(
            [
                Train(
                    "loop-train",
                    "LOOP",
                    Track(0),
                    Junction(1),
                    "Loop Station",
                    destinationTrack),
            ],
            tracks,
            [
                new StationSnapshot(
                    "loop-station",
                    "Loop Station",
                    new GridPoint(0, 0),
                    [new PlatformSnapshot(1, destinationTrack)]),
            ],
            clearances);

        return Assert.Single(new OperationsAnalyzer().Analyze(snapshot).Trains);
    }

    private static OperationalSnapshot CreateBranchSnapshot(
        BranchSelection selection,
        int seed = 0)
    {
        const string source = "Node:Track:branch-source";
        const string entry = "Node:Semaphore:branch-entry";
        const string approach = "Node:Track:branch-approach";
        const string junction = "Node:Switch:branch-junction";
        const string branchA = "Node:Track:branch-a";
        const string branchB = "Node:Track:branch-b";
        const string signalA = "Node:Semaphore:branch-a";
        const string signalB = "Node:Semaphore:branch-b";
        const string platformA = "Node:Track:branch-a-platform";
        const string platformB = "Node:Track:branch-b-platform";
        IReadOnlyList<string> selected = selection switch
        {
            BranchSelection.None => [],
            BranchSelection.First => [branchA],
            BranchSelection.Both => [branchA, branchB],
            BranchSelection.Unrelated => ["Node:Track:not-adjacent"],
            _ => throw new ArgumentOutOfRangeException(nameof(selection)),
        };
        var random = new Random(seed);
        var tracks = new[]
        {
            Segment(source, "Node:Sink:branch-origin", entry),
            Segment(approach, entry, junction),
            Segment(branchA, junction, signalA),
            Segment(branchB, junction, signalB),
            Segment(platformA, signalA, "Node:Sink:branch-a-exit"),
            Segment(platformB, signalB, "Node:Sink:branch-b-exit"),
        }.OrderBy(_ => random.Next()).ToArray();
        var clearances = new[]
        {
            Clearance(
                source,
                NetworkNodeKind.Track,
                RouteClearanceInterpretation.TrainOccupied),
            Clearance(entry, NetworkNodeKind.Signal),
            Clearance(approach, NetworkNodeKind.Track),
            Clearance(junction, NetworkNodeKind.Switch, selected),
            Clearance(branchA, NetworkNodeKind.Track),
            Clearance(branchB, NetworkNodeKind.Track),
            Clearance(signalA, NetworkNodeKind.Signal, [platformA]),
            Clearance(signalB, NetworkNodeKind.Signal, [platformB]),
            Clearance(platformA, NetworkNodeKind.Track),
            Clearance(platformB, NetworkNodeKind.Track),
        }.OrderBy(_ => random.Next()).ToArray();

        return Snapshot(
            [
                Train(
                    "branch-train",
                    "BRANCH",
                    source,
                    entry,
                    "Branch Station",
                    platformA),
            ],
            tracks,
            [
                new StationSnapshot(
                    "branch-station",
                    "Branch Station",
                    new GridPoint(5, 0),
                    [
                        new PlatformSnapshot(1, platformA),
                        new PlatformSnapshot(2, platformB),
                    ]),
            ],
            clearances);
    }

    private static IReadOnlyDictionary<string, TrainOperationsAssessment>
        AnalyzeParallelRoutes(int seed)
    {
        var random = new Random(seed);
        var first = CreateLinearComponent("a", "A100", 1);
        var second = CreateLinearComponent("b", "B200", 2);
        var decoyTracks = new[]
        {
            Segment(
                "Node:Track:decoy",
                "Node:Sink:decoy-first",
                "Node:Sink:decoy-second"),
        };
        var decoyClearances = new[]
        {
            Clearance("Node:Track:decoy", NetworkNodeKind.Track),
        };
        var snapshot = Snapshot(
            first.Trains.Concat(second.Trains)
                .OrderBy(_ => random.Next())
                .ToArray(),
            first.Tracks.Concat(second.Tracks)
                .Concat(decoyTracks)
                .OrderBy(_ => random.Next())
                .ToArray(),
            first.Stations.Concat(second.Stations)
                .OrderBy(_ => random.Next())
                .ToArray(),
            first.Clearances.Concat(second.Clearances)
                .Concat(decoyClearances)
                .OrderBy(_ => random.Next())
                .ToArray());

        return new OperationsAnalyzer().Analyze(snapshot).Trains
            .ToDictionary(
                train => train.ReportingNumber,
                StringComparer.Ordinal);
    }

    private static TopologyComponent CreateLinearComponent(
        string prefix,
        string reportingNumber,
        int platformNumber)
    {
        var source = $"Node:Track:{prefix}-source";
        var entry = $"Node:Semaphore:{prefix}-entry";
        var approach = $"Node:Track:{prefix}-approach";
        var platformSignal = $"Node:Semaphore:{prefix}-platform";
        var platform = $"Node:Track:{prefix}-platform";
        var stationName = $"{prefix.ToUpperInvariant()} Station";

        return new TopologyComponent(
            [
                Train(
                    $"{prefix}-train",
                    reportingNumber,
                    source,
                    entry,
                    stationName,
                    platform),
            ],
            [
                Segment(source, $"Node:Sink:{prefix}-origin", entry),
                Segment(approach, entry, platformSignal),
                Segment(platform, platformSignal, $"Node:Sink:{prefix}-exit"),
            ],
            [
                new StationSnapshot(
                    $"{prefix}-station",
                    stationName,
                    new GridPoint(platformNumber, 0),
                    [new PlatformSnapshot(platformNumber, platform)]),
            ],
            [
                Clearance(
                    source,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.TrainOccupied),
                Clearance(entry, NetworkNodeKind.Signal, [platform]),
                Clearance(approach, NetworkNodeKind.Track),
                Clearance(platformSignal, NetworkNodeKind.Signal, [platform]),
                Clearance(platform, NetworkNodeKind.Track),
            ]);
    }

    private static OperationalSnapshot CreateRouteEventSnapshot(
        RoutePhase phase,
        int seed)
    {
        const string control = "Node:Semaphore:event-entry";
        const string junction = "Node:Switch:event-throat";
        const string platform2 = "Node:Track:event-platform-2";
        const string platform5 = "Node:Track:event-platform-5";
        var random = new Random(seed);
        var tracks = new[]
        {
            Segment(
                "Node:Track:event-source",
                "Node:Sink:event-origin",
                control),
            Segment("Node:Track:event-shared", control, junction),
            Segment(
                "Node:Track:event-branch-2",
                junction,
                "Node:Semaphore:event-platform-2"),
            Segment(
                platform2,
                "Node:Semaphore:event-platform-2",
                "Node:Sink:event-exit-2"),
            Segment(
                "Node:Track:event-branch-5",
                junction,
                "Node:Semaphore:event-platform-5"),
            Segment(
                platform5,
                "Node:Semaphore:event-platform-5",
                "Node:Sink:event-exit-5"),
        }.OrderBy(_ => random.Next()).ToArray();
        var target = phase switch
        {
            RoutePhase.Established => platform2,
            RoutePhase.Retargeted => platform5,
            _ => null,
        };
        var clearances = target is null
            ? Array.Empty<RouteClearanceObservation>()
            :
            [
                Clearance(
                    control,
                    NetworkNodeKind.Signal,
                    [target, target]),
            ];

        return Snapshot(
            [],
            tracks,
            [
                new StationSnapshot(
                    "event-station",
                    "Event Station",
                    new GridPoint(5, 2),
                    [
                        new PlatformSnapshot(2, platform2),
                        new PlatformSnapshot(5, platform5),
                    ]),
            ],
            clearances.OrderBy(_ => random.Next()).ToArray());
    }

    private static void AssertRouteChange(
        OperationsReport report,
        RouteChangeKind kind,
        int? previousPlatform,
        int? currentPlatform)
    {
        var change = Assert.Single(report.RouteChanges);
        Assert.Equal(kind, change.Kind);
        Assert.Equal("Node:Semaphore:event-entry", change.ControlNodeId);
        Assert.Equal(
            previousPlatform,
            change.PreviousDestination?.PlatformNumber);
        Assert.Equal(
            currentPlatform,
            change.CurrentDestination?.PlatformNumber);
        Assert.Equal(
            change.PreviousTargetNodeIds
                .Order(StringComparer.Ordinal),
            change.PreviousTargetNodeIds);
        Assert.Equal(
            change.CurrentTargetNodeIds
                .Order(StringComparer.Ordinal),
            change.CurrentTargetNodeIds);
    }

    private static OperationalSnapshot Snapshot(
        IReadOnlyList<TrainSnapshot> trains,
        IReadOnlyList<TrackSegmentSnapshot> tracks,
        IReadOnlyList<StationSnapshot> stations,
        IReadOnlyList<RouteClearanceObservation> clearances) =>
        new(
            new GameVersion(2, 3, 24),
            DateTimeOffset.UnixEpoch,
            1_000,
            trains,
            tracks,
            stations,
            clearances);

    private static TrainSnapshot Train(
        string id,
        string reportingNumber,
        string occupiedTrack,
        string heading,
        string stationName,
        string destinationTrack) =>
        new(
            id,
            reportingNumber,
            20,
            20,
            [occupiedTrack],
            heading,
            null,
            0,
            [],
            [
                new ScheduledStopSnapshot(
                    stationName,
                    destinationTrack,
                    0,
                    1_000,
                    false,
                    false,
                    false),
            ]);

    private static TrackSegmentSnapshot Segment(
        string id,
        string firstEndpoint,
        string secondEndpoint) =>
        new(
            id,
            id,
            [firstEndpoint, secondEndpoint],
            [new GridPoint(0, 0), new GridPoint(1, 0)],
            null,
            null,
            1);

    private static RouteClearanceObservation Clearance(
        string id,
        NetworkNodeKind kind,
        IReadOnlyList<string>? connectedNodeIds = null) =>
        Clearance(
            id,
            kind,
            RouteClearanceInterpretation.Allocated,
            connectedNodeIds);

    private static RouteClearanceObservation Clearance(
        string id,
        NetworkNodeKind kind,
        RouteClearanceInterpretation interpretation,
        IReadOnlyList<string>? connectedNodeIds = null) =>
        new(
            id,
            id,
            kind,
            interpretation == RouteClearanceInterpretation.TrainOccupied
                ? 2
                : 1,
            connectedNodeIds ?? [],
            interpretation,
            RouteClearanceOrigin.Unknown);

    private static string Track(int index) => $"Node:Track:loop-{index:D2}";

    private static string Junction(int index) =>
        $"Node:Switch:loop-{index:D2}";

    public enum BranchSelection
    {
        None,
        First,
        Both,
        Unrelated,
    }

    private enum RoutePhase
    {
        Before,
        Established,
        Retargeted,
        Released,
    }

    private sealed record TopologyComponent(
        IReadOnlyList<TrainSnapshot> Trains,
        IReadOnlyList<TrackSegmentSnapshot> Tracks,
        IReadOnlyList<StationSnapshot> Stations,
        IReadOnlyList<RouteClearanceObservation> Clearances);
}
