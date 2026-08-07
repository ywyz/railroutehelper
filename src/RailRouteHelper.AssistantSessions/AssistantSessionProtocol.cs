using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.AssistantSessions;

/// <summary>Message names used by the assistant-session JSONL stream.</summary>
public static class AssistantSessionMessageTypes
{
    public const string SessionStart = "assistant-session-start";
    public const string TrainUpsert = "assistant-train-upsert";
    public const string Frame = "assistant-frame";
    public const string AlertAction = "assistant-alert-action";
    public const string SessionEnd = "assistant-session-end";
}

/// <summary>Versioned codecs for all v1 assistant-session payloads.</summary>
public static class AssistantSessionProtocol
{
    public const int PayloadVersion = 1;

    public static RealtimeEnvelope CreateSessionStartEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        SessionStart payload) =>
        Create(sequence, capturedAtUtc, AssistantSessionMessageTypes.SessionStart, payload);

    public static RealtimeEnvelope CreateTrainUpsertEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        TrainDefinition payload) =>
        Create(sequence, capturedAtUtc, AssistantSessionMessageTypes.TrainUpsert, payload);

    public static RealtimeEnvelope CreateFrameEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        AssistantFrame payload) =>
        Create(sequence, capturedAtUtc, AssistantSessionMessageTypes.Frame, payload);

    public static RealtimeEnvelope CreateAlertActionEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        AlertAction payload) =>
        Create(sequence, capturedAtUtc, AssistantSessionMessageTypes.AlertAction, payload);

    public static RealtimeEnvelope CreateSessionEndEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        SessionEnd payload) =>
        Create(sequence, capturedAtUtc, AssistantSessionMessageTypes.SessionEnd, payload);

    // Short aliases are useful at call sites and preserve a single canonical implementation.
    public static RealtimeEnvelope CreateSessionStart(long sequence, DateTimeOffset capturedAtUtc, SessionStart payload) =>
        CreateSessionStartEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateTrainUpsert(long sequence, DateTimeOffset capturedAtUtc, TrainDefinition payload) =>
        CreateTrainUpsertEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateFrame(long sequence, DateTimeOffset capturedAtUtc, AssistantFrame payload) =>
        CreateFrameEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateAlertAction(long sequence, DateTimeOffset capturedAtUtc, AlertAction payload) =>
        CreateAlertActionEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateSessionEnd(long sequence, DateTimeOffset capturedAtUtc, SessionEnd payload) =>
        CreateSessionEndEnvelope(sequence, capturedAtUtc, payload);

    public static SessionStart DecodeSessionStart(RealtimeEnvelope envelope) =>
        Decode<SessionStart>(envelope, AssistantSessionMessageTypes.SessionStart);

    public static TrainDefinition DecodeTrainUpsert(RealtimeEnvelope envelope) =>
        Decode<TrainDefinition>(envelope, AssistantSessionMessageTypes.TrainUpsert);

    public static AssistantFrame DecodeFrame(RealtimeEnvelope envelope) =>
        Decode<AssistantFrame>(envelope, AssistantSessionMessageTypes.Frame);

    public static AlertAction DecodeAlertAction(RealtimeEnvelope envelope) =>
        Decode<AlertAction>(envelope, AssistantSessionMessageTypes.AlertAction);

    public static SessionEnd DecodeSessionEnd(RealtimeEnvelope envelope) =>
        Decode<SessionEnd>(envelope, AssistantSessionMessageTypes.SessionEnd);

    public static object DecodePayload(RealtimeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.MessageType switch
        {
            AssistantSessionMessageTypes.SessionStart => DecodeSessionStart(envelope),
            AssistantSessionMessageTypes.TrainUpsert => DecodeTrainUpsert(envelope),
            AssistantSessionMessageTypes.Frame => DecodeFrame(envelope),
            AssistantSessionMessageTypes.AlertAction => DecodeAlertAction(envelope),
            AssistantSessionMessageTypes.SessionEnd => DecodeSessionEnd(envelope),
            _ => throw new JsonException($"Unsupported assistant-session message type '{envelope.MessageType}'."),
        };
    }

    private static RealtimeEnvelope Create<T>(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string messageType,
        T payload)
        where T : IAssistantSessionPayload
    {
        ArgumentNullException.ThrowIfNull(payload);
        return RealtimeProtocolCodec.CreateEnvelope(
            sequence,
            capturedAtUtc,
            messageType,
            payload);
    }

    private static T Decode<T>(RealtimeEnvelope envelope, string messageType)
        where T : IAssistantSessionPayload
    {
        var payload = RealtimeProtocolCodec.DecodePayload<T>(envelope, messageType);
        if (payload.PayloadVersion != PayloadVersion)
        {
            throw new JsonException(
                $"Assistant-session payload version {payload.PayloadVersion} is unsupported; "
                + $"this build supports {PayloadVersion}.");
        }

        return payload;
    }
}

/// <summary>Compatibility facade with a codec-shaped name.</summary>
public static class AssistantSessionCodec
{
    public static byte[] EncodeLine(RealtimeEnvelope envelope) => RealtimeProtocolCodec.EncodeLine(envelope);

    public static RealtimeEnvelope DecodeLine(ReadOnlySpan<byte> line) => RealtimeProtocolCodec.DecodeLine(line);

    public static RealtimeEnvelope CreateSessionStartEnvelope(long sequence, DateTimeOffset capturedAtUtc, SessionStart payload) =>
        AssistantSessionProtocol.CreateSessionStartEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateTrainUpsertEnvelope(long sequence, DateTimeOffset capturedAtUtc, TrainDefinition payload) =>
        AssistantSessionProtocol.CreateTrainUpsertEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateFrameEnvelope(long sequence, DateTimeOffset capturedAtUtc, AssistantFrame payload) =>
        AssistantSessionProtocol.CreateFrameEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateAlertActionEnvelope(long sequence, DateTimeOffset capturedAtUtc, AlertAction payload) =>
        AssistantSessionProtocol.CreateAlertActionEnvelope(sequence, capturedAtUtc, payload);

    public static RealtimeEnvelope CreateSessionEndEnvelope(long sequence, DateTimeOffset capturedAtUtc, SessionEnd payload) =>
        AssistantSessionProtocol.CreateSessionEndEnvelope(sequence, capturedAtUtc, payload);

    public static SessionStart DecodeSessionStart(RealtimeEnvelope envelope) => AssistantSessionProtocol.DecodeSessionStart(envelope);

    public static TrainDefinition DecodeTrainUpsert(RealtimeEnvelope envelope) => AssistantSessionProtocol.DecodeTrainUpsert(envelope);

    public static AssistantFrame DecodeFrame(RealtimeEnvelope envelope) => AssistantSessionProtocol.DecodeFrame(envelope);

    public static AlertAction DecodeAlertAction(RealtimeEnvelope envelope) => AssistantSessionProtocol.DecodeAlertAction(envelope);

    public static SessionEnd DecodeSessionEnd(RealtimeEnvelope envelope) => AssistantSessionProtocol.DecodeSessionEnd(envelope);
}
