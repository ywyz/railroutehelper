using System.Net;
using System.Net.Sockets;
using RailRouteHelper.Core;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Runtime;

public sealed class RuntimeSnapshotClient : IAsyncDisposable
{
    private readonly TcpClient _client = new();
    private readonly int _port;
    private NetworkStream? _stream;

    public RuntimeSnapshotClient(int port = RuntimeTelemetryServer.DefaultPort)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "TCP port must be between 1 and 65535.");
        }

        _port = port;
    }

    public async Task ConnectAsync(
        CancellationToken cancellationToken = default)
    {
        if (_stream is not null)
        {
            throw new InvalidOperationException(
                "The runtime snapshot client is already connected.");
        }

        await _client.ConnectAsync(
            IPAddress.Loopback,
            _port,
            cancellationToken);
        _stream = _client.GetStream();
    }

    public async ValueTask PublishAsync(
        long sequence,
        DateTimeOffset capturedAtUtc,
        string sessionId,
        string networkId,
        OperationalSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (_stream is null)
        {
            throw new InvalidOperationException(
                "ConnectAsync must complete before publishing snapshots.");
        }

        var envelope = RuntimeSnapshotProtocol.CreateEnvelope(
            sequence,
            capturedAtUtc,
            sessionId,
            networkId,
            snapshot);
        await _stream.WriteAsync(
            RealtimeProtocolCodec.EncodeLine(envelope),
            cancellationToken);
        await _stream.FlushAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }

        _client.Dispose();
    }
}
