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

4. **Done: region painting in the Planner.**
   - A Regions card in the Planner: Off or Paint, and West, Midwest, South and Northeast with how many tiles each has.
   - In Paint, a drag puts the built tiles it passes over in the region picked (Shift: a rectangle; Ctrl: take them
     out of any), and the map shows every built tile in its region's colour, unset ones outlined.
   - Saved straight away to `Regions.json` in the world's folder, tile to region.

5. **Done: export, first version.** File > Export for Players and Servers in the editor (or `--export` with
   `--route`) saves, then writes `Export/WORLD/`:
   - `REGION.hbpack` per region (`Unassigned.hbpack` for tiles in none; `World.hbpack` when no regions are set): the
     tiles, Brotli-compressed with heights as residuals from a neighbour prediction and sculpting baked in, then the
     route's track files kept whole, then an index with a hash per tile and file (`PackFile`).
   - `World.hbserver` and `REGION.hbserver`: the rail graph with rail-top heights baked in, switches and signals, so a
     server needs no terrain (`RailGraphFile`). A region keeps the edges whose middle is in its tiles.
   - `manifest.json`: version, and every pack's and tile's hash.
   - Then reads everything back and checks it: heights within 0.05 m, cover and track files exact, and the world graph
     the same edge for edge.
   - Plymouth: 178 tiles, 49.2 MB as built, 38.5 MB packed (about 216 KB a tile, still 2 m everywhere); server track
     476 KB for 25 km.
   - Still to come: visible track per tile (packs carry the network whole for now), mixed-resolution terrain, and a
     test of a region border that cuts through track (Plymouth's laid track all fell in one region in every split
     tried).

6. **Done: simulation out of the UI.** A new project, `RailWerkz.Sim`, with no window, drawing, input or sound (it
   builds on its own, referencing only `RailWerkz.Core`):
   - `Railway`: every cut on the track, stepping them, coupling and cuts taken off when they meet too hard, splits,
     switches forced when run through, and saving and putting stock back. Lifted out of `Builder/TrainPanel`, which
     now drives it and draws it.
   - `SwitchStates`: which way each switch is set. Lifted out of `World/NetworkMeshes`, which now swings the blades to
     match it. Kept across the track being laid out again.
   - Moved in: the trains and their physics, `RailGraph`, `RailGraphFile`, `TrackPiece`, `TurnoutSizes`, `PlacedCut`,
     `UnitText`. Building the graph from the editor's lines stays in the game (`World/RailGraphBuilder`).
   - Namespaces are unchanged (`Highball.Trains`, `TrackPiece` still `Highball.World`) to keep the move small.
   - Checked in the editor: saved stock put back, a locomotive starting up and pulling, a train uncoupled and drawing
     apart.

7. **Done: the game runs from packs, first version.**
   - The export installs the packs in `Content/Packs/WORLD/` once they check out (`Content/Packs/` is ignored).
   - Single Player lists the installed packs by world name (and region), and opens one to drive.
   - `PackSource` opens a pack: tiles are read from it as they're needed (full heights near the camera and ahead of
     it, as from a route folder), and its track files are unpacked into a cache folder (`%APPDATA%/Highball/PackCache`)
     that stands in for the route's folder, so the track is laid out as the editor does it. Anything written while
     playing lands in that cache.
   - Driving uses the editor's 3D view in a playing mode: straight into 3D, no Planner, Esc leaves, sculpting isn't
     kept. `--pack WORLD/REGION` opens one the same way.
   - Checked with `Content/Routes/World` hidden: Plymouth opened from the pack alone (8.6 s, against 6.7 s from the
     folder) and a locomotive placed and driven.
   - Still to come: a driving screen of its own (the editor's tool strip still shows, its edits just aren't kept),
     stock left on the track when a pack's made, and a version check against the manifest.

8. **Done: server and Host/Join, first version.**
   - `RailWerkz.Server`: a world server that loads only the server track (`.hbserver`, no terrain). Runs headless
     (`dotnet run --project RailWerkz.Server -- PATH.hbserver [PORT] [WORLD]`) or inside the game when a player hosts.
   - `Protocol`: Hello, Welcome (player number, world and region to open, track checksum), Ready (the player's own
     checksum once its pack's laid out; refused if it differs), CutAdded, CutState (front, coupler slack and car
     speeds, 20 a second), CutRemoved, Switch.
   - The server keeps what everyone shares, switches and every cut's latest state, so a player joining late sees it
     all, and takes a player's cuts off when they leave.
   - In the game (`Multiplayer/NetPlay`): Multiplayer > Host a session (an installed world with its server track) or
     Join session (the address and port set there). Joining opens the world and region the server says, if it's
     installed. This player's cuts are sent; other players' are shown where they say (`Railway.AddRemote`,
     `Train.Mirror`), carried on at their last speed between updates. Another driver's train can't be driven or taken
     off. `--host WORLD/REGION` and `--join ADDRESS` do the same for testing.
   - The export installs the `.hbserver` files beside the packs.
   - `RailGraph.Checksum` is FNV-1a, the same in every process, so players and the server can compare tracks.
   - Checked: a headless server with two game windows, and a game hosting with a second joining. Both times the
     second player saw the first's locomotive moving and speeding up.
   - **First version: each player's game runs its own trains and the server passes their states on.** Running every
     train on the server (the `Railway` in `RailWerkz.Sim`), so trains can couple and collide across players and AI
     can run, is next. Not tested yet: switches thrown by one player showing for another, and a player leaving.

9. **Done: a driving screen of its own, first version.** Single Player and Multiplayer open `Screens/DriveScreen`, not
   the Route Builder: the same world underneath (the editor's 3D view with `Driving` on: no tool strip, panel, editing
   hotkeys or track overlay), with its own header and footer.
   - Header: Game menu (Consist, Settings, Leave), the camera views as tabs, the world and the session.
   - Cameras: F1 cab (the engineer's seat in the lead unit, looking round with the right mouse button), F2 orbit,
     which is the Route Builder's Follow camera (flown as ever, carried along by the train as it's drawn, so it doesn't
     shake; it starts behind the lead unit), F3 free. The cab also reads the lead unit as it's drawn. More views on F4
     and up later.
   - Footer: the train's name, speed, throttle, reverser and brake pipe; the controls; notices.
   - The train window (build, place, drive, power, vehicles, coupling) and its air graphs, on C.
   - Esc: a pause menu (resume, settings, leave).
   - `--driveview cab|orbit|free` and `--hidewindows` for testing.

Later: smaller terrain (the size target), the owner overlay, crossing zone borders, signalling logic.
