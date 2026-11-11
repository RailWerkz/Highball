# Multiplayer World

The plan for turning routes built in the Route Builder into one continuous, multiplayer world covering most of the
US rail network, with the end goal of driving New York to Los Angeles by rail without a loading screen. This is a
design document for the `mmo` branch: nothing here is built yet.

## Goals

- **One company world** anyone can join: the whole national network, one authoritative simulation, 1000+ players.
- **Self-hosted region servers** in a server browser: anyone can host one region (West, Midwest, South, Northeast),
  self-contained, separate from the company world.
- Roughly **75% of US rail** (~170,000 km of line) playable.
- **Seamless travel**: a train can run across zones, tiles and regions with no visible borders or loads.
- **No downloading during play.** Content is installed up front; the only network traffic in a session is game state.
- The whole install **under 50 GB** if we can, and no worse than ~80–90 GB.

## Three layers

Three things that look alike are kept apart, because each has its own job and its own size:

| Layer | What it's for | Granularity |
|---|---|---|
| Projection zones | Coordinate maths | fixed zones over North America (~10 for the lower 48) |
| Tiles | Storage and loading | 2 km squares, map-wide IDs |
| Regions | Download packs and self-hosted servers | West, Midwest, South, Northeast |

A region is just a set of tiles plus its part of the track graph. Region borders are not projection borders.

## Projection zones

Today each route has its own Transverse Mercator projection centred on its origin (`RouteProjection`), and tile
`I_J` is relative to that origin. That's exact for one route but can't scale: two routes don't share a grid, and the
projection is only accurate within a few hundred kilometres.

**Decision: fixed 6° zones, UTM-style, across North America (zones 1–22, with 10–19 covering the lower 48).**

- Each zone has a fixed central meridian. Scale error stays under 0.1% anywhere in a zone.
- `RouteProjection` is built from a zone instead of a route origin. The Transverse Mercator maths stays as it is.
- The zone you're in is your rendering frame. Crossing into the next zone **rebases** the camera and train into the
  new frame in a single frame, the same operation as a floating-origin shift, which we need at this scale anyway.
- Zone borders have a small **overlap strip**: tiles near the border are available in both zones, so the crossing
  has both sides loaded.

**Rejected:**

- *Four big regions as projection areas.* Too wide: a region the size of the West is ~2,500 km across, so
  distances at its edges would be off by 1.5–2% (15–20 m per km) and north would turn by close to 10°.
- *A frame per tile.* A seam at every tile border, 250,000 times over instead of ~10.

## Tiles

Tiles keep the idea they have now: **each tile holds everything you can see inside it.**

What changes:

1. **Map-wide IDs**: `(zone, i, j)` on the zone's fixed 2 km grid. The same ID means the same ground in every pack
   and on every server.
2. **Visual track moves into tiles**: rail and sleeper geometry, bridges, objects and signal heads are stored with
   the tile they're in, so the client loads them with the terrain. Each piece points to the simulation graph by
   map-wide ID ("this signal head is signal #48213"), which keeps what you see in step with the simulation.
3. **Smaller terrain**:
   - 2 m height samples only near track (within ~300 m), 10 m elsewhere. 10 m is what the source data has across
     most of the US anyway.
   - Heights stored as differences from neighbouring samples and compressed with zstd instead of gzip.
   - Target ~40–60 KB per tile, against ~360 KB today.

### Size

| | Per tile | ~200–250k tiles |
|---|---|---|
| Today's format (2 m everywhere, gzip) | ~360 KB | ~80–90 GB |
| Target format | ~40–60 KB | ~25–40 GB |

## Simulation graph

The simulation runs on the **track graph**, not on coordinates: blocks, edges, lengths, connections. Projections
don't matter to it at all.

- **Map-wide IDs** for every node, edge, signal and switch, from day one. Never per-route IDs.
- **Real edge lengths** measured along the earth once, at build time, plus grades on each edge. The simulation is
  exact everywhere, and a track crossing a zone border is just one more connection.
- **One binary file per region**, replacing `Network.json.gz`, `Signalling.json`, `Switches.json`, `Junctions.json`
  and the rest. This is all a server loads.

AI trains and signalling run on this graph. Trains are positions along edges, and signalling is event-driven (only
when a train enters or leaves a block), which keeps both cheap at national scale.

## Packs

Content is **installed once, like a game install or DLC**, never fetched during play.

- One pack per region: West, Midwest, South, Northeast. These are also the regions self-hosted servers run.
- A pack has a tile index at the front and the tile data after it, and is memory-mapped.
- Every tile has a content hash. **Patches replace only the tiles that changed**, never a whole pack.
- When you join a server, it announces which packs and versions it uses. If you're missing something, the game asks
  you to download it **before** you connect, never mid-session.

## Streaming (from disk)

"Streaming" means **loading data already on your disk** as the train moves, never downloading.

- Load tiles a few km ahead **along the planned route**, not in a circle. A train can only go where the rails go, so
  we know what's coming.
- Drop tiles that are far behind.
- Reading a tile is a jump to its offset in the memory-mapped pack plus a decompress: trivial for any SSD at train
  speeds.

New York to Los Angeles is about 4,500 km of track, 2–3 days of real time at realistic speeds, through about 8 zones.
None of that needs anything more than the above.

## Servers

**A server never needs terrain.** It loads the simulation graph and runs:

- train positions (distance along an edge)
- signal and interlocking state
- AI traffic and dispatch
- player sessions

Terrain lives only on clients.

### Company world

- One authoritative simulation of the national graph, for every player.
- The national graph is a few million edges, which fits easily in memory.
- **Interest management**: each client only gets updates for trains near it, so ~1000 players at ~10 updates a
  second is reasonable for one machine.
- If one machine ever isn't enough, the simulation can be split into regional servers that hand trains to each other
  at graph nodes (subdivision borders are the natural places). Map-wide IDs keep that possible; we don't build it until
  we need it.

### Self-hosted region servers

- Listed in an in-game server browser.
- Each one runs one region's graph and is entirely self-contained.
- Separate from the company world: you can't drive off the edge of one hosted region into another.
- A client needs only that region's pack.

| | Company world | Self-hosted server |
|---|---|---|
| Server holds | National graph, trains, signals, AI | One region's graph, trains, signals, AI |
| Server terrain | None | None |
| Client needs | All four region packs | That region's pack |
| Internet during play | Game traffic only | Game traffic only |

## Ownership

Who owns what track, from public data:

- **FRA's North American Rail Network (NARN)**, free US government data. Each line segment has its owners
  (`RROWNER1–3`, as reporting marks such as `CSXT`, `UP`, `BNSF`, `NS`), the railroads with trackage rights
  (`TRKRGHTS1–9`), subdivision and division, number of tracks, passenger use and yard names.
- **OpenStreetMap**, which we already read: `operator=`, `owner=`, `usage=` and `service=` tags. Patchier, but more
  detailed for yards and sidings. (`OsmExtract` doesn't keep these tags yet.)

This is a reference for building track in the editor, nothing more. NARN's lines don't follow OSM's geometry
exactly, so each NARN segment is matched to the closest OSM tracks to show who owns what.

NARN is accurate for main lines; small spurs and yard tracks can be missing or wrong.

In the builder: an **Owner** mode for the track overlay next to the current Class mode (`YardGuides.DrawClasses`),
with lines coloured by owner, a legend of reporting marks, and owner, trackage rights and subdivision on hover.

## Current code

What the code does today, from a read of the whole codebase on 2026-11-10. Paths are under `Highball/`.

### Positions and rendering

- **A floating origin already exists.** Positions are doubles on the CPU, and the GPU only gets float offsets from
  the camera: terrain patches, track chunks, vegetation, models, lights, audio, clouds (`World/Camera.cs`). Big
  coordinates don't make geometry shake.
- **One gap: the shaders' broad noise.** `WorldRenderer.cs:216` sends the camera's absolute position as a float, and
  `terrain.frag` / `line.frag` add it back for large-scale ground and track-wear patterns. At UTM northings
  (3–5.5 million m) a float only resolves to 0.25–0.5 m, so those patterns would jitter. Fix by keeping world
  coordinates small (subtract a reference from each zone's northing) or by wrapping that position in doubles the way
  `TexOrigin` already is.
- `TerrainSource.cs:365-455` works out land-cover lookups in float; switch to double.
- **Tile maths is copied by hand in about ten places** instead of going through `RouteGrid`: `TerrainSource`,
  `TerrainEdits`, `TerrainNetwork`, `NetworkLines`, `TrackAudit`, `PlannerView`, `RouteGenerator`. All of it assumes one
  continuous plane, which is what zones break.
- **North isn't quite up in a zone.** The code treats −z as true north (camera yaw, sun from the origin's lat/lon).
  Near a zone's edge, grid north is 2–3° off true north, which shows in the sun, the compass and headings.
- Spatial hashes and chunk keys are all safe at UTM magnitudes.

### Route data and IDs

- **Stored as lat/lon, so it survives a projection change:** the network (`Network.json.gz`, `Nodes.json.gz`), placed
  objects, stock, circuit joints and anchors, junction markers.
- **Stored in projected metres, so it needs converting:** tile heights, cover and sculpt edits (`Tiles/I_J/`, sampled
  on today's grid), `Track.bin.gz` (a cache, rebuilt on its own), and hand-shaped track in `TrackEdits.bin.gz`.
- **Playable track is the hand-laid track.** Mapped OSM rail is only a guide (`RouteNetwork.IsGuide`), drawn as ground
  marks. Hand-laid ways have negative IDs, or keep their OSM ID tagged `highball:edited`.
- **Persistent IDs today:** OSM IDs, negative IDs for hand-made ways and nodes, placed-object IDs from timestamps,
  circuit names (`TC0042`) kept by position, bridge keys taken from nearby nodes.
- **IDs worked out again on every solve, so unusable as stable IDs:** line keys (a hash of geometry, which changes
  when a line is edited or cut differently by tile edges), line indices, graph edge and vertex indices.
- **Bug: `RouteNetwork.NewId()` can hand out an ID again.** It returns one below the lowest ID currently in use and
  resets on load, so deleting the lowest hand-made way and reloading lets its ID come back. Anything still keyed by the
  old ID (`Switches.json`, `SwitchCurves.json`, a hand throw's node, bridge keys) then attaches to the new track.
- **Signals aren't linked to track.** A signal is a placed object with a position and heading, nothing more.
  `Signalling.json` links circuits to track by position only.
- **Two separate track graphs:** signalling's `TrackGraph` (from network ways) and trains' `RailGraph` (from solved
  lines). Different indexing, not linked.
- **Building more tiles** merges fresh OSM data (hand edits kept), forces the whole track to solve again, and drops
  hand shapes on lines that now get cut differently. A rebuilt tile also loses hand-painted cover.

### Trains, signalling, networking

- **Trains already run on the track graph.** A position is `(edge, distance, direction)` (`Trains/TrackPosition.cs`),
  and physics runs in fixed 1/120 s steps (`Train.Update`). None of the train classes touch Vulkan or ImGui.
- **But the graph gets heights from terrain.** `RailGraph.Build` reads track heights sampled from the tiles, so a
  server without terrain needs heights baked into its data.
- **The train world lives in UI code.** The list of trains, stepping, coupling, collisions and splits are inside
  `Builder/TrainPanel.cs`. Switch state lives in `World/NetworkMeshes.cs`, a rendering class. Trains only run in the
  builder; Single Player says "Not available yet."
- **Signalling simulates nothing yet.** There's an editor for track circuits; no occupancy, aspects or interlocking.
- **Networking is a working transport that nothing uses.** `RailWerkz.Network/Session.cs` wraps RakNet (host, join,
  reliable and unreliable sends, events) and doesn't depend on rendering, so it can run headless. No game messages
  exist, and Host and Join in the Multiplayer menu say "Not available yet."
- **Everything is one Windows exe.** There's no project a server could reference for just the simulation.

## Work

In order. Each step ends with something to test.

1. **Done: one world.** Route Builder opens `Content/Routes/World` directly, with PlymouthYard copied in.
2. **Done: zone projection, for one zone.**
   - `RouteProjection(zone)`: UTM zone, central meridian at x = 0, z = 0 at 4,000,000 m north of the equator
     (`ZoneNorth`), so coordinates stay small. New worlds pick their zone from where they start; `Route.json` has
     `zone`. Routes without one still open on the old projection around their origin.
   - Sun, moon and stars turned by how far true north is from the plane's north there (`GridNorth`).
   - `--migrate-zone NAME` moves an old route onto its zone: tiles resampled (heights Catmull-Rom, sculpting bilinear,
     cover nearest), headings turned, solved track left to be solved again, old folder kept as `NAME.prezone`.
   - The world is on zone 17: 178 tiles from the old 134. 87 tiles at the edge of the built area are only partly over
     the old ground, with the gap filled from the nearest samples; building them again in the Planner fills them
     properly.
   - Left for crossing zone borders: tile IDs with the zone in them, the hand-copied tile maths, rebasing when crossing.

3. **Done: stable IDs.**
   - Track's stable IDs are its way and node IDs: hand-laid ways and nodes (negative), and OpenStreetMap ones kept by
     tagging them edited. Lines and graph edges are worked out again on every fit, so the export names edges by the
     nodes at their ends.
   - `RouteNetwork.NewId()` no longer hands out an ID again: the lowest one handed out is saved in `Ids.json`. For a
     world saved before that, it also starts below every ID that `Switches.json`, `SwitchCurves.json` and placed
     objects name.
   - Signals link to track: `Node` and `Toward` on the signal are the nodes it stands between, in the direction of the
     trains it governs. Set when placed or moved, and filled in or fixed up from where the signal stands when the
     editor opens or an edit takes a node out.

4. **Region painting in the Planner.** Mark tiles as West, Midwest, South or Northeast, saved with the world.
5. **Export.**
   - Client pack per region: tiles plus visible track.
   - Server data: track graph with heights baked in, switches, signals.

   *Done when* exporting Plymouth gives both, and they read back.
6. **Simulation out of the UI.**
   - The train world moves out of `TrainPanel`, and switch state out of `NetworkMeshes`.
   - Both go into a simulation project with no rendering, which the game and a server both reference.
7. **The game runs from packs.** Driving loads tiles from the installed pack, ahead along the track.
8. **Server.**
   - A headless server on the simulation project and `Session`, loading only the server data.
   - Host and Join wired up.

   *Done when* two clients see each other's trains on Plymouth.

Later: smaller terrain (the size target), the owner overlay, crossing zone borders, signalling logic.
