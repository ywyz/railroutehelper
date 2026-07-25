using RailRouteHelper.Core;
using RailRouteHelper.Runtime;

namespace RailRouteHelper.Runtime.Tests;

public sealed class RuntimeSnapshotCollectorTests
{
    [Fact]
    public async Task Collector_publishes_periodic_snapshots_to_loopback_server()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var server = new RuntimeTelemetryServer(port: 0);
        var received = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = server.RunAsync(
            (_, _) =>
            {
                if (server.Status.AcceptedFrames >= 1)
                {
                    received.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.IsListening,
            cancellation.Token);
        var collector = new RuntimeSnapshotCollector(
            new IncrementingSource(),
            server.Port,
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10));
        var collectorTask = collector.RunAsync(cancellation.Token);

        await received.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.AcceptedFrames >= 2,
            cancellation.Token);
        cancellation.Cancel();
        await Task.WhenAll(serverTask, collectorTask);

        Assert.True(collector.Status.PublishedFrames >= 2);
        Assert.Equal(1, collector.Status.SuccessfulConnections);
        Assert.False(collector.Status.IsRunning);
        Assert.False(collector.Status.IsConnected);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException(
            "The expected collector state was not reached.");
    }

    private sealed class IncrementingSource : IRuntimeSnapshotSource
    {
        private ulong _ticks;

        public ValueTask<CapturedRuntimeSnapshot?> CaptureAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ticks++;
            return ValueTask.FromResult<CapturedRuntimeSnapshot?>(
                new CapturedRuntimeSnapshot(
                    "collector-network",
                    new OperationalSnapshot(
                        new GameVersion(3, 0, 0),
                        DateTimeOffset.UnixEpoch.AddSeconds((long)_ticks),
                        _ticks,
                        [],
                        [],
                        [],
                        [])));
        }
    }
}
