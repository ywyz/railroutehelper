using System.Text.Json;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;
using RailRouteHelper.Replay;

namespace RailRouteHelper.Replay.Tests;

public sealed class ProtocolReplayReaderTests
{
    [Fact]
    public async Task ReadAllAsync_yields_recorded_messages_in_order()
    {
        var bytes = Enumerable.Range(10, 3)
            .Select(CreateEnvelope)
            .SelectMany(RealtimeProtocolCodec.EncodeLine)
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var reader = new ProtocolReplayReader();
        var replayed = new List<RealtimeEnvelope>();

        await foreach (var envelope in reader.ReadAllAsync(
                           stream,
                           TestContext.Current.CancellationToken))
        {
            replayed.Add(envelope);
        }

        Assert.Equal([10L, 11L, 12L], replayed.Select(item => item.Sequence));
        Assert.True(stream.CanRead);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(10, 12)]
    public async Task ReadAllAsync_rejects_a_non_contiguous_sequence(
        int firstSequence,
        int secondSequence)
    {
        var bytes = new[] { firstSequence, secondSequence }
            .Select(CreateEnvelope)
            .SelectMany(RealtimeProtocolCodec.EncodeLine)
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var reader = new ProtocolReplayReader();

        var error = await Assert.ThrowsAsync<ReplaySequenceException>(
            async () =>
            {
                await foreach (var _ in reader.ReadAllAsync(
                                   stream,
                                   TestContext.Current.CancellationToken))
                {
                }
            });

        Assert.Equal(2L, error.LineNumber);
        Assert.Equal(11L, error.ExpectedSequence);
        Assert.Equal((long)secondSequence, error.ActualSequence);
    }

    [Fact]
    public async Task ReadAllAsync_reports_the_line_number_of_malformed_json()
    {
        var validLine = RealtimeProtocolCodec.EncodeLine(CreateEnvelope(10));
        var invalidLine = System.Text.Encoding.UTF8.GetBytes("{not-json}\n");
        await using var stream = new MemoryStream([.. validLine, .. invalidLine]);
        var reader = new ProtocolReplayReader();

        var error = await Assert.ThrowsAsync<ReplayLineException>(
            async () =>
            {
                await foreach (var _ in reader.ReadAllAsync(
                                   stream,
                                   TestContext.Current.CancellationToken))
                {
                }
            });

        Assert.Equal(2L, error.LineNumber);
        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public async Task ReadOperationsReportsAsync_filters_mixed_protocol_messages()
    {
        var capturedAt = new DateTimeOffset(
            2026,
            7,
            23,
            13,
            0,
            0,
            TimeSpan.Zero);
        var diagnostic = RealtimeProtocolCodec.CreateEnvelope(
            20,
            capturedAt,
            "save-monitor-diagnostic",
            new { code = "synthetic" });
        var first = CreateOperationsEnvelope(
            21,
            capturedAt.AddSeconds(1),
            "automatic-1.mp.lz4",
            RouteChangeKind.Established);
        var second = CreateOperationsEnvelope(
            22,
            capturedAt.AddSeconds(2),
            "automatic-2.mp.lz4",
            RouteChangeKind.Retargeted);
        var bytes = new[] { diagnostic, first, second }
            .SelectMany(RealtimeProtocolCodec.EncodeLine)
            .ToArray();
        await using var stream = new MemoryStream(bytes);
        var replayed = new List<OperationsReportReplayItem>();

        await foreach (var item in new ProtocolReplayReader()
                           .ReadOperationsReportsAsync(
                               stream,
                               TestContext.Current.CancellationToken))
        {
            replayed.Add(item);
        }

        Assert.Equal([21L, 22L], replayed.Select(item => item.Sequence));
        Assert.Equal(
            ["automatic-1.mp.lz4", "automatic-2.mp.lz4"],
            replayed.Select(item => item.Payload.SourceSaveName));
        Assert.Equal(
            [RouteChangeKind.Established, RouteChangeKind.Retargeted],
            replayed.Select(
                item => Assert.Single(item.Payload.Report.RouteChanges).Kind));
    }

    private static RealtimeEnvelope CreateEnvelope(int sequence) =>
        new(
            ProtocolVersion: ProtocolVersions.Current,
            Sequence: sequence,
            CapturedAtUtc: new DateTimeOffset(
                2026,
                1,
                2,
                3,
                4,
                sequence,
                TimeSpan.Zero),
            MessageType: "snapshot",
            Payload: JsonSerializer.SerializeToElement(new { sequence }));

    private static RealtimeEnvelope CreateOperationsEnvelope(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string sourceSaveName,
        RouteChangeKind kind) =>
        OperationsReportProtocol.CreateEnvelope(
            sequence,
            capturedAtUtc,
            sourceSaveName,
            "rail-route-save/2.3-observed/v1",
            "synthetic-network",
            "2.3.24",
            (ulong)(sequence * 100),
            new OperationsReport(
                [],
                [
                    new RouteChangeObservation(
                        kind,
                        "Node:Semaphore:auto-entry",
                        [],
                        ["Node:Track:auto-platform"],
                        null,
                        null),
                ]));
}
