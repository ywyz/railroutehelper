using RailRouteHelper.Monitoring;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;
using RailRouteHelper.Replay;

namespace RailRouteHelper.Monitoring.Tests;

public sealed class SaveDirectoryMonitorTests
{
    [Fact]
    public async Task Nantong_existing_saves_are_ordered_and_replayed()
    {
        var directory = TestDirectory.Create();
        try
        {
            var afterPath = Path.Combine(directory, "manual-after.mp.lz4");
            var beforePath = Path.Combine(directory, "manual-before.mp.lz4");
            await SyntheticSaveFixture.WriteManualAsync(
                afterPath,
                gameTimeTicks: 200,
                routeEstablished: true);
            await SyntheticSaveFixture.WriteManualAsync(
                beforePath,
                gameTimeTicks: 100,
                routeEstablished: false);
            File.SetLastWriteTimeUtc(
                afterPath,
                new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(
                beforePath,
                new DateTime(2026, 7, 23, 12, 1, 0, DateTimeKind.Utc));

            var envelopes = new List<RealtimeEnvelope>();
            await foreach (var envelope in new SaveDirectoryMonitor().WatchAsync(
                               directory,
                               new SaveDirectoryWatchOptions
                               {
                                   Follow = false,
                                   FileStabilityInterval = TimeSpan.Zero,
                               },
                               TestContext.Current.CancellationToken))
            {
                envelopes.Add(envelope);
            }

            Assert.Equal([0L, 1L], envelopes.Select(item => item.Sequence));
            var reports = envelopes
                .Select(OperationsReportProtocol.Decode)
                .ToArray();
            Assert.Equal(
                ["manual-before.mp.lz4", "manual-after.mp.lz4"],
                reports.Select(item => item.SourceSaveName));
            Assert.Empty(reports[0].Report.RouteChanges);
            var established = Assert.Single(
                reports[1].Report.RouteChanges,
                change => change.ControlNodeId == "Node:Semaphore:manual-entry");
            Assert.Equal(RouteChangeKind.Established, established.Kind);
            var train = Assert.Single(reports[1].Report.Trains);
            Assert.Equal(TrainRouteReachability.Reachable, train.Reachability);
            Assert.Equal(TrainOperationalStatus.ApproachingStation, train.Status);

            await using var recording = new MemoryStream();
            foreach (var envelope in envelopes)
            {
                await recording.WriteAsync(
                    RealtimeProtocolCodec.EncodeLine(envelope),
                    TestContext.Current.CancellationToken);
            }

            recording.Position = 0;
            var replayed = new List<OperationsReportReplayItem>();
            await foreach (var item in new ProtocolReplayReader()
                               .ReadOperationsReportsAsync(
                                   recording,
                                   TestContext.Current.CancellationToken))
            {
                replayed.Add(item);
            }

            Assert.Equal(
                reports.Select(item => item.SourceSaveName),
                replayed.Select(item => item.Payload.SourceSaveName));
            Assert.Equal(
                RouteChangeKind.Established,
                Assert.Single(
                    replayed[1].Payload.Report.RouteChanges,
                    change =>
                        change.ControlNodeId
                        == "Node:Semaphore:manual-entry").Kind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task New_save_is_emitted_while_monitoring_continues()
    {
        var directory = TestDirectory.Create();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var firstEnvelope = ReadFirstAsync(
                new SaveDirectoryMonitor().WatchAsync(
                    directory,
                    new SaveDirectoryWatchOptions
                    {
                        IncludeExisting = false,
                        StartingSequence = 40,
                        ScanInterval = TimeSpan.FromMilliseconds(50),
                        FileStabilityInterval = TimeSpan.Zero,
                    },
                    timeout.Token),
                timeout.Token);
            await Task.Delay(
                TimeSpan.FromMilliseconds(100),
                timeout.Token);

            await SyntheticSaveFixture.WriteManualAsync(
                Path.Combine(directory, "manual-live.mp.lz4"),
                gameTimeTicks: 300,
                routeEstablished: true);
            var envelope = await firstEnvelope;
            var payload = OperationsReportProtocol.Decode(envelope);

            Assert.Equal(40L, envelope.Sequence);
            Assert.Equal("manual-live.mp.lz4", payload.SourceSaveName);
            Assert.Equal(
                TrainRouteReachability.Reachable,
                Assert.Single(payload.Report.Trains).Reachability);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Taiyuan_sequence_survives_an_unreadable_save_and_replays()
    {
        var directory = TestDirectory.Create();
        try
        {
            await File.WriteAllBytesAsync(
                Path.Combine(directory, "unreadable.mp.lz4"),
                [0xc1],
                TestContext.Current.CancellationToken);
            await SyntheticSaveFixture.WriteAutomaticAsync(
                Path.Combine(directory, "automatic-2.mp.lz4"),
                gameTimeTicks: 200,
                AutomaticRouteTarget.Platform2);
            await SyntheticSaveFixture.WriteAutomaticAsync(
                Path.Combine(directory, "automatic-3.mp.lz4"),
                gameTimeTicks: 300,
                AutomaticRouteTarget.Released);
            await SyntheticSaveFixture.WriteAutomaticAsync(
                Path.Combine(directory, "automatic-1.mp.lz4"),
                gameTimeTicks: 100,
                AutomaticRouteTarget.Platform5);

            var envelopes = new List<RealtimeEnvelope>();
            await foreach (var envelope in new SaveDirectoryMonitor().WatchAsync(
                               directory,
                               new SaveDirectoryWatchOptions
                               {
                                   Follow = false,
                                   FileStabilityInterval = TimeSpan.Zero,
                                   StartingSequence = 10,
                               },
                               TestContext.Current.CancellationToken))
            {
                envelopes.Add(envelope);
            }

            var diagnosticEnvelope = Assert.Single(
                envelopes,
                item =>
                    item.MessageType
                    == SaveMonitorDiagnosticProtocol.MessageType);
            var diagnostic = SaveMonitorDiagnosticProtocol.Decode(
                diagnosticEnvelope);
            Assert.Equal("unreadable.mp.lz4", diagnostic.SourceSaveName);
            Assert.Equal("save-container-invalid", diagnostic.Code);

            var reports = envelopes
                .Where(
                    item =>
                        item.MessageType
                        == OperationsReportProtocol.MessageType)
                .Select(OperationsReportProtocol.Decode)
                .ToArray();
            Assert.Equal(
                [
                    "automatic-1.mp.lz4",
                    "automatic-2.mp.lz4",
                    "automatic-3.mp.lz4",
                ],
                reports.Select(item => item.SourceSaveName));
            Assert.Equal(
                RouteChangeKind.Retargeted,
                Assert.Single(
                    reports[1].Report.RouteChanges,
                    change =>
                        change.ControlNodeId
                        == "Node:Semaphore:auto-entry").Kind);
            Assert.Equal(
                RouteChangeKind.Released,
                Assert.Single(
                    reports[2].Report.RouteChanges,
                    change =>
                        change.ControlNodeId
                        == "Node:Semaphore:auto-entry").Kind);

            await using var recording = new MemoryStream();
            foreach (var envelope in envelopes)
            {
                await recording.WriteAsync(
                    RealtimeProtocolCodec.EncodeLine(envelope),
                    TestContext.Current.CancellationToken);
            }

            recording.Position = 0;
            var replayed = new List<OperationsReportReplayItem>();
            await foreach (var item in new ProtocolReplayReader()
                               .ReadOperationsReportsAsync(
                                   recording,
                                   TestContext.Current.CancellationToken))
            {
                replayed.Add(item);
            }

            Assert.Equal(3, replayed.Count);
            Assert.Equal(
                RouteChangeKind.Released,
                Assert.Single(
                    replayed[2].Payload.Report.RouteChanges,
                    change =>
                        change.ControlNodeId
                        == "Node:Semaphore:auto-entry").Kind);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<RealtimeEnvelope> ReadFirstAsync(
        IAsyncEnumerable<RealtimeEnvelope> source,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in source.WithCancellation(cancellationToken))
        {
            return envelope;
        }

        throw new InvalidOperationException(
            "The monitor completed without emitting a message.");
    }
}
