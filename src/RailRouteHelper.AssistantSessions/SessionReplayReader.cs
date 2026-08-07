using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.AssistantSessions;

public sealed record SessionReplayReaderOptions
{
    public bool TolerateTrailingIncompleteLine { get; init; }
}

public class SessionReplayException : Exception
{
    public SessionReplayException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class SessionReplayLineException : SessionReplayException
{
    public SessionReplayLineException(long lineNumber, Exception innerException)
        : base($"Assistant-session replay line {lineNumber} is invalid.", innerException)
    {
        LineNumber = lineNumber;
    }

    public long LineNumber { get; }
}

public sealed class SessionReplaySequenceException : SessionReplayException
{
    public SessionReplaySequenceException(long lineNumber, long expected, long actual)
        : base($"Assistant-session replay line {lineNumber} has sequence {actual}; expected {expected}.")
    {
        LineNumber = lineNumber;
        Expected = expected;
        Actual = actual;
    }

    public long LineNumber { get; }

    public long Expected { get; }

    public long Actual { get; }
}

/// <summary>Reads assistant-session JSONL while enforcing a contiguous envelope sequence.
/// If configured, a crash-truncated final line is ignored, but malformed complete lines never are.</summary>
public sealed class SessionReplayReader
{
    private readonly SessionReplayReaderOptions _options;

    public SessionReplayReader(bool tolerateTrailingIncompleteLine = false)
        : this(new SessionReplayReaderOptions { TolerateTrailingIncompleteLine = tolerateTrailingIncompleteLine })
    {
    }

    public SessionReplayReader(SessionReplayReaderOptions? options)
    {
        _options = options ?? new SessionReplayReaderOptions();
    }

    public IReadOnlyList<RealtimeEnvelope> ReadAll(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The replay stream must be readable.", nameof(source));
        }

        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return Parse(buffer.ToArray());
    }

    public async IAsyncEnumerable<RealtimeEnvelope> ReadAllAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("The replay stream must be readable.", nameof(source));
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        foreach (var envelope in Parse(buffer.ToArray()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return envelope;
        }
    }

    public IReadOnlyList<RealtimeEnvelope> Read(Stream source) => ReadAll(source);

    private IReadOnlyList<RealtimeEnvelope> Parse(byte[] bytes)
    {
        var result = new List<RealtimeEnvelope>();
        long? previousSequence = null;
        long lineNumber = 0;
        var start = 0;
        for (var index = 0; index <= bytes.Length; index++)
        {
            if (index < bytes.Length && bytes[index] != (byte)'\n')
            {
                continue;
            }

            var hasNewline = index < bytes.Length;
            var length = index - start;
            if (length > 0 && bytes[start + length - 1] == (byte)'\r')
            {
                length--;
            }

            // An empty segment after a terminal newline is not a record.
            if (length == 0)
            {
                start = index + 1;
                continue;
            }

            lineNumber++;
            RealtimeEnvelope envelope;
            try
            {
                envelope = RealtimeProtocolCodec.DecodeLine(bytes.AsSpan(start, length));
            }
            catch (Exception error) when (error is JsonException or UnsupportedProtocolVersionException)
            {
                if (!hasNewline && _options.TolerateTrailingIncompleteLine)
                {
                    break;
                }

                throw new SessionReplayLineException(lineNumber, error);
            }

            if (previousSequence is { } previous)
            {
                long expected;
                try
                {
                    expected = checked(previous + 1);
                }
                catch (OverflowException error)
                {
                    throw new SessionReplayException("Assistant-session sequence overflow.", error);
                }

                if (envelope.Sequence != expected)
                {
                    throw new SessionReplaySequenceException(lineNumber, expected, envelope.Sequence);
                }
            }

            previousSequence = envelope.Sequence;
            result.Add(envelope);
            start = index + 1;
        }

        return result;
    }
}
