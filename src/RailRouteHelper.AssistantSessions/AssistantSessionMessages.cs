using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace RailRouteHelper.AssistantSessions;

public interface IAssistantSessionPayload
{
    int PayloadVersion { get; }
}

/// <summary>The severity attached to an observed assistant alert.</summary>
public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>The server-side lifecycle of an alert occurrence.</summary>
public enum AlertLifecycleState
{
    Active,
    Resolved,
    Stale,
}

/// <summary>The independent user acknowledgement state of an alert.</summary>
public enum AlertUserState
{
    Unseen,
    Acknowledged,
    Snoozed,
}

public enum AlertActionKind
{
    Acknowledge,
    Snooze,
    Unsnooze,
    MarkUnseen,
}

public enum TimetablePointKind
{
    PlannedArrival,
    PlannedDeparture,
    ActualArrival,
    ActualDeparture,
    PredictedArrival,
    PredictedDeparture,
}

/// <summary>A station call and its optional plan/observation timestamps.
/// Relative plans intentionally have offsets only; no absolute timestamp is inferred.</summary>
public sealed record TrainStop
{
    public TrainStop(
        string stationId,
        string? stationName = null,
        int sequence = 0,
        DateTimeOffset? plannedArrivalUtc = null,
        DateTimeOffset? plannedDepartureUtc = null,
        DateTimeOffset? actualArrivalUtc = null,
        DateTimeOffset? actualDepartureUtc = null,
        DateTimeOffset? predictedArrivalUtc = null,
        DateTimeOffset? predictedDepartureUtc = null,
        bool relativeTimes = false,
        TimeSpan? plannedArrivalOffset = null,
        TimeSpan? plannedDepartureOffset = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stationId);
        StationId = stationId;
        StationName = string.IsNullOrWhiteSpace(stationName) ? stationId : stationName;
        Sequence = sequence;
        PlannedArrivalUtc = plannedArrivalUtc;
        PlannedDepartureUtc = plannedDepartureUtc;
        ActualArrivalUtc = actualArrivalUtc;
        ActualDepartureUtc = actualDepartureUtc;
        PredictedArrivalUtc = predictedArrivalUtc;
        PredictedDepartureUtc = predictedDepartureUtc;
        RelativeTimes = relativeTimes;
        PlannedArrivalOffset = plannedArrivalOffset;
        PlannedDepartureOffset = plannedDepartureOffset;
    }

    public string StationId { get; init; }

    public string StationName { get; init; }

    /// <summary>Zero-based or source-provided order in this train's own route.</summary>
    public int Sequence { get; init; }

    public DateTimeOffset? PlannedArrivalUtc { get; init; }

    public DateTimeOffset? PlannedDepartureUtc { get; init; }

    public DateTimeOffset? ActualArrivalUtc { get; init; }

    public DateTimeOffset? ActualDepartureUtc { get; init; }

    public DateTimeOffset? PredictedArrivalUtc { get; init; }

    public DateTimeOffset? PredictedDepartureUtc { get; init; }

    public bool RelativeTimes { get; init; }

    public TimeSpan? PlannedArrivalOffset { get; init; }

    public TimeSpan? PlannedDepartureOffset { get; init; }

    // These aliases make the wire model pleasant to consume without duplicating data.
    public string StationCode => StationId;

    public DateTimeOffset? PlannedArrival => PlannedArrivalUtc;

    public DateTimeOffset? PlannedDeparture => PlannedDepartureUtc;
}

/// <summary>A train definition and its ordered station calls.</summary>
public sealed record TrainDefinition : IAssistantSessionPayload
{
    [JsonConstructor]
    public TrainDefinition(
        string trainId,
        IReadOnlyList<TrainStop> stops,
        string? serviceName = null,
        string? origin = null,
        string? destination = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainId);
        ArgumentNullException.ThrowIfNull(stops);
        TrainId = trainId;
        ServiceName = serviceName;
        Origin = origin;
        Destination = destination;
        Stops = new ReadOnlyCollection<TrainStop>(stops.ToList());
    }

    public TrainDefinition(
        string trainId,
        IEnumerable<TrainStop> stops,
        string? serviceName = null,
        string? origin = null,
        string? destination = null)
        : this(trainId, stops?.ToList() ?? throw new ArgumentNullException(nameof(stops)), serviceName, origin, destination)
    {
    }

    public string TrainId { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public string? ServiceName { get; init; }

    public string? Origin { get; init; }

    public string? Destination { get; init; }

    public IReadOnlyList<TrainStop> Stops { get; init; }

    public string Id => TrainId;

    public IReadOnlyList<TrainStop> StationCalls => Stops;

    public IReadOnlyList<string> Stations => Stops.Select(stop => stop.StationId).ToArray();
}

/// <summary>Dynamic state reported for a train in an assistant frame. Schedule
/// topology belongs to <see cref="TrainDefinition"/>; these fields remain separate
/// so a replay retains both route and live state.</summary>
public sealed record AssistantTrainState
{
    public AssistantTrainState()
    {
    }

    public AssistantTrainState(string trainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trainId);
        TrainId = trainId;
    }

    public string TrainId { get; init; } = string.Empty;

    public string? ReportingNumber { get; init; }

    public double? Speed { get; init; }

    public double? TargetSpeed { get; init; }

    public double? MaxSpeed { get; init; }

    public double? DelaySeconds { get; init; }

    public bool OnBoard { get; init; }

    public bool Waiting { get; init; }

    public bool Finished { get; init; }

    public bool BrokenDown { get; init; }

    public bool CanDepart { get; init; }

    public int? LookaheadCount { get; init; }

    public bool? HasValidRoute { get; init; }

    public bool? NeedsRouteAhead { get; init; }

    public bool? HasSignal { get; init; }

    public string? SignalState { get; init; }

    public int? SignalAllocationState { get; init; }

    public int? FrontAllocationState { get; init; }

    public int? RouteTotalSteps { get; init; }

    public int? RouteCurrentStep { get; init; }

    public int? RouteRemainingSteps { get; init; }

    public string? CurrentStation { get; init; }

    public int? CurrentPlatform { get; init; }

    public string? NextStation { get; init; }

    public int? NextPlatform { get; init; }

    public int VisitIndex { get; init; } = -1;

    public int VisitCount { get; init; }

    public int? ScheduledVisitCount { get; init; }

    public string? LastVisitStation { get; init; }

    public int? LastVisitPlatform { get; init; }

    public bool LastVisitDeparted { get; init; }

    public bool? LastVisitNonStop { get; init; }

    public int? LastVisitStopMinutes { get; init; }

    public double? LastArrivalScheduleDeviationSeconds { get; init; }

    public double? LastDepartureScheduleDelaySeconds { get; init; }

    public bool RequiresDirectionChange { get; init; }

    public double? NextArrivalSeconds { get; init; }

    public double? DepartureRemainingSeconds { get; init; }

    public int? CurrentStopMinutes { get; init; }

    public double? CurrentDepartureScheduleDelaySeconds { get; init; }

    public double? NotMovingSinceSeconds { get; init; }

    public double? NextPrepareSeconds { get; init; }

    public string? StopReasons { get; init; }

    public double? MapEntryGameTimeSeconds { get; init; }

    public double? MapExitGameTimeSeconds { get; init; }

    public string? MapEntryStation { get; init; }

    public string? MapExitStation { get; init; }

    public int? MapEntryPlatform { get; init; }

    public int? MapExitPlatform { get; init; }

    public bool? MapEntryNonStop { get; init; }

    public bool? MapExitNonStop { get; init; }

    public bool? NextStationNonStop { get; init; }

    public string Id => TrainId;

    public double? Delay => DelaySeconds;
}

/// <summary>A structured alert emitted by the assistant for one frame.</summary>
public sealed record ObservedAlert
{
    public ObservedAlert(
        string code,
        AlertSeverity severity,
        string? subjectId = null,
        string? title = null,
        string? detail = null,
        string? stationId = null,
        IReadOnlyDictionary<string, string>? attributes = null,
        string? subjectDisplayName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        Severity = severity;
        SubjectId = subjectId;
        Title = title;
        Detail = detail;
        StationId = stationId;
        SubjectDisplayName = subjectDisplayName;
        Attributes = attributes is null
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))
            : new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(attributes, StringComparer.Ordinal));
    }

    public string Code { get; init; }

    public AlertSeverity Severity { get; init; }

    public string? SubjectId { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    public string? StationId { get; init; }

    public IReadOnlyDictionary<string, string> Attributes { get; init; }

    public string AlertType => Code;

    public string? EntityId => SubjectId;

    /// <summary>Human-facing label for the subject. It is intentionally excluded
    /// from <see cref="Fingerprint"/> because labels can change without changing
    /// the underlying entity.</summary>
    public string? SubjectDisplayName { get; init; }

    public string? SubjectName => SubjectDisplayName;

    /// <summary>A stable identity based on semantic fields, never on the frame or wall clock.</summary>
    public string Fingerprint => ComputeFingerprint(this);

    public static string ComputeFingerprint(ObservedAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);
        var builder = new StringBuilder();
        AppendCanonical(builder, alert.Code);
        AppendCanonical(builder, alert.SubjectId);
        AppendCanonical(builder, alert.StationId);
        // Attributes are sorted so object/dictionary insertion order cannot alter identity.
        foreach (var pair in alert.Attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            AppendCanonical(builder, pair.Key);
            AppendCanonical(builder, pair.Value);
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void AppendCanonical(StringBuilder builder, string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        builder.Append(normalized.Length).Append(':').Append(normalized).Append('|');
    }
}

/// <summary>A frame sampled from the running assistant session.</summary>
public sealed record AssistantFrame : IAssistantSessionPayload
{
    [JsonConstructor]
    public AssistantFrame(
        long frameSequence,
        DateTimeOffset capturedAtUtc,
        bool isConnected,
        IReadOnlyList<TrainDefinition>? trains = null,
        IReadOnlyList<ObservedAlert>? observedAlerts = null,
        bool isSuccessful = true,
        IReadOnlyList<AssistantTrainState>? trainStates = null,
        double? gameTimeSeconds = null,
        bool gameReady = true)
    {
        if (frameSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameSequence));
        }

        FrameSequence = frameSequence;
        CapturedAtUtc = capturedAtUtc;
        IsConnected = isConnected;
        IsSuccessful = isSuccessful;
        GameReady = gameReady;
        GameTimeSeconds = gameTimeSeconds;
        Trains = new ReadOnlyCollection<TrainDefinition>((trains ?? []).ToList());
        ObservedAlerts = new ReadOnlyCollection<ObservedAlert>((observedAlerts ?? []).ToList());
        TrainStates = new ReadOnlyCollection<AssistantTrainState>((trainStates ?? []).ToList());
    }

    public AssistantFrame(
        long frameSequence,
        DateTimeOffset capturedAtUtc,
        IReadOnlyList<TrainDefinition>? trains = null,
        IReadOnlyList<ObservedAlert>? observedAlerts = null,
        bool isConnected = true,
        bool isSuccessful = true,
        IReadOnlyList<AssistantTrainState>? trainStates = null,
        double? gameTimeSeconds = null,
        bool gameReady = true)
        : this(frameSequence, capturedAtUtc, isConnected, trains, observedAlerts, isSuccessful, trainStates, gameTimeSeconds, gameReady)
    {
    }

    public long FrameSequence { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public DateTimeOffset CapturedAtUtc { get; init; }

    public bool IsConnected { get; init; }

    public bool IsSuccessful { get; init; }

    public bool GameReady { get; init; }

    public double? GameTimeSeconds { get; init; }

    public IReadOnlyList<TrainDefinition> Trains { get; init; }

    public IReadOnlyList<ObservedAlert> ObservedAlerts { get; init; }

    public IReadOnlyList<AssistantTrainState> TrainStates { get; init; }

    public long Sequence => FrameSequence;

    public bool Connected => IsConnected;

    public IReadOnlyList<ObservedAlert> Alerts => ObservedAlerts;
}

public sealed record SessionStart : IAssistantSessionPayload
{
    public SessionStart(
        string sessionId,
        DateTimeOffset startedAtUtc,
        string? source = null,
        string? selectedTrainId = null,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        StartedAtUtc = startedAtUtc;
        Source = source;
        SelectedTrainId = selectedTrainId;
        Metadata = metadata is null
            ? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal))
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(metadata, StringComparer.Ordinal));
    }

    public string SessionId { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public DateTimeOffset StartedAtUtc { get; init; }

    public string? Source { get; init; }

    public string? SelectedTrainId { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; }
}

public sealed record SessionEnd : IAssistantSessionPayload
{
    public SessionEnd(
        string sessionId,
        DateTimeOffset endedAtUtc,
        string? reason = null,
        long? frameCount = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionId = sessionId;
        EndedAtUtc = endedAtUtc;
        Reason = reason;
        FrameCount = frameCount;
    }

    public string SessionId { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public DateTimeOffset EndedAtUtc { get; init; }

    public string? Reason { get; init; }

    public long? FrameCount { get; init; }
}

public sealed record AlertAction : IAssistantSessionPayload
{
    public AlertAction(
        string alertId,
        AlertActionKind action,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset? snoozeUntilUtc = null,
        string? note = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        AlertId = alertId;
        Action = action;
        OccurredAtUtc = occurredAtUtc;
        SnoozeUntilUtc = snoozeUntilUtc;
        Note = note;
    }

    public string AlertId { get; init; }

    public int PayloadVersion { get; init; } = 1;

    public AlertActionKind Action { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    public DateTimeOffset? SnoozeUntilUtc { get; init; }

    public string? Note { get; init; }

    public string Id => AlertId;
}

public sealed record AlertOccurrence
{
    public required string AlertId { get; init; }

    public required string Fingerprint { get; init; }

    public required ObservedAlert Observation { get; init; }

    public required AlertLifecycleState Lifecycle { get; init; }

    public required AlertUserState UserState { get; init; }

    public DateTimeOffset FirstSeenAtUtc { get; init; }

    public DateTimeOffset LastSeenAtUtc { get; init; }

    public DateTimeOffset? ResolvedAtUtc { get; init; }

    public DateTimeOffset? SnoozedUntilUtc { get; init; }

    public int ConsecutiveMissingFrames { get; init; }

    /// <summary>Number of successful connected frames in which this occurrence was observed.</summary>
    public int ObservationCount { get; init; }

    public int Generation { get; init; }

    public AlertLifecycleState State => Lifecycle;
}

public sealed record AlertCenterSnapshot(
    IReadOnlyList<AlertOccurrence> Alerts,
    bool IsConnected,
    DateTimeOffset CapturedAtUtc)
{
    public IReadOnlyList<AlertOccurrence> Active => Alerts.Where(alert => alert.Lifecycle == AlertLifecycleState.Active).ToList();
}
