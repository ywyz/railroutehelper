using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Runtime;

public sealed class RuntimeTelemetryServer : IAsyncDisposable
{
    public const int DefaultPort = 5081;

    public const int DefaultMaxFrameBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly TcpListener _listener;
    private readonly int _maxFrameBytes;
    private readonly Dictionary<string, long> _lastSequenceBySession =
        new(StringComparer.Ordinal);
    private RuntimeTelemetryStatus _status;
    private bool _started;

    public RuntimeTelemetryServer(
        int port = DefaultPort,
        int maxFrameBytes = DefaultMaxFrameBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        if (port > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(port),
                "TCP port must be between 0 and 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);
        _listener = new TcpListener(IPAddress.Loopback, port);
        _maxFrameBytes = maxFrameBytes;
        _status = RuntimeTelemetryStatus.Stopped(port);
    }

    public int Port
    {
        get
        {
            lock (_gate)
            {
                return _status.Port;
            }
        }
    }

    public RuntimeTelemetryStatus Status
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
        Func<RuntimeSnapshotMessage, CancellationToken, ValueTask> onSnapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSnapshot);
        Start();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(
                        cancellationToken);
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using var acceptedClient = client;
                SetConnected(true);
                IncrementAcceptedConnections();
                try
                {
                    await foreach (var message in ReadClientAsync(
                                       client,
                                       cancellationToken))
                    {
                        ValidateSequence(message);
                        await onSnapshot(message, cancellationToken);
                        RecordAcceptedFrame(message.Envelope.CapturedAtUtc);
                    }
                }
                catch (OperationCanceledException)
                    when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception error) when (
                    error is IOException
                    or InvalidDataException
                    or JsonException
                    or SocketException
                    or ArgumentException
                    or InvalidOperationException
                    or OverflowException)
                {
                    RecordRejectedConnection(error);
                }
                finally
                {
                    SetConnected(false);
                }
            }
        }
        finally
        {
            Stop();
        }
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        return ValueTask.CompletedTask;
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException(
                    "The runtime telemetry server is already running.");
            }

            _listener.Start();
            _started = true;
            var endpoint = (IPEndPoint)_listener.LocalEndpoint;
            _status = _status with
            {
                IsListening = true,
                Port = endpoint.Port,
                LastError = null,
            };
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            if (_started)
            {
                _listener.Stop();
                _started = false;
            }

            _status = _status with
            {
                IsListening = false,
                IsCollectorConnected = false,
            };
        }
    }

    private async IAsyncEnumerable<RuntimeSnapshotMessage> ReadClientAsync(
        TcpClient client,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var pending = new MemoryStream();
        var buffer = new byte[16 * 1024];
        await using var stream = client.GetStream();
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                if (pending.Length != 0)
                {
                    throw new InvalidDataException(
                        "Runtime telemetry connection ended with a partial frame.");
                }

                yield break;
            }

            var offset = 0;
            for (var index = 0; index < read; index++)
            {
                if (buffer[index] != (byte)'\n')
                {
                    continue;
                }

                pending.Write(buffer, offset, index - offset);
                yield return DecodeFrame(pending);
                pending.SetLength(0);
                offset = index + 1;
            }

            pending.Write(buffer, offset, read - offset);
            if (pending.Length > _maxFrameBytes)
            {
                throw new InvalidDataException(
                    $"Runtime telemetry frame exceeds {_maxFrameBytes} bytes.");
            }
        }
    }

    private RuntimeSnapshotMessage DecodeFrame(MemoryStream frame)
    {
        if (frame.Length == 0)
        {
            throw new InvalidDataException(
                "Runtime telemetry frame may not be empty.");
        }

        if (frame.Length > _maxFrameBytes)
        {
            throw new InvalidDataException(
                $"Runtime telemetry frame exceeds {_maxFrameBytes} bytes.");
        }

        var length = checked((int)frame.Length);
        var buffer = frame.GetBuffer().AsSpan(0, length);
        if (!buffer.IsEmpty && buffer[^1] == (byte)'\r')
        {
            buffer = buffer[..^1];
        }

        var envelope = RealtimeProtocolCodec.DecodeLine(buffer);
        var payload = RuntimeSnapshotProtocol.Decode(envelope);
        return new RuntimeSnapshotMessage(envelope, payload);
    }

    private void ValidateSequence(RuntimeSnapshotMessage message)
    {
        lock (_gate)
        {
            var sessionId = message.Payload.SessionId;
            var sequence = message.Envelope.Sequence;
            if (_lastSequenceBySession.TryGetValue(
                    sessionId,
                    out var previousSequence)
                && sequence <= previousSequence)
            {
                throw new InvalidDataException(
                    $"Runtime session '{sessionId}' sequence {sequence} "
                    + $"does not follow {previousSequence}.");
            }

            _lastSequenceBySession[sessionId] = sequence;
        }
    }

    private void SetConnected(bool connected)
    {
        lock (_gate)
        {
            _status = _status with { IsCollectorConnected = connected };
        }
    }

    private void IncrementAcceptedConnections()
    {
        lock (_gate)
        {
            _status = _status with
            {
                AcceptedConnections = checked(
                    _status.AcceptedConnections + 1),
            };
        }
    }

    private void RecordAcceptedFrame(DateTimeOffset capturedAtUtc)
    {
        lock (_gate)
        {
            _status = _status with
            {
                AcceptedFrames = checked(_status.AcceptedFrames + 1),
                LastFrameAtUtc = capturedAtUtc,
            };
        }
    }

    private void RecordRejectedConnection(Exception error)
    {
        lock (_gate)
        {
            _status = _status with
            {
                RejectedConnections = checked(
                    _status.RejectedConnections + 1),
                LastError = error.Message,
            };
        }
    }
}

public sealed record RuntimeSnapshotMessage(
    RealtimeEnvelope Envelope,
    RuntimeSnapshotPayload Payload);
