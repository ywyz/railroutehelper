using RailRouteHelper.Core;

namespace RailRouteHelper.Operations;

public sealed class OperationsAnalyzer
{
    public OperationsReport Analyze(
        OperationalSnapshot current,
        OperationalSnapshot? previous = null)
    {
        ArgumentNullException.ThrowIfNull(current);

        var topology = BuildTopology(current.TrackSegments);
        var allocatedNodeIds = current.RouteClearances
            .Select(clearance => clearance.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        var selectedConnections = current.RouteClearances.ToDictionary(
            clearance => clearance.NodeId,
            clearance => (IReadOnlySet<string>)clearance.ConnectedNodeIds
                .ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var platformLocations = current.Stations
            .SelectMany(
                station => station.Platforms.Select(
                    platform => new StationTrackLocation(
                        station.Name,
                        platform.TrackNodeId,
                        platform.Number)))
            .GroupBy(location => location.TrackNodeId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key!,
                group => group.First(),
                StringComparer.Ordinal);

        var trains = current.Trains
            .Select(
                train => AnalyzeTrain(
                    train,
                    topology,
                    allocatedNodeIds,
                    selectedConnections,
                    platformLocations,
                    current.GameTimeTicks))
            .OrderBy(train => train.ReportingNumber, StringComparer.Ordinal)
            .ThenBy(train => train.TrainId, StringComparer.Ordinal)
            .ToArray();
        var routeChanges = previous is null
            ? []
            : CompareRoutes(previous, current);

        return new OperationsReport(trains, routeChanges);
    }

    private static TrainOperationsAssessment AnalyzeTrain(
        TrainSnapshot train,
        IReadOnlyDictionary<string, IReadOnlySet<string>> topology,
        IReadOnlySet<string> allocatedNodeIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> selectedConnections,
        IReadOnlyDictionary<string, StationTrackLocation> platformLocations,
        ulong? gameTimeTicks)
    {
        var currentLocation = train.OccupiedNodeIds
            .Select(platformLocations.GetValueOrDefault)
            .FirstOrDefault(location => location is not null);
        var currentStopIndex = train.CurrentStopIndex < int.MaxValue
            ? (int)train.CurrentStopIndex
            : -1;
        var nextStop =
            currentStopIndex >= 0
            && currentStopIndex < train.ScheduledStops.Count
                ? train.ScheduledStops[currentStopIndex]
                : null;
        StationTrackLocation? destination = null;
        if (nextStop is not null)
        {
            int? platformNumber = null;
            if (nextStop.TrackNodeId is { } scheduledTrackNodeId
                && platformLocations.TryGetValue(
                    scheduledTrackNodeId,
                    out var destinationLocation))
            {
                platformNumber = destinationLocation.PlatformNumber;
            }

            destination = new StationTrackLocation(
                nextStop.StationName,
                nextStop.TrackNodeId,
                platformNumber);
        }
        var atScheduledPlatform = IsAtScheduledPlatform(
            train,
            currentStopIndex,
            currentLocation);
        var stationStatus = IsDepartingScheduledPlatform(
                train,
                currentStopIndex,
                currentLocation)
            ? TrainOperationalStatus.DepartingStation
            : atScheduledPlatform
                ? TrainOperationalStatus.AtScheduledPlatform
                : (TrainOperationalStatus?)null;

        if (destination?.TrackNodeId is not { } destinationTrackNodeId
            || train.HeadingTowardNodeId is null)
        {
            return new TrainOperationsAssessment(
                train.Id,
                train.ReportingNumber,
                train.OccupiedNodeIds,
                currentLocation,
                destination,
                TrainRouteReachability.Unknown,
                stationStatus ?? TrainOperationalStatus.Unknown,
                null,
                null,
                [
                    new OperationalEvidence(
                        "forward-route-unavailable",
                        EvidenceCertainty.Observed,
                        "The save has no next platform track or direction node."),
                ]);
        }

        if (train.OccupiedNodeIds.Contains(
                destinationTrackNodeId,
                StringComparer.Ordinal))
        {
            return new TrainOperationsAssessment(
                train.Id,
                train.ReportingNumber,
                train.OccupiedNodeIds,
                currentLocation,
                destination,
                TrainRouteReachability.Reachable,
                TrainOperationalStatus.AtScheduledPlatform,
                destination.TrackNodeId,
                null,
                [
                    new OperationalEvidence(
                        "destination-occupied",
                        EvidenceCertainty.Observed,
                        "The train currently occupies its next scheduled platform."),
                ]);
        }

        var traversal = TraverseForward(
            train,
            topology,
            allocatedNodeIds,
            selectedConnections);
        if (traversal.Ambiguous)
        {
            return new TrainOperationsAssessment(
                train.Id,
                train.ReportingNumber,
                train.OccupiedNodeIds,
                currentLocation,
                destination,
                TrainRouteReachability.Unknown,
                stationStatus ?? TrainOperationalStatus.Unknown,
                null,
                null,
                [
                    new OperationalEvidence(
                        "forward-branch-ambiguous",
                        EvidenceCertainty.Inferred,
                        "More than one allocated forward branch exists without "
                        + "one selected connection."),
                ]);
        }

        if (traversal.ReachableNodeIds.Contains(destinationTrackNodeId))
        {
            return new TrainOperationsAssessment(
                train.Id,
                train.ReportingNumber,
                train.OccupiedNodeIds,
                currentLocation,
                destination,
                TrainRouteReachability.Reachable,
                stationStatus ?? TrainOperationalStatus.ApproachingStation,
                destinationTrackNodeId,
                null,
                [
                    new OperationalEvidence(
                        "allocated-path-to-platform",
                        EvidenceCertainty.Inferred,
                        "A continuous allocated path connects the train direction "
                        + "to its next scheduled platform."),
                ]);
        }

        var clearedThroughNodeId = traversal.TerminalNodeIds.Count == 1
            ? traversal.TerminalNodeIds.Single()
            : null;
        var firstUnclearedNodeId = traversal.BoundaryNodeIds.Count == 1
            ? traversal.BoundaryNodeIds.Single()
            : null;
        var possiblyBlocked =
            stationStatus is null
            && train.CurrentSpeed <= 0
            && train.NotMovingSinceTicks is >= 0
            && gameTimeTicks is { } gameTime
            && (ulong)train.NotMovingSinceTicks.Value < gameTime
            && traversal.BoundaryNodeIds.Count > 0;

        return new TrainOperationsAssessment(
            train.Id,
            train.ReportingNumber,
            train.OccupiedNodeIds,
            currentLocation,
            destination,
            TrainRouteReachability.NotReachable,
            stationStatus
                ?? (possiblyBlocked
                ? TrainOperationalStatus.PossibleBlocked
                : train.CurrentSpeed > 0
                    ? TrainOperationalStatus.RunningTowardRouteLimit
                    : TrainOperationalStatus.WaitingForRoute),
            clearedThroughNodeId,
            firstUnclearedNodeId,
            [
                new OperationalEvidence(
                    possiblyBlocked
                        ? "stationary-route-gap"
                        : "allocated-path-incomplete",
                    EvidenceCertainty.Inferred,
                    possiblyBlocked
                        ? "The train has remained stationary and its allocated "
                            + "forward path has a route gap."
                        : "The allocated forward component does not include the "
                            + "next scheduled platform."),
            ]);
    }

    private static bool IsAtScheduledPlatform(
        TrainSnapshot train,
        int currentStopIndex,
        StationTrackLocation? currentLocation)
    {
        if (currentLocation?.TrackNodeId is not { } occupiedPlatformTrack)
        {
            return false;
        }

        var firstCandidateIndex = Math.Max(0, currentStopIndex - 1);
        var lastCandidateIndex = Math.Min(
            train.ScheduledStops.Count - 1,
            currentStopIndex);
        for (var index = firstCandidateIndex;
             index <= lastCandidateIndex;
             index++)
        {
            if (string.Equals(
                    train.ScheduledStops[index].TrackNodeId,
                    occupiedPlatformTrack,
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDepartingScheduledPlatform(
        TrainSnapshot train,
        int currentStopIndex,
        StationTrackLocation? currentLocation)
    {
        if (train.CurrentSpeed <= 0
            || currentStopIndex <= 0
            || currentStopIndex > train.ScheduledStops.Count
            || currentLocation?.TrackNodeId is not { } occupiedPlatformTrack
            || !string.Equals(
                train.ScheduledStops[currentStopIndex - 1].TrackNodeId,
                occupiedPlatformTrack,
                StringComparison.Ordinal))
        {
            return false;
        }

        return train.OccupiedNodeIds.Any(
            nodeId => !string.Equals(
                nodeId,
                occupiedPlatformTrack,
                StringComparison.Ordinal));
    }

    private static ForwardTraversal TraverseForward(
        TrainSnapshot train,
        IReadOnlyDictionary<string, IReadOnlySet<string>> topology,
        IReadOnlySet<string> allocatedNodeIds,
        IReadOnlyDictionary<string, IReadOnlySet<string>> selectedConnections)
    {
        var occupied = train.OccupiedNodeIds.ToHashSet(StringComparer.Ordinal);
        var reachable = new HashSet<string>(occupied, StringComparer.Ordinal);
        var boundary = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        var ambiguous = false;

        if (train.HeadingTowardNodeId is { } heading
            && allocatedNodeIds.Contains(heading))
        {
            reachable.Add(heading);
            queue.Enqueue(heading);
        }
        else if (train.HeadingTowardNodeId is { } unclearedHeading)
        {
            boundary.Add(unclearedHeading);
        }

        while (queue.Count > 0)
        {
            var nodeId = queue.Dequeue();
            if (!topology.TryGetValue(nodeId, out var adjacentNodeIds))
            {
                continue;
            }

            var candidates = adjacentNodeIds
                .Where(
                    adjacentNodeId =>
                        !occupied.Contains(adjacentNodeId)
                        && !reachable.Contains(adjacentNodeId))
                .ToArray();
            foreach (var unclearedNodeId in candidates.Where(
                         candidate => !allocatedNodeIds.Contains(candidate)))
            {
                boundary.Add(unclearedNodeId);
            }

            var allocatedCandidates = candidates
                .Where(allocatedNodeIds.Contains)
                .ToArray();
            if (allocatedCandidates.Length > 1)
            {
                selectedConnections.TryGetValue(
                    nodeId,
                    out var selectedNodeIds);
                var selectedCandidates = allocatedCandidates
                    .Where(
                        candidate =>
                            selectedNodeIds?.Contains(candidate) is true)
                    .ToArray();
                if (selectedCandidates.Length != 1)
                {
                    ambiguous = true;
                    continue;
                }

                allocatedCandidates = selectedCandidates;
            }

            foreach (var adjacentNodeId in allocatedCandidates)
            {
                reachable.Add(adjacentNodeId);
                queue.Enqueue(adjacentNodeId);
            }
        }

        var terminals = reachable
            .Where(nodeId => !occupied.Contains(nodeId))
            .Where(
                nodeId => !topology.TryGetValue(nodeId, out var adjacent)
                    || adjacent.All(
                        adjacentNodeId =>
                            occupied.Contains(adjacentNodeId)
                            || !allocatedNodeIds.Contains(adjacentNodeId)))
            .Order(StringComparer.Ordinal)
            .ToArray();

        return new ForwardTraversal(
            reachable,
            terminals,
            boundary.Order(StringComparer.Ordinal).ToArray(),
            ambiguous);
    }

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildTopology(
        IReadOnlyList<TrackSegmentSnapshot> tracks)
    {
        var topology =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            foreach (var endpointNodeId in track.EndpointNodeIds)
            {
                AddEdge(topology, track.Id, endpointNodeId);
                AddEdge(topology, endpointNodeId, track.Id);
            }
        }

        return topology.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlySet<string>)pair.Value,
            StringComparer.Ordinal);
    }

    private static void AddEdge(
        IDictionary<string, HashSet<string>> topology,
        string fromNodeId,
        string toNodeId)
    {
        if (!topology.TryGetValue(fromNodeId, out var adjacent))
        {
            adjacent = new HashSet<string>(StringComparer.Ordinal);
            topology[fromNodeId] = adjacent;
        }

        adjacent.Add(toNodeId);
    }

    private static IReadOnlyList<RouteChangeObservation> CompareRoutes(
        OperationalSnapshot previous,
        OperationalSnapshot current)
    {
        var previousTargets = ReadRouteTargets(previous);
        var currentTargets = ReadRouteTargets(current);
        var changes = new List<RouteChangeObservation>();

        foreach (var controlNodeId in previousTargets.Keys
                     .Union(currentTargets.Keys, StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            var before = previousTargets.GetValueOrDefault(controlNodeId) ?? [];
            var after = currentTargets.GetValueOrDefault(controlNodeId) ?? [];
            if (before.SequenceEqual(after, StringComparer.Ordinal))
            {
                continue;
            }

            var kind = (before.Count, after.Count) switch
            {
                (0, > 0) => RouteChangeKind.Established,
                ( > 0, 0) => RouteChangeKind.Released,
                _ => RouteChangeKind.Retargeted,
            };
            changes.Add(
                new RouteChangeObservation(
                    kind,
                    controlNodeId,
                    before,
                    after,
                    ResolveDestination(previous, before),
                    ResolveDestination(current, after)));
        }

        return changes;
    }

    private static StationTrackLocation? ResolveDestination(
        OperationalSnapshot snapshot,
        IReadOnlyList<string> targetNodeIds)
    {
        foreach (var station in snapshot.Stations)
        {
            var platform = station.Platforms.FirstOrDefault(
                candidate => targetNodeIds.Contains(
                    candidate.TrackNodeId,
                    StringComparer.Ordinal));
            if (platform is not null)
            {
                return new StationTrackLocation(
                    station.Name,
                    platform.TrackNodeId,
                    platform.Number);
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>
        ReadRouteTargets(OperationalSnapshot snapshot) =>
        snapshot.RouteClearances
            .Where(clearance => clearance.ConnectedNodeIds.Count > 0)
            .ToDictionary(
                clearance => clearance.NodeId,
                clearance => (IReadOnlyList<string>)clearance.ConnectedNodeIds
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray(),
                StringComparer.Ordinal);

    private sealed record ForwardTraversal(
        IReadOnlySet<string> ReachableNodeIds,
        IReadOnlyList<string> TerminalNodeIds,
        IReadOnlyList<string> BoundaryNodeIds,
        bool Ambiguous);
}
