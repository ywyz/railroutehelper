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
| Train | A current, non-disposed train with an identifier, reporting number, speed, occupied nodes, and direction target. |
| Train occupancy | The ordered network-node references currently occupied by a train. |
| Route clearance | Track or network-node capacity allocated ahead of, or occupied by, a train. The exact meaning of each raw allocation code must be evidence-backed. |
| Manual route clearance | A route clearance known to have been established by the player. This is the user's “交路” in this project; it does **not** mean a timetable service or rolling-stock circulation. |
| Route-clearance observation | Save evidence that a node has a non-zero allocation code. Its origin remains `Unknown` until the save format distinguishes manual from automatic clearance or a controlled comparison proves it. |
| Blocked train | A train that is stationary and cannot make forward progress. Zero speed alone is insufficient to establish this state. |

## Context boundaries

- **Save ingestion** produces a lossless `SaveValue` tree and assigns no gameplay
  meaning.
- **Schema interpretation** selects an explicit game-version schema and maps
  evidence into an operational snapshot.
- **Operations** reasons about reachability, approaching trains, and possible
  blockages from snapshots.
- **Protocol and replay** transport or reproduce snapshots without depending on
  the save format.

## Invariants

- Unknown game versions are never silently treated as a known schema.
- Raw allocation codes are retained even when their semantic label is inferred.
- Manual and automatic route-clearance origins are not conflated.
- Reading a save never writes to the save, the game directory, or the game
  process.

## Open domain questions

- Confirm the authoritative meanings of allocation codes `0`, `1`, `2`, and the
  observed but rare `3`.
- Find a reliable discriminator between player-established and
  automatically-established route clearance.
- Define how adjacent allocated network nodes are grouped into one operational
  route and where its forward limit lies.
- Define blockage using elapsed game time, stop reason, direction, and reachable
  cleared nodes rather than speed alone.
