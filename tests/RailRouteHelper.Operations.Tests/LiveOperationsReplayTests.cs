using RailRouteHelper.LiveOperations;
using RailRouteHelper.Protocol;
using RailRouteHelper.Replay;

namespace RailRouteHelper.Operations.Tests;

public sealed partial class ControlledSaveReplayTests
{
    [Fact]
    public async Task Nantong_protocol_replay_projects_latest_live_operations()
    {
        var before = NantongSnapshot(routeEstablished: false);
        var current = NantongSnapshot(routeEstablished: true);
        var analyzer = new OperationsAnalyzer();
        var envelopes = new[]
        {
            OperationsReportProtocol.CreateEnvelope(
                0,
                DateTimeOffset.UnixEpoch,
                "nantong-1.mp.lz4",
                "synthetic/v1",
                "nantong-synthetic",
                "2.3.24",
                100,
                analyzer.Analyze(before)),
            OperationsReportProtocol.CreateEnvelope(
                1,
                DateTimeOffset.UnixEpoch.AddMinutes(1),
                "nantong-2.mp.lz4",
                "synthetic/v1",
                "nantong-synthetic",
                "2.3.24",
                200,
                analyzer.Analyze(current, before)),
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var recording = await CreateRecordingAsync(
            envelopes,
            cancellationToken);
        var projector = new LiveOperationsProjector();

        await foreach (var envelope in
                       new ProtocolReplayReader().ReadAllAsync(
                           recording,
                           cancellationToken))
        {
            projector.Apply(envelope);
        }

        var state = projector.Current;
        Assert.Equal(1, state.LastSequence);
        var network = Assert.Single(state.Networks);
        Assert.Equal("nantong-synthetic", network.NetworkId);
        Assert.Equal("nantong-2.mp.lz4", network.SourceSaveName);
        var train = Assert.Single(network.Trains);
        Assert.Equal("T-MANUAL", train.ReportingNumber);
        Assert.Equal(
            TrainOperationalStatus.ApproachingStation,
            train.Status);
        var routeChange = Assert.Single(
            network.RouteChanges,
            change => change.ControlNodeId == EntrySignal);
        Assert.Equal(RouteChangeKind.Established, routeChange.Kind);
        Assert.Empty(state.Alerts);
    }

    [Fact]
    public async Task Taiyuan_protocol_replay_retains_retarget_and_release_timeline()
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
        var analyzer = new OperationsAnalyzer();
        var start = DateTimeOffset.UnixEpoch.AddHours(1);
        var envelopes = new[]
        {
            OperationsReportProtocol.CreateEnvelope(
                10,
                start,
                "taiyuan-1.mp.lz4",
                "synthetic/v1",
                "taiyuan-synthetic",
                "2.3.24",
                100,
                analyzer.Analyze(platform5)),
            OperationsReportProtocol.CreateEnvelope(
                11,
                start.AddMinutes(1),
                "taiyuan-2.mp.lz4",
                "synthetic/v1",
                "taiyuan-synthetic",
                "2.3.24",
                200,
                analyzer.Analyze(platform2, platform5)),
            OperationsReportProtocol.CreateEnvelope(
                12,
                start.AddMinutes(2),
                "taiyuan-3.mp.lz4",
                "synthetic/v1",
                "taiyuan-synthetic",
                "2.3.24",
                300,
                analyzer.Analyze(released, platform2)),
        };
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var recording = await CreateRecordingAsync(
            envelopes,
            cancellationToken);
        var projector = new LiveOperationsProjector();

        await foreach (var envelope in
                       new ProtocolReplayReader().ReadAllAsync(
                           recording,
                           cancellationToken))
        {
            projector.Apply(envelope);
        }

        var network = Assert.Single(projector.Current.Networks);
        var train = Assert.Single(network.Trains);
        Assert.Equal("T-AUTO-2", train.ReportingNumber);
        Assert.Equal(
            TrainOperationalStatus.AtScheduledPlatform,
            train.Status);
        var entrySignalTimeline = network.RecentRouteChanges
            .Where(
                item =>
                    item.Change.ControlNodeId
                    == TaiyuanEntrySignal)
            .ToArray();
        Assert.Equal(2, entrySignalTimeline.Length);
        Assert.Equal(11, entrySignalTimeline[0].Sequence);
        Assert.Equal(
            RouteChangeKind.Retargeted,
            entrySignalTimeline[0].Change.Kind);
        Assert.Equal(12, entrySignalTimeline[1].Sequence);
        Assert.Equal(
            RouteChangeKind.Released,
            entrySignalTimeline[1].Change.Kind);
    }

    [Fact]
    public void Possible_blocked_alert_is_updated_then_resolved()
    {
        var projector = new LiveOperationsProjector();
        var openedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);
        var lastObservedAt = openedAt.AddMinutes(1);
        var resolvedAt = openedAt.AddMinutes(2);

        projector.Apply(
            CreateOperationsEnvelope(
                5,
                openedAt,
                TrainOperationalStatus.PossibleBlocked));
        projector.Apply(
            CreateOperationsEnvelope(
                6,
                lastObservedAt,
                TrainOperationalStatus.PossibleBlocked));
        projector.Apply(
            CreateOperationsEnvelope(
                7,
                resolvedAt,
                TrainOperationalStatus.ApproachingStation));

        var alert = Assert.Single(projector.Current.Alerts);
        Assert.Equal(
            LiveOperationsAlertKind.PossibleBlockedTrain,
            alert.Kind);
        Assert.Equal(
            LiveOperationsAlertSeverity.Warning,
            alert.Severity);
        Assert.Equal(LiveOperationsAlertStatus.Resolved, alert.Status);
        Assert.Equal("alert-network", alert.NetworkId);
        Assert.Equal("alert-train", alert.TrainId);
        Assert.Equal("T-ALERT", alert.ReportingNumber);
        Assert.Equal(5, alert.OpenedSequence);
        Assert.Equal(6, alert.LastObservedSequence);
        Assert.Equal(7, alert.ResolvedSequence);
        Assert.Equal(openedAt, alert.OpenedAtUtc);
        Assert.Equal(lastObservedAt, alert.LastObservedAtUtc);
        Assert.Equal(resolvedAt, alert.ResolvedAtUtc);
        Assert.Equal(2, alert.ObservationCount);
    }

    [Fact]
    public void Published_projection_collections_are_read_only()
    {
        var before = NantongSnapshot(routeEstablished: false);
        var current = NantongSnapshot(routeEstablished: true);
        var envelope = OperationsReportProtocol.CreateEnvelope(
            20,
            DateTimeOffset.UnixEpoch,
            "immutable.mp.lz4",
            "synthetic/v1",
            "immutable-network",
            "2.3.24",
            20,
            new OperationsAnalyzer().Analyze(current, before));
        var projector = new LiveOperationsProjector();

        projector.Apply(envelope);

        var network = Assert.Single(projector.Current.Networks);
        var train = Assert.Single(network.Trains);
        var occupiedNodes = Assert.IsAssignableFrom<IList<string>>(
            train.OccupiedNodeIds);
        Assert.Throws<NotSupportedException>(
            () => occupiedNodes[0] = "tampered");
        var routeChange = Assert.Single(
            network.RouteChanges,
            change => change.ControlNodeId == EntrySignal);
        var targetNodes = Assert.IsAssignableFrom<IList<string>>(
            routeChange.CurrentTargetNodeIds);
        Assert.Throws<NotSupportedException>(
            () => targetNodes[0] = "tampered");
    }

    private static RealtimeEnvelope CreateOperationsEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        TrainOperationalStatus status)
    {
        var train = new TrainOperationsAssessment(
            "alert-train",
            "T-ALERT",
            ["Node:Track:alert"],
            null,
            new StationTrackLocation(
                "Alert Destination",
                "Node:Track:destination",
                1),
            status == TrainOperationalStatus.PossibleBlocked
                ? TrainRouteReachability.NotReachable
                : TrainRouteReachability.Reachable,
            status,
            null,
            status == TrainOperationalStatus.PossibleBlocked
                ? "Node:Track:gap"
                : null,
            []);
        return OperationsReportProtocol.CreateEnvelope(
            sequence,
            capturedAtUtc,
            $"alert-{sequence}.mp.lz4",
            "synthetic/v1",
            "alert-network",
            "2.3.24",
            checked((ulong)sequence),
            new OperationsReport([train], []));
    }

    private static async Task<MemoryStream> CreateRecordingAsync(
        IEnumerable<RealtimeEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        var recording = new MemoryStream();
        foreach (var envelope in envelopes)
        {
            await recording.WriteAsync(
                RealtimeProtocolCodec.EncodeLine(envelope),
                cancellationToken);
        }

        recording.Position = 0;
        return recording;
    }
}
