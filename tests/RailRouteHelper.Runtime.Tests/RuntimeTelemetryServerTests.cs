using System.Net;
using System.Net.Sockets;
using RailRouteHelper.Protocol;
using RailRouteHelper.Runtime;

namespace RailRouteHelper.Runtime.Tests;

public sealed class RuntimeTelemetryServerTests
{
    [Fact]
    public async Task Loopback_server_accepts_fragmented_frame_and_reconnect()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var server = new RuntimeTelemetryServer(port: 0);
        var received = new List<RuntimeSnapshotMessage>();
        var twoFrames = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = server.RunAsync(
            (message, _) =>
            {
                lock (received)
                {
                    received.Add(message);
                    if (received.Count == 2)
                    {
                        twoFrames.TrySetResult();
                    }
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.IsListening,
            cancellation.Token);

        var first = RuntimeSnapshotProtocol.CreateEnvelope(
            0,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            "session-a",
            "network-a",
            RuntimeTestSnapshots.Empty(1));
        var firstBytes = RealtimeProtocolCodec.EncodeLine(first);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(
                IPAddress.Loopback,
                server.Port,
                cancellation.Token);
            await using var stream = client.GetStream();
            var midpoint = firstBytes.Length / 2;
            await stream.WriteAsync(
                firstBytes.AsMemory(0, midpoint),
                cancellation.Token);
            await stream.WriteAsync(
                firstBytes.AsMemory(midpoint),
                cancellation.Token);
        }

        await using (var client = new RuntimeSnapshotClient(server.Port))
        {
            await client.ConnectAsync(cancellation.Token);
            await client.PublishAsync(
                1,
                DateTimeOffset.UnixEpoch.AddSeconds(2),
                "session-a",
                "network-a",
                RuntimeTestSnapshots.Empty(2),
                cancellation.Token);
        }

        await twoFrames.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.AcceptedFrames == 2,
            cancellation.Token);
        cancellation.Cancel();
        await serverTask;

        Assert.Collection(
            received,
            item => Assert.Equal(0, item.Envelope.Sequence),
            item => Assert.Equal(1, item.Envelope.Sequence));
        Assert.Equal(2, server.Status.AcceptedConnections);
        Assert.Equal(0, server.Status.RejectedConnections);
        Assert.False(server.Status.IsListening);
    }

    [Fact]
    public async Task Bad_session_sequence_isolated_to_connection()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await using var server = new RuntimeTelemetryServer(port: 0);
        var accepted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = server.RunAsync(
            (message, _) =>
            {
                if (message.Payload.SessionId == "session-b")
                {
                    accepted.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.IsListening,
            cancellation.Token);

        await PublishOneAsync(server.Port, "session-a", 3, cancellation.Token);
        await PublishOneAsync(server.Port, "session-a", 3, cancellation.Token);
        await PublishOneAsync(server.Port, "session-b", 0, cancellation.Token);

        await accepted.Task.WaitAsync(cancellation.Token);
        await WaitUntilAsync(
            () => server.Status.RejectedConnections == 1,
            cancellation.Token);
        cancellation.Cancel();
        await serverTask;

        Assert.Equal(2, server.Status.AcceptedFrames);
        Assert.Equal(1, server.Status.RejectedConnections);
        Assert.Contains(
            "does not follow",
            server.Status.LastError,
            StringComparison.Ordinal);
    }

    private static async Task PublishOneAsync(
        int port,
        string sessionId,
        long sequence,
        CancellationToken cancellationToken)
    {
        await using var client = new RuntimeSnapshotClient(port);
        await client.ConnectAsync(cancellationToken);
        await client.PublishAsync(
            sequence,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence),
            sessionId,
            "network",
            RuntimeTestSnapshots.Empty((ulong)sequence),
            cancellationToken);
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

        throw new TimeoutException("The expected runtime state was not reached.");
    }
}
