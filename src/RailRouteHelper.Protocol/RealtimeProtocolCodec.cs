using System.Text.Json;

namespace RailRouteHelper.Protocol;

public static class RealtimeProtocolCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

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
