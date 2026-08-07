namespace RailRouteHelper.AssistantSessions;

/// <summary>Projects frame observations and user actions into an alert center.
/// Lifecycle (Active/Resolved/Stale) is intentionally independent of the user's
/// Unseen/Acknowledged/Snoozed state.</summary>
public sealed class AlertCenterProjector
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AlertOccurrence> _currentByFingerprint = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AlertOccurrence> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _generations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _warningStreaks = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private AlertCenterSnapshot _snapshot = new([], true, DateTimeOffset.MinValue);

    public AlertCenterSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    public IReadOnlyList<AlertOccurrence> Alerts => Snapshot.Alerts;

    public IReadOnlyList<AlertOccurrence> ActiveAlerts => Snapshot.Active;

    public AlertCenterSnapshot Apply(AssistantFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            var now = frame.CapturedAtUtc;
            if (!frame.IsConnected || !frame.IsSuccessful)
            {
                // A disconnected/failed frame is not evidence that an alert disappeared.
                foreach (var id in _order.ToArray())
                {
                    var item = _byId[id];
                    if (item.Lifecycle == AlertLifecycleState.Active)
                    {
                        Update(item with { Lifecycle = AlertLifecycleState.Stale });
                    }
                }

                _warningStreaks.Clear();
                return Publish(frame.IsConnected, now);
            }

            var observed = frame.ObservedAlerts
                .GroupBy(alert => alert.Fingerprint, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

            foreach (var fingerprint in _warningStreaks.Keys.ToArray())
            {
                if (!observed.ContainsKey(fingerprint))
                {
                    _warningStreaks.Remove(fingerprint);
                }
            }

            // Missing counts are only advanced by successful connected frames.
            foreach (var item in _currentByFingerprint.Values.ToArray())
            {
                if (observed.ContainsKey(item.Fingerprint))
                {
                    continue;
                }

                var missing = item.ConsecutiveMissingFrames + 1;
                if (missing >= 3 && item.Lifecycle is AlertLifecycleState.Active or AlertLifecycleState.Stale)
                {
                    Update(item with
                    {
                        Lifecycle = AlertLifecycleState.Resolved,
                        ResolvedAtUtc = now,
                        ConsecutiveMissingFrames = missing,
                    });
                }
                else
                {
                    Update(item with { ConsecutiveMissingFrames = missing });
                }
            }

            foreach (var (fingerprint, observation) in observed)
            {
                if (_currentByFingerprint.TryGetValue(fingerprint, out var existing)
                    && existing.Lifecycle is AlertLifecycleState.Active or AlertLifecycleState.Stale)
                {
                    var userState = existing.UserState;
                    if (userState == AlertUserState.Snoozed
                        && existing.SnoozedUntilUtc is { } snoozeUntil
                        && snoozeUntil <= now)
                    {
                        userState = AlertUserState.Unseen;
                    }

                    Update(existing with
                    {
                        Observation = observation,
                        Lifecycle = AlertLifecycleState.Active,
                        LastSeenAtUtc = now,
                        ConsecutiveMissingFrames = 0,
                        ObservationCount = existing.ObservationCount + 1,
                        ResolvedAtUtc = null,
                        UserState = userState,
                    });
                    continue;
                }

                if (observation.Severity == AlertSeverity.Warning)
                {
                    var streak = _warningStreaks.TryGetValue(fingerprint, out var prior) ? prior + 1 : 1;
                    _warningStreaks[fingerprint] = streak;
                    if (streak < 2)
                    {
                        continue;
                    }
                }

                // A resolved occurrence is never re-opened: this creates a new occurrence id.
                var occurrence = NewOccurrence(observation, now);
                _currentByFingerprint[fingerprint] = occurrence;
                _byId.Add(occurrence.AlertId, occurrence);
                _order.Add(occurrence.AlertId);
            }

            return Publish(true, now);
        }
    }

    public AlertCenterSnapshot ApplyFrame(AssistantFrame frame) => Apply(frame);

    public AlertCenterSnapshot Project(AssistantFrame frame) => Apply(frame);

    public AlertCenterSnapshot ApplyAction(AlertAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            if (!_byId.TryGetValue(action.AlertId, out var item))
            {
                return _snapshot;
            }

            var updated = action.Action switch
            {
                AlertActionKind.Acknowledge => item with
                {
                    UserState = AlertUserState.Acknowledged,
                    SnoozedUntilUtc = null,
                },
                AlertActionKind.Snooze => item with
                {
                    UserState = AlertUserState.Snoozed,
                    SnoozedUntilUtc = action.SnoozeUntilUtc,
                },
                AlertActionKind.Unsnooze => item with
                {
                    UserState = AlertUserState.Unseen,
                    SnoozedUntilUtc = null,
                },
                AlertActionKind.MarkUnseen => item with
                {
                    UserState = AlertUserState.Unseen,
                    SnoozedUntilUtc = null,
                },
                _ => item,
            };
            Update(updated);
            return Publish(_snapshot.IsConnected, action.OccurredAtUtc);
        }
    }

    public AlertCenterSnapshot ApplyAlertAction(AlertAction action) => ApplyAction(action);

    public AlertOccurrence? Find(string alertId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alertId);
        lock (_gate)
        {
            return _byId.TryGetValue(alertId, out var value) ? value : null;
        }
    }

    private AlertOccurrence NewOccurrence(ObservedAlert observation, DateTimeOffset now)
    {
        var fingerprint = observation.Fingerprint;
        var generation = _generations.TryGetValue(fingerprint, out var previous) ? previous + 1 : 1;
        _generations[fingerprint] = generation;
        // Deterministic IDs aid replay while generation ensures recurrence gets a new ID.
        var alertId = $"{fingerprint}:{generation}";
        return new AlertOccurrence
        {
            AlertId = alertId,
            Fingerprint = fingerprint,
            Observation = observation,
            Lifecycle = AlertLifecycleState.Active,
            UserState = AlertUserState.Unseen,
            FirstSeenAtUtc = now,
            LastSeenAtUtc = now,
            ConsecutiveMissingFrames = 0,
            ObservationCount = 1,
            Generation = generation,
        };
    }

    private void Update(AlertOccurrence occurrence)
    {
        _byId[occurrence.AlertId] = occurrence;
        if (_currentByFingerprint.TryGetValue(occurrence.Fingerprint, out var current)
            && current.AlertId == occurrence.AlertId)
        {
            _currentByFingerprint[occurrence.Fingerprint] = occurrence;
        }
    }

    private AlertCenterSnapshot Publish(bool connected, DateTimeOffset capturedAtUtc)
    {
        var alerts = _order.Select(id => _byId[id]).ToArray();
        _snapshot = new AlertCenterSnapshot(alerts, connected, capturedAtUtc);
        return _snapshot;
    }
}
