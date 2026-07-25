using RailRouteHelper.Core;
using RailRouteHelper.LiveOperations;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.Runtime;

public sealed class RuntimeOperationsPipeline
{
    private readonly object _gate = new();
    private readonly OperationsAnalyzer _analyzer;
    private readonly LiveOperationsProjector _projector;
    private readonly Dictionary<string, OperationalSnapshot> _previousBySession =
        new(StringComparer.Ordinal);
    private long _nextReportSequence;

    public RuntimeOperationsPipeline(
        LiveOperationsProjector projector,
        OperationsAnalyzer? analyzer = null)
    {
        ArgumentNullException.ThrowIfNull(projector);
        _projector = projector;
        _analyzer = analyzer ?? new OperationsAnalyzer();
    }

    public RealtimeEnvelope Apply(RuntimeSnapshotMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        lock (_gate)
        {
            var payload = message.Payload;
            _previousBySession.TryGetValue(
                payload.SessionId,
                out var previous);
            var report = _analyzer.Analyze(payload.Snapshot, previous);
            _previousBySession[payload.SessionId] = payload.Snapshot;
            var envelope = OperationsReportProtocol.CreateEnvelope(
                _nextReportSequence++,
                message.Envelope.CapturedAtUtc,
                $"runtime:{payload.SessionId}",
                RuntimeSnapshotProtocol.SchemaId,
                payload.NetworkId,
                payload.Snapshot.GameVersion.ToString(),
                payload.Snapshot.GameTimeTicks,
                report);
            _projector.Apply(envelope);
            return envelope;
        }
    }
}
