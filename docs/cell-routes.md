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
5. **Too small a window**: a line laid differently with points outside the dirty cells, or a registered thing outside them that
   names one, means the cells weren't enough (a road or bridge running far out): they're grown to take in the lines laid differently
   and laid again, twice at most, then in full.
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

| Edit | Before (whole route handed) | After: first window | After: grown window | Total after | Taken in, before / after |
|---|---|---|---|---|---|
| Branch off the main line (default) | 5,913 | 3,616 (25 dirty cells, no spill) | - | 3.6 s | 720 / 607 |
| Branch near a rail bridge | 6,710 | 3,033 (28 cells), spilled on a 14 km guide mark | 3,259 (71 cells) | 9.0 s with reshape 2,745 | 824 / 390 |
| Spur in a yard | 6,903 | see the report (re-measured after a fix) | | | 552 / 198 |

The window's passes cost about half the whole route's (build 1.3 s against 2.8, profiles 0.9-1.0 against 1.5-1.7), the merge
0.45-0.5 s. A spill doubles it. What's left (see the report): the window is still 25 cells (lines are whole objects, so a split
main line makes its whole length dirty), the merge walks every spread field of the route (O(route), 0.2-0.4 s), and long roads
and guide marks laid near an edit spill.

Exactness (`HB_LOCALCHECK=1`, default edit, no spill): the 3,263 lines in the 25 dirty cells within 2 mm in plan and 4.8 mm in
height of a full rebuild, none missing by name; outside the 25 cells touched, every line is the object it was and every spread
entry previous's own (the 851 slope-end shapes are copies pointed at the merged lines; 104 `_railsNear` and `_wallSpans` entries
are caches the shown lines filled meanwhile); nothing points at a line or thing that isn't in the merged lines; rail steps at
junctions 0.019 m, as in the full rebuild. Things in the dirty cells differ in count from a full rebuild where the window works
them out round frozen lines (layouts 80/71, bridges 11 unmatched, `_surfaceShifts` 42/34, `_sideBySide` 19/15).

Saves: after two edits, the second save wrote 58 cells of 576 (72 asked for, 518 left as they were) in the background (spread 744
ms over the whole route, encode 423, files 99); the open after read all 576 warm.

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
