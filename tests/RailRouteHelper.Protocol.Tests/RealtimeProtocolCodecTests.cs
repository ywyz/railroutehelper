using System.Text;
using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Protocol.Tests;

public sealed class RealtimeProtocolCodecTests
{
    [Fact]
    public void EncodeLine_then_decodeLine_preserves_the_versioned_envelope()
    {
        var payload = JsonSerializer.SerializeToElement(new { source = "synthetic" });
        var envelope = new RealtimeEnvelope(
            ProtocolVersion: ProtocolVersions.Current,
            Sequence: 42,
            CapturedAtUtc: new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            MessageType: "snapshot",
            Payload: payload);

        var encoded = RealtimeProtocolCodec.EncodeLine(envelope);
        var decoded = RealtimeProtocolCodec.DecodeLine(encoded);

        Assert.EndsWith("\n", Encoding.UTF8.GetString(encoded));
        Assert.Equal(envelope.ProtocolVersion, decoded.ProtocolVersion);
        Assert.Equal(envelope.Sequence, decoded.Sequence);
        Assert.Equal(envelope.CapturedAtUtc, decoded.CapturedAtUtc);
        Assert.Equal(envelope.MessageType, decoded.MessageType);
        Assert.Equal("synthetic", decoded.Payload.GetProperty("source").GetString());
    }

    [Fact]
    public void DecodeLine_rejects_an_unsupported_protocol_version()
    {
        const string line = """
            {"protocolVersion":2,"sequence":0,"capturedAtUtc":"2026-01-02T03:04:05+00:00","messageType":"snapshot","payload":{}}
            """;

        var error = Assert.Throws<UnsupportedProtocolVersionException>(
            () => RealtimeProtocolCodec.DecodeLine(Encoding.UTF8.GetBytes(line)));

        Assert.Equal(2, error.ActualVersion);
        Assert.Equal(ProtocolVersions.Current, error.SupportedVersion);
    }
}
