namespace RailRouteHelper.Monitoring;

public sealed record SaveDirectoryWatchOptions
{
    public bool Follow { get; init; } = true;

    public bool IncludeExisting { get; init; } = true;

    public long StartingSequence { get; init; }

    public TimeSpan ScanInterval { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan FileStabilityInterval { get; init; } =
        TimeSpan.FromMilliseconds(250);
}
