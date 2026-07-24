using System.Collections.ObjectModel;
using RailRouteHelper.Operations;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.LiveOperations;

public sealed class LiveOperationsProjector
{
    private readonly object _gate = new();
    private readonly int _maxResolvedAlerts;
    private readonly int _maxRouteChangesPerNetwork;
    private readonly Dictionary<string, LiveNetworkOperations> _networks =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LiveOperationsAlert> _activeAlerts =
        new(StringComparer.Ordinal);
    private readonly List<LiveOperationsAlert> _resolvedAlerts = [];
    private LiveOperationsSnapshot _current = LiveOperationsSnapshot.Empty;

    public LiveOperationsProjector(
        int maxResolvedAlerts = 200,
        int maxRouteChangesPerNetwork = 100)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResolvedAlerts);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            maxRouteChangesPerNetwork);
        _maxResolvedAlerts = maxResolvedAlerts;
        _maxRouteChangesPerNetwork = maxRouteChangesPerNetwork;
    }

    public LiveOperationsSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool Apply(RealtimeEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!string.Equals(
                envelope.MessageType,
                OperationsReportProtocol.MessageType,
                StringComparison.Ordinal))
        {
            return false;
        }

        var payload = OperationsReportProtocol.Decode(envelope);
        lock (_gate)
        {
            var projectedTrains = ReadOnly(
                payload.Report.Trains.Select(CloneTrain));
            var projectedRouteChanges = ReadOnly(
                payload.Report.RouteChanges.Select(CloneRouteChange));
            _networks.TryGetValue(payload.NetworkId, out var previousNetwork);
            var recentRouteChanges = ReadOnly(
                (previousNetwork?.RecentRouteChanges
                    ?? Array.Empty<LiveRouteChange>())
                .Concat(
                    projectedRouteChanges.Select(
                        change => new LiveRouteChange(
                            envelope.Sequence,
                            envelope.CapturedAtUtc,
                            change)))
                .TakeLast(_maxRouteChangesPerNetwork));
            var network = new LiveNetworkOperations(
                payload.NetworkId,
                payload.SourceSaveName,
                payload.SchemaId,
                payload.GameVersion,
                payload.GameTimeTicks,
                envelope.Sequence,
                envelope.CapturedAtUtc,
                projectedTrains,
                projectedRouteChanges,
                recentRouteChanges);
            _networks[payload.NetworkId] = network;
            ApplyAlertLifecycle(
                payload.NetworkId,
                envelope,
                projectedTrains);
            _current = new LiveOperationsSnapshot(
                envelope.Sequence,
                envelope.CapturedAtUtc,
                ReadOnly(
                    _networks.Values
                        .OrderBy(item => item.NetworkId, StringComparer.Ordinal)),
                ReadOnly(
                    _activeAlerts.Values
                        .OrderByDescending(item => item.LastObservedSequence)
                        .Concat(
                            _resolvedAlerts.OrderByDescending(
                                item => item.ResolvedSequence))));
        }

        return true;
    }

    private void ApplyAlertLifecycle(
        string networkId,
        RealtimeEnvelope envelope,
        IReadOnlyList<TrainOperationsAssessment> trains)
    {
        var observedFingerprints = new HashSet<string>(
            StringComparer.Ordinal);
        foreach (var train in trains.Where(
                     item =>
                         item.Status
                         == TrainOperationalStatus.PossibleBlocked))
        {
            var fingerprint = AlertFingerprint(networkId, train.TrainId);
            observedFingerprints.Add(fingerprint);
            if (_activeAlerts.TryGetValue(fingerprint, out var active))
            {
                _activeAlerts[fingerprint] = active with
                {
                    ReportingNumber = train.ReportingNumber,
                    Summary = AlertSummary(train),
                    LastObservedSequence = envelope.Sequence,
                    LastObservedAtUtc = envelope.CapturedAtUtc,
                    ObservationCount = checked(active.ObservationCount + 1),
                };
                continue;
            }

            _activeAlerts[fingerprint] = new LiveOperationsAlert(
                $"{fingerprint}:{envelope.Sequence}",
                fingerprint,
                networkId,
                LiveOperationsAlertKind.PossibleBlockedTrain,
                LiveOperationsAlertSeverity.Warning,
                LiveOperationsAlertStatus.Active,
                train.TrainId,
                train.ReportingNumber,
                AlertSummary(train),
                envelope.Sequence,
                envelope.Sequence,
                null,
                envelope.CapturedAtUtc,
                envelope.CapturedAtUtc,
                null,
                1);
        }

        var resolved = _activeAlerts
            .Where(
                item =>
                    string.Equals(
                        item.Value.NetworkId,
                        networkId,
                        StringComparison.Ordinal)
                    && !observedFingerprints.Contains(item.Key))
            .Select(item => item.Key)
            .ToArray();
        foreach (var fingerprint in resolved)
        {
            var active = _activeAlerts[fingerprint];
            _activeAlerts.Remove(fingerprint);
            _resolvedAlerts.Add(
                active with
                {
                    Status = LiveOperationsAlertStatus.Resolved,
                    ResolvedSequence = envelope.Sequence,
                    ResolvedAtUtc = envelope.CapturedAtUtc,
                });
        }

        if (_resolvedAlerts.Count > _maxResolvedAlerts)
        {
            _resolvedAlerts.RemoveRange(
                0,
                _resolvedAlerts.Count - _maxResolvedAlerts);
        }
    }

    private static string AlertFingerprint(string networkId, string trainId)
    {
        return $"possible-blocked-train:{networkId}:{trainId}";
    }

    private static string AlertSummary(TrainOperationsAssessment train)
    {
        var destination = train.NextDestination is { } location
            ? location.PlatformNumber is { } platform
                ? $"{location.StationName} platform {platform}"
                : location.StationName
            : "its next destination";
        return $"Train {train.ReportingNumber} may be blocked before {destination}.";
    }

    private static TrainOperationsAssessment CloneTrain(
        TrainOperationsAssessment train)
    {
        return train with
        {
            OccupiedNodeIds = ReadOnly(train.OccupiedNodeIds),
            Evidence = ReadOnly(
                train.Evidence.Select(
                    evidence => evidence with { })),
        };
    }

    private static RouteChangeObservation CloneRouteChange(
        RouteChangeObservation routeChange)
    {
        return routeChange with
        {
            PreviousTargetNodeIds = ReadOnly(
                routeChange.PreviousTargetNodeIds),
            CurrentTargetNodeIds = ReadOnly(
                routeChange.CurrentTargetNodeIds),
        };
    }

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values)
    {
        return new ReadOnlyCollection<T>(values.ToArray());
    }
}

public sealed record LiveOperationsSnapshot(
    long? LastSequence,
    DateTimeOffset? LastUpdatedAtUtc,
    IReadOnlyList<LiveNetworkOperations> Networks,
    IReadOnlyList<LiveOperationsAlert> Alerts)
{
    public static LiveOperationsSnapshot Empty { get; } = new(
        null,
        null,
        Array.Empty<LiveNetworkOperations>(),
        Array.Empty<LiveOperationsAlert>());
}

public sealed record LiveNetworkOperations(
    string NetworkId,
    string SourceSaveName,
    string SchemaId,
    string GameVersion,
    ulong? GameTimeTicks,
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    IReadOnlyList<TrainOperationsAssessment> Trains,
    IReadOnlyList<RouteChangeObservation> RouteChanges,
    IReadOnlyList<LiveRouteChange> RecentRouteChanges);

public sealed record LiveRouteChange(
    long Sequence,
    DateTimeOffset CapturedAtUtc,
    RouteChangeObservation Change);

public sealed record LiveOperationsAlert(
    string AlertId,
    string Fingerprint,
    string NetworkId,
    LiveOperationsAlertKind Kind,
    LiveOperationsAlertSeverity Severity,
    LiveOperationsAlertStatus Status,
    string? TrainId,
    string? ReportingNumber,
    string Summary,
    long OpenedSequence,
    long LastObservedSequence,
    long? ResolvedSequence,
    DateTimeOffset OpenedAtUtc,
    DateTimeOffset LastObservedAtUtc,
    DateTimeOffset? ResolvedAtUtc,
    int ObservationCount);

public enum LiveOperationsAlertKind
{
    PossibleBlockedTrain,
}

public enum LiveOperationsAlertSeverity
{
    Warning,
}

public enum LiveOperationsAlertStatus
{
    Active,
    Resolved,
}
