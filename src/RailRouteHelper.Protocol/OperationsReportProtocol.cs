using System.Text.Json;
using RailRouteHelper.Operations;

namespace RailRouteHelper.Protocol;

public static class OperationsReportProtocol
{
    public const string MessageType = "operations-report";

    public const int PayloadVersion = 1;

    public static RealtimeEnvelope CreateEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string sourceSaveName,
        string schemaId,
        string networkId,
        string gameVersion,
        ulong? gameTimeTicks,
        OperationsReport report)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSaveName);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(networkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(gameVersion);
        ArgumentNullException.ThrowIfNull(report);

        return RealtimeProtocolCodec.CreateEnvelope(
            sequence,
            capturedAtUtc,
            MessageType,
            new OperationsReportPayload(
                PayloadVersion,
                sourceSaveName,
                schemaId,
                networkId,
                gameVersion,
                gameTimeTicks,
                report));
    }

    public static OperationsReportPayload Decode(RealtimeEnvelope envelope)
    {
        var payload =
            RealtimeProtocolCodec.DecodePayload<OperationsReportPayload>(
                envelope,
                MessageType);
        if (payload.PayloadVersion != PayloadVersion)
        {
            throw new JsonException(
                $"Operations report payload version {payload.PayloadVersion} "
                + $"is unsupported; this build supports {PayloadVersion}.");
        }

        return payload;
    }
}

public sealed record OperationsReportPayload(
    int PayloadVersion,
    string SourceSaveName,
    string SchemaId,
    string NetworkId,
    string GameVersion,
    ulong? GameTimeTicks,
    OperationsReport Report);
