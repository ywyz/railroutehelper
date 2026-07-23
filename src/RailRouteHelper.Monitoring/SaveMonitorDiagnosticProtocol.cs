using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Monitoring;

public static class SaveMonitorDiagnosticProtocol
{
    public const string MessageType = "save-monitor-diagnostic";

    public const int PayloadVersion = 1;

    internal static RealtimeEnvelope CreateEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string sourceSaveName,
        string code,
        string description) =>
        RealtimeProtocolCodec.CreateEnvelope(
            sequence,
            capturedAtUtc,
            MessageType,
            new SaveMonitorDiagnosticPayload(
                PayloadVersion,
                sourceSaveName,
                code,
                description));

    public static SaveMonitorDiagnosticPayload Decode(
        RealtimeEnvelope envelope)
    {
        var payload =
            RealtimeProtocolCodec.DecodePayload<SaveMonitorDiagnosticPayload>(
                envelope,
                MessageType);
        if (payload.PayloadVersion != PayloadVersion)
        {
            throw new JsonException(
                $"Save monitor diagnostic payload version "
                + $"{payload.PayloadVersion} is unsupported; this build "
                + $"supports {PayloadVersion}.");
        }

        return payload;
    }
}

public sealed record SaveMonitorDiagnosticPayload(
    int PayloadVersion,
    string SourceSaveName,
    string Code,
    string Description);
