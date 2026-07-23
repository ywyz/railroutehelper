namespace RailRouteHelper.Operations;

public sealed record OperationsReport(
    IReadOnlyList<TrainOperationsAssessment> Trains,
    IReadOnlyList<RouteChangeObservation> RouteChanges);

public sealed record TrainOperationsAssessment(
    string TrainId,
    string ReportingNumber,
    IReadOnlyList<string> OccupiedNodeIds,
    StationTrackLocation? CurrentLocation,
    StationTrackLocation? NextDestination,
    TrainRouteReachability Reachability,
    TrainOperationalStatus Status,
    string? ClearedThroughNodeId,
    string? FirstUnclearedNodeId,
    IReadOnlyList<OperationalEvidence> Evidence);

public sealed record StationTrackLocation(
    string StationName,
    string? TrackNodeId,
    int? PlatformNumber);

public sealed record RouteChangeObservation(
    RouteChangeKind Kind,
    string ControlNodeId,
    IReadOnlyList<string> PreviousTargetNodeIds,
    IReadOnlyList<string> CurrentTargetNodeIds,
    StationTrackLocation? PreviousDestination,
    StationTrackLocation? CurrentDestination);

public sealed record OperationalEvidence(
    string Code,
    EvidenceCertainty Certainty,
    string Description);

public enum TrainRouteReachability
{
    Unknown,
    Reachable,
    NotReachable,
}

public enum TrainOperationalStatus
{
    Unknown,
    AtScheduledPlatform,
    DepartingStation,
    ApproachingStation,
    RunningTowardRouteLimit,
    WaitingForRoute,
    PossibleBlocked,
}

public enum RouteChangeKind
{
    Established,
    Retargeted,
    Released,
}

public enum EvidenceCertainty
{
    Observed,
    Inferred,
}
