using RailRouteHelper.Core;

namespace RailRouteHelper.Operations.Tests;

public sealed class ControlledSaveReplayTests
{
    [Fact]
    public void Nantong_route_establishment_reaches_platform_2()
    {
        var before = NantongSnapshot(routeEstablished: false);
        var current = NantongSnapshot(routeEstablished: true);

        var report = new OperationsAnalyzer().Analyze(current, before);

        var train = Assert.Single(report.Trains);
        Assert.Equal("T-MANUAL", train.ReportingNumber);
        Assert.Equal("Manual Station", train.NextDestination?.StationName);
        Assert.Equal(2, train.NextDestination?.PlatformNumber);
        Assert.Null(train.CurrentLocation);
        Assert.Equal(
            TrainRouteReachability.Reachable,
            train.Reachability);
        Assert.Equal(
            TrainOperationalStatus.ApproachingStation,
            train.Status);
        Assert.Equal(Platform2Track, train.ClearedThroughNodeId);
        Assert.Null(train.FirstUnclearedNodeId);

        var routeChange = Assert.Single(
            report.RouteChanges,
            change => change.ControlNodeId == EntrySignal);
        Assert.Equal(RouteChangeKind.Established, routeChange.Kind);
        Assert.Equal(EntrySignal, routeChange.ControlNodeId);
        Assert.Equal([Platform2Track], routeChange.CurrentTargetNodeIds);
    }

    [Fact]
    public void Taiyuan_automatic_sequence_retargets_then_releases_entry_route()
    {
        var platform5 = TaiyuanSnapshot(
            "T-AUTO-5",
            TaiyuanPlatform5Track,
            TaiyuanSourceTrack,
            routeTargetTrackNodeId: TaiyuanPlatform5Track);
        var platform2 = TaiyuanSnapshot(
            "T-AUTO-2",
            TaiyuanPlatform2Track,
            TaiyuanSourceTrack,
            routeTargetTrackNodeId: TaiyuanPlatform2Track);
        var released = TaiyuanSnapshot(
            "T-AUTO-2",
            TaiyuanPlatform2Track,
            TaiyuanPlatform2Track,
            routeTargetTrackNodeId: null);
        released = released with
        {
            Trains =
            [
                Assert.Single(released.Trains) with
                {
                    NotMovingSinceTicks = 50,
                },
            ],
        };
        var analyzer = new OperationsAnalyzer();

        var retargeted = analyzer.Analyze(platform2, platform5);
        var retarget = Assert.Single(
            retargeted.RouteChanges,
            change => change.ControlNodeId == TaiyuanEntrySignal);
        Assert.Equal(RouteChangeKind.Retargeted, retarget.Kind);
        Assert.Equal(5, retarget.PreviousDestination?.PlatformNumber);
        Assert.Equal(2, retarget.CurrentDestination?.PlatformNumber);
        var approaching = Assert.Single(retargeted.Trains);
        Assert.Equal(
            TrainOperationalStatus.ApproachingStation,
            approaching.Status);
        Assert.Equal(
            TrainRouteReachability.Reachable,
            approaching.Reachability);

        var afterRelease = analyzer.Analyze(released, platform2);
        var release = Assert.Single(
            afterRelease.RouteChanges,
            change => change.ControlNodeId == TaiyuanEntrySignal);
        Assert.Equal(RouteChangeKind.Released, release.Kind);
        Assert.Equal(2, release.PreviousDestination?.PlatformNumber);
        Assert.Null(release.CurrentDestination);
        var atPlatform = Assert.Single(afterRelease.Trains);
        Assert.Equal(
            TrainOperationalStatus.AtScheduledPlatform,
            atPlatform.Status);
        Assert.Equal(
            "Automatic Station",
            atPlatform.CurrentLocation?.StationName);
        Assert.Equal(2, atPlatform.CurrentLocation?.PlatformNumber);
        Assert.Equal(
            "Automatic Exit",
            atPlatform.NextDestination?.StationName);
        Assert.DoesNotContain(
            atPlatform.Evidence,
            evidence => evidence.Code == "stationary-route-gap");
    }

    [Fact]
    public void Stationary_train_with_a_route_gap_is_only_possibly_blocked()
    {
        var snapshot = NantongSnapshot(routeEstablished: false);
        var stoppedTrain = Assert.Single(snapshot.Trains) with
        {
            CurrentSpeed = 0,
            NotMovingSinceTicks = 100,
        };
        snapshot = snapshot with
        {
            GameTimeTicks = 200,
            Trains = [stoppedTrain],
        };

        var train = Assert.Single(
            new OperationsAnalyzer().Analyze(snapshot).Trains);

        Assert.Equal(
            TrainRouteReachability.NotReachable,
            train.Reachability);
        Assert.Equal(
            TrainOperationalStatus.PossibleBlocked,
            train.Status);
        Assert.Equal(EntrySignal, train.FirstUnclearedNodeId);
        Assert.Contains(
            train.Evidence,
            evidence =>
                evidence.Code == "stationary-route-gap"
                && evidence.Certainty == EvidenceCertainty.Inferred);
    }

    [Fact]
    public void Ambiguous_forward_branch_is_not_reported_as_reachable()
    {
        var snapshot = TaiyuanSnapshot(
            "T-AUTO-2",
            TaiyuanPlatform2Track,
            TaiyuanSourceTrack,
            routeTargetTrackNodeId: TaiyuanPlatform2Track);
        var ambiguousClearances = snapshot.RouteClearances
            .Select(
                clearance =>
                    clearance.NodeId is TaiyuanEntrySignal or TaiyuanJunction
                        ? clearance with { ConnectedNodeIds = [] }
                        : clearance)
            .Concat(
                [
                    Clearance(
                        TaiyuanPlatform5Branch,
                        NetworkNodeKind.Track,
                        RouteClearanceInterpretation.Allocated,
                        1),
                    Clearance(
                        TaiyuanPlatform5Signal,
                        NetworkNodeKind.Signal,
                        RouteClearanceInterpretation.Allocated,
                        1),
                    Clearance(
                        TaiyuanPlatform5Track,
                        NetworkNodeKind.Track,
                        RouteClearanceInterpretation.Allocated,
                        1),
                ])
            .ToArray();
        snapshot = snapshot with { RouteClearances = ambiguousClearances };

        var train = Assert.Single(
            new OperationsAnalyzer().Analyze(snapshot).Trains);

        Assert.Equal(TrainRouteReachability.Unknown, train.Reachability);
        Assert.Equal(TrainOperationalStatus.Unknown, train.Status);
        Assert.Null(train.ClearedThroughNodeId);
        Assert.Contains(
            train.Evidence,
            evidence => evidence.Code == "forward-branch-ambiguous");
    }

    [Fact]
    public void Moving_train_that_still_spans_the_previous_platform_is_departing()
    {
        var snapshot = TaiyuanSnapshot(
            "T-AUTO-5",
            TaiyuanPlatform5Track,
            TaiyuanPlatform5Track,
            routeTargetTrackNodeId: null);
        var train = Assert.Single(snapshot.Trains) with
        {
            CurrentSpeed = 22.22222328186035,
            OccupiedNodeIds =
            [
                TaiyuanPlatform5Track,
                "Node:Semaphore:auto-platform-5-exit",
                "Node:Track:auto-departure",
            ],
            HeadingTowardNodeId = "Node:Switch:auto-departure-throat",
        };
        snapshot = snapshot with { Trains = [train] };

        var assessment = Assert.Single(
            new OperationsAnalyzer().Analyze(snapshot).Trains);

        Assert.Equal(
            TrainOperationalStatus.DepartingStation,
            assessment.Status);
        Assert.Equal(
            "Automatic Station",
            assessment.CurrentLocation?.StationName);
        Assert.Equal(
            "Automatic Exit",
            assessment.NextDestination?.StationName);
    }

    private static OperationalSnapshot NantongSnapshot(bool routeEstablished)
    {
        var tracks = new[]
        {
            Track(SourceTrack, "depot exit", "Node:Sink:manual-origin", EntrySignal),
            Track(ApproachTrack, "approach", EntrySignal, Switch),
            Track(ConnectorTrack, "connector", Switch, PlatformSignal),
            new TrackSegmentSnapshot(
                Platform2Track,
                "manual platform 2",
                [PlatformSignal, "Node:Semaphore:manual-platform-2-exit"],
                [new GridPoint(3, 0), new GridPoint(4, 0)],
                "manual-station",
                2,
                routeEstablished ? 1 : 0),
        };
        var clearances = routeEstablished
            ? new[]
            {
                Clearance(
                    SourceTrack,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.TrainOccupied,
                    2),
                Clearance(
                    EntrySignal,
                    NetworkNodeKind.Signal,
                    RouteClearanceInterpretation.Allocated,
                    1,
                    Platform2Track),
                Clearance(
                    ApproachTrack,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.Allocated,
                    1),
                Clearance(
                    Switch,
                    NetworkNodeKind.Switch,
                    RouteClearanceInterpretation.Allocated,
                    1,
                    ConnectorTrack),
                Clearance(
                    ConnectorTrack,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.Allocated,
                    1),
                Clearance(
                    PlatformSignal,
                    NetworkNodeKind.Signal,
                    RouteClearanceInterpretation.Allocated,
                    1,
                    Platform2Track),
                Clearance(
                    Platform2Track,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.Allocated,
                    1),
            }
            :
            [
                Clearance(
                    SourceTrack,
                    NetworkNodeKind.Track,
                    RouteClearanceInterpretation.TrainOccupied,
                    2),
            ];
        var train = new TrainSnapshot(
            "train-manual",
            "T-MANUAL",
            22.22222328186035,
            22.22222328186035,
            [SourceTrack],
            EntrySignal,
            null,
            1,
            [],
            [
                new ScheduledStopSnapshot(
                    "Manual Origin",
                    "Node:Track:manual-origin",
                    90,
                    90,
                    false,
                    false,
                    false),
                new ScheduledStopSnapshot(
                    "Manual Station",
                    Platform2Track,
                    100,
                    200,
                    false,
                    false,
                    false),
                new ScheduledStopSnapshot(
                    "Manual Exit",
                    "Node:Track:manual-exit",
                    300,
                    300,
                    false,
                    false,
                    false),
            ]);

        return new OperationalSnapshot(
            new GameVersion(2, 3, 24),
            DateTimeOffset.UnixEpoch,
            routeEstablished ? 200UL : 100UL,
            [train],
            tracks,
            [
                new StationSnapshot(
                    "manual-station",
                    "Manual Station",
                    new GridPoint(4, 0),
                    [new PlatformSnapshot(2, Platform2Track)]),
            ],
            clearances);
    }

    private static TrackSegmentSnapshot Track(
        string id,
        string name,
        string firstEndpoint,
        string secondEndpoint) =>
        new(
            id,
            name,
            [firstEndpoint, secondEndpoint],
            [new GridPoint(0, 0), new GridPoint(1, 0)],
            null,
            null,
            1);

    private static RouteClearanceObservation Clearance(
        string id,
        NetworkNodeKind kind,
        RouteClearanceInterpretation interpretation,
        int rawCode,
        params string[] connectedNodeIds) =>
        new(
            id,
            id,
            kind,
            rawCode,
            connectedNodeIds,
            interpretation,
            RouteClearanceOrigin.Unknown);

    private static OperationalSnapshot TaiyuanSnapshot(
        string reportingNumber,
        string scheduledTrackNodeId,
        string occupiedTrackNodeId,
        string? routeTargetTrackNodeId)
    {
        var tracks = new[]
        {
            Track(
                TaiyuanSourceTrack,
                "石太客专进站",
                "Node:DepartureSensor:auto-origin",
                TaiyuanEntrySignal),
            Track(
                TaiyuanSharedTrack,
                "automatic station throat",
                TaiyuanEntrySignal,
                TaiyuanJunction),
            Track(
                TaiyuanPlatform2Branch,
                "automatic platform 2 branch",
                TaiyuanJunction,
                TaiyuanPlatform2Signal),
            new TrackSegmentSnapshot(
                TaiyuanPlatform2Track,
                "automatic platform 2",
                [TaiyuanPlatform2Signal, "Node:Semaphore:auto-platform-2-exit"],
                [new GridPoint(3, 2), new GridPoint(4, 2)],
                "automatic-station",
                2,
                routeTargetTrackNodeId == TaiyuanPlatform2Track ? 1 : 0),
            Track(
                TaiyuanPlatform5Branch,
                "automatic platform 5 branch",
                TaiyuanJunction,
                TaiyuanPlatform5Signal),
            new TrackSegmentSnapshot(
                TaiyuanPlatform5Track,
                "automatic platform 5",
                [TaiyuanPlatform5Signal, "Node:Semaphore:auto-platform-5-exit"],
                [new GridPoint(3, 5), new GridPoint(4, 5)],
                "automatic-station",
                5,
                routeTargetTrackNodeId == TaiyuanPlatform5Track ? 1 : 0),
        };
        var clearances = new List<RouteClearanceObservation>
        {
            Clearance(
                occupiedTrackNodeId,
                NetworkNodeKind.Track,
                RouteClearanceInterpretation.TrainOccupied,
                2),
        };
        if (routeTargetTrackNodeId is not null)
        {
            var branch = routeTargetTrackNodeId == TaiyuanPlatform2Track
                ? TaiyuanPlatform2Branch
                : TaiyuanPlatform5Branch;
            var platformSignal =
                routeTargetTrackNodeId == TaiyuanPlatform2Track
                    ? TaiyuanPlatform2Signal
                    : TaiyuanPlatform5Signal;
            clearances.AddRange(
                [
                    Clearance(
                        TaiyuanEntrySignal,
                        NetworkNodeKind.Signal,
                        RouteClearanceInterpretation.Allocated,
                        1,
                        routeTargetTrackNodeId),
                    Clearance(
                        TaiyuanSharedTrack,
                        NetworkNodeKind.Track,
                        RouteClearanceInterpretation.Allocated,
                        1),
                    Clearance(
                        TaiyuanJunction,
                        NetworkNodeKind.Switch,
                        RouteClearanceInterpretation.Allocated,
                        1,
                        branch),
                    Clearance(
                        branch,
                        NetworkNodeKind.Track,
                        RouteClearanceInterpretation.Allocated,
                        1),
                    Clearance(
                        platformSignal,
                        NetworkNodeKind.Signal,
                        RouteClearanceInterpretation.Allocated,
                        1,
                        routeTargetTrackNodeId),
                    Clearance(
                        routeTargetTrackNodeId,
                        NetworkNodeKind.Track,
                        RouteClearanceInterpretation.Allocated,
                        1),
                ]);
        }

        var train = new TrainSnapshot(
            $"train-{reportingNumber.ToLowerInvariant()}",
            reportingNumber,
            occupiedTrackNodeId == scheduledTrackNodeId ? 0 : 22.22222328186035,
            22.22222328186035,
            [occupiedTrackNodeId],
            occupiedTrackNodeId == TaiyuanSourceTrack
                ? TaiyuanEntrySignal
                : "Node:Semaphore:auto-platform-2-exit",
            null,
            occupiedTrackNodeId == TaiyuanSourceTrack ? 1UL : 2UL,
            [],
            [
                new ScheduledStopSnapshot(
                    "Automatic Origin",
                    "Node:Track:auto-origin",
                    50,
                    50,
                    false,
                    false,
                    false),
                new ScheduledStopSnapshot(
                    "Automatic Station",
                    scheduledTrackNodeId,
                    100,
                    200,
                    false,
                    false,
                    false),
                new ScheduledStopSnapshot(
                    "Automatic Exit",
                    TaiyuanNextTrack,
                    300,
                    300,
                    false,
                    false,
                    false),
            ]);

        return new OperationalSnapshot(
            new GameVersion(2, 3, 24),
            DateTimeOffset.UnixEpoch,
            100,
            [train],
            tracks,
            [
                new StationSnapshot(
                    "automatic-station",
                    "Automatic Station",
                    new GridPoint(4, 3),
                    [
                        new PlatformSnapshot(2, TaiyuanPlatform2Track),
                        new PlatformSnapshot(5, TaiyuanPlatform5Track),
                    ]),
            ],
            clearances);
    }

    private const string SourceTrack = "Node:Track:manual-source";
    private const string EntrySignal = "Node:Semaphore:manual-entry";
    private const string ApproachTrack = "Node:Track:manual-approach";
    private const string Switch = "Node:Switch:manual-throat";
    private const string ConnectorTrack = "Node:Track:manual-connector";
    private const string PlatformSignal = "Node:Semaphore:manual-platform-2";
    private const string Platform2Track = "Node:Track:manual-platform-2";
    private const string TaiyuanSourceTrack =
        "Node:Track:auto-source";
    private const string TaiyuanEntrySignal = "Node:Semaphore:auto-entry";
    private const string TaiyuanSharedTrack =
        "Node:Track:auto-shared";
    private const string TaiyuanJunction = "Node:Switch:auto-throat";
    private const string TaiyuanPlatform2Branch =
        "Node:Track:auto-platform-2-branch";
    private const string TaiyuanPlatform2Signal =
        "Node:Semaphore:auto-platform-2";
    private const string TaiyuanPlatform2Track =
        "Node:Track:auto-platform-2";
    private const string TaiyuanPlatform5Branch =
        "Node:Track:auto-platform-5-branch";
    private const string TaiyuanPlatform5Signal =
        "Node:Semaphore:auto-platform-5";
    private const string TaiyuanPlatform5Track =
        "Node:Track:auto-platform-5";
    private const string TaiyuanNextTrack =
        "Node:Track:auto-exit";
}
