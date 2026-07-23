namespace RailRouteHelper.Core;

public sealed record OperationalSnapshot(
    GameVersion GameVersion,
    DateTimeOffset ObservedAtUtc,
    ulong? GameTimeTicks,
    IReadOnlyList<TrainSnapshot> Trains,
    IReadOnlyList<TrackSegmentSnapshot> TrackSegments,
    IReadOnlyList<StationSnapshot> Stations,
    IReadOnlyList<RouteClearanceObservation> RouteClearances);

public sealed record TrainSnapshot(
    string Id,
    string ReportingNumber,
    double CurrentSpeed,
    double TargetSpeed,
    IReadOnlyList<string> OccupiedNodeIds,
    string? HeadingTowardNodeId,
    long? NotMovingSinceTicks,
    ulong CurrentStopIndex,
    IReadOnlyList<int> RawStopReasonCodes,
    IReadOnlyList<ScheduledStopSnapshot> ScheduledStops);

public sealed record ScheduledStopSnapshot(
    string StationName,
    string? TrackNodeId,
    ulong FromTicks,
    ulong ToTicks,
    bool Departed,
    bool Exited,
    bool Terminus);

public sealed record TrackSegmentSnapshot(
    string Id,
    string FriendlyName,
    IReadOnlyList<string> EndpointNodeIds,
    IReadOnlyList<GridPoint> EndpointGridPoints,
    string? StationId,
    int? PlatformNumber,
    int RawAllocationCode);

public sealed record StationSnapshot(
    string Id,
    string Name,
    GridPoint? GridPosition,
    IReadOnlyList<PlatformSnapshot> Platforms);

public sealed record PlatformSnapshot(
    int Number,
    string TrackNodeId);

public readonly record struct GridPoint(double X, double Y);

public sealed record RouteClearanceObservation(
    string NodeId,
    string FriendlyName,
    NetworkNodeKind NodeKind,
    int RawAllocationCode,
    RouteClearanceInterpretation Interpretation,
    RouteClearanceOrigin Origin);

public enum NetworkNodeKind
{
    Other,
    Track,
    Signal,
    Switch,
    AutoBlock,
}

public enum RouteClearanceInterpretation
{
    Allocated,
    TrainOccupied,
    UnknownAllocated,
}

public enum RouteClearanceOrigin
{
    Unknown,
    Manual,
    Automatic,
}
