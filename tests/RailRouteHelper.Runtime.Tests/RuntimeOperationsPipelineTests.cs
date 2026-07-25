using RailRouteHelper.Core;
using RailRouteHelper.LiveOperations;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;
using RailRouteHelper.Runtime;

namespace RailRouteHelper.Runtime.Tests;

public sealed class RuntimeOperationsPipelineTests
{
    [Fact]
    public void Consecutive_runtime_snapshots_create_deterministic_route_events()
    {
        var projector = new LiveOperationsProjector();
        var pipeline = new RuntimeOperationsPipeline(projector);
        var before = RuntimeTestSnapshots.WithRouteTargets([]);
        var established = RuntimeTestSnapshots.WithRouteTargets(["platform-2"]);
        var retargeted = RuntimeTestSnapshots.WithRouteTargets(["platform-3"]);
        var released = RuntimeTestSnapshots.WithRouteTargets([]);

        pipeline.Apply(Message(0, before));
        pipeline.Apply(Message(1, established));
        pipeline.Apply(Message(2, retargeted));
        pipeline.Apply(Message(3, released));

        var network = Assert.Single(projector.Current.Networks);
        Assert.Equal(RuntimeSnapshotProtocol.SchemaId, network.SchemaId);
        Assert.Equal("runtime:session", network.SourceSaveName);
        Assert.Collection(
            network.RecentRouteChanges,
            change => Assert.Equal(
                RouteChangeKind.Established,
                change.Change.Kind),
            change => Assert.Equal(
                RouteChangeKind.Retargeted,
                change.Change.Kind),
            change => Assert.Equal(
                RouteChangeKind.Released,
                change.Change.Kind));
        Assert.Equal(3, projector.Current.LastSequence);
    }

    [Fact]
    public void New_session_does_not_compare_against_stale_previous_session()
    {
        var projector = new LiveOperationsProjector();
        var pipeline = new RuntimeOperationsPipeline(projector);
        pipeline.Apply(
            Message(
                0,
                RuntimeTestSnapshots.WithRouteTargets(["platform-2"]),
                "old-session"));

        var envelope = pipeline.Apply(
            Message(
                0,
                RuntimeTestSnapshots.WithRouteTargets([]),
                "new-session"));

        Assert.Empty(OperationsReportProtocol.Decode(envelope).Report.RouteChanges);
    }

    private static RuntimeSnapshotMessage Message(
        long sequence,
        OperationalSnapshot snapshot,
        string sessionId = "session")
    {
        var envelope = RuntimeSnapshotProtocol.CreateEnvelope(
            sequence,
            snapshot.ObservedAtUtc,
            sessionId,
            "network",
            snapshot);
        return new RuntimeSnapshotMessage(
            envelope,
            RuntimeSnapshotProtocol.Decode(envelope));
    }
}
