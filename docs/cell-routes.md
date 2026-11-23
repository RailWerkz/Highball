# Cell routes

The plan for making the route builder's track work in small fixed cells instead of over the whole route, so that
opening the builder and placing track cost the same whether the world is one county or the whole country. This is a
design document: nothing here is built yet. It fills in phase 5 of `performance-plan.md` ("region streaming ... a
redesign of `NetworkLines`' solve, not a loader change") and the "visible track moves into tiles" step of `mmo.md`.

## The problem, measured

On `Content/Routes/World` (574 tiles, 235 km of track, 33,000 lines of which 31,000 are roads), 2026-11-22:

| | Time | Scales with |
|---|---|---|
| Open, nothing cached | 50–90 s | the whole route |
| Open, compiled route cached | ~7 s | the whole route (it reads all 450 MB) |
| Placing a branch off a main line | ~6 s background, 0.8 s freeze | the whole route |

Every step of an edit (lay the track, shape the roads, work out the grades, find the bridges and layouts, shape the
ground, take the meshes in) walks over every line in the route, even after the local rebuild keeps most of them as they
were. An open does the same before anything can be shown. The caches added so far skip that work only while nothing has
changed, and the compiled route throws itself away whenever the game is built again.

At 10,000 miles (16,000 km, about 70 times World) all of this grows 70 times over: opens near an hour cold, edits in
minutes, the compiled route near 30 GB, and all of it in memory at once. No speed-up of the current passes changes that,
because the shape is wrong, not the speed.

## Goals

- **An edit costs what the edit touches.** Placing a crossover rebuilds a few square kilometres, whatever the size of the
  world. Target: under a second from release to seeing the finished track, with no freeze.
- **An open costs what's near the camera.** Target: under 5 s to a usable builder, whatever the size of the world, and
  a new build of the game doesn't make the next open slow.
- **Memory follows the camera.** Only cells near it are in memory, in the builder as in driving.
- **The same data serves the builder, single player, packs and servers** (`mmo.md`), so nothing is laid out twice in
  two ways.
- **Results don't change.** A cell rebuilt on its own comes out within millimetres of the same cell from a full rebuild,
  checked the way `HB_LOCALCHECK` checks today.

## Cells

**A cell is a tile: the 2 km square the ground is already stored in.** It's internal; nobody defines or sees cells.
Regions (`RouteRegions`, West, Midwest, South, Northeast) stay what they are: labels on sets of tiles for packs and
servers. A region of thousands of square miles is just many cells.

Why tiles: the ground, its shaping cache (`GroundCache`), cover, sculpting and packs are all per tile already. What's
missing is the track side, which today is one object for the whole route (`NetworkLines`).

When zones are crossed (`mmo.md`), a cell's ID becomes `(zone, i, j)`, as tiles' do.

## What a cell holds

The source of truth stays what it is today: the network (ways and nodes, in latitude and longitude), hand shapes,
switch sizes and curves, bridge edits, placed objects, sculpting. Everything below is **built data**, worked out from
those and kept so nothing has to be worked out again.

Per cell, in `Cells/I_J.cell` (one file, versioned, compressed; written beside `Tiles/I_J/`):

- **Pieces**: every line's stretch inside the cell, cut at the cell's border. A line crossing three cells is three
  pieces. Each piece has the line's identity (its chain, see Identity), its points (x, z, y, s, sink, shift, structures)
  and where it carries on into the next cell.
- **Border points**: where each piece crosses the border, with its position, heading, curvature, height and grade.
  The piece on the other side starts at exactly the same point. This is what lets a cell be rebuilt with its
  neighbours held as they are (see Edits).
- **Turnouts, layouts, crossing pads, bridges and bridge ends** whose anchor point is in the cell. One that reaches into
  the next cell is stored once, in the cell of its anchor, and listed in the other's index.
- **Shaped ground**: what `GroundCache` keeps per tile today.
- **Meshes' inputs** only; meshes themselves stay built on the GPU side as now.
- **The network's ways and nodes in the cell** (see The network per cell), so an edit loads only the cells it touches.
- **A stamp**: a hash of what the cell was built from: its ways and nodes, the hand data in it, and its neighbours'
  border points. Not the game build (see Versions).

Size: World's 450 MB of built data is mostly per-point arrays for 33,000 lines, so about 0.8 MB a cell uncompressed. It
compresses well (sampled every 2 m, smooth), and the packs already show tiles going from 360 KB to 63 KB. Measuring
that is the first thing stage 1 does.

## Identity

Pieces need names that last across rebuilds. Today's line keys are hashes of geometry, which change when a line's cut
differently, and hand shapes are keyed by them (`TrackEdits`), which is why "hand-shaped lines no longer match the map
data" appears after some edits.

- A chain is named by its ways: the ID of its first way and node, which are stable already (`Ids.json`, `mmo.md` step 3).
- A piece is named by its chain and its cell.
- Hand shapes move to being keyed by way and node span, not by line key, so they survive a line being cut differently.

## Edits

An edit changes ways, nodes or hand data. Then:

1. **Mark the dirty cells**: every cell the changed ways and nodes are in (as they were and as they are), plus one ring
   round them. A crossover dirties one to four cells, so with the ring, 9 to 16: 36 to 64 km².
2. **Load the context**: the ring outside the dirty cells, read-only. Its pieces are held exactly as they are, as kept
   lines are today (`NetworkLine.Frozen`).
3. **Lay, grade and shape the dirty cells only**: the existing passes (solve, roads, profiles, bridges, layouts, ground)
   run on the dirty cells' ways, against the held context. Where a chain leaves the dirty cells, its border point is a
   pin: the new track has to meet it at the same place, heading and height.
4. **Save** the dirty cells' new pieces, ground and stamps; bump their versions.
5. **Show** the new cells: meshes rebuilt for those cells only.

This is the local rebuild of 2026-11-22 with its outer edge made exact. That work already showed the passes coping
with held lines all round: an edit with everything beyond 1 km held came out within 5 mm of a full rebuild, line for
line, over all 33,343 lines. What changes is that the passes are only ever handed the dirty cells and the ring, never the
whole route, so each costs the same however big the world is.

### Long things

- **Grades along long lines.** A grade worked out along a 30 km line changes only near the edit; today's hold already
  eases changes out between 600 m and 1 km. A dirty area of at least one ring of 2 km cells covers that.
- **Bridges, layouts and yards bigger than a ring.** The dirty area grows to take in the whole of anything it cuts
  through: a bridge's every cell, a yard's every cell. That's rare and stays bounded (a yard is a few km).
- **Junction heights.** A node on the dirty area's edge takes the held side's height, as `Junctions()` does for kept
  lines now.
- **A change that really does reach far** (raising a whole long grade by hand, a new ruling grade): the edit tool says
  how far it reaches and the dirty area is that big. It costs what it touches, which is then a lot.

## Opens

1. Read the route's small files: `Route.json`, the cell index (which cells exist, their stamps, a coarse overview of
   their track for the 2D map).
2. Load the cells near the camera, nearest first, on background tasks, as terrain patches stream now.
3. The builder is usable as soon as the cells round the camera are in. More stream in and drop out as the camera moves.

An open never solves anything. A cell whose stamp doesn't match what it's built from (changed outside the builder, a
build version bump) is rebuilt when it's first loaded, on its own, as an edit would.

The 2D Planner map at national scale draws from the overview in the cell index, not from loaded cells.

## Versions

Today the compiled route is thrown away whenever the game is built again (it's stamped with the assembly's ID), so
nearly every launch during development opens cold.

- Each cell's built data has a **format version**: bumped only when the file layout changes. A mismatch means read
  failure, so it's rebuilt.
- And a **build version** of the passes: bumped by hand when a change to the laying, grading or shaping should show in
  cells already built. A mismatch rebuilds the cell when it's loaded, not the whole world at once.
- Changing code without bumping either leaves built cells as they are.

## The network per cell

`Network.json.gz` (8.6 MB for World) and `Nodes.json.gz` are whole-route files; at national scale they'd be several GB,
loaded and saved whole on every Ctrl+S.

- Ways and nodes are stored per cell. A way crossing cells is stored in the cell of its first node and listed in the
  others' indexes; nodes are stored in their own cell.
- Saving writes only the cells changed since the last save.
- The editor's network (`RouteNetwork`) becomes a window over the cells loaded, with the same API for the tools.

## Multiplayer and single player

- **Driving** (single player and multiplayer) loads cells from packs as the train moves, as `mmo.md` plans for tiles:
  pieces, ground, objects and signal heads per cell, with the rail graph per region for the server. Nothing is solved
  on a player's machine; today a pack lays its track out on open (`PackSource`, 4.3 s for World), which can't scale.
- **The server** keeps loading only the rail graph (`.hbserver`), built from the cells' pieces. Graph edges stay named
  by their end nodes, so a cell rebuilt doesn't rename edges elsewhere.
- **Building together** (several people editing one world) fits the same shape: one machine owns the world, takes
  edits as network changes, rebuilds the dirty cells and sends the new cells' versions; others fetch the cells they
  have loaded. Two people editing in different places never touch the same cells. Two in the same cells take turns per
  cell. This isn't planned work yet; the point is that cells don't stand in its way, and the whole-route design does.

## What stays and what goes

**Stays:** how track is laid (chains, fairing, alignment, turnouts, crossovers), how grades are worked out, how roads and
bridges are fitted, how ground is shaped to the track, the meshes, the rail graph, the tools' behaviour.

**Goes or changes:**
- `NetworkLines` as one object for the route: becomes a window over the loaded cells, with the same API at first so the
  45 files that use it keep working, then narrowed.
- `Track.bin.gz`, `CompiledRoute` (`Cache/Lines.bin`), `LocalRebuild` and today's hold of far rail: replaced by cells
  and their borders.
- The whole-route network files: replaced by per-cell network data.
- Line keys as identities: replaced by chain and cell names.

## Stages

Each stage ends with something measured and checked, and the builder working throughout.

1. **Cells written and read.** After a full build, write every cell (pieces, border points, the rest) and an index.
   Open reads the cells back into a `NetworkLines` instead of solving or reading `Lines.bin`. Checked: the lines read
   back match the lines built (as `HB_COMPILECHECK` does). Measured: cell sizes, open time. Gains: opens no longer cold
   after every code change.
2. **Edits rebuild dirty cells.** The local rebuild is handed only the dirty cells' ways plus the ring as held pieces,
   and saves only those cells. Checked: against a full rebuild of the same edit, every cell within millimetres.
   Measured: edit times for a switch, a crossover, a siding, a yard, a bridge. Gains: edits cost what they touch.
3. **The network per cell.** Ways and nodes saved and loaded per cell; Ctrl+S writes only changed cells.
4. **Streaming in the builder.** Only cells near the camera are loaded; `NetworkLines` and `RouteNetwork` become windows;
   the Planner draws from the overview. Gains: opens in seconds and memory follows the camera, at any size.
5. **Packs and servers from cells.** Packs carry cells; driving streams them; the server graph is built from them.

Stages 1 and 2 fix what hurts now (slow opens, slow edits). Stage 4 is what makes national scale work.

## Open questions

- **Cell size.** 2 km matches the tiles. A smaller cell (1 km) means smaller dirty areas but more border crossings; the
  ring has to be at least as wide as an edit's reach (1 km today), so 2 km with one ring is the plan.
- **Roads.** World's lines are 94% roads. Roads near track matter (crossings, bridges, ground); roads far from any track
  might be kept only as coarse marks, or not at all, which would shrink everything.
- **Hand shapes across cells.** A hand-shaped line crossing a dirty cell's edge: the shape is kept, and the border point
  has to come from the shape, not from solving.
- **Mapped track (guides).** Stored per cell like roads.

## Risks

- **Results drifting from a full rebuild** at borders. Every stage checks cells against a full rebuild, and a dev flag
  keeps doing so (as `HB_LOCALCHECK`).
- **Passes that read the whole route in hidden ways** (a global index, a "nearest of all lines" search). Today's work
  found some (crossing pads made from lines before they're put back, decks searching every line). Each has to take the
  loaded window, not everything.
- **Moving 45 files off whole-route `NetworkLines`.** Keeping its API over the window first, then narrowing it, keeps the
  builder working at every step.
