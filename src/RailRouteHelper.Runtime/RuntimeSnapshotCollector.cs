using System.Net.Sockets;

namespace RailRouteHelper.Runtime;

public sealed class RuntimeSnapshotCollector
{
    private readonly object _gate = new();
    private readonly IRuntimeSnapshotSource _source;
    private readonly int _port;
    private readonly TimeSpan _captureInterval;
    private readonly TimeSpan _reconnectDelay;
    private RuntimeCollectorStatus _status = RuntimeCollectorStatus.Stopped;

    public RuntimeSnapshotCollector(
        IRuntimeSnapshotSource source,
        int port = RuntimeTelemetryServer.DefaultPort,
        TimeSpan? captureInterval = null,
        TimeSpan? reconnectDelay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        if (port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "TCP port must be between 1 and 65535.");
        }

        _source = source;
        _port = port;
        _captureInterval = captureInterval ?? TimeSpan.FromMilliseconds(500);
        _reconnectDelay = reconnectDelay ?? TimeSpan.FromSeconds(1);
        if (_captureInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(captureInterval));
        }

        if (_reconnectDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconnectDelay));
        }
    }

    public RuntimeCollectorStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public async Task RunAsync(
        CancellationToken cancellationToken = default)
    {
        var sessionId = Guid.NewGuid().ToString("N");
        long sequence = 0;
        UpdateStatus(
            status => status with
            {
                IsRunning = true,
                SessionId = sessionId,
                LastError = null,
            });
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await using var client = new RuntimeSnapshotClient(_port);
                    await client.ConnectAsync(cancellationToken);
                    UpdateStatus(
                        status => status with
                        {
                            IsConnected = true,
                            SuccessfulConnections = checked(
                                status.SuccessfulConnections + 1),
                            LastError = null,
                        });
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var captured = await _source.CaptureAsync(
                            cancellationToken);
                        if (captured is not null)
                        {
                            await client.PublishAsync(
                                sequence,
                                captured.Snapshot.ObservedAtUtc,
                                sessionId,
                                captured.NetworkId,
                                captured.Snapshot,
                                cancellationToken);
                            sequence = checked(sequence + 1);
                            UpdateStatus(
                                status => status with
                                {
                                    PublishedFrames = checked(
                                        status.PublishedFrames + 1),
                                    LastPublishedAtUtc =
                                        captured.Snapshot.ObservedAtUtc,
                                });
                        }

                        await Task.Delay(
                            _captureInterval,
                            cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (
                    error is IOException
                    or SocketException
                    or InvalidOperationException)
                {
                    UpdateStatus(
                        status => status with
                        {
                            IsConnected = false,
                            LastError = error.Message,
                        });
                    await Task.Delay(_reconnectDelay, cancellationToken);
                }
                finally
                {
                    UpdateStatus(
                        status => status with { IsConnected = false });
                }
            }
        }
        finally
        {
            UpdateStatus(
                status => status with
                {
                    IsRunning = false,
                    IsConnected = false,
                });
        }
    }

    private void UpdateStatus(
        Func<RuntimeCollectorStatus, RuntimeCollectorStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status);
        }
    }
}

public sealed record RuntimeCollectorStatus(
    bool IsRunning,
    bool IsConnected,
    string? SessionId,
    long SuccessfulConnections,
    long PublishedFrames,
    DateTimeOffset? LastPublishedAtUtc,
    string? LastError)
{
    public static RuntimeCollectorStatus Stopped { get; } = new(
        false,
        false,
        null,
        0,
        0,
        null,
        null);
}
