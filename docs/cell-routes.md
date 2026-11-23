# Cell routes

The plan for making the route builder's track work in small fixed cells instead of over the whole route, so that
opening the builder and placing track cost the same whether the world is one county or the whole country. This is a
design document; stage 1 is built (see Cell format). It fills in phase 5 of `performance-plan.md` ("region streaming ... a
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

### Network files

Built in stage 3 (`Routes/NetworkCells.cs`, `Routes/RouteNetwork.cs`). Under `<route>/Network/`:

- `I_J.bin`, one per cell with anything in it (`TileId`'s name; the tile of a point is
  `route.Grid.TileAt(route.Projection.Forward(geo))`). A way is in the cell of its first point, a tagged node in its own
  cell. Ways with no position (what's left of an OpenStreetMap way taken out by hand) are in `Loose.bin`.
- `Index.json`: the format version, the grid the cells were cut on (zone and tile size: a change rewrites every cell),
  and per cell its hash (SHA-256 of the file, 16 bytes), size, way and node counts, and **`Reach`**: the other cells its
  ways pass through (their points and the boxes of the segments between them).
- A cell file is Brotli (quality 5) over a small binary layout: ways by id with delta-coded ids and node ids, points as
  delta-coded whole numbers of 1e-7 degrees with how the double is made from them (the JSON and PBF readers make them
  differently), or the raw double when neither gives it back bit for bit (track laid by hand). Everything reads back
  exactly.
- Every file is written to `NAME.tmp` and moved over; the index last. A save cut short can leave a way in two cells (the
  new one written, the old not yet): the load keeps one and writes both cells again on the next save.

`RouteNetwork` remembers each way's and node's stored object and cell; a save writes the cells of everything changed
since (as it was and as it is, so a way whose first point moves across a border leaves the old cell's file), then the
index and `Ids.json`. A way must be replaced whole (`Set`), never changed in place: the save also finds any way that isn't
the stored object, and warns if it wasn't marked. Old routes: `Load` reads `Network.json.gz` and `Nodes.json.gz` when
there's no index; the first save writes every cell and renames them `*.old` (nothing reads those).

**Loading a subset later (stage 4).** Everything needed to load only some cells is in the index: to load the network round
cell X, read the cell files of X and of every cell whose `Reach` names X (each way crossing X is stored once, in the cell
it starts in). Nodes need only their own cell. The load already decodes cells independently (in parallel) and keeps
which cell each way came from, so a window over some cells is a filter on which files it reads, plus saving only cells
whose every way is loaded. `Loose.bin` is small and always read.

On World (60,952 ways, 629 nodes, 2026-11-22): 1,570 cells (ways run beyond the 574 built tiles), 5.8 MB on disk against
8.2 MB for the old files; load 0.2 s against 0.5–0.8 s; a save after a small edit writes 1 or 2 cells in about 15 ms
against 1.8 s for the whole files.

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

## Cell format (stage 1, built 2026-11-22)

Code: `Highball/World/CellStore*.cs`. Files: `<route>/Cells/I_J.cell` for every cell with anything in it (572 for World) and
`<route>/Cells/index.bin`. Written whole after a cold open and after Ctrl+S (`TerrainSource.SaveCells`, in the background,
guarded by `EditorView._saves`); read whole on open (`CellStore.Read(route, index, cells)` takes a set of cells, but for now
it must be all of them). `Cache/Lines.bin` (`CompiledRoute`) is gone; writing the cells deletes a stale one.

### Inventory: every field, and where it goes

`NetworkLine` (one line):

| Field | Kind | Where |
|---|---|---|
| X, Z, Y, Ground, S, Junction, Shift, Sink, RawX, RawZ, MappedX, MappedZ (when as long as the line), Drape (5 per point) | per point | pieces |
| Structures, Layers | per segment | pieces (segment i with point i) |
| Index, Key, Chain, Rail, Class, Tracks, Gauge, Disused, Embedded, Exact, Mark, Width, Lanes, Oneway, Unpaved, Formation, Bed, BedHeight, MinX..MaxZ, Tiles, Nodes, Alignment, TurnoutZones (refer to turnouts), RailCuts, TieCuts, BedDrops, _halo | per line | head (the piece holding point 0) |
| _runBoxes | derived, lazy | dropped, made again when asked for |
| _origin | the last rebuild's line (frozen copies) | dropped (as `CompiledRoute` did: a line read is nobody's copy) |

The cut is decided per array by its length (count: per point, k x count: k per point, count - 1: per segment, anything
else whole in the head), so a field added later is kept whatever it is.

`NetworkLines` (`CellStore.Rules`; a field with no rule is kept in the index and warned about):

| Kind | Fields | Anchor |
|---|---|---|
| Lines | `_lines` | pieces |
| Indexes by tile | `_byTile` (order lines shape a tile in), `_pointsByTile`, `_ballast`, `_preparedKeys` | the tile |
| Anchored things | `Turnouts`, `Layouts`, `_pads`, `CrossingAssemblies`, `Bridges`, `_endShapes` (slope ends), `_detailRegions`, `_detailCells`, `_crossings`, `_railCells` (25 m grid) | their X, Z (Ex, Ez; the grid cell's middle) |
| Per line | `_shapingHashes`, `_linePins`, `_pinnedPoints`, `_wallsBeside`, `_bridgeEnds` (at the first end's point), `_extended`, `_surfaceShifts`, `_sideBySide`, `_continuing`, `_wallSpans`, `_railsNear`, `_byChain`, `_seenChains`, `_needGround` | the line's head, or the point named |
| By node | `Links`, `CrossoverProblems`, `SwitchOptions`, `Fitted`, `_nodePins`, `_spreadNodes`, `_forks`, `_widestAt`, `_junctionNodes`, `_roadEnds`, `_railAt`, `_junctionRounds` (junction heights, a list of rounds by node) | the node's point on a line |
| Route-wide | `_spacing`, `_quads`, `_ballastSamples`, `_handSet`, `_padsHash`, `Sizes`, `Curves`, the three locks | the index |
| Restored | `Ways`, `Shapes`, `BuiltTiles`, `_crossingObjects`, `Before`, `BridgeEdits`, `DetailCache` | put back from what's opened (`CellStore.Restore`) |
| Not needed after the build | `_liftCells` (LiftBridges' grid, let go at the start of FinishProfiles), `_lapWatch`, `_lapName`, `_laps` | dropped |

Things with no cell (a chain that made no line, 17,804 in World; 3 node pins on nodes on no line) go in the index.
Derived indexes are kept rather than rebuilt: several (`_railCells`, `_detailCells`, `_roadEnds`) are read without being
rebuilt, and keeping them is what makes the read-back exact.

### Layout

A cell file: a plain header (magic, format, I, J, codec, shuffle, sizes), then two compressed blocks.

- **Block A**, read first for every cell: the pieces (line index, the line's point count, first point, count, key, how each
  array is cut, and a count of tagged extension blocks, 0 for now: stage 2's border points go there), and the things
  this cell owns that others may refer to (kind, ordinal, type).
- **Block B**: each piece's array slices (byte planes: all first bytes, then all second bytes...), then one
  `GraphSnapshot` per cell: the heads (line index, mask of what was cut, then every other field), then the spread
  elements grouped by field, each with its ordinal in the route-wide list, set or dictionary.

References: `GraphSnapshot` writes a line, a registered thing (an item of a spread list of objects: turnouts, layouts,
pads, assemblies, bridges, slope ends) or the `NetworkLines` itself as `(kind, index)`: 0 the root, 1 line index,
2 + field the thing's ordinal. Reading makes every line and registered thing empty first (from the index's line count
and the cells' block A), then fills them cell by cell in parallel, then puts the lists, sets and dictionaries
together in ordinal order, so their order is what it was.

The index: magic, `FormatVersion`, `BuildVersion`, the inputs stamp, a hash of `Rules`, the line count, the schema (every
type the cells hold and its layout: `GraphSnapshot.LayoutOf`, field names and types all the way down through value
types), each spread field's count (and null or not), each cell's size and hash, then its own part laid out as a cell
(things with no cell, and the route-wide fields).

### Versions

- `CellStore.FormatVersion`: the files' layout. A mismatch: not read, worked out and written again.
- `CellStore.BuildVersion`: bumped by hand when laying, grading or shaping changes should show. Also keys the ground
  cache (`GroundCache`), which used to be keyed by the assembly's MVID and so reshaped every tile after every build.
- The schema: a type the cells hold laid out differently in this build (a field added to `Turnout`, say) means the
  cells aren't read, rather than read as garbage. A change of code alone keeps them.
- The inputs stamp: `CompiledRoute.StampOf` without the MVID (input files including `RouteNetwork.Files`, `Track.bin.gz`
  size and time, built tiles' heights size and time, grade crossings).
- Each cell's hash in the index: a cell from another written set isn't read.

### Measured (World, 2026-11-22)

| | |
|---|---|
| `Cells/` total | 162 MB in 572 cells + a 206 KB index (from 407 MB raw); `Lines.bin` was 453 MB |
| Biggest, median cell | 1,553 KB (`-102_348`), 201 KB |
| Pieces | 39,436 for 33,345 lines; 554,780 other things spread |
| Raw bytes, biggest parts | `_pointsByTile` 48 MB; X, Z, RawX, RawZ 47–48 MB each; S, Y, Junction, Ground, Layers 24 MB each; heads 19 MB; Drape 14 MB |
| Write (all cells, 16 threads) | 3.5–3.7 s at a cold open (spread 0.5, encode 2.2–2.6, files 0.5–0.7) |
| Read (all cells) | 1.2–1.4 s: files and hashes 0.1, made 0.2, read 0.65, put together 0.2–0.4 (`Lines.bin`: 2.6 s) |
| Open, warm | 5.6–6.0 s (stamp 0.3, cells 1.3, ground keys 2.0, reshape from the ground cache 1.2); and the same after a new build |
| Open, cold | 52–57 s (unchanged: solving, ground, profiles, then writing) |
| Peak working set, warm open | 3.3–3.6 GB (whole game) |

Compression, every cell's blocks summed on one thread:

| | Size | Pack | Unpack |
|---|---|---|---|
| zlib optimal, byte planes | 181 MB | 9.5 s | 0.88 s |
| zlib optimal, as laid out | 242 MB | 12.3 s | 1.23 s |
| brotli 4, byte planes | 169 MB | 8.7 s | 1.60 s |
| **brotli 5, byte planes (chosen)** | **163 MB** | 15.7 s | 1.56 s |
| brotli 5, as laid out | 190 MB | 26.2 s | 2.43 s |
| brotli 9, byte planes | 162 MB | 53.7 s | 2.22 s |

Byte planes win everywhere (25–30% smaller, and faster to unpack). Brotli 5 is 10% smaller than zlib for 0.7 s more
unpacking on one thread, under 0.1 s on all of them; writes are in the background.

Checked (`HB_CELLCHECK=1`): an open from the cells against the lines worked out afresh, every field of `NetworkLines` and
everything under it, 89 million values, bit for bit: 0 differ; every line's shaping hash and every tile's shaping key: 0
differ. After writing (at a cold open, and after an edit and a save) the cells read straight back against the lines in
memory: 0 differ, and no object written into two cells.

### For stage 2 (edits writing their cells) and stage 4 (streaming)

- **A piece finds its line by line index**, and checks the line's key. Indices are only stable within one written set,
  which is all stage 1 needs (every cell's hash is in the index). Rewriting some cells means either keeping line indices
  stable across a rebuild or naming lines by chain (the piece header has the key; it should carry the chain id, and the
  heads' `Chain`) and resolving by name.
- **References are (kind, ordinal)**: a line by its index, a turnout, layout, pad, assembly, bridge, slope end or switch
  option by its place in the route-wide list or dictionary. Order is kept by ordinal, so rewriting one cell's
  turnouts renumbers everyone's. Stage 2 needs stable names here too (turnout by node, bridge by key, layout by node).
- **Where things went**: per-line dictionaries with the line's head; by-node ones at the node's first point on a line;
  by-tile ones in the tile. A thing that reaches into other cells (a bridge, a long turnout's legs) is in its anchor's cell
  only, and nothing yet lists it in the others: a partial read has to load the anchor's cell or know to.
- **Reading a set**: `Read` takes a set, but returns null unless every line's pieces and every referred-to thing are in
  it. Stage 4 needs `NetworkLines` to be a window: lines held in part, and references into unloaded cells.
- **Border points**: each piece has a count of tagged extension blocks (0 now), for its border point.
- **Version policy**: a field added to `NetworkLines` gets a rule in `CellStore.Rules` (unruled ones are kept in the
  index, with a warning). Changing any held type's fields, or the rules, makes the cells unreadable, so they're rebuilt.

## Packs (stage 5, built 2026-11-22)

Code: `Routes/WorldExport.cs`, `Routes/PackFile.cs` (pack version 4), `Routes/PackSource.cs`, `Routes/PackTrack.cs`,
`World/TerrainSource.Pack.cs`. A player's game reads the finished track from the pack; it never solves, grades or reshapes
at open.

**What a pack carries** (`REGION.hbpack`, files after the tiles):

- The tiles as before (heights as decimetre residuals, sculpting baked in, cover).
- `Cells/index.bin` and every `Cells/I_J.cell` of the route, as written by `CellStore` (byte for byte). The export writes the
  route's cells first if they aren't of the lines as they stand (`CellStore.Write` with `StampOf`); from the editor it waits for
  any cell write under way and holds off the save's own.
- `Ground/I_J.bin` for every tile shaping changed: its shaped ground as cut and fill off the heights as built (whole
  centimetres, run by run, delta-coded, byte planes, Brotli 9: `PackFile.EncodeGround`). The game adds it to the pack's heights
  (`PackSource.LoadShaped`), so shaped ground meets the ground round it with no step. Not unpacked: read with the tile.
- The small track files still read beside the cells: `Route.json`, objects, bridge and switch settings, hand shapes,
  signalling, and the network (`RouteNetwork.Files`) that `CellStore.Restore` puts the ways back from.
- `Pack.json` (`PackFile.About`): pack version, `CellStore.FormatVersion` and `BuildVersion`, ground version, the game
  version that made it.

**Region borders.** For now every region's pack carries every cell of the route: `CellStore.Read` reads only the whole set
(a line's pieces, a turnout or a bridge can be in cells of other regions, and a thing is in its anchor's cell only). So a
region's pack has all the world's lines, including some over ground it doesn't have. Once cells can be read as a window
(stage 4), a region's pack carries the cells of its tiles plus every cell holding a piece of a line, or the anchor of a thing,
that has a piece in its tiles: the same closure a window round the region would load. The server track is unchanged: a region
keeps the edges whose middle is in its tiles (`WorldExport.RegionKeeps`), and the game playing a region's pack checks the same
subset (`PackTrack.Checksum`, through `RailGraphFile` so the renumbering is the server's).

**Ground exact under shaped ground.** Ground away from rail is kept within 1 m (`PackFile.TerrainTolerance`); with shaped ground
added to that, roads away from rail would sit up to 1 m off their beds. So the export also keeps ground exact in every 100 m cell
(and one round it) where a shaped sample would be more than 0.1 m off (`NearShaped`, worked out for the whole route first so
tiles sharing a border judge it alike). That's most of World, roads being everywhere: tiles 93 MB against 30 MB.
`HB_PACKROUGHROADS=1` packs as before (rail only).

**Opening** (`PackSource.Open`, then `TerrainSource.OpenPack` from `RouteWorld`): `Pack.json` checked, the track files
unpacked to `%APPDATA%/Highball/PackCache/WORLD/REGION-HASH` (the hash of the pack's path; only when the pack's changed, each file
checked against the pack's hash), the cells' index read and checked (`ReadIndex`, `Index.Stale(index.Stamp)`: build version,
rules and schema; the inputs stamp is the editor's business). Then the cells read, `Restore`, every tile read with its shaped
ground, and the road marks draped. Anything this game can't read (an older pack, another cell format or build, another schema)
is refused with `PackOutdatedException`: "World.hbpack needs re-exporting: ...". `CellStore` reads a route's cells only when it
isn't a pack's, so the pack hands it the cache folder as a route of its own (`PackSource.CellRoute`).

**Still shaped on the player's machine:** full-resolution tiles near the camera are shaped as they stream in,
`TerrainSource.Want` calling `Lines.Conform` on the heights as loaded, as in the editor (and `ConformFine` for detail inserts).
For a pack that should be `route.Pack?.LoadShaped(id).Heights` with no `Conform`; `TerrainSource.cs` is stage 2's, so it's left.
The coarse heights and everything at open come from the pack.

**Checked by the export** (read back): tiles and cover as before; every embedded file byte for byte; shaped ground's cut and
fill within 5 mm; the pack opened as Single Player opens it, its cells read, and the rail graph built from them the editor's and
of the same checksum as the server track's.

### Measured (World, one region, 2026-11-22)

| | Before (lays out) | After (cells) |
|---|---|---|
| `World.hbpack` | 38.7 MB | 279.5 MB: tiles 93.0, shaped ground 14.8 (552 tiles), cells 162.7 (572 + index), network 8.2, rest 0.6 |
| Export, after a warm open | 15 s | 54 s (cells current; NearShaped's first pass, tiles at Brotli 9, the check's 2–3.5 s read-back) |
| Open from the pack | 52.7 s twice (solve 4.8 s or read, profiles 10–11 s, reshape 28–33 s) | 4.7–5.4 s to the terrain open (files 1.1, cells 1.3, tiles 2.3); pack open 60 ms, 0.8 s when it unpacks |
| Peak working set at open | 6.0 GB | 1.9–2.1 GB |

Shaped ground: kept apart 14.8 MB, against the ground cache's 111.6 MB; baked into the tiles instead it would be 93.6 MB against
107.8 MB apart (31.5 MB against 44.7 with rough roads). Baking is smaller, but the game's streaming still shapes the heights it
loads (above), so the heights as they came have to be in the pack; once that's taken out, baking saves 14 MB.

The cells dominate a pack (they're 58% of it): X, Z, RawX, RawZ, `_pointsByTile`, per point, for 33,000 lines of which 31,000
are roads (see Open questions: Roads).

### For stage 2 (stable names, partial rewrites) and stage 4 (streaming)

- A pack carries the cells byte for byte with the index's hashes, so whatever stage 2 changes in the format needs nothing here
  but `CellStore.FormatVersion` bumped; old packs are then refused plainly. A partial rewrite that keeps one consistent written
  set (an index whose hashes are the cells') packs the same way.
- `PackSource` relies on `ReadIndex` returning null for another format, `Index.Stale` (with the index's own stamp) for build,
  rules and schema, and `Read` taking the whole set. If stamps or hashes move into the cells, or `Read` takes a window, a pack
  still opens with `Read(CellRoute, index, index.Cells.Keys)`.
- `ReadIndex` logs "working the lines out" when it refuses a format; for a pack that isn't what happens (the pack's refused).
- Stage 4: regional packs cut down to the closure above, read through the same window the builder uses, cell by cell as the
  train moves (the pack's file entries already give each cell's offset, so reading them from the pack without unpacking is
  a small step).

## Edits by cells (stage 2, built 2026-11-22)

Code: `TerrainNetwork.RebuildNetwork` and `LocalFor`, `WayIndex`, `LocalRebuild` (Dirty, Context, Seen), `NetworkLines.Outside`,
`AddContext`, `FreezeFarRail` (rail outside the cells put back by name), `CellStore.Merge` and `CheckMerge`, `CellStore.Write(only)`,
`NetworkMeshes.FindRuns(changes)`.

An edit to track alone (anything else, a changed level crossing, new shapes, switch sizes or curves, built tiles: in full, as
before):

1. **Dirty cells.** The cells of the places the edit changed (its ways as they were and as they are, and `RebuildAround`), the
   cells of every line of the chains through the changed ways' nodes (a line split by a new switch is new lines its whole
   length) and of every rail line with a point within `RailOuter` (1 km) of the edit, then one ring. **Context**: one ring more.
2. **Handed ways.** `WayIndex` (kept up to date way by way, built in the background at open) gives the ways through the context
   cells, made whole as chains (through every node only two ways end at), plus every way meeting one of those at a node, made whole
   too: so every chain through the cells is formed, fanned and fitted with all its tracks, as in a full build.
3. **The passes on the window only.** `Build` lays the chains with any of their length in a dirty cell (roads, marks) and every
   handed rail chain; every other line over the context is taken from the last lines frozen (`AddContext`), rail laid outside the
   dirty cells is put back by name (`FreezeFarRail`), and the existing holds (`Keep`, `HoldFarRail`, `KeepFarPads`, halo) work as
   before but over the window's lines (`LocalRebuild.Seen`), never the route's.
4. **Merge** (`CellStore.Merge`): a copy of the last lines in which each line the window laid differently (bit for bit) takes the
   place of the one of its name, new lines fill the places of those gone and then the end (lines from the end fill places left
   over, keeping their objects: `NetworkLine.MoveTo`), and every spread field's entries anchored in a dirty cell are the window's
   (with previous's kept where the window didn't work one out and it's of no line changed), every other one previous's. The
   window's objects are pointed at the lines and things they'll be with (frozen copies at their originals, things alike at
   previous's). Turnouts, layouts, pads, bridges and the other registered things alike field for field are previous's objects.
5. **The border.** A line laid again that reaches out of the dirty cells is held there (`NetworkLines.HoldAtBorder`): its points
   outside are the last line's of its name exactly, those within 600 m inside eased onto them, so outside it is what it was by
   construction; what's anchored along it outside (a bridge on a 14 km guide mark, a turnout) is previous's, copied to name the line
   laid (`Repointer`; a turnout copied so has its other lines copied too, sharing their arrays). A window's line with no point in a
   dirty cell is previous's; one with no line of its name there is the window's own making at its edge (a chain whose far ways
   weren't handed) and is left out.
   **Paths** (logged, `Local rebuild path:`): `cells (N)`; `cells grown by a ring` when the edit changes something outside the
   cells all the same (a thing outside naming a line laid differently and not held); `whole route` (every way handed, as before
   stage 2) when the cells would be more than 40 (`MostCells`) or the grown window still isn't enough.
6. **Reshape, take in, save** only what changed: the merge's lines made and gone drive the reshaping and the meshes (`FindRuns`
   for those lines only; decks look along the lines near them, `NearLines`, not every line); a save writes only the cells touched
   since the last write (`TerrainSource._unsavedCells`), and the index.

### Identity and the format (FormatVersion 4, BuildVersion 2)

- `NetworkLine.Name`: the chain's id and where along it the line starts. Lines in a tile shape it in an order by name, not index
  (`Priority`), shaping hashes take the name, road ends are ordered by name: a line's place in the set no longer changes anything
  it builds.
- Every spread element's place in its field (`OrdinalOf`) is a hash of what it is (a dictionary's key, a set's or list's item; a
  line by name, a registered thing by `IdentityOf` and all it holds), not its position: a cell left as it was still fits cells
  written later. References to registered things are by that place (32 bits; one alike already there takes the next). Lists read
  back in that order; `HB_CELLCHECK` compares lists in it and sets and dictionaries by content.
- A tile's ground key takes only the pads near it (it took every pad, so any crossing changed changed every tile's key).
- `Track.bin.gz` (`SolvedTrack`) is gone: it was good only until the network's next edit, and the cells keep the lines finished.
  A full build always solves; writing cells deletes an old `Track.bin.gz` and `Track.stamp.bin`; it's no longer in the stamp.
- Bridges are found with the lines at a full build and kept in the cells (they were found when the meshes were first built, so
  missing from cells written at an open, and the first save after had to write every cell).

### Measured (World, 2026-11-23)

Background ms (`Track rebuild, ms`), main thread ms (`taken in`):

| Edit | Before (`cells/base`) | Path | After | Taken in, before / after |
|---|---|---|---|---|
| Branch off the main line (default) | 5,913 | cells (25) | 3,392 (build 1,297, profiles 955, merge 479, reshape 418) | 720 / 196 |
| Spur in a yard | 6,903 | cells (25) | 3,386 (build 1,032, profiles 733, merge 416, reshape 948) | 552 / 115 |
| Branch near a rail bridge | 6,710 | cells (28) | 4,767 (build 1,344, profiles 1,050, merge 473, reshape 1,608 over 7 tiles) | 824 / 224 |

(`HB_LOCALCHECK=1` runs, its full rebuild timed apart. In runs without it the same edits took 2.6 s and 3.9 s, take-in 0.5-0.55 s:
take-in varies with what the meshes are building at the time.) Over the three edits: 6, 1 and 3 lines held at the border, 0
re-pointed things needed (the held lines came out as they were everywhere), 14 window-edge lines left out (yard), no spill, no
growth, no fallback. Before the border hold the bridge and yard edits spilled and took 9.0 s and 20.9 s; the yard's 15 s reshape
over 32 tiles was the grown window's (lines gone far out); with the cells holding it shapes 4 tiles in 0.9 s.

Exactness (`HB_LOCALCHECK=1`): default and yard: every line in the dirty cells (3,263 and 2,430) within 2 mm in plan and 4.8 mm
(3.8 mm) in height of a full rebuild, none missing by name; bridge: 3,530 lines, 1 over 1 cm in plan (1.6 cm) and 2 in height,
worst 12.6 cm, a road laid again near the edit at (-201250, -697511), not explained. Outside the cells touched: 0 lines changed;
of ~375,000 spread entries, ~1,100 aren't previous's objects (850-940 slope-end shapes, copies pointed at the merged lines;
~110 each of `_railsNear` and `_wallSpans`, caches the shown lines filled meanwhile). No reference to a line or thing not in the
merged lines. Rail steps at junctions as in the full rebuild (0.019, 0.009, 0.000 m). Things in the dirty cells differ in count
from a full rebuild where the window works them out round frozen lines (layouts 80/71, 64/61, 76/72; a few bridges unmatched;
`_surfaceShifts`, `_sideBySide`).

Saves (`save_test.sh`, `HB_CELLCHECK=1`): two edits, each saved. The first save after cells written by an older build writes them
all (a field came since); the second wrote 26 cells of 576 (28 asked for, 550 left as they were; spread 1,100 ms over the whole
route, encode 301, files 121) and read straight back against the lines in memory, 88.8 million values: 0 differ. The open after
reads all 576 warm, 33,346 lines, as many as a fresh build of the same network. Against a fresh build it can't compare bit for
bit any more: a merged set keeps lines' places, a fresh one numbers them in solve order, so every index-keyed field differs.

## Streaming (stage 4, built 2026-11-23)

Code: `CellStore.Read` (a window), `ReadFor`, `CellWindow` (`CellStore.Read.cs`), `NetworkLine.Absent`, `NetworkLines.Window` and `Warm`,
`TerrainSource.Stream.cs` (`Stream`, `ReadWindow`, `ChangeTo`, `WholeTrack`), `TerrainNetwork.GrowFor`, `EditorView.PollStream`,
`NetworkOverview`, `WorldExport` (a region's cells). FormatVersion 5.

### The window

The builder's `NetworkLines` is a window over the route's cells, **complete** in the cells within `HB_WINDOW` rings of the camera's cell
(default 4: 9 x 9 cells, 18 km square, past the track's 6 km drawing; `HB_WINDOW=whole` reads every cell, as before). Complete means every line with a point there or
reaching its ground (its tile's `_byTile`) is loaded, and everything anchored there: what's shown, shaped, edited and written back. To be
complete in cells C, `Read` reads C, the ring round C (so what's anchored in C finds the lines near it: the rails beside a bridge end, a
road under a deck), and every cell the lines of those reach into (`Index.Reach`). Lines are loaded whole.

- **Places.** A window's lines keep their places in the route's set (the index's `LineCount`). A place whose line isn't loaded holds
  `NetworkLine.Absent`: one shared object, no points, no nodes, bounds that take in nothing. So every int naming a line by place
  (`_byTile`, `_pointsByTile`, `Fitted`, `_continuing`, `_needGround`, the meshes' runs) means the same in every window and on disk, and a
  line read again stands in its old place. The merge no longer moves lines to fill gaps (`Moved` is always empty): a place left over holds
  `Absent`, and a whole read takes holes.
- **What a window can't stand behind.** From a cell that isn't complete, the lines' tile indexes (`_byTile`, `_pointsByTile`, `_ballast`)
  and detail regions aren't taken, and anything naming a line or thing not loaded is dropped. In a complete cell such a name is dropped where
  it's a list's or set's item or a dictionary's key, else read as null (a bridge end's `Under`; a list of lines has those not loaded taken
  out), and the cell is **tainted** (`CellWindow.Tainted`: never written from the window). None came up on World.
- **Made again, not read.** What's made whole when first asked for (`_bridgeEnds`, `_wallsBeside`, `_endShapes`, `_byChain`, `_railCells`,
  `_roadEnds`, `_railAt`, `_needGround`, the detail regions: `CellStore.WindowRemade`) can't be made whole by a window. It's left out of a
  window's read and made again from the lines loaded, in the background (`NetworkLines.Warm`); a window's write writes it absent, so a whole
  read makes it again too.
- **The ground.** Complete tiles are shaped as at any open (the ground cache, keyed by what shapes them). Other tiles are shown as last
  shaped (`ShapeAsKept`: the ground cache under the key it was kept with), and so are their full heights as they stream (`Want`). Finer
  ground (detail regions) is shaped in complete cells only. A tile's key now takes only the bridge ends within `EndReach` (300 m) of it, not
  every end of every line over it: the far ends of a long line aren't loaded at a window's edge, and keyed with them its edge tiles were
  shaped again for nothing (every key changed once: the first open after shapes every tile again).
- **Writes.** A window writes only cells it's complete in, untainted (`CellWindow.Writes`), and only with an index of the rest to keep. It
  never writes what it read from a cell it isn't complete in (`CellWindow.Foreign`, by field and place: with only the window's lines to go
  by, a thing anchored by a node or a chain on lines not loaded has no cell, and went into the index besides its own cell: "in two cells"
  at the next whole read), and of what has no cell only what it read from the index's own part (`CellWindow.Rest`). The index's spread
  counts aren't the route's after a window's write: written -1, not checked.

### Format 5

- The index has, per cell, its **reach**: the other cells the lines with a piece in it, or reaching its ground, have pieces in. A cell not
  written keeps its reach (a line through it laid again changes its pieces there, so it's written).
- A cell's block A ends with its **reach lines**: the places of the lines reaching its ground with no piece in it.
- Places may have no line (holes).
- Older cells: "format 4, this build reads 5; working the lines out" (one cold open). A pack of format 4 is refused ("needs re-exporting:
  its cells are of format 4, this game reads 5").

### Streaming

`EditorView.PollStream`, once a frame: when a cell within the rings isn't complete, or a complete one is more than a ring further out
(`WindowStale`; the ring more is hysteresis), `TerrainSource.Stream` reads the window again round the camera in the background
(`ReadWindow`: the cells read, what isn't kept in them taken from the lines shown, `RestoreFrom`; tiles keyed for the ground cache; `Warm`),
and works out the change from the lines shown (`ChangeTo`), place by place: the same line read again (key, name, count) stands, its meshes
untouched; any other is made (loaded) or gone (let go); a tile newly complete whose ground the lines shape otherwise than it's shown is
shaped. It's taken in on the main thread as an edit's change is (`TerrainSource.Apply`; `NetworkMeshes.Replace` with Made and Gone, the runs
of those lines only; crossings' decks kept for lines read again). Never while a rebuild is under way or cells are being written
(`TerrainSource.CellsLock`), and **held while anything laid isn't written to the cells yet** (`CellsUnsaved`: reading them would lose it),
until the next save.

### Edits in a window

1. `LocalFor` works out the dirty cells and context from the window, as before.
2. `GrowFor`: the context has to be complete (the edit's laid against it). If it isn't, the window's read again complete in it too, and the
   local rebuild worked out again over it (twice at most). With the camera at the edit and 4 rings it is already; with 3 rings the
   standard edits grew it by 5-10 cells (0.7-1.0 s).
3. Laid and merged as before, over the window: the merge copies the window's lines and things, not the route's. A road chain not near
   the edit is kept as the last lines have it whether or not the window saw it (`Keep`): a window has seen only the chains anchored in the
   cells it read, and laid again those far roads were sampled and profiled for nothing.
4. The cells the merge touched have to be complete to be written. If they aren't, the window's grown to them and the edit laid again. (Asking
   up front for every cell the lines over the dirty cells reach grew the window by 46-74 cells for the standard edits; the merges touched only
   the dirty cells.)
5. Growing reads the cells, so it can't be done while something laid isn't written: an edit then needing cells not loaded lays the whole
   route ("Local rebuild path: whole route (... track laid that isn't saved yet in the cells)").
6. The window as grown is taken in first (`NetworkChange.Streamed`), then the edit's change.

`HB_LOCALCHECK` still lays the whole route from every way for its comparison (the network isn't a window).

### The rest

- **Exports** are of the whole route (the server needs the whole rail graph): from a window, the editor writes the window's cells if
  they're not current, then reads every cell (`TerrainSource.WholeTrack`).
- **Regional packs.** A region's pack carries `ReadFor` of its tiles, its cells read as a window complete in them, and the index whole; one
  pack of the whole route carries every cell. A pack opens as a window complete in its own tiles (`TerrainSource.OpenPack`); the export's
  check reads it so. Driving doesn't stream cells as the train moves.
- **The network in brief** (`Network/Overview.bin`, `NetworkOverview`): per network cell its ways thinned to within 5 m (rail) or 15 m
  (roads) and each kind's length, written with the network's cells (a cell's part again only when it's written). The Planner draws its map
  and counts its lengths from it while its network is as saved, rather than from every way's every point.
- **The stamp** (`StampOf`) hashes the network's index (which holds every cell file's hash) rather than every cell file.
- **Placed stock** whose track isn't loaded is left off as it's put back (the panel keeps it); stock let go as the window moved is kept
  as saved when the stock's saved (`TrainSession.SaveStock`; it was dropped from `Stock.json`).
- **Junction markers** (`Junctions.json`, the Planner's): a window writes those of its complete cells and keeps the rest.
- **Chunk jobs** build from a copy of their chunk's runs: a take-in edits the runs in place while a job iterates them (an
  `IndexOutOfRangeException` when a window was grown and an edit taken in on the same frame).

### Measured (World, 2026-11-23)

Same machine and route copy for both; `cells/base` with its own build (Logs/base-bin), its cells written by it first. The camera at the
default edit's place (42.38216, -83.46732) for the opens.

**Opens** (warm; terrain open from `Terrain open, ms`; to usable: from "Routes: Ok" to the first rail graph):

| | Cells read | Lines loaded | Cells (read + restore) | Terrain open | To usable | Peak working set | Managed heap at open |
|---|---|---|---|---|---|---|---|
| `cells/base` | 572 (165 MB) | 33,345 | 1,348-1,433 ms | 4,044-4,384 ms | 9-10 s | 3,249-3,255 MB | 1,771-1,911 MB |
| window, 1 ring (9 complete) | 65 (31 MB) | 3,117 | 437 ms | 3,163 ms | 7 s | 1,377 MB | 470 MB |
| window, 2 rings (25) | 73 (34 MB) | 4,776 | 448 ms | 3,339 ms | 7 s | 1,493 MB | 446 MB |
| window, 3 rings (49) | 102 (48 MB) | 6,187 | 569 ms | 3,470 ms | 7 s | 1,580 MB | 495 MB |
| window, 4 rings (81, the default) | 114 (53 MB) | 8,166 | 618-625 ms | 3,512-3,761 ms | 7 s | 1,566-1,572 MB | 432-648 MB |

What's left of a windowed open is the route-wide part: the stamp (0.3 s), every tile's heights read for its coarse copy and shaped as kept
(1.3 s), the network loaded whole (0.7-0.8 s, twice: the editor and the Planner, from the old whole files here). The window's own read is
0.4-0.6 s for 65-114 cells (files 26-32 ms, made 59-112, read 177-320, put together 104-197).

**Flying** west at 200 m/s with a 2-ring window (`--fly 200`), 80 s, off the end of the rail: a step every 2 km (10 s), 8 steps. Each read
49-78 cells (15-34 MB) in 0.1-0.6 s in the background and worked out its change in 1-139 ms; taken in on the main thread in 1-46 ms (terrain
0-5, line meshes 1-45); 38-116 lines loaded and 476-1,404 let go a step, 4,832 lines in memory falling to 749 off the rail, 2 tiles shaped
again in all. The worst frame of the second a step came in: 39-91 ms (82, 91, 65, 56, 49, 48, 39), against 10-24 ms in the seconds between
(and 100 ms frames at the start, the first meshes building, as at any open). The take-in accounts for under half of it; the rest is likely
the collector (a step allocates 100-300 MB; the managed heap swings 0.65-1.7 GB): with the collector's pauses logged (4 rings, the default, 102-126 cells a step read in 0.3-0.8 s), it stopped every thread for 110-242 ms in all during each step's read (0-1 full collections), and the slow frames fall in those seconds (50-100 ms). The read allocates its cells' bytes, every line's arrays and every thing afresh; reading less (only the cells a step adds) or reusing buffers is what would take it away. Working set 1.75-2.5 GB once settled (3.7 GB at
the first step, meshes still building). The window always had the cells round the camera complete before it got near their edge (a step
takes under 0.6 s, 120 m at this speed; the window reaches 4 km).

**Edits** (`edit_test.sh`, `HB_LOCALCHECK=1`, the camera at each edit), background ms (`Track rebuild, ms`, without the check) and
main thread (`taken in`):

| Edit | `cells/base` | 4 rings (default) | 3 rings | Merge, base / 4 rings | Taken in, base / 4 rings / 3 rings |
|---|---|---|---|---|---|
| Branch off the main line (default) | 3,963 | 3,671 | 5,222 (995 growing the window) | 482 / 348 | 151 / 169 / 170 |
| Spur in a yard | 3,495 | 4,034 | 4,470 (691) | 415 / 296 | 87 / 230 / 224 |
| Branch near a rail bridge | 4,887 | 4,020 | 5,073 (1,031) | 517 / 342 | 174 / 191 / 198 |

(`cells/base` here on this machine today; stage 2 measured 3.4 / 3.4 / 4.8 s.) With 4 rings no edit grew the window; with 3 they grew it by
the context's cells outside it (5, 5, 10), reading the whole window again. The yard is still 15% slower than `cells/base`: its rail runs on
out of the window, and rail laid outside it can't be put back by name from lines not loaded (`FreezeFarRail`), so it's profiled (1.26 s
against 0.74). Take-in is 20-140 ms longer: the window's grown or read lines show up as more runs found again.

Exactness as before, all three: default 3,263 lines in the dirty cells, 0 over 1 cm (worst 2.0 mm in plan, 4.8 mm in height); yard 2,430, 0
over 1 cm (3.8 mm); bridge 3,530, 1 over 1 cm in plan (1.6 cm) and 2 in height, worst 12.6 cm (the same road near (-201250, -697511)).
Outside the cells touched: 0 lines changed; 10-76 slope-end shapes not previous's objects (as stage 2's copies). No reference to a line or
thing not in the merged lines. The merges touched only the dirty cells (25, 25, 28).

**Saves** (`save_test.sh`, `HB_CELLCHECK=1`; the first edit with the camera at it, the second from the camera's start, where the window
had nothing: grown to 105 cells for it): each wrote only its cells, 23 and 26 of 572 (25 and 28 asked for, the rest left as they were),
spread 249 and 232 ms over the window (stage 2: 1,100 ms over the whole route), encode 338 and 278, files 80 and 71. Read straight back as
the same window, 12.9 and 14.1 million values: 1 differs, `_seenChains` (95-104 chains the edit's build saw whose lines aren't loaded: not
written from a window, by the rule above; they're laid as unseen next time). Ground keys: 16 of 38 and 18 of 42 complete tiles differ (the
bridge ends are made again on read from every line read; the merged lines kept those the edit's laid lines found, which can differ beside
the window's edge): those tiles are shaped again at the next open. The index after them is 434 KB with only what has no cell in it (3
node pins and 17,804 chains), and the next open reads all 572 cells whole. The network's overview: 1,570 cells, 156,812 points, 823 KB,
worked out in 0.22-0.28 s for all of them (the first save after the old whole files).

**Packs and servers**:
- Export from a 2-ring window: the whole route's track read for it (33,345 lines), exported in 40 s; World.hbpack 282.6 MB with all 572
  cells; World.hbserver 709 edges, 188 switches, checksum 47FAA460, as on `cells/base`; the pack's cells read back give the same graph.
- `--pack World/World`: opens in 5.1 s to the terrain (a window complete in all 574 tiles: the whole route); `--fly` streams the tiles.
- `--host World/World` and `--join 127.0.0.1`: both players' track checksum 47FAA460 as the server's. The server loads only its
  `.hbserver` (the whole region's rail graph); it doesn't read cells.
- Two regions (the copy's tiles split at I = -105 by a `Regions.json` made for the test, then removed): Midwest.hbpack, 187 tiles, carries
  215 of the 572 cells (112.1 MB); West.hbpack, 387 tiles, 414 cells (73.7 MB); each pack's cells read as a window of its tiles give its
  server track's checksum (Midwest 513 edges, 56F5995B; West 196 edges, 29284070). `--pack World/West` opens with 414 cells read, 10,184
  lines, in 3.1 s; `--host World/Midwest` and `--join`: both 56F5995B, as the server.

### At 100 times World (the USA route), from the per-cell costs

World: 574 tiles, 572 cells, 33,345 lines, 60,952 ways. Times 100: ~57,000 cells, 3.3 million lines, 6 million ways.

| | `cells/base` | Stage 4 |
|---|---|---|
| Cells on disk | 16.5 GB (0.29 MB a cell) | the same |
| Cells' index | ~33 MB | ~43 MB (200 B a cell of reach; 30 MB of it the index's own part) |
| Network on disk | 0.6 GB per cell, + 80 MB of overview | the same |
| Tiles, ground cache | 18 GB, 11 GB | the same |
| Track read at open | 150 s, all of it (1.5 s x 100) | 0.5 s for the window round the camera, + ~0.1 s of places, + **~10 s for the index's own part** (17,807 things for World, read at every window read: 1.8 million) |
| Track in memory | ~100-160 GB managed (0.9-1.6 GB x 100): can't | 0.4-0.5 GB, the window, + ~0.1 GB of places and the index's own part |
| Network at open | 20 s, whole, and its memory x 100 | the same (not a window yet) |
| Terrain at open | 130-140 s (every tile read), ~6 GB of coarse copies | the same (not streamed) |
| Stamp | every network cell file hashed (0.6 GB) + 57,000 tiles' sizes | the network's index hashed (~30 MB) + the tiles' sizes (~1 s) |
| An edit | whole-route merge and save spread, minutes | the same as on World: ~5 s, the merge 0.3 s and the save's spread 0.25 s over the window (+ tens of ms over the places) |
| A stream step | - | 0.1-0.6 s, + ~10 s for the index's own part until it's cut down |
| A region's pack | every cell of the route (16.5 GB) | its cells and those its lines reach (Midwest: 38% of World's cells for 33% of its tiles) |

So at national scale the track no longer decides the open: it's the terrain's every-tile read (~2.5 minutes), the network loaded whole
(~20 s), and the index's own part (~10 s, also at every stream step). Those three are what's next, in that order of cost; the index's part
is the cheapest to fix (chains with no line anchored by their first way, or dropped).

### Not done, and what still reads the whole route

- **`RouteNetwork` isn't a window.** The editor loads every network cell at open (0.2 s for World from cells; 0.7-0.8 s from the
  old whole files), and so does the Planner. Making it one is the same shape as the lines' (Index.json has each cell's `Reach`): load the
  cells round the complete ones, hold changed cells while unsaved, read a way's new cell before writing it; `WayIndex`, `LocalFor` and the
  tools then see the window. `HB_LOCALCHECK` and the whole-route fallback would load all of it.
- **The terrain's tiles.** Every tile's heights are read at open for its coarse copy (1.3-1.5 s for World), and every coarse copy and its
  land cover are kept. At national scale this is the largest cost left at open; it needs the terrain itself to stream (a coarse pyramid).
- **The index's own part** (what has no cell: 17,807 things for World, 17,804 of them `_seenChains`, chains that gave no line) is read at
  every window read, ~0.1 s for World. Chains with no line could be anchored by their first way's cell, or not kept.
- **Places for every line of the route.** A window's set of lines is as long as the route's (`Absent` in most places), and the read makes a
  few arrays that long: 33,346 places for World, ~3.3 million at 100 times. Cheap (tens of MB, tens of ms) but not nothing.
- **Driving** reads a region's pack as one window, not streamed as the train moves; multiplayer needs the region's whole rail graph (the
  server's) to check against, which a streamed client would take from the region's `.hbserver` rather than its lines.
- **A long line is one object.** A line laid again changes its pieces in every cell it crosses (their point counts), so the cells touched by
  an edit include all of every line laid again; with `HoldAtBorder` that was the dirty cells in all three standard edits, but a main line
  laid again its whole length would touch all its cells.
- **Window growth reads the whole window again** (0.6-1.2 s for 5-10 cells more), rather than only the cells it adds.
- **Unsaved edits hold the window** where it is (until a save); an edit needing cells not loaded then lays the whole route.

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
   the Planner draws from the overview. Gains: opens in seconds and memory follows the camera, at any size. Built 2026-11-23 for
   `NetworkLines` (see Streaming); `RouteNetwork` and the terrain's tiles still load whole.
5. **Packs and servers from cells.** Packs carry cells; driving streams them; the server graph is built from them. Built 2026-11-22;
   regional packs carrying their own window's cells 2026-11-23; driving doesn't stream them yet.

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
