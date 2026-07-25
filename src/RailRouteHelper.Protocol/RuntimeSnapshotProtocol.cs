using System.Text.Json;
using RailRouteHelper.Core;

namespace RailRouteHelper.Protocol;

public static class RuntimeSnapshotProtocol
{
    public const string MessageType = "runtime-snapshot";

    public const int PayloadVersion = 1;

    public const string SchemaId = "rail-route-runtime/v1";

    public static RealtimeEnvelope CreateEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string sessionId,
        string networkId,
        OperationalSnapshot snapshot)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkId);
        ArgumentNullException.ThrowIfNull(snapshot);

        return RealtimeProtocolCodec.CreateEnvelope(
            sequence,
            capturedAtUtc,
            MessageType,
            new RuntimeSnapshotPayload(
                PayloadVersion,
                sessionId,
                networkId,
                snapshot));
    }

    public static RuntimeSnapshotPayload Decode(RealtimeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.Sequence < 0)
        {
            throw new JsonException(
                "Runtime snapshot sequence must be non-negative.");
        }

        var payload =
            RealtimeProtocolCodec.DecodePayload<RuntimeSnapshotPayload>(
                envelope,
                MessageType);
        if (payload.PayloadVersion != PayloadVersion)
        {
            throw new JsonException(
                $"Runtime snapshot payload version {payload.PayloadVersion} "
                + $"is unsupported; this build supports {PayloadVersion}.");
        }

        if (string.IsNullOrWhiteSpace(payload.SessionId)
            || string.IsNullOrWhiteSpace(payload.NetworkId)
            || payload.Snapshot is null)
        {
            throw new JsonException(
                "Runtime snapshot requires sessionId, networkId, and snapshot.");
        }

        ValidateSnapshot(payload.Snapshot);
        return payload;
    }

    private static void ValidateSnapshot(OperationalSnapshot snapshot)
    {
        if (snapshot.Trains is null
            || snapshot.TrackSegments is null
            || snapshot.Stations is null
            || snapshot.RouteClearances is null)
        {
            throw new JsonException(
                "Runtime snapshot collections may not be null.");
        }

        if (snapshot.Trains.Any(
                train =>
                    train is null
                    || string.IsNullOrWhiteSpace(train.Id)
                    || string.IsNullOrWhiteSpace(train.ReportingNumber)
                    || train.OccupiedNodeIds is null
                    || train.RawStopReasonCodes is null
                    || train.ScheduledStops is null
                    || train.ScheduledStops.Any(stop => stop is null))
            || snapshot.TrackSegments.Any(
                track =>
                    track is null
                    || string.IsNullOrWhiteSpace(track.Id)
                    || track.EndpointNodeIds is null
                    || track.EndpointGridPoints is null)
            || snapshot.Stations.Any(
                station =>
                    station is null
                    || string.IsNullOrWhiteSpace(station.Id)
                    || string.IsNullOrWhiteSpace(station.Name)
                    || station.Platforms is null
                    || station.Platforms.Any(platform => platform is null))
            || snapshot.RouteClearances.Any(
                clearance =>
                    clearance is null
                    || string.IsNullOrWhiteSpace(clearance.NodeId)
                    || clearance.ConnectedNodeIds is null))
        {
            throw new JsonException(
                "Runtime snapshot contains an incomplete entity.");
        }
    }
}

public sealed record RuntimeSnapshotPayload(
    int PayloadVersion,
    string SessionId,
    string NetworkId,
    OperationalSnapshot Snapshot);
