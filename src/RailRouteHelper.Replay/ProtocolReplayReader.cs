using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Replay;

public sealed class ProtocolReplayReader
{
    public async IAsyncEnumerable<OperationsReportReplayItem>
        ReadOperationsReportsAsync(
            Stream source,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var envelope in ReadAllAsync(source, cancellationToken))
        {
            if (!string.Equals(
                    envelope.MessageType,
                    OperationsReportProtocol.MessageType,
                    StringComparison.Ordinal))
            {
                continue;
            }

            yield return new OperationsReportReplayItem(
                envelope.Sequence,
                envelope.CapturedAtUtc,
                OperationsReportProtocol.Decode(envelope));
        }
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

        using var textReader = new StreamReader(
            source,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096,
            leaveOpen: true);

        long lineNumber = 0;
        long? previousSequence = null;
        while (await textReader.ReadLineAsync(cancellationToken) is { } line)
        {
            lineNumber++;
            RealtimeEnvelope envelope;
            try
            {
                envelope = RealtimeProtocolCodec.DecodeLine(Encoding.UTF8.GetBytes(line));
            }
            catch (Exception error) when (
                error is JsonException or UnsupportedProtocolVersionException)
            {
                throw new ReplayLineException(lineNumber, error);
            }

            if (previousSequence is { } previous)
            {
                var expected = checked(previous + 1);
                if (envelope.Sequence != expected)
                {
                    throw new ReplaySequenceException(
                        lineNumber,
                        expected,
                        envelope.Sequence);
                }
            }

            previousSequence = envelope.Sequence;
            yield return envelope;
        }
    }
}

public sealed record OperationsReportReplayItem(
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    OperationsReportPayload Payload);
