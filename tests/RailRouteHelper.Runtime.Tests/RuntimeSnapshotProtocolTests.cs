using RailRouteHelper.Core;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Runtime.Tests;

public sealed class RuntimeSnapshotProtocolTests
{
    [Fact]
    public void Runtime_snapshot_round_trips_without_game_assembly_types()
    {
        var snapshot = RuntimeTestSnapshots.Empty(gameTicks: 123);
        var envelope = RuntimeSnapshotProtocol.CreateEnvelope(
            9,
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            "session-a",
            "network-a",
            snapshot);

        var decoded = RuntimeSnapshotProtocol.Decode(
            RealtimeProtocolCodec.DecodeLine(
                RealtimeProtocolCodec.EncodeLine(envelope)));

        Assert.Equal("session-a", decoded.SessionId);
        Assert.Equal("network-a", decoded.NetworkId);
        Assert.Equal(new GameVersion(3, 0, 0), decoded.Snapshot.GameVersion);
        Assert.Equal((ulong)123, decoded.Snapshot.GameTimeTicks);
        Assert.Empty(decoded.Snapshot.Trains);
    }
}
