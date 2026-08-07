using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using RailRouteHelper.AssistantSessions;

namespace RailRouteAssistantDesktop;

/// <summary>Translates the legacy local /data DTOs to the shared assistant-session
/// protocol.  Lifecycle and replay semantics stay in AssistantSessions; this
/// class only performs field conversion at the desktop boundary.</summary>
internal sealed class AssistantSessionAdapter
{
    private readonly Dictionary<string, RelativeObservation> _relativeObservations = new(StringComparer.OrdinalIgnoreCase);
    private double? _lastGameTimeSeconds;

    public AssistantFrame ToFrame(
        AssistantSnapshot snapshot,
        long sequence,
        DateTimeOffset capturedAtUtc,
        bool isConnected = true,
        bool isSuccessful = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.GameTimeSeconds.HasValue && _lastGameTimeSeconds.HasValue &&
            snapshot.GameTimeSeconds.Value + 300 < _lastGameTimeSeconds.Value)
            _relativeObservations.Clear();
        _lastGameTimeSeconds = snapshot.GameTimeSeconds;
        return new AssistantFrame(
            sequence,
            capturedAtUtc,
            isConnected,
            snapshot.Trains.Select(train => ToDefinition(train, snapshot, capturedAtUtc, isConnected && isSuccessful)).ToList(),
            snapshot.Alerts.Select(ToObservedAlert).ToList(),
            isSuccessful,
            snapshot.Trains.Select(ToState).ToList(),
            snapshot.GameTimeSeconds,
            snapshot.GameReady);
    }

    public IReadOnlyList<TrainData> FromFrame(AssistantFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var states = frame.TrainStates.ToDictionary(state => state.TrainId, StringComparer.OrdinalIgnoreCase);
        return frame.Trains.Select(definition => FromDefinition(definition, states.TryGetValue(definition.TrainId, out var state) ? state : null)).ToList();
    }

    public IReadOnlyList<AlertData> AlertsFromFrame(AssistantFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame.ObservedAlerts.Select(alert => new AlertData
        {
            Id = alert.Fingerprint,
            Level = alert.Severity.ToString().ToLowerInvariant(),
            TrainName = alert.SubjectId ?? string.Empty,
            Message = string.IsNullOrWhiteSpace(alert.Detail) ? alert.Title ?? alert.Code : alert.Detail,
            Status = "active"
        }).ToList();
    }

    private TrainDefinition ToDefinition(TrainData train, AssistantSnapshot snapshot, DateTimeOffset capturedAtUtc, bool captureObservation)
    {
        var state = ToState(train);
        var relativeObservations = CaptureRelativeObservations(train, state, snapshot, captureObservation);
        var stops = (train.ScheduledStops ?? new List<ScheduledStopData>())
            .Select((stop, index) => ToStop(stop, index, train, state, snapshot, capturedAtUtc, relativeObservations))
            .ToList();
        return new TrainDefinition(
            string.IsNullOrWhiteSpace(train.Id) ? train.Name ?? "?" : train.Id,
            stops,
            serviceName: train.Name,
            origin: stops.FirstOrDefault()?.StationName,
            destination: stops.LastOrDefault()?.StationName);
    }

    private static TrainStop ToStop(ScheduledStopData stop, int index, TrainData train, AssistantTrainState state, AssistantSnapshot snapshot, DateTimeOffset capturedAtUtc, IReadOnlyDictionary<int, RelativeObservation> relativeObservations)
    {
        bool relative = stop.RelativeTimes;
        bool visited = state.VisitIndex >= 0 && index <= state.VisitIndex;
        DateTimeOffset? plannedArrival = relative ? null : ToUtc(stop.ArrivalTimeSec);
        DateTimeOffset? plannedDeparture = relative ? null : ToUtc(stop.DepartureTimeSec);
        DateTimeOffset? actualArrival = null;
        DateTimeOffset? actualDeparture = null;
        if (relative && relativeObservations.TryGetValue(index, out var relativeObservation))
        {
            actualArrival = ToUtc(relativeObservation.ArrivalSeconds);
            actualDeparture = ToUtc(relativeObservation.DepartureSeconds);
        }
        if (visited && index == state.VisitIndex)
        {
            if (plannedArrival.HasValue && state.LastArrivalScheduleDeviationSeconds.HasValue)
                actualArrival = plannedArrival.Value.AddSeconds(state.LastArrivalScheduleDeviationSeconds.Value);
            if (plannedDeparture.HasValue && state.LastDepartureScheduleDelaySeconds.HasValue)
                actualDeparture = plannedDeparture.Value.AddSeconds(state.LastDepartureScheduleDelaySeconds.Value);
        }
        DateTimeOffset? predictedArrival = null;
        if (!relative && !visited && index == Math.Max(0, state.VisitIndex + 1) && state.NextArrivalSeconds.HasValue && snapshot.GameTimeSeconds.HasValue)
            predictedArrival = ToUtc(snapshot.GameTimeSeconds.Value + state.NextArrivalSeconds.Value);
        return new TrainStop(
            stationId: stop.Station ?? $"station-{index}",
            stationName: stop.Station,
            sequence: index,
            plannedArrivalUtc: plannedArrival,
            plannedDepartureUtc: plannedDeparture,
            actualArrivalUtc: actualArrival,
            actualDepartureUtc: actualDeparture,
            predictedArrivalUtc: predictedArrival,
            relativeTimes: relative,
            plannedArrivalOffset: relative ? ToOffset(stop.ArrivalTimeSec) : null,
            plannedDepartureOffset: relative ? ToOffset(stop.DepartureTimeSec) : null);
    }

    private static AssistantTrainState ToState(TrainData train)
        => new(string.IsNullOrWhiteSpace(train.Id) ? train.Name ?? "?" : train.Id)
        {
            ReportingNumber = train.Name,
            Speed = train.Speed,
            TargetSpeed = train.TargetSpeed,
            MaxSpeed = train.MaxSpeed,
            DelaySeconds = train.Delay,
            OnBoard = train.OnBoard,
            Waiting = train.Waiting,
            Finished = train.Finished,
            BrokenDown = train.BrokenDown,
            CanDepart = train.CanDepart,
            LookaheadCount = train.Lookahead,
            HasValidRoute = train.HasRoute,
            NeedsRouteAhead = train.NeedsRoute,
            HasSignal = train.HasSignal,
            SignalState = train.SignalState,
            SignalAllocationState = train.SignalAllocationState,
            FrontAllocationState = train.FrontAllocationState,
            RouteTotalSteps = train.RouteTotal,
            RouteCurrentStep = train.RouteCurrent,
            RouteRemainingSteps = train.RouteRemaining,
            CurrentStation = train.CurrentStation,
            CurrentPlatform = train.CurrentPlatform > 0 ? train.CurrentPlatform : null,
            NextStation = train.NextStation,
            NextPlatform = train.Platform > 0 ? train.Platform : null,
            VisitIndex = train.ScheduledVisitIndex,
            VisitCount = train.ActualVisitCount,
            ScheduledVisitCount = train.ScheduledVisitCount,
            LastVisitStation = train.LastVisitStation,
            LastVisitPlatform = train.LastVisitPlatform > 0 ? train.LastVisitPlatform : null,
            LastVisitDeparted = train.LastVisitDeparted,
            LastVisitNonStop = train.LastVisitNonStop,
            LastVisitStopMinutes = train.LastVisitStopMinutes,
            LastArrivalScheduleDeviationSeconds = train.LastArrivalScheduleDeviationSec,
            LastDepartureScheduleDelaySeconds = train.LastDepartureScheduleDelaySec,
            RequiresDirectionChange = train.RequiresDirectionChange,
            NextArrivalSeconds = train.NextArrivalSec,
            DepartureRemainingSeconds = train.DepartureRemainingSec,
            CurrentStopMinutes = train.CurrentStopMinutes,
            CurrentDepartureScheduleDelaySeconds = train.CurrentDepartureScheduleDelaySec,
            NotMovingSinceSeconds = train.NotMovingSince,
            NextPrepareSeconds = train.NextPrepareSec,
            StopReasons = train.StopReasons,
            MapEntryGameTimeSeconds = train.MapEntryTimeSec,
            MapExitGameTimeSeconds = train.MapExitTimeSec,
            MapEntryStation = train.MapEntryStation,
            MapExitStation = train.MapExitStation,
            MapEntryPlatform = train.MapEntryPlatform > 0 ? train.MapEntryPlatform : null,
            MapExitPlatform = train.MapExitPlatform > 0 ? train.MapExitPlatform : null,
            MapEntryNonStop = train.MapEntryNonStop,
            MapExitNonStop = train.MapExitNonStop,
            NextStationNonStop = train.NextStationNonStop
        };

    private IReadOnlyDictionary<int, RelativeObservation> CaptureRelativeObservations(
        TrainData train,
        AssistantTrainState state,
        AssistantSnapshot snapshot,
        bool captureObservation)
    {
        var result = new Dictionary<int, RelativeObservation>();
        if (!captureObservation || !snapshot.GameTimeSeconds.HasValue || state.VisitIndex < 0)
            return result;
        int index = state.VisitIndex;
        if (index >= train.ScheduledStops.Count || !train.ScheduledStops[index].RelativeTimes)
            return result;
        string trainId = string.IsNullOrWhiteSpace(train.Id) ? train.Name ?? "?" : train.Id;
        string key = $"{trainId}:{index}";
        if (!_relativeObservations.TryGetValue(key, out var observation))
            observation = new RelativeObservation(snapshot.GameTimeSeconds, null);
        if (state.LastVisitDeparted && !observation.DepartureSeconds.HasValue)
            observation = observation with { DepartureSeconds = snapshot.GameTimeSeconds };
        _relativeObservations[key] = observation;
        foreach (var pair in _relativeObservations)
        {
            if (!pair.Key.StartsWith(trainId + ":", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(pair.Key[(trainId.Length + 1)..], out var stopIndex))
                result[stopIndex] = pair.Value;
        }
        return result;
    }

    private static ObservedAlert ToObservedAlert(AlertData alert)
    {
        var severity = (alert.Level ?? string.Empty).ToLowerInvariant() switch
        {
            "critical" or "error" => AlertSeverity.Critical,
            "warning" or "warn" => AlertSeverity.Warning,
            _ => AlertSeverity.Info,
        };
        var primary = string.IsNullOrWhiteSpace(alert.PrimaryTrainId) ? alert.TrainName : alert.PrimaryTrainId;
        var code = !string.IsNullOrWhiteSpace(alert.Kind)
            ? alert.Kind
            : string.IsNullOrWhiteSpace(alert.Id)
                ? "legacy-message:" + LegacyMessageHash(primary, alert.Message)
                : "legacy-message:" + LegacyMessageHash(primary, alert.Message);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["kind"] = alert.Kind ?? string.Empty,
            ["primaryTrainId"] = primary ?? string.Empty,
            ["relatedTrainIds"] = string.Join(",", alert.RelatedTrainIds ?? new List<string>()),
            ["stationName"] = alert.StationName ?? string.Empty,
            ["platformNumber"] = alert.PlatformNumber?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ["routeTrackIds"] = string.Join(",", alert.RouteTrackIds ?? new List<string>())
        };
        return new ObservedAlert(
            code,
            severity,
            subjectId: primary,
            title: alert.Message,
            detail: alert.Message,
            stationId: alert.StationName,
            attributes: attributes,
            subjectDisplayName: alert.TrainName);
    }

    private static TrainData FromDefinition(TrainDefinition definition, AssistantTrainState state)
    {
        var train = new TrainData
        {
            Id = definition.TrainId,
            Name = state?.ReportingNumber ?? definition.ServiceName ?? definition.TrainId,
            Speed = (int)Math.Round(state?.Speed ?? 0),
            TargetSpeed = (float)(state?.TargetSpeed ?? 0),
            MaxSpeed = (float)(state?.MaxSpeed ?? 0),
            Delay = state?.DelaySeconds ?? 0,
            OnBoard = state?.OnBoard ?? true,
            Waiting = state?.Waiting ?? false,
            Finished = state?.Finished ?? false,
            BrokenDown = state?.BrokenDown ?? false,
            CanDepart = state?.CanDepart ?? false,
            Lookahead = state?.LookaheadCount ?? 0,
            HasRoute = state?.HasValidRoute ?? false,
            NeedsRoute = state?.NeedsRouteAhead ?? false,
            HasSignal = state?.HasSignal ?? false,
            SignalState = state?.SignalState ?? string.Empty,
            SignalAllocationState = state?.SignalAllocationState ?? -1,
            FrontAllocationState = state?.FrontAllocationState ?? -1,
            RouteTotal = state?.RouteTotalSteps ?? 0,
            RouteCurrent = state?.RouteCurrentStep ?? 0,
            RouteRemaining = state?.RouteRemainingSteps ?? 0,
            CurrentStation = state?.CurrentStation ?? string.Empty,
            CurrentPlatform = state?.CurrentPlatform ?? 0,
            NextStation = state?.NextStation ?? string.Empty,
            Platform = state?.NextPlatform ?? 0,
            ScheduledVisitIndex = state?.VisitIndex ?? -1,
            ActualVisitCount = state?.VisitCount ?? 0,
            ScheduledVisitCount = state?.ScheduledVisitCount ?? 0,
            LastVisitStation = state?.LastVisitStation ?? string.Empty,
            LastVisitPlatform = state?.LastVisitPlatform ?? 0,
            LastVisitDeparted = state?.LastVisitDeparted ?? false,
            LastVisitNonStop = state?.LastVisitNonStop ?? false,
            LastVisitStopMinutes = state?.LastVisitStopMinutes ?? 0,
            LastArrivalScheduleDeviationSec = state?.LastArrivalScheduleDeviationSeconds,
            LastDepartureScheduleDelaySec = state?.LastDepartureScheduleDelaySeconds,
            RequiresDirectionChange = state?.RequiresDirectionChange ?? false,
            NextArrivalSec = state?.NextArrivalSeconds,
            DepartureRemainingSec = state?.DepartureRemainingSeconds,
            CurrentStopMinutes = state?.CurrentStopMinutes ?? 0,
            CurrentDepartureScheduleDelaySec = state?.CurrentDepartureScheduleDelaySeconds,
            NotMovingSince = state?.NotMovingSinceSeconds,
            NextPrepareSec = state?.NextPrepareSeconds,
            StopReasons = state?.StopReasons ?? string.Empty,
            MapEntryTimeSec = state?.MapEntryGameTimeSeconds,
            MapExitTimeSec = state?.MapExitGameTimeSeconds,
            MapEntryStation = state?.MapEntryStation ?? string.Empty,
            MapExitStation = state?.MapExitStation ?? string.Empty,
            MapEntryPlatform = state?.MapEntryPlatform ?? 0,
            MapExitPlatform = state?.MapExitPlatform ?? 0,
            MapEntryNonStop = state?.MapEntryNonStop ?? false,
            MapExitNonStop = state?.MapExitNonStop ?? false,
            NextStationNonStop = state?.NextStationNonStop ?? false,
            ScheduledStops = definition.Stops.Select(FromStop).ToList()
        };
        train.ScheduledVisitCount = train.ScheduledStops.Count;
        return train;
    }

    private static ScheduledStopData FromStop(TrainStop stop)
    {
        return new ScheduledStopData
        {
            Station = stop.StationName,
            ArrivalTimeSec = stop.RelativeTimes ? stop.PlannedArrivalOffset?.TotalSeconds : ToSeconds(stop.PlannedArrivalUtc),
            DepartureTimeSec = stop.RelativeTimes ? stop.PlannedDepartureOffset?.TotalSeconds : ToSeconds(stop.PlannedDepartureUtc),
            RelativeTimes = stop.RelativeTimes,
            NonStop = stop.PlannedArrivalUtc == stop.PlannedDepartureUtc && !stop.RelativeTimes
        };
    }

    private static DateTimeOffset? ToUtc(double? seconds)
    {
        if (!seconds.HasValue) return null;
        return DateTimeOffset.UnixEpoch.AddSeconds(seconds.Value);
    }

    private static TimeSpan? ToOffset(double? seconds)
        => seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : null;

    private static double? ToSeconds(DateTimeOffset? value)
        => value.HasValue ? (value.Value - DateTimeOffset.UnixEpoch).TotalSeconds : null;

    private static string LegacyMessageHash(string train, string message)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{train ?? string.Empty}\u001f{message ?? string.Empty}"));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private sealed record RelativeObservation(double? ArrivalSeconds, double? DepartureSeconds);
}
