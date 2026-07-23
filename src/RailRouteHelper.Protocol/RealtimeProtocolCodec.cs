using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailRouteHelper.Protocol;

public static class RealtimeProtocolCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase),
        },
    };

    public static RealtimeEnvelope CreateEnvelope<TPayload>(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string messageType,
        TPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentNullException.ThrowIfNull(payload);

        return new RealtimeEnvelope(
            ProtocolVersions.Current,
            sequence,
            capturedAtUtc,
            messageType,
            JsonSerializer.SerializeToElement(payload, SerializerOptions));
    }

    public static TPayload DecodePayload<TPayload>(
        RealtimeEnvelope envelope,
        string expectedMessageType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedMessageType);
        if (!string.Equals(
                envelope.MessageType,
                expectedMessageType,
                StringComparison.Ordinal))
        {
            throw new JsonException(
                $"Expected protocol message type '{expectedMessageType}', "
                + $"but received '{envelope.MessageType}'.");
        }

        return envelope.Payload.Deserialize<TPayload>(SerializerOptions)
            ?? throw new JsonException(
                $"Protocol message '{expectedMessageType}' contains a null payload.");
    }

    public static byte[] EncodeLine(RealtimeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ValidateVersion(envelope.ProtocolVersion);

        var json = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
        var line = new byte[json.Length + 1];
        json.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    public static RealtimeEnvelope DecodeLine(ReadOnlySpan<byte> line)
    {
        if (!line.IsEmpty && line[^1] == (byte)'\n')
        {
            line = line[..^1];
        }

        var envelope = JsonSerializer.Deserialize<RealtimeEnvelope>(line, SerializerOptions)
            ?? throw new JsonException("The protocol line contains a null envelope.");
        ValidateVersion(envelope.ProtocolVersion);
        return envelope;
    }

    private static void ValidateVersion(int actualVersion)
    {
        if (actualVersion != ProtocolVersions.Current)
        {
            throw new UnsupportedProtocolVersionException(
                actualVersion,
                ProtocolVersions.Current);
        }
    }
}
