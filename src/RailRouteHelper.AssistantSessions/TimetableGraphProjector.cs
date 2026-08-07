namespace RailRouteHelper.AssistantSessions;

public sealed record CorridorStation(string StationId, string StationName, int Index);

public sealed record TimetablePoint
{
    public required string TrainId { get; init; }

    public required string StationId { get; init; }

    public required string StationName { get; init; }

    public required TimetablePointKind Kind { get; init; }

    public DateTimeOffset? AbsoluteTimeUtc { get; init; }

    public TimeSpan? RelativeTime { get; init; }

    public bool RelativeTimes { get; init; }

    public int CorridorIndex { get; init; }

    public DateTimeOffset? PlannedTimeUtc =>
        Kind is TimetablePointKind.PlannedArrival or TimetablePointKind.PlannedDeparture
            ? AbsoluteTimeUtc
            : null;

    public DateTimeOffset? ActualTimeUtc =>
        Kind is TimetablePointKind.ActualArrival or TimetablePointKind.ActualDeparture
            ? AbsoluteTimeUtc
            : null;

    public DateTimeOffset? PredictedTimeUtc =>
        Kind is TimetablePointKind.PredictedArrival or TimetablePointKind.PredictedDeparture
            ? AbsoluteTimeUtc
            : null;
}

public sealed record TrainCorridor(
    string TrainId,
    int Direction,
    IReadOnlyList<TimetablePoint> Points)
{
    public bool IsForward => Direction >= 0;
}

public sealed record TimetableGraphSnapshot(
    IReadOnlyList<CorridorStation> Corridor,
    IReadOnlyList<TrainCorridor> Trains,
    string? SelectedTrainId)
{
    public IReadOnlyList<CorridorStation> Stations => Corridor;
}

/// <summary>Builds a station corridor around one selected train and projects
/// plans, actual events and predictions from every train sharing at least two stations.</summary>
public sealed class TimetableGraphProjector
{
    private readonly string _selectedTrainId;
    private readonly Dictionary<string, TrainDefinition> _trains = new(StringComparer.Ordinal);

    public TimetableGraphProjector(string selectedTrainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedTrainId);
        _selectedTrainId = selectedTrainId;
    }

    public TimetableGraphSnapshot Snapshot => BuildSnapshot();

    public TimetableGraphSnapshot Apply(AssistantFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        foreach (var train in frame.Trains)
        {
            ApplyTrain(train);
        }

        return BuildSnapshot();
    }

    public TimetableGraphSnapshot ApplyTrain(TrainDefinition train)
    {
        ArgumentNullException.ThrowIfNull(train);
        _trains[train.TrainId] = _trains.TryGetValue(train.TrainId, out var previous)
            ? MergeTrain(previous, train)
            : train;
        return BuildSnapshot();
    }

    public TimetableGraphSnapshot Project(IEnumerable<TrainDefinition> trains)
    {
        ArgumentNullException.ThrowIfNull(trains);
        foreach (var train in trains)
        {
            ApplyTrain(train);
        }

        return BuildSnapshot();
    }

    public TimetableGraphSnapshot Project(IEnumerable<AssistantFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        foreach (var frame in frames)
        {
            Apply(frame);
        }

        return BuildSnapshot();
    }

    public static TimetableGraphSnapshot Build(
        string selectedTrainId,
        IEnumerable<TrainDefinition> trains)
    {
        return new TimetableGraphProjector(selectedTrainId).Project(trains);
    }

    public static TimetableGraphSnapshot Project(
        string selectedTrainId,
        IEnumerable<TrainDefinition> trains)
    {
        return Build(selectedTrainId, trains);
    }

    private static TrainDefinition MergeTrain(TrainDefinition previous, TrainDefinition latest)
    {
        var priorByKey = new Dictionary<string, TrainStop>(StringComparer.Ordinal);
        foreach (var stop in previous.Stops)
        {
            priorByKey[StopKey(stop)] = stop;
        }
        var merged = new List<TrainStop>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var incoming in latest.Stops.OrderBy(stop => stop.Sequence))
        {
            var key = StopKey(incoming);
            priorByKey.TryGetValue(key, out var prior);
            var value = prior is null
                ? incoming
                : incoming with
                {
                    // A frame may omit a plan, but it must never erase a known plan.
                    PlannedArrivalUtc = incoming.PlannedArrivalUtc ?? prior.PlannedArrivalUtc,
                    PlannedDepartureUtc = incoming.PlannedDepartureUtc ?? prior.PlannedDepartureUtc,
                    PlannedArrivalOffset = incoming.PlannedArrivalOffset ?? prior.PlannedArrivalOffset,
                    PlannedDepartureOffset = incoming.PlannedDepartureOffset ?? prior.PlannedDepartureOffset,
                    // Actual events are historical: retain them when a later poll omits them.
                    ActualArrivalUtc = incoming.ActualArrivalUtc ?? prior.ActualArrivalUtc,
                    ActualDepartureUtc = incoming.ActualDepartureUtc ?? prior.ActualDepartureUtc,
                    // Predictions are latest-known values. Once an actual event is
                    // observed, the corresponding prediction is no longer current.
                    PredictedArrivalUtc = incoming.ActualArrivalUtc is not null
                        ? null
                        : incoming.PredictedArrivalUtc ?? prior.PredictedArrivalUtc,
                    PredictedDepartureUtc = incoming.ActualDepartureUtc is not null
                        ? null
                        : incoming.PredictedDepartureUtc ?? prior.PredictedDepartureUtc,
                };
            merged.Add(value);
            seen.Add(key);
        }

        // Keep an actual event at a station that disappeared from a partial update in history.
        merged.AddRange(previous.Stops
            .Where(stop => !seen.Contains(StopKey(stop))
                && (stop.ActualArrivalUtc is not null
                    || stop.ActualDepartureUtc is not null
                    || stop.PredictedArrivalUtc is not null
                    || stop.PredictedDepartureUtc is not null))
            .OrderBy(stop => stop.Sequence));

        return new TrainDefinition(
            latest.TrainId,
            merged,
            latest.ServiceName ?? previous.ServiceName,
            latest.Origin ?? previous.Origin,
            latest.Destination ?? previous.Destination)
        {
            PayloadVersion = latest.PayloadVersion,
        };
    }

    private static string StopKey(TrainStop stop) => $"{stop.Sequence}:{stop.StationId}";

    private TimetableGraphSnapshot BuildSnapshot()
    {
        if (!_trains.TryGetValue(_selectedTrainId, out var baseTrain))
        {
            return new TimetableGraphSnapshot([], [], _selectedTrainId);
        }

        var baseStops = baseTrain.Stops
            .OrderBy(stop => stop.Sequence)
            .ThenBy(stop => stop.StationId, StringComparer.Ordinal)
            .ToList();
        var corridor = baseStops
            .Select((stop, index) => new CorridorStation(stop.StationId, stop.StationName, index))
            .ToArray();
        var indexByStation = corridor
            .Select((station, index) => (station, index))
            .ToDictionary(item => item.station.StationId, item => item.index, StringComparer.Ordinal);

        var trainViews = new List<TrainCorridor>();
        foreach (var train in _trains.Values.OrderBy(item => item.TrainId, StringComparer.Ordinal))
        {
            var trainStops = train.Stops
                .OrderBy(stop => stop.Sequence)
                .ThenBy(stop => stop.StationId, StringComparer.Ordinal)
                .ToList();
            var shared = trainStops
                .Where(stop => indexByStation.ContainsKey(stop.StationId))
                .GroupBy(stop => stop.StationId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (shared.Count < 2)
            {
                continue;
            }

            var sharedIndexes = shared.Select(stop => indexByStation[stop.StationId]).ToArray();
            var direction = DetermineDirection(sharedIndexes);
            var points = new List<TimetablePoint>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var stop in trainStops)
            {
                if (!indexByStation.TryGetValue(stop.StationId, out var corridorIndex))
                {
                    continue;
                }

                AddPlanPoint(train, stop, corridorIndex, TimetablePointKind.PlannedArrival, stop.PlannedArrivalUtc, stop.PlannedArrivalOffset, seen, points);
                AddPlanPoint(train, stop, corridorIndex, TimetablePointKind.PlannedDeparture, stop.PlannedDepartureUtc, stop.PlannedDepartureOffset, seen, points);
                AddAbsolutePoint(train, stop, corridorIndex, TimetablePointKind.ActualArrival, stop.ActualArrivalUtc, seen, points);
                AddAbsolutePoint(train, stop, corridorIndex, TimetablePointKind.ActualDeparture, stop.ActualDepartureUtc, seen, points);
                AddAbsolutePoint(train, stop, corridorIndex, TimetablePointKind.PredictedArrival, stop.PredictedArrivalUtc, seen, points);
                AddAbsolutePoint(train, stop, corridorIndex, TimetablePointKind.PredictedDeparture, stop.PredictedDepartureUtc, seen, points);
            }

            trainViews.Add(new TrainCorridor(train.TrainId, direction, points
                .OrderBy(point => point.CorridorIndex)
                .ThenBy(point => point.AbsoluteTimeUtc)
                .ThenBy(point => point.Kind)
                .ToArray()));
        }

        return new TimetableGraphSnapshot(corridor, trainViews, _selectedTrainId);
    }

    private static int DetermineDirection(IReadOnlyList<int> indexes)
    {
        for (var i = 1; i < indexes.Count; i++)
        {
            if (indexes[i] > indexes[i - 1])
            {
                return 1;
            }

            if (indexes[i] < indexes[i - 1])
            {
                return -1;
            }
        }

        return 1;
    }

    private static void AddPlanPoint(
        TrainDefinition train,
        TrainStop stop,
        int corridorIndex,
        TimetablePointKind kind,
        DateTimeOffset? absolute,
        TimeSpan? relative,
        HashSet<string> seen,
        List<TimetablePoint> points)
    {
        if (absolute is null && relative is null)
        {
            return;
        }

        // A source marked relative is authoritative: do not turn an offset into a fake UTC plan.
        if (stop.RelativeTimes)
        {
            absolute = null;
        }

        var key = EventKey(train.TrainId, stop.StationId, kind, absolute, relative, stop.RelativeTimes);
        if (!seen.Add(key))
        {
            return;
        }

        points.Add(new TimetablePoint
        {
            TrainId = train.TrainId,
            StationId = stop.StationId,
            StationName = stop.StationName,
            Kind = kind,
            AbsoluteTimeUtc = absolute,
            RelativeTime = relative,
            RelativeTimes = stop.RelativeTimes,
            CorridorIndex = corridorIndex,
        });
    }

    private static void AddAbsolutePoint(
        TrainDefinition train,
        TrainStop stop,
        int corridorIndex,
        TimetablePointKind kind,
        DateTimeOffset? absolute,
        HashSet<string> seen,
        List<TimetablePoint> points)
    {
        if (absolute is null)
        {
            return;
        }

        var key = EventKey(train.TrainId, stop.StationId, kind, absolute, null, false);
        if (!seen.Add(key))
        {
            return;
        }

        points.Add(new TimetablePoint
        {
            TrainId = train.TrainId,
            StationId = stop.StationId,
            StationName = stop.StationName,
            Kind = kind,
            AbsoluteTimeUtc = absolute,
            CorridorIndex = corridorIndex,
        });
    }

    private static string EventKey(
        string trainId,
        string stationId,
        TimetablePointKind kind,
        DateTimeOffset? absolute,
        TimeSpan? relative,
        bool isRelative)
    {
        return string.Join(
            '|',
            trainId,
            stationId,
            kind,
            isRelative ? relative?.Ticks.ToString() ?? string.Empty : absolute?.UtcTicks.ToString() ?? string.Empty);
    }
}
