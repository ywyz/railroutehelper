using RailRouteHelper.Protocol;

namespace RailRouteHelper.AssistantSessions;

public sealed record SessionRecorderOptions
{
    public TimeSpan FlushInterval { get; init; } = TimeSpan.FromSeconds(1);

    public bool FlushToDisk { get; init; }
}

/// <summary>Append-only JSONL recorder for one assistant-session file.
/// A new recorder refuses to open an existing target (so a new session can never
/// accidentally append to an old session); all writes made through this instance
/// append at the file end. A timer periodically flushes the stream.</summary>
public sealed class SessionRecorder : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly SessionRecorderOptions _options;
    private readonly Timer _flushTimer;
    private bool _disposed;

    public SessionRecorder(string path, TimeSpan? flushInterval = null, bool flushToDisk = false)
        : this(path, new SessionRecorderOptions
        {
            FlushInterval = flushInterval ?? TimeSpan.FromSeconds(1),
            FlushToDisk = flushToDisk,
        })
    {
    }

    public SessionRecorder(string path, SessionRecorderOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _options = options ?? new SessionRecorderOptions();
        if (_options.FlushInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FlushInterval must be positive.");
        }

        var fullPath = System.IO.Path.GetFullPath(path);
        var directory = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // CreateNew is deliberate: opening a second recorder cannot overwrite or merge with a prior session.
        _stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            options: FileOptions.WriteThrough);
        _flushTimer = new Timer(
            static state => ((SessionRecorder)state!).FlushSafely(),
            this,
            _options.FlushInterval,
            _options.FlushInterval);
        Path = fullPath;
    }

    public string Path { get; }

    public void Append(RealtimeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var line = RealtimeProtocolCodec.EncodeLine(envelope);
        lock (_gate)
        {
            ThrowIfDisposed();
            _stream.Write(line, 0, line.Length);
        }
    }

    public void Record(RealtimeEnvelope envelope) => Append(envelope);

    public Task AppendAsync(RealtimeEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        var line = RealtimeProtocolCodec.EncodeLine(envelope);
        lock (_gate)
        {
            ThrowIfDisposed();
            _stream.Write(line, 0, line.Length);
        }

        return Task.CompletedTask;
    }

    public Task RecordAsync(RealtimeEnvelope envelope, CancellationToken cancellationToken = default) =>
        AppendAsync(envelope, cancellationToken);

    public void Flush()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _stream.Flush(_options.FlushToDisk);
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            _stream.Flush(_options.FlushToDisk);
        }

        return Task.CompletedTask;
    }

    private void FlushSafely()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _stream.Flush(_options.FlushToDisk);
            }
            catch (ObjectDisposedException)
            {
                // Dispose may race a timer callback; the stream is already safely closed.
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _flushTimer.Dispose();
            try
            {
                _stream.Flush(_options.FlushToDisk);
            }
            finally
            {
                _stream.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
