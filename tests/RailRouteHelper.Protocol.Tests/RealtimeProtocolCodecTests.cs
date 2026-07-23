using System.Text;
using System.Text.Json;
using RailRouteHelper.Operations;
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

    [Fact]
    public void Operations_report_round_trips_as_a_versioned_typed_payload()
    {
        var report = new OperationsReport(
            [
                new TrainOperationsAssessment(
                    "train-manual",
                    "T-MANUAL",
                    ["Node:Track:source"],
                    null,
                    new StationTrackLocation(
                        "Manual Station",
                        "Node:Track:platform-2",
                        2),
                    TrainRouteReachability.Reachable,
                    TrainOperationalStatus.ApproachingStation,
                    "Node:Track:platform-2",
                    null,
                    [
                        new OperationalEvidence(
                            "allocated-path-to-platform",
                            EvidenceCertainty.Inferred,
                            "Synthetic route reaches platform 2."),
                    ]),
            ],
            [
                new RouteChangeObservation(
                    RouteChangeKind.Established,
                    "Node:Semaphore:entry",
                    [],
                    ["Node:Track:platform-2"],
                    null,
                    new StationTrackLocation(
                        "Manual Station",
                        "Node:Track:platform-2",
                        2)),
            ]);
        var envelope = OperationsReportProtocol.CreateEnvelope(
            sequence: 7,
            capturedAtUtc: new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                0,
                TimeSpan.Zero),
            sourceSaveName: "manual-after.mp.lz4",
            schemaId: "rail-route-save/2.3-observed/v1",
            networkId: "synthetic-network",
            gameVersion: "2.3.24",
            gameTimeTicks: 200,
            report);

        var encoded = RealtimeProtocolCodec.EncodeLine(envelope);
        var decoded = OperationsReportProtocol.Decode(
            RealtimeProtocolCodec.DecodeLine(encoded));
        var json = Encoding.UTF8.GetString(encoded);

        Assert.Equal(OperationsReportProtocol.PayloadVersion, decoded.PayloadVersion);
        Assert.Equal("manual-after.mp.lz4", decoded.SourceSaveName);
        Assert.Equal("synthetic-network", decoded.NetworkId);
        Assert.Equal((ulong)200, decoded.GameTimeTicks);
        Assert.Equal(
            TrainOperationalStatus.ApproachingStation,
            Assert.Single(decoded.Report.Trains).Status);
        Assert.Equal(
            RouteChangeKind.Established,
            Assert.Single(decoded.Report.RouteChanges).Kind);
        Assert.Contains("\"messageType\":\"operations-report\"", json);
        Assert.Contains("\"status\":\"approachingStation\"", json);
    }
}
