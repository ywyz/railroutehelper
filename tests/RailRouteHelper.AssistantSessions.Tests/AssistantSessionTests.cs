using System.Text;
using RailRouteHelper.AssistantSessions;
using RailRouteHelper.Protocol;

namespace RailRouteHelper.AssistantSessions.Tests;

public sealed class AssistantSessionTests
{
    [Fact]
    public void Versioned_messages_round_trip()
    {
        var start = new SessionStart("s-1", DateTimeOffset.UnixEpoch, "test");
        var line = RealtimeProtocolCodec.EncodeLine(
            AssistantSessionProtocol.CreateSessionStartEnvelope(0, DateTimeOffset.UnixEpoch, start));
        var decoded = AssistantSessionProtocol.DecodeSessionStart(RealtimeProtocolCodec.DecodeLine(line));

        Assert.Equal(start.SessionId, decoded.SessionId);
        Assert.Equal(1, decoded.PayloadVersion);
        Assert.Contains("assistant-session-start", Encoding.UTF8.GetString(line));
    }

    [Fact]
    public void Structured_frame_round_trips_with_stops_and_alerts()
    {
        var frame = new AssistantFrame(
            3,
            DateTimeOffset.UnixEpoch,
            true,
            [new TrainDefinition("T1", [new TrainStop("A", sequence: 0, relativeTimes: true, plannedArrivalOffset: TimeSpan.FromMinutes(5))])],
            [new ObservedAlert("late", AlertSeverity.Warning, "T1", "Late")],
            trainStates: [new AssistantTrainState("T1")
            {
                Speed = 42,
                TargetSpeed = 80,
                MaxSpeed = 120,
                DelaySeconds = 12.5,
                CurrentStation = "A",
                CurrentPlatform = 3,
                NextStation = "B",
                NextPlatform = 4,
                VisitIndex = 1,
                VisitCount = 2,
                ScheduledVisitCount = 5,
                LookaheadCount = 7,
                HasValidRoute = true,
                NeedsRouteAhead = true,
                HasSignal = true,
                SignalAllocationState = 1,
                FrontAllocationState = 2,
                RouteTotalSteps = 10,
                RouteCurrentStep = 4,
                RouteRemainingSteps = 6,
                NextStationNonStop = true,
                LastVisitStation = "P",
                LastVisitPlatform = 2,
                LastVisitNonStop = true,
                LastVisitStopMinutes = 3,
                CurrentStopMinutes = 4,
                CurrentDepartureScheduleDelaySeconds = 5.5,
                NextPrepareSeconds = 6.5,
                MapEntryGameTimeSeconds = 100,
                MapExitGameTimeSeconds = 200,
                MapEntryStation = "X",
                MapExitStation = "Y",
                MapEntryPlatform = 1,
                MapExitPlatform = 9,
                MapEntryNonStop = true,
                MapExitNonStop = false,
            }],
            gameTimeSeconds: 123.5,
            gameReady: true);
        var encoded = RealtimeProtocolCodec.EncodeLine(AssistantSessionProtocol.CreateFrameEnvelope(3, frame.CapturedAtUtc, frame));
        var decoded = AssistantSessionProtocol.DecodeFrame(RealtimeProtocolCodec.DecodeLine(encoded));

        Assert.Equal("T1", Assert.Single(decoded.Trains).TrainId);
        Assert.Equal(TimeSpan.FromMinutes(5), Assert.Single(Assert.Single(decoded.Trains).Stops).PlannedArrivalOffset);
        Assert.Equal("late", Assert.Single(decoded.ObservedAlerts).Code);
        var state = Assert.Single(decoded.TrainStates);
        Assert.Equal(42, state.Speed);
        Assert.Equal(120, state.MaxSpeed);
        Assert.Equal(7, state.LookaheadCount);
        Assert.True(state.HasValidRoute);
        Assert.Equal(1, state.SignalAllocationState);
        Assert.Equal(10, state.RouteTotalSteps);
        Assert.True(state.NextStationNonStop);
        Assert.Equal(5, state.ScheduledVisitCount);
        Assert.True(state.LastVisitNonStop);
        Assert.Equal(3, state.LastVisitStopMinutes);
        Assert.Equal(4, state.CurrentStopMinutes);
        Assert.Equal(5.5, state.CurrentDepartureScheduleDelaySeconds);
        Assert.Equal(6.5, state.NextPrepareSeconds);
        Assert.Equal(100, state.MapEntryGameTimeSeconds);
        Assert.Equal("X", state.MapEntryStation);
        Assert.Equal(1, state.MapEntryPlatform);
        Assert.Equal(9, state.MapExitPlatform);
        Assert.True(state.MapEntryNonStop);
        Assert.False(state.MapExitNonStop);
        Assert.Equal(4, state.NextPlatform);
        Assert.Equal(123.5, decoded.GameTimeSeconds);
    }

    [Fact]
    public void Recorder_appends_and_replay_tolerates_a_truncated_tail()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assistant-{Guid.NewGuid():N}.jsonl");
        try
        {
            using (var recorder = new SessionRecorder(path, TimeSpan.FromHours(1)))
            {
                recorder.Append(AssistantSessionProtocol.CreateSessionStartEnvelope(0, DateTimeOffset.UnixEpoch, new SessionStart("s", DateTimeOffset.UnixEpoch)));
                recorder.Flush();
            }

            File.AppendAllText(path, "{\"protocolVersion\":1", Encoding.UTF8);
            using var stream = File.OpenRead(path);
            var envelopes = new SessionReplayReader(tolerateTrailingIncompleteLine: true).ReadAll(stream);
            Assert.Single(envelopes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Replay_accepts_a_complete_final_record_without_newline()
    {
        var envelope = AssistantSessionProtocol.CreateSessionStartEnvelope(
            0,
            DateTimeOffset.UnixEpoch,
            new SessionStart("s", DateTimeOffset.UnixEpoch));
        var bytes = RealtimeProtocolCodec.EncodeLine(envelope);
        using var stream = new MemoryStream(bytes.AsSpan(0, bytes.Length - 1).ToArray());

        var decoded = new SessionReplayReader(tolerateTrailingIncompleteLine: true).ReadAll(stream);

        Assert.Single(decoded);
        Assert.Equal(AssistantSessionMessageTypes.SessionStart, decoded[0].MessageType);
    }

    [Fact]
    public void Recorder_refuses_to_overwrite_an_existing_session_file()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"assistant-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "existing\n");
        try
        {
            Assert.Throws<IOException>(() => new SessionRecorder(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Alert_lifecycle_and_user_state_are_independent()
    {
        var projector = new AlertCenterProjector();
        var alert = new ObservedAlert("late", AlertSeverity.Critical, "T1");
        var t0 = DateTimeOffset.UnixEpoch;
        var first = projector.Apply(new AssistantFrame(0, t0, true, [], [alert]));
        var occurrence = Assert.Single(first.Active);
        Assert.Equal(1, occurrence.ObservationCount);
        projector.ApplyAction(new AlertAction(occurrence.AlertId, AlertActionKind.Acknowledge, t0));
        Assert.Equal(1, Assert.Single(projector.Snapshot.Alerts).ObservationCount);
        projector.Apply(new AssistantFrame(1, t0.AddMinutes(1), true, [], []));
        projector.Apply(new AssistantFrame(2, t0.AddMinutes(2), true, [], []));
        var resolved = projector.Apply(new AssistantFrame(3, t0.AddMinutes(3), true, [], []));

        var final = Assert.Single(resolved.Alerts);
        Assert.Equal(AlertLifecycleState.Resolved, final.Lifecycle);
        Assert.Equal(AlertUserState.Acknowledged, final.UserState);
        Assert.Equal(1, final.ObservationCount);
    }

    [Fact]
    public void Observation_count_increments_only_when_the_active_occurrence_is_seen()
    {
        var projector = new AlertCenterProjector();
        var t0 = DateTimeOffset.UnixEpoch;
        var alert = new ObservedAlert("counted", AlertSeverity.Critical, "T1");
        var first = Assert.Single(projector.Apply(new AssistantFrame(0, t0, true, [], [alert])).Active);
        var stale = Assert.Single(projector.Apply(new AssistantFrame(1, t0.AddSeconds(1), false, [], [])).Alerts);
        Assert.Equal(AlertLifecycleState.Stale, stale.Lifecycle);
        Assert.Equal(1, stale.ObservationCount);

        var seenAgain = Assert.Single(projector.Apply(new AssistantFrame(2, t0.AddSeconds(2), true, [], [alert])).Active);
        Assert.Equal(first.AlertId, seenAgain.AlertId);
        Assert.Equal(2, seenAgain.ObservationCount);
        projector.ApplyAction(new AlertAction(seenAgain.AlertId, AlertActionKind.Acknowledge, t0.AddSeconds(3)));
        Assert.Equal(2, Assert.Single(projector.Snapshot.Alerts).ObservationCount);
    }

    [Fact]
    public void Subject_display_name_is_wire_visible_but_not_part_of_alert_fingerprint()
    {
        var byId = new ObservedAlert("late", AlertSeverity.Warning, "primary-1", subjectDisplayName: "G123");
        var renamed = new ObservedAlert("late", AlertSeverity.Warning, "primary-1", subjectDisplayName: "G124");
        Assert.Equal(byId.Fingerprint, renamed.Fingerprint);

        var envelope = AssistantSessionProtocol.CreateFrameEnvelope(
            0,
            DateTimeOffset.UnixEpoch,
            new AssistantFrame(0, DateTimeOffset.UnixEpoch, true, [], [byId]));
        var decoded = AssistantSessionProtocol.DecodeFrame(RealtimeProtocolCodec.DecodeLine(RealtimeProtocolCodec.EncodeLine(envelope)));
        Assert.Equal("G123", Assert.Single(decoded.ObservedAlerts).SubjectDisplayName);
    }

    [Fact]
    public void Warning_requires_two_connected_frames_and_recurrence_gets_new_id()
    {
        var projector = new AlertCenterProjector();
        var t0 = DateTimeOffset.UnixEpoch;
        var warning = new ObservedAlert("warning", AlertSeverity.Warning, "T1");
        Assert.Empty(projector.Apply(new AssistantFrame(0, t0, true, [], [warning])).Active);
        var opened = Assert.Single(projector.Apply(new AssistantFrame(1, t0.AddSeconds(1), true, [], [warning])).Active);
        projector.Apply(new AssistantFrame(2, t0.AddSeconds(2), true, [], []));
        projector.Apply(new AssistantFrame(3, t0.AddSeconds(3), true, [], []));
        projector.Apply(new AssistantFrame(4, t0.AddSeconds(4), true, [], []));
        projector.Apply(new AssistantFrame(5, t0.AddSeconds(5), true, [], [warning]));
        var recurring = Assert.Single(projector.Apply(new AssistantFrame(6, t0.AddSeconds(6), true, [], [warning])).Active);
        Assert.NotEqual(opened.AlertId, recurring.AlertId);
    }

    [Fact]
    public void Timetable_uses_base_corridor_and_does_not_forge_relative_plan_times()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var baseTrain = new TrainDefinition("base", new[]
        {
            new TrainStop("A", sequence: 0, plannedDepartureUtc: t0),
            new TrainStop("B", sequence: 1, plannedArrivalUtc: t0.AddHours(1)),
            new TrainStop("C", sequence: 2, plannedArrivalUtc: t0.AddHours(2)),
        });
        var relative = new TrainDefinition("relative", new[]
        {
            new TrainStop("C", sequence: 0, relativeTimes: true, plannedArrivalOffset: TimeSpan.FromHours(2)),
            new TrainStop("B", sequence: 1, relativeTimes: true, plannedArrivalOffset: TimeSpan.FromHours(1)),
            new TrainStop("A", sequence: 2, relativeTimes: true),
        });

        var snapshot = TimetableGraphProjector.Build("base", [baseTrain, relative]);
        Assert.Equal(["A", "B", "C"], snapshot.Corridor.Select(station => station.StationId));
        var reverse = Assert.Single(snapshot.Trains, train => train.TrainId == "relative");
        Assert.Equal(-1, reverse.Direction);
        Assert.All(reverse.Points.Where(point => point.Kind == TimetablePointKind.PlannedArrival), point =>
        {
            Assert.Null(point.PlannedTimeUtc);
            Assert.True(point.RelativeTimes);
        });
    }

    [Fact]
    public void Timetable_merges_frame_updates_without_losing_historical_actual_events()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var first = new TrainDefinition("base", [
            new TrainStop("A", sequence: 0, actualDepartureUtc: t0),
            new TrainStop("B", sequence: 1),
        ]);
        var second = new TrainDefinition("base", [
            new TrainStop("A", sequence: 0),
            new TrainStop("B", sequence: 1, predictedArrivalUtc: t0.AddMinutes(5)),
        ]);
        var projector = new TimetableGraphProjector("base");
        projector.Apply(new AssistantFrame(0, t0, true, [first], []));
        var snapshot = projector.Apply(new AssistantFrame(1, t0.AddMinutes(1), true, [second], []));
        var baseView = Assert.Single(snapshot.Trains, train => train.TrainId == "base");
        Assert.Contains(baseView.Points, point => point.Kind == TimetablePointKind.ActualDeparture && point.StationId == "A");
        Assert.Contains(baseView.Points, point => point.Kind == TimetablePointKind.PredictedArrival && point.StationId == "B");
    }

    [Fact]
    public void Timetable_removes_a_prediction_after_actual_arrival()
    {
        var planned = DateTimeOffset.UnixEpoch.AddHours(1);
        var predicted = planned.AddMinutes(3);
        var actual = planned.AddMinutes(2);
        var projector = new TimetableGraphProjector("base");

        projector.ApplyTrain(new TrainDefinition("base", [
            new TrainStop("A", sequence: 0, plannedArrivalUtc: planned),
            new TrainStop("B", sequence: 1, plannedArrivalUtc: planned.AddMinutes(10), predictedArrivalUtc: predicted),
        ]));
        var snapshot = projector.ApplyTrain(new TrainDefinition("base", [
            new TrainStop("A", sequence: 0, plannedArrivalUtc: planned),
            new TrainStop("B", sequence: 1, plannedArrivalUtc: planned.AddMinutes(10), actualArrivalUtc: actual),
        ]));

        var points = Assert.Single(snapshot.Trains).Points;
        Assert.Contains(points, point => point.Kind == TimetablePointKind.ActualArrival && point.AbsoluteTimeUtc == actual);
        Assert.DoesNotContain(points, point => point.Kind == TimetablePointKind.PredictedArrival && point.StationId == "B");
    }
}
