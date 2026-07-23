using RailRouteHelper.Core;
using RailRouteHelper.SaveFiles;

namespace RailRouteHelper.SaveSchema;

internal sealed class SaveSchemaMapperV2_3 : ISaveSchemaMapper
{
    private const string SchemaId = "rail-route-save/2.3-observed/v1";

    private static readonly IReadOnlySet<GameVersion> Versions =
        new HashSet<GameVersion>
        {
            new(2, 3, 17),
            new(2, 3, 18),
            new(2, 3, 22),
            new(2, 3, 23),
            new(2, 3, 24),
        };

    public IReadOnlySet<GameVersion> SupportedGameVersions => Versions;

    public SaveMappingResult Map(SaveDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = SaveTreeReader.RequireMap(document.Root, "$");
        var rawVersion = SaveTreeReader.RequireString(
            SaveTreeReader.Require(root, "gameVersion", "$.gameVersion"),
            "$.gameVersion");
        if (!GameVersion.TryParse(rawVersion, out var gameVersion)
            || !Versions.Contains(gameVersion))
        {
            throw new UnsupportedGameVersionException(rawVersion);
        }

        var (stations, trackStations, stationsWithoutPosition) =
            MapStations(root);
        var (tracks, routeClearances, unknownAllocationCodes) =
            MapNetwork(root, trackStations);
        var trains = MapTrains(root);
        var gameTimeTicks = MapGameTime(root);

        var diagnostics = new List<SaveMappingDiagnostic>();
        if (routeClearances.Any(
                clearance => clearance.Origin is RouteClearanceOrigin.Unknown))
        {
            diagnostics.Add(
                new SaveMappingDiagnostic(
                    "route-clearance-origin-unknown",
                    SaveMappingDiagnosticSeverity.Information,
                    "Some allocated nodes have no explicit origin marker; "
                    + "manual and sensor-triggered automatic route clearance "
                    + "remain indistinguishable for those nodes."));
        }

        if (stationsWithoutPosition > 0)
        {
            diagnostics.Add(
                new SaveMappingDiagnostic(
                    "station-position-unset",
                    SaveMappingDiagnosticSeverity.Information,
                    $"{stationsWithoutPosition} station(s) have no grid "
                    + "position; their identity and platforms were retained."));
        }

        if (unknownAllocationCodes.Count > 0)
        {
            diagnostics.Add(
                new SaveMappingDiagnostic(
                    "unknown-allocation-code",
                    SaveMappingDiagnosticSeverity.Warning,
                    "Unrecognized allocation code(s) were retained without "
                    + $"a semantic label: {string.Join(", ", unknownAllocationCodes)}."));
        }

        var snapshot = new OperationalSnapshot(
            gameVersion,
            document.LastWriteTimeUtc,
            gameTimeTicks,
            trains,
            tracks,
            stations,
            routeClearances);
        return new SaveMappingResult(SchemaId, snapshot, diagnostics);
    }

    private static (
        IReadOnlyList<StationSnapshot> Stations,
        IReadOnlyDictionary<string, TrackStationReference> TrackStations,
        int StationsWithoutPosition)
        MapStations(SaveMap root)
    {
        const string repositoryPath = "$.savedStationRepository";
        var repository = SaveTreeReader.RequireMap(
            SaveTreeReader.Require(
                root,
                "savedStationRepository",
                repositoryPath),
            repositoryPath);
        var savedStations = SaveTreeReader.RequireArray(
            SaveTreeReader.Require(
                repository,
                "savedStations",
                $"{repositoryPath}.savedStations"),
            $"{repositoryPath}.savedStations");

        var stations = new List<StationSnapshot>(savedStations.Items.Count);
        var trackStations =
            new Dictionary<string, TrackStationReference>(StringComparer.Ordinal);
        var stationsWithoutPosition = 0;
        for (var index = 0; index < savedStations.Items.Count; index++)
        {
            var path = $"{repositoryPath}.savedStations[{index}]";
            var savedStation = SaveTreeReader.RequireMap(
                savedStations.Items[index],
                path);
            var data = SaveTreeReader.RequireMap(
                SaveTreeReader.Require(
                    savedStation,
                    "stationData",
                    $"{path}.stationData"),
                $"{path}.stationData");
            var stationId = SaveTreeReader.RequireString(
                SaveTreeReader.Require(data, "uuid", $"{path}.stationData.uuid"),
                $"{path}.stationData.uuid");
            var name = SaveTreeReader.RequireString(
                SaveTreeReader.Require(data, "name", $"{path}.stationData.name"),
                $"{path}.stationData.name");
            var gridPositionValue = SaveTreeReader.Require(
                data,
                "gridPoint",
                $"{path}.stationData.gridPoint");
            GridPoint? gridPosition;
            if (gridPositionValue is SaveNil)
            {
                gridPosition = null;
                stationsWithoutPosition++;
            }
            else
            {
                gridPosition = ReadGridPoint(
                    gridPositionValue,
                    $"{path}.stationData.gridPoint");
            }
            var savedPlatforms = SaveTreeReader.RequireArray(
                SaveTreeReader.Require(
                    data,
                    "platformsData",
                    $"{path}.stationData.platformsData"),
                $"{path}.stationData.platformsData");

            var platforms =
                new List<PlatformSnapshot>(savedPlatforms.Items.Count);
            for (var platformIndex = 0;
                 platformIndex < savedPlatforms.Items.Count;
                 platformIndex++)
            {
                var platformPath =
                    $"{path}.stationData.platformsData[{platformIndex}]";
                var savedPlatform = SaveTreeReader.RequireMap(
                    savedPlatforms.Items[platformIndex],
                    platformPath);
                var number = SaveTreeReader.RequireInt32(
                    SaveTreeReader.Require(
                        savedPlatform,
                        "platformNum",
                        $"{platformPath}.platformNum"),
                    $"{platformPath}.platformNum");
                var trackNodeId = SaveTreeReader.RequireString(
                    SaveTreeReader.Require(
                        savedPlatform,
                        "trackRef",
                        $"{platformPath}.trackRef"),
                    $"{platformPath}.trackRef");
                platforms.Add(new PlatformSnapshot(number, trackNodeId));
                trackStations.TryAdd(
                    trackNodeId,
                    new TrackStationReference(stationId, number));
            }

            stations.Add(
                new StationSnapshot(stationId, name, gridPosition, platforms));
        }

        return (stations, trackStations, stationsWithoutPosition);
    }

    private static (
        IReadOnlyList<TrackSegmentSnapshot> Tracks,
        IReadOnlyList<RouteClearanceObservation> RouteClearances,
        IReadOnlySet<int> UnknownAllocationCodes)
        MapNetwork(
            SaveMap root,
            IReadOnlyDictionary<string, TrackStationReference> trackStations)
    {
        const string repositoryPath = "$.savedNodeRepository";
        var repository = SaveTreeReader.RequireMap(
            SaveTreeReader.Require(root, "savedNodeRepository", repositoryPath),
            repositoryPath);
        var nodes = SaveTreeReader.RequireArray(
            SaveTreeReader.Require(
                repository,
                "nodes",
                $"{repositoryPath}.nodes"),
            $"{repositoryPath}.nodes");

        var tracks = new List<TrackSegmentSnapshot>();
        var clearances = new List<RouteClearanceObservation>();
        var unknownAllocationCodes = new SortedSet<int>();

        for (var index = 0; index < nodes.Items.Count; index++)
        {
            var path = $"{repositoryPath}.nodes[{index}]";
            var node = SaveTreeReader.RequireMap(nodes.Items[index], path);
            var id = SaveTreeReader.RequireString(
                SaveTreeReader.Require(node, "Name", $"{path}.Name"),
                $"{path}.Name");
            var friendlyName = SaveTreeReader.RequireString(
                SaveTreeReader.Require(
                    node,
                    "FriendlyName",
                    $"{path}.FriendlyName"),
                $"{path}.FriendlyName");
            var internalStatePath = $"{path}.InternalState";
            var internalState = SaveTreeReader.RequireUnionMap(
                SaveTreeReader.Require(
                    node,
                    "InternalState",
                    internalStatePath),
                internalStatePath);
            var active = SaveTreeReader.RequireBoolean(
                SaveTreeReader.Require(
                    internalState,
                    "active",
                    $"{internalStatePath}[1].active"),
                $"{internalStatePath}[1].active");
            var allocationCode = SaveTreeReader.RequireInt32(
                SaveTreeReader.Require(
                    internalState,
                    "allocationState",
                    $"{internalStatePath}[1].allocationState"),
                $"{internalStatePath}[1].allocationState");

            if (active && allocationCode != 0)
            {
                var interpretation = allocationCode switch
                {
                    1 => RouteClearanceInterpretation.Allocated,
                    2 => RouteClearanceInterpretation.TrainOccupied,
                    _ => RouteClearanceInterpretation.UnknownAllocated,
                };
                if (interpretation is RouteClearanceInterpretation.UnknownAllocated)
                {
                    unknownAllocationCodes.Add(allocationCode);
                }

                clearances.Add(
                    new RouteClearanceObservation(
                        id,
                        friendlyName,
                        InferNodeKind(id),
                        allocationCode,
                        ReadConnectedNodeIds(
                            internalState,
                            $"{internalStatePath}[1]"),
                        interpretation,
                        ReadRouteClearanceOrigin(
                            internalState,
                            $"{internalStatePath}[1]")));
            }

            if (!active
                || !id.StartsWith("Node:Track:", StringComparison.Ordinal))
            {
                continue;
            }

            var modelPath = $"{path}.modelObjectData";
            var model = SaveTreeReader.RequireUnionMap(
                SaveTreeReader.Require(node, "modelObjectData", modelPath),
                modelPath);
            var savedEndpoints = SaveTreeReader.RequireArray(
                SaveTreeReader.Require(
                    model,
                    "endPoints",
                    $"{modelPath}[1].endPoints"),
                $"{modelPath}[1].endPoints");
            var endpoints = new List<string>(savedEndpoints.Items.Count);
            for (var endpointIndex = 0;
                 endpointIndex < savedEndpoints.Items.Count;
                 endpointIndex++)
            {
                endpoints.Add(
                    SaveTreeReader.RequireDirectReference(
                        savedEndpoints.Items[endpointIndex],
                        $"{modelPath}[1].endPoints[{endpointIndex}]"));
            }

            var savedGridPoints = SaveTreeReader.RequireArray(
                SaveTreeReader.Require(
                    model,
                    "endPointGridPoints",
                    $"{modelPath}[1].endPointGridPoints"),
                $"{modelPath}[1].endPointGridPoints");
            var gridPoints = new List<GridPoint>(savedGridPoints.Items.Count);
            for (var pointIndex = 0;
                 pointIndex < savedGridPoints.Items.Count;
                 pointIndex++)
            {
                gridPoints.Add(
                    ReadGridPoint(
                        savedGridPoints.Items[pointIndex],
                        $"{modelPath}[1].endPointGridPoints[{pointIndex}]"));
            }

            trackStations.TryGetValue(id, out var station);
            tracks.Add(
                new TrackSegmentSnapshot(
                    id,
                    friendlyName,
                    endpoints,
                    gridPoints,
                    station?.StationId,
                    station?.PlatformNumber,
                    allocationCode));
        }

        return (tracks, clearances, unknownAllocationCodes);
    }

    private static IReadOnlyList<TrainSnapshot> MapTrains(SaveMap root)
    {
        const string repositoryPath = "$.savedTrainRepository";
        var repository = SaveTreeReader.RequireMap(
            SaveTreeReader.Require(root, "savedTrainRepository", repositoryPath),
            repositoryPath);
        var savedTrains = SaveTreeReader.RequireArray(
            SaveTreeReader.Require(
                repository,
                "savedTrains",
                $"{repositoryPath}.savedTrains"),
            $"{repositoryPath}.savedTrains");
        var trains = new List<TrainSnapshot>();

        for (var index = 0; index < savedTrains.Items.Count; index++)
        {
            var path = $"{repositoryPath}.savedTrains[{index}]";
            var savedTrain = SaveTreeReader.RequireMap(
                savedTrains.Items[index],
                path);
            var disposed = SaveTreeReader.RequireBoolean(
                SaveTreeReader.Require(
                    savedTrain,
                    "disposed",
                    $"{path}.disposed"),
                $"{path}.disposed");
            if (disposed)
            {
                continue;
            }

            var initialized = SaveTreeReader.RequireBoolean(
                SaveTreeReader.Require(
                    savedTrain,
                    "initialized",
                    $"{path}.initialized"),
                $"{path}.initialized");
            if (!initialized)
            {
                continue;
            }

            var id = SaveTreeReader.RequireString(
                SaveTreeReader.Require(savedTrain, "uuid", $"{path}.uuid"),
                $"{path}.uuid");
            var reportingNumber = SaveTreeReader.RequireString(
                SaveTreeReader.Require(
                    savedTrain,
                    "reportingNumber",
                    $"{path}.reportingNumber"),
                $"{path}.reportingNumber");
            var occupied = ReadUnionReferences(
                SaveTreeReader.Require(
                    savedTrain,
                    "occupiedNodes",
                    $"{path}.occupiedNodes"),
                $"{path}.occupiedNodes");
            var headingToward = SaveTreeReader.OptionalUnionReference(
                SaveTreeReader.Require(
                    savedTrain,
                    "headsTowards",
                    $"{path}.headsTowards"),
                $"{path}.headsTowards");
            var notMovingSince = SaveTreeReader.OptionalInt64(
                SaveTreeReader.Require(
                    savedTrain,
                    "notMovingSince",
                    $"{path}.notMovingSince"),
                $"{path}.notMovingSince");
            var stopReasons = ReadIntegerArray(
                SaveTreeReader.Require(
                    savedTrain,
                    "stopReasons",
                    $"{path}.stopReasons"),
                $"{path}.stopReasons");
            var scheduledStops = ReadScheduledStops(
                SaveTreeReader.Require(
                    savedTrain,
                    "scheduledVisits",
                    $"{path}.scheduledVisits"),
                $"{path}.scheduledVisits");

            trains.Add(
                new TrainSnapshot(
                    id,
                    reportingNumber,
                    SaveTreeReader.RequireNumber(
                        SaveTreeReader.Require(
                            savedTrain,
                            "currentSpeed",
                            $"{path}.currentSpeed"),
                        $"{path}.currentSpeed"),
                    SaveTreeReader.RequireNumber(
                        SaveTreeReader.Require(
                            savedTrain,
                            "targetSpeed",
                            $"{path}.targetSpeed"),
                        $"{path}.targetSpeed"),
                    occupied,
                    headingToward,
                    notMovingSince,
                    SaveTreeReader.RequireUnsignedInteger(
                        SaveTreeReader.Require(
                            savedTrain,
                            "currentStopIndex",
                            $"{path}.currentStopIndex"),
                        $"{path}.currentStopIndex"),
                    stopReasons,
                    scheduledStops));
        }

        return trains;
    }

    private static IReadOnlyList<ScheduledStopSnapshot> ReadScheduledStops(
        SaveValue value,
        string path)
    {
        var savedStops = SaveTreeReader.RequireArray(value, path);
        var stops = new List<ScheduledStopSnapshot>(savedStops.Items.Count);
        for (var index = 0; index < savedStops.Items.Count; index++)
        {
            var stopPath = $"{path}[{index}]";
            var savedStop = SaveTreeReader.RequireMap(
                savedStops.Items[index],
                stopPath);
            var stationReferencePath = $"{stopPath}.stationReference";
            var stationReference = SaveTreeReader.RequireMap(
                SaveTreeReader.Require(
                    savedStop,
                    "stationReference",
                    stationReferencePath),
                stationReferencePath);
            var stationName = SaveTreeReader.RequireString(
                SaveTreeReader.Require(
                    stationReference,
                    "name",
                    $"{stationReferencePath}.name"),
                $"{stationReferencePath}.name");
            var trackNodeId = SaveTreeReader.OptionalUnionReference(
                SaveTreeReader.Require(
                    savedStop,
                    "track",
                    $"{stopPath}.track"),
                $"{stopPath}.track");

            stops.Add(
                new ScheduledStopSnapshot(
                    stationName,
                    trackNodeId,
                    SaveTreeReader.RequireUnsignedInteger(
                        SaveTreeReader.Require(
                            savedStop,
                            "from",
                            $"{stopPath}.from"),
                        $"{stopPath}.from"),
                    SaveTreeReader.RequireUnsignedInteger(
                        SaveTreeReader.Require(
                            savedStop,
                            "to",
                            $"{stopPath}.to"),
                        $"{stopPath}.to"),
                    SaveTreeReader.RequireBoolean(
                        SaveTreeReader.Require(
                            savedStop,
                            "departed",
                            $"{stopPath}.departed"),
                        $"{stopPath}.departed"),
                    SaveTreeReader.RequireBoolean(
                        SaveTreeReader.Require(
                            savedStop,
                            "exited",
                            $"{stopPath}.exited"),
                        $"{stopPath}.exited"),
                    SaveTreeReader.RequireBoolean(
                        SaveTreeReader.Require(
                            savedStop,
                            "terminus",
                            $"{stopPath}.terminus"),
                        $"{stopPath}.terminus")));
        }

        return stops;
    }

    private static IReadOnlyList<string> ReadUnionReferences(
        SaveValue value,
        string path)
    {
        var savedReferences = SaveTreeReader.RequireArray(value, path);
        var references = new List<string>(savedReferences.Items.Count);
        for (var index = 0; index < savedReferences.Items.Count; index++)
        {
            var referencePath = $"{path}[{index}]";
            var reference = SaveTreeReader.OptionalUnionReference(
                savedReferences.Items[index],
                referencePath);
            if (reference is null)
            {
                throw new InvalidSaveSchemaException(
                    referencePath,
                    "a non-null node reference");
            }

            references.Add(reference);
        }

        return references;
    }

    private static IReadOnlyList<int> ReadIntegerArray(
        SaveValue value,
        string path)
    {
        var savedIntegers = SaveTreeReader.RequireArray(value, path);
        var integers = new List<int>(savedIntegers.Items.Count);
        for (var index = 0; index < savedIntegers.Items.Count; index++)
        {
            integers.Add(
                SaveTreeReader.RequireInt32(
                    savedIntegers.Items[index],
                    $"{path}[{index}]"));
        }

        return integers;
    }

    private static IReadOnlyList<string> ReadConnectedNodeIds(
        SaveMap internalState,
        string path)
    {
        var value = SaveTreeReader.Optional(internalState, "Connected");
        if (value is null or SaveNil)
        {
            return [];
        }

        var savedConnections = SaveTreeReader.RequireArray(
            value,
            $"{path}.Connected");
        var connections = new List<string>(savedConnections.Items.Count);
        for (var index = 0; index < savedConnections.Items.Count; index++)
        {
            if (savedConnections.Items[index] is SaveNil)
            {
                continue;
            }

            connections.Add(
                SaveTreeReader.RequireString(
                    savedConnections.Items[index],
                    $"{path}.Connected[{index}]"));
        }

        return connections;
    }

    private static RouteClearanceOrigin ReadRouteClearanceOrigin(
        SaveMap internalState,
        string path)
    {
        var value = SaveTreeReader.Optional(
            internalState,
            "PerpetualAutoRoute");
        if (value is null)
        {
            return RouteClearanceOrigin.Unknown;
        }

        return SaveTreeReader.RequireBoolean(
            value,
            $"{path}.PerpetualAutoRoute")
            ? RouteClearanceOrigin.Automatic
            : RouteClearanceOrigin.Unknown;
    }

    private static ulong? MapGameTime(SaveMap root)
    {
        const string path = "$.savedTimeController";
        var controller = SaveTreeReader.RequireMap(
            SaveTreeReader.Require(root, "savedTimeController", path),
            path);
        var value = SaveTreeReader.Require(
            controller,
            "currentTimeOfDay",
            $"{path}.currentTimeOfDay");
        return SaveTreeReader.OptionalUnsignedInteger(
            value,
            $"{path}.currentTimeOfDay");
    }

    private static GridPoint ReadGridPoint(SaveValue value, string path)
    {
        var coordinates = SaveTreeReader.RequireArray(value, path);
        if (coordinates.Items.Count != 2)
        {
            throw new InvalidSaveSchemaException(
                path,
                "a two-coordinate array");
        }

        return new GridPoint(
            SaveTreeReader.RequireNumber(coordinates.Items[0], $"{path}[0]"),
            SaveTreeReader.RequireNumber(coordinates.Items[1], $"{path}[1]"));
    }

    private static NetworkNodeKind InferNodeKind(string id)
    {
        if (id.StartsWith("Node:Track:", StringComparison.Ordinal))
        {
            return NetworkNodeKind.Track;
        }

        if (id.StartsWith("Node:Semaphore:", StringComparison.Ordinal))
        {
            return NetworkNodeKind.Signal;
        }

        if (id.StartsWith("Node:Switch:", StringComparison.Ordinal))
        {
            return NetworkNodeKind.Switch;
        }

        return id.StartsWith("Node:AutoBlock:", StringComparison.Ordinal)
            ? NetworkNodeKind.AutoBlock
            : NetworkNodeKind.Other;
    }

    private sealed record TrackStationReference(
        string StationId,
        int PlatformNumber);
}
