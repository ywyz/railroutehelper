# Domain context

Rail Route Helper observes a player's own Rail Route state without controlling the
game. Its domain language describes what can be established from a save or an
approved future real-time source; uncertain reverse-engineered meanings remain
explicitly uncertain.

## Ubiquitous language

| Term | Meaning |
| --- | --- |
| Game version | The version string embedded in a save. It selects the only field schema that may interpret that save. |
| Save schema | A version-scoped set of paths and value shapes used to turn a lossless save tree into an operational snapshot. |
| Operational snapshot | An immutable observation of trains, track segments, stations, and route-clearance evidence at one save time. |
| Network node | A named element in the game's rail graph, such as a track, signal, switch, or autoblock. |
| Track segment | A network node that trains can occupy and that connects two endpoint nodes. |
| Station | A named operational location with a stable save identifier and grid position. |
| Train | A current train with `disposed=false` and `initialized=true`, plus an identifier, reporting number, speed, occupied nodes, and direction target. |
| Train occupancy | The ordered network-node references currently occupied by a train. |
| Route clearance | Track or network-node capacity allocated ahead of, or occupied by, a train. The exact meaning of each raw allocation code must be evidence-backed. |
| Manual route clearance | A route clearance known to have been established by the player. This is the user's “交路” in this project; it does **not** mean a timetable service or rolling-stock circulation. |
| Route-clearance observation | Save evidence that a node has a non-zero allocation code. Its origin remains `Unknown` except where an explicit marker such as `PerpetualAutoRoute=true` proves automatic operation. |
| Operations report | A conservative projection of one current snapshot and an optional previous snapshot into train reachability and route-change observations. |
| Operations report message | A protocol-v1 envelope carrying an Operations report payload with its own payload version, source save name, schema, network identity, game version, and game time. |
| Save monitor | A read-only directory observer that stabilizes, deduplicates, groups, orders, and compares save revisions before emitting protocol messages. |
| Save monitor diagnostic | A versioned protocol message explaining why one save revision was skipped without exposing its absolute path or stopping other save streams. |
| Live operations projection | An immutable, thread-safe view built from Operations report messages: the latest trains per network, a bounded route-change timeline, and bounded alert history. |
| Operations alert | A lifecycle-tracked warning derived from a conservative Operations state. The first alert kind is `PossibleBlockedTrain`; it is not proof that the train is blocked. |
| Alert fingerprint | The stable network/kind/train identity used to update one active alert. A recurrence after resolution opens a new alert ID with the same fingerprint. |
| Train route reachability | Whether the next scheduled platform is in the train's uniquely selected, continuously allocated forward component: `Reachable`, `NotReachable`, or `Unknown`. |
| Route change | A control node's target transition between two snapshots: established, retargeted, or released. |
| Blocked train | A train that is stationary and cannot make forward progress. Zero speed alone is insufficient to establish this state. |
| Possibly blocked train | A train with observed stationary duration and an inferred forward route gap. This is a warning, not proof that the train cannot move for another reason. |

## Context boundaries

- **Save ingestion** produces a lossless `SaveValue` tree and assigns no gameplay
  meaning.
- **Schema interpretation** selects an explicit game-version schema and maps
  evidence into an operational snapshot.
- **Operations** reasons about reachability, approaching trains, and possible
  blockages from snapshots.
- **Protocol and replay** transport or reproduce Operations reports without
  depending on the save format.
- **Monitoring** owns directory observation, file stabilization, per-network
  ordering, previous-snapshot state, and conversion to protocol messages.
- **Live operations** consumes protocol messages and owns latest-view projection,
  bounded event history, and alert lifecycle; it does not read saves.
- **Web** is a loopback-only Adapter that feeds Monitoring messages into Live
  operations and exposes the immutable projection through HTML and JSON.

## Invariants

- Unknown game versions are never silently treated as a known schema.
- Raw allocation codes are retained even when their semantic label is inferred.
- Manual and automatic route-clearance origins are not conflated.
- A possible topology path is not treated as reachable when the selected branch
  is ambiguous.
- Reading a save never writes to the save, the game directory, or the game
  process.
- Snapshots with different topology-derived network identities are never
  compared as one route-change sequence.
- A `PossibleBlockedTrain` alert remains a warning and is resolved by a later
  report for that network when the condition is absent; it is never upgraded to
  a proven blockage by the projector or UI.

## Open domain questions

- Confirm the authoritative meanings of allocation codes `0`, `1`, `2`, and the
  observed but rare `3`.
- Find a reliable discriminator between player-established and
  automatically-established route clearance.
- Validate the current forward-component grouping on complex junctions, loops,
  and multiple simultaneous allocations.
- Determine which stop-reason codes can safely refine the current conservative
  possible-blockage rule.
