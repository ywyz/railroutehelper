using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RailRouteAssistantDesktop;

/// <summary>
/// Desktop-side boundary for the local assistant HTTP endpoint.  MainForm only
/// consumes the mapped snapshot and does not know the wire-format details.
/// Keeping this adapter small also lets the sessions/operations service replace
/// the endpoint without coupling WinForms controls to JSON names.
/// </summary>
public sealed class AssistantApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public AssistantApiClient(HttpClient http, string endpoint = "http://localhost:8787/data", bool ownsClient = true)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        Endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _ownsClient = ownsClient;
    }

    public HttpClient HttpClient => _http;
    public string Endpoint { get; }

    public async Task<AssistantSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(Endpoint, cancellationToken).ConfigureAwait(true);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(true);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(true);
        return AssistantSnapshotMapper.Map(document.RootElement);
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}

public sealed class AssistantSnapshot
{
    public bool GameReady { get; init; }
    public string GameTime { get; init; } = string.Empty;
    public double? GameTimeSeconds { get; init; }
    public int? ApiVersion { get; init; }
    public string PluginVersion { get; init; } = string.Empty;
    public List<AlertData> Alerts { get; init; } = new();
    public List<TrainData> Trains { get; init; } = new();
}

internal static class AssistantSnapshotMapper
{
    public static AssistantSnapshot Map(JsonElement root)
    {
        var snapshot = new AssistantSnapshot
        {
            GameReady = GetBool(root, "gameReady"),
            GameTime = GetString(root, "gameTime") ?? string.Empty,
            GameTimeSeconds = GetNullableDouble(root, "gameTimeSeconds") ?? ParseClock(GetString(root, "gameTime")),
            ApiVersion = GetNullableInt(root, "apiVersion"),
            PluginVersion = GetString(root, "pluginVersion") ?? string.Empty
        };

        if (TryArray(root, "alerts", out var alerts))
        {
            foreach (var element in alerts.EnumerateArray())
                snapshot.Alerts.Add(MapAlert(element));
        }

        if (TryArray(root, "trains", out var trains))
        {
            foreach (var element in trains.EnumerateArray())
                snapshot.Trains.Add(MapTrain(element));
        }

        return snapshot;
    }

    private static AlertData MapAlert(JsonElement element)
    {
        var alert = new AlertData
        {
            Id = GetString(element, "id") ?? GetString(element, "alertId") ?? string.Empty,
            Level = GetString(element, "level") ?? GetString(element, "severity") ?? "info",
            TrainName = GetString(element, "train") ?? GetString(element, "trainName") ?? string.Empty,
            Message = GetString(element, "message") ?? GetString(element, "summary") ?? string.Empty,
            Kind = GetString(element, "kind") ?? GetString(element, "type") ?? string.Empty,
            PrimaryTrainId = GetString(element, "primaryTrainId") ?? GetString(element, "primaryTrain") ?? string.Empty,
            StationName = GetString(element, "stationName") ?? GetString(element, "station") ?? string.Empty,
            PlatformNumber = GetNullableInt(element, "platformNumber") ?? GetNullableInt(element, "platform"),
            Status = GetString(element, "status") ?? "active",
            FirstSeen = GetString(element, "firstSeen") ?? GetString(element, "firstSeenAt") ?? string.Empty,
            LastSeen = GetString(element, "lastSeen") ?? GetString(element, "lastSeenAt") ?? string.Empty,
            Duration = GetString(element, "duration") ?? string.Empty,
            OccurrenceCount = GetInt(element, "occurrenceCount", GetInt(element, "count", 1)),
            Acknowledged = GetBool(element, "acknowledged"),
            MutedUntil = GetString(element, "mutedUntil") ?? string.Empty
        };
        if (string.IsNullOrWhiteSpace(alert.PrimaryTrainId)) alert.PrimaryTrainId = alert.TrainName;
        if (TryArray(element, "relatedTrainIds", out var related))
            alert.RelatedTrainIds.AddRange(related.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).Where(item => !string.IsNullOrWhiteSpace(item)));
        else if (TryArray(element, "relatedTrains", out var relatedTrains))
            alert.RelatedTrainIds.AddRange(relatedTrains.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).Where(item => !string.IsNullOrWhiteSpace(item)));
        if (TryArray(element, "routeTrackIds", out var tracks))
            alert.RouteTrackIds.AddRange(tracks.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null).Where(item => !string.IsNullOrWhiteSpace(item)));
        return alert;
    }

    private static TrainData MapTrain(JsonElement element)
    {
        // The original endpoint uses required fields for the core state.  The
        // adapter keeps sensible defaults for optional fields so newer plugins
        // can add/remove diagnostics without taking down the desktop window.
        var train = new TrainData
        {
            Id = GetString(element, "id") ?? string.Empty,
            Name = GetString(element, "name") ?? "?",
            Speed = GetInt(element, "speed"),
            TargetSpeed = GetFloat(element, "targetSpeed"),
            MaxSpeed = GetFloat(element, "maxSpeed"),
            Delay = GetDouble(element, "delay"),
            CanDepart = GetBool(element, "canDepart"),
            Finished = GetBool(element, "finished"),
            BrokenDown = GetBool(element, "brokenDown"),
            OnBoard = GetBool(element, "onBoard"),
            Waiting = GetBool(element, "waiting"),
            Lookahead = GetInt(element, "lookahead"),
            NeedsRoute = GetBool(element, "needsRoute"),
            HasRoute = GetBool(element, "hasRoute", GetBool(element, "hasValidRoute")),
            RouteTotal = GetInt(element, "routeTotal", GetInt(element, "routeTotalSteps")),
            RouteCurrent = GetInt(element, "routeCurrent", GetInt(element, "routeCur", GetInt(element, "routeCurrentStep"))),
            RouteRemaining = GetInt(element, "routeRemaining", GetInt(element, "routeRemain", GetInt(element, "routeRemainingSteps"))),
            HasSignal = GetBool(element, "hasSignal"),
            SignalState = GetString(element, "signalState") ?? string.Empty,
            Platform = GetInt(element, "platform"),
            NextStation = GetString(element, "nextStation") ?? string.Empty,
            NextStationNonStop = GetBool(element, "nextStationNonStop"),
            ActualVisitCount = GetInt(element, "actualVisitCount"),
            ScheduledVisitCount = GetInt(element, "scheduledVisitCount"),
            ScheduledVisitIndex = GetInt(element, "scheduledVisitIndex", -1),
            LastVisitStation = GetString(element, "lastVisitStation") ?? string.Empty,
            LastVisitPlatform = GetInt(element, "lastVisitPlatform"),
            LastVisitNonStop = GetBool(element, "lastVisitNonStop"),
            LastVisitStopMinutes = GetInt(element, "lastVisitStopMinutes"),
            LastVisitDeparted = GetBool(element, "lastVisitDeparted"),
            LastArrivalScheduleDeviationSec = GetNullableDouble(element, "lastArrivalScheduleDeviationSec"),
            LastDepartureScheduleDelaySec = GetNullableDouble(element, "lastDepartureScheduleDelaySec"),
            RequiresDirectionChange = GetBool(element, "requiresDirectionChange"),
            CurrentStation = GetString(element, "currentStation") ?? string.Empty,
            CurrentPlatform = GetInt(element, "currentPlatform"),
            CurrentStopMinutes = GetInt(element, "currentStopMinutes"),
            DepartureRemainingSec = GetNullableDouble(element, "departureRemainingSec"),
            CurrentDepartureScheduleDelaySec = GetNullableDouble(element, "currentDepartureScheduleDelaySec"),
            StopReasons = GetString(element, "stopReasons") ?? string.Empty,
            NextPrepareSec = GetNullableDouble(element, "nextPrepareSec"),
            NextArrivalSec = GetNullableDouble(element, "nextArrivalSec"),
            NotMovingSince = GetNullableDouble(element, "notMovingSince"),
            SignalAllocationState = GetInt(element, "signalAllocationState", -1),
            FrontAllocationState = GetInt(element, "frontAllocationState", -1),
            MapEntryTimeSec = GetNullableDouble(element, "mapEntryTimeSec"),
            MapExitTimeSec = GetNullableDouble(element, "mapExitTimeSec"),
            MapEntryStation = GetString(element, "mapEntryStation") ?? string.Empty,
            MapExitStation = GetString(element, "mapExitStation") ?? string.Empty,
            MapEntryPlatform = GetInt(element, "mapEntryPlatform"),
            MapExitPlatform = GetInt(element, "mapExitPlatform"),
            MapEntryNonStop = GetBool(element, "mapEntryNonStop"),
            MapExitNonStop = GetBool(element, "mapExitNonStop")
        };

        if (TryArray(element, "scheduledStops", out var stops))
        {
            foreach (var stop in stops.EnumerateArray())
            {
                train.ScheduledStops.Add(new ScheduledStopData
                {
                    Station = GetString(stop, "station") ?? string.Empty,
                    Platform = GetInt(stop, "platform"),
                    ArrivalTimeSec = GetNullableDouble(stop, "arrivalTimeSec"),
                    DepartureTimeSec = GetNullableDouble(stop, "departureTimeSec"),
                    StopMinutes = GetInt(stop, "stopMinutes"),
                    RelativeTimes = GetBool(stop, "relativeTimes"),
                    NonStop = GetBool(stop, "nonStop")
                });
            }
        }

        return train;
    }

    private static bool TryArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out value)
            && value.ValueKind == JsonValueKind.Array;
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool GetBool(JsonElement element, string name, bool fallback = false)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;
    }

    private static int GetInt(JsonElement element, string name, int fallback = 0)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            ? result
            : fallback;
    }

    private static float GetFloat(JsonElement element, string name, float fallback = 0)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetSingle(out var result)
            ? result
            : fallback;
    }

    private static double GetDouble(JsonElement element, string name, double fallback = 0)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result)
            ? result
            : fallback;
    }

    private static double? GetNullableDouble(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var result)
            ? result
            : null;
    }

    private static int? GetNullableInt(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var result)
            ? result
            : null;
    }

    private static double? ParseClock(string value)
    {
        return TimeSpan.TryParse(value, out var time) ? time.TotalSeconds : null;
    }
}
