# Route Builder

One screen per route, with three views of it. They share the top bar (menus, the views, the route and whether it's saved), the side
panel on the left and the status line along the bottom. In the editors the tool strip runs down the left edge with undo and redo at
its foot, the side panel beside it starts with the tool in hand's options, and the status line says where the pointer is, how high the ground is there and where
the camera is, then what the tool's controls are.

| View | Key | What it's for |
|---|---|---|
| Planner | F1 | The route's tiles over OpenStreetMap: pick tiles and build them, set road and rail detail |
| 2D Editor | F2 | The built terrain from straight above: precise track and terrain work |
| 3D Editor | F3 | The built terrain, flown through |

Tab cycles Planner → 2D Editor → 3D Editor. The editors are one view of the terrain: once it has opened it stays open,
so switching costs nothing. It opens again only after a build adds tiles. Going from the Planner to an editor puts the
camera over the middle of the map; coming back centres the map on the camera.

Esc in an editor drops whatever is in progress (a path being drawn, then the selection), then goes back to the
Planner. In the Planner it leaves the builder. Switching back to the Planner or leaving saves the edits.

## Planner

| Input | Does |
|---|---|
| Left drag | Add the tiles the pointer passes over, marked as the kind of heights picked in the panel; dragging over them again re-marks them |
| Shift + left drag | Add a rectangle of tiles between the drag's ends |
| Ctrl + left drag | Remove tiles, shown in red as you drag (with Shift, a rectangle) |
| Right or middle drag | Pan |
| Wheel | Zoom |

Each tile gets one kind of heights, blue for Lidar (USGS 3DEP, the best it has there) and amber for Standard (AWS Terrain Tiles).
Both get the same roads and rail from OpenStreetMap. Built tiles show theirs faintly. Lidar Along Track asks OpenStreetMap where the
rail runs and marks the selection lidar where it does, standard elsewhere. A tile eases over its outer 300 m to meet any tile already
built beside it, so different kinds join without a step. Regenerate keeps each tile's kind.

Select: Along Track in the panel changes what a left drag does: it marks an area (as big as 200 by 200 tiles, drawn as one
white box), asks OpenStreetMap where the rail runs in it, and adds the tiles track passes through plus those within the
Padding of them (0 to 10 tiles, counting diagonals, 1 to start). Track tiles get Lidar and padding Standard unless the two
pickers under Padding say otherwise. Ctrl still removes tiles as in Tiles mode.

The side panel has Go To (a place name, or `lat, lon`), road and rail detail, the kinds of heights, Build / Cancel / Clear Selection, build
progress, and how much of each road and rail class is on the map. The first area picked fixes the route's origin and
tile grid; every later area lands on the same grid.

The detail starts at what the route was last built at. Regenerate builds every built tile again at the detail now set,
and fetches the roads and rail afresh in place of the old: lowering the detail takes out what it no longer asks for.
Track edited or laid by hand stays.

From zoom 13 the map marks every junction as the editor last read them (it writes `Junctions.json` in the route's
folder whenever it lays the track, see Junctions below): a diamond for a switch, a square for a crossover, a circle for
anything else; orange where it's drawn with models, red where it needs fixing. Hovering one says what it is and what's
wrong. Needs Fixing in the side panel lists every red one with why; clicking one goes to it.

In Route Builder → Open Route, Enter opens the highlighted route and Del deletes it (its whole folder), once confirmed.

## Terrain shaping

Wherever a terrain square's two diagonals climb very differently, it's split along the gentler one, so steep banks come out as slopes
rather than a sawtooth. Rock on steep ground takes the colour of the land cover around it. Ground under a bridge is kept below its
deck, easing off over the last 6 m to each end. All of this happens as tiles load, so it needs no rebuild and hand sculpting still
goes on top.

## Moving around the editors

| | 2D Editor | 3D Editor |
|---|---|---|
| Right drag | Pan | Look |
| Middle drag | Pan | |
| WASD | Pan | Move |
| Q / E | | Down / up |
| Wheel | Zoom about the pointer | Flying speed |
| Left Alt+wheel | | Zoom the lens in or out, up to 10x |
| Shift | Faster | Faster |

## Tools

The strip on the left picks the tool for the left mouse button, and so do these keys. Terrain on the strip goes back to the brush
last used, and its panel has the brushes, their radius and the covers to paint.

| Key | Tool |
|---|---|
| 1 | Free Look |
| 2–5 | Raise, Lower, Smooth, Flatten (to where the stroke began) |
| 6 | Paint Cover (P picks the cover) |
| 7 | Edit Track |
| 8 | Pen |
| 9 | Track Circuits |
| 0 | Switches |
| - | Shape Track |
| = | Lay Yard |
| P | Parallel (with Paint in hand, P picks the cover instead) |
| O or \\ | Objects |

`[` and `]` size the brush. Ctrl+Z and Ctrl+Y undo and redo any edit, terrain or track. Ctrl+S saves.

### Pen (8)

Draws curved track, as a pen tool draws paths.

| Input | Does |
|---|---|
| Click | Corner point: straight track either side |
| Click and drag | Smooth point; the drag pulls out its handles |
| Drag a placed point or handle | Reshape before laying |
| Enter | Lay the track |
| Backspace | Remove the last point |
| Esc | Drop the path |

- A point placed on a track's control point (it turns green) joins the new track there.
- A point placed partway along a track branches off it: a switch.
- While drawing, the pointer label and the side panel show the length, the tightest curve radius, and the ground grade
  under the path, end to end and the steepest over any 100 m. That's the ground's grade; the laid track's own profile
  shows in the grade overlay once it's built.
- Laid track is stored as control points every 10 m. Simplify thins out the ones it doesn't need.

### Edit Track (7)

Works on the track's control points, OpenStreetMap's or your own.

| Input | Does |
|---|---|
| Click a point | Select it |
| Shift + click | Add it to the selection, or take it out |
| Drag on the ground | Box select (Shift adds) |
| Click on the ground | Clear the selection |
| Drag a selected point | Move the whole selection |
| L | Straighten |
| K | Smooth |
| J | Join |
| U | Split |
| Delete | Delete the selection (or the point under the pointer) |
| Esc | Clear the selection |

The side panel and the Edit menu have the same operations, plus Simplify:

- **Straighten**: each run of selected points that follow one another along a track lines up on the straight line
  between the run's first and last point, keeping their spacing.
- **Smooth**: evens out kinks in each run, its ends staying put. Taubin smoothing, so curves don't shrink.
- **Simplify**: takes out points that add nothing to the shape (within 0.25 m of the line the rest make). Way ends and
  any point another track runs through are kept.
- **Join**: makes the ways the selection touches into as few as it can.
- **Split**: unjoins track at the one point selected: a track through it becomes two, or two tracks ending on it come apart, each new end drawn back 0.75 m. They keep their beds, and Join puts them back together. Not at a junction or a switch.
  - Two ways that end on the same point merge, as long as no other track runs through that point.
  - Loose ends (no other track there) that are both selected and within 50 m of each other are bridged, nearest pair
    first.
  - A point where three tracks meet is a junction and never merges.
- **Delete**: takes the selected points out of every track through them. A track left with fewer than two points goes.

Every operation is one undo step. Moving a point moves every track through it, so junctions stay joined. Edited and
new ways are tagged `highball:edited`, so building the tiles again doesn't overwrite them. An OpenStreetMap way that's
deleted or joined away stays in the network as an empty marker tagged `highball:removed`, so it doesn't come back.

### Track Circuits (9)

Every stretch of track belongs to a track circuit with an id (`TrackCircuitID`, such as `TC0042`). These are the
sections whose occupancy the signalling will see. Each circuit is drawn in its own colour with its id; insulated
joints show as ticks across the track (white: laid out automatically, yellow: placed by hand).

| Input | Does |
|---|---|
| Click | Select the circuit under the pointer |
| Shift + click on the track | Put a joint there |
| Drag a joint | Move it along the track |
| Delete | Take out the joint under the pointer |

The side panel shows the totals, the circuit under the pointer, and the selected circuit's length and switches; its
id can be changed there (Enter). Automatic Joints → Lay Out Again redoes the automatic joints at the block length
given, keeping the ones placed by hand. Every change is one undo step.

How it works:

- The rail ways become a track graph. Vertices are where track ends or meets other track: a switch, a crossing, a
  buffer stop. Edges are the track between them, however many ways each runs through.
- Only the joints are stored. The circuits are the pieces the graph falls into when it's cut at every joint, so
  adding, moving or taking out a joint splits or merges circuits by itself, and a switch always sits inside one
  circuit together with its legs up to their joints.
- Ids are kept with a point on their circuit's track, so they follow the circuit through track edits and rebuilds.
  New circuits take new ids.
- The first time a route opens in an editor, joints are laid out automatically:
  - on every leg of every switch or crossing, 35 m out from it, unless the leg is too short to part from the next
    one (then the switches it joins share a circuit, as a crossover's would);
  - along plain line, about every block length (1200 m by default), evenly spaced.
- Everything is rebuilt on a background job whenever the track, the joints or the ids change. Finding the circuit at
  a point on the track is a binary search along its edge, ready for train occupancy.
- Saved as `Signalling.json` in the route's folder: the joints and the ids' anchors as latitude and longitude, the
  block length, and the next id number.

## Switches (T)

Where one single track divides into two, the track is built as a turnout rather than two lines overlapping, worked
out from the real geometry at the OpenStreetMap node (the point of switch):

- The two routes leaving together: the one running straighter on from the toe is the normal route, the other the
  reverse.
- The diverging route is laid out as one curve (two arcs meeting smoothly) first, rather than at whatever angle OpenStreetMap drew it: from the point
  of switch, leaving along the normal route, onto the mapped track in line with it, at the nearest point it can reach
  bending one way only and no tighter than 80 m in yards and industry, 237 m on main and branch lines. Where the mapped
  track settles alongside, so it can't bend one way only, it eases over and back as gently as the room allows. The frog
  falls wherever the routes come apart, at whatever angle that makes, and the size a switch is called is the standard one
  nearest that angle. A size picked with the Switches tool (0) sets the tightest the curve may be, if it can be kept to.
  Junctions where no curve of at least 60 m radius will do, or whose routes don't part before the normal route ends, are
  left as mapped and listed as needing fixing.
- A crossover or ladder link is one curve from one point of switch to the other. A longer or curved track with a switch
  at each end, such as a siding, keeps its shape.
- In a ladder, where a switch's diverging track runs into the toe of the next switch, the next switch is slid along its
  track (up to 40 m) if it's mapped too close: to just past where a curve from this switch's points first reaches the
  mapped track no sharper than the track's class allows.
- The frog is where the two routes' inner rails cross. Closure rails run from the blades' heels to it and carry on
  past it as wing rails, each a 1 7/8 in flangeway off the frog point. A guard rail faces the frog inside each outer rail, the same flangeway off it.
- The switch blades run from the point of switch to where the two routes are 14 cm apart, so an open blade clears its stock rail (3 to 16 m; 8.2 m on a No. 10), planed thin at the
  tips.
- The normal route's ties become long timbers out past the frog, carrying plates for both routes; the reverse route
  lays none of its own until then.
- One ballast bed carries both routes: the normal route's widens out under the reverse route until their ballast
  parts, and the reverse route lays none of its own until there. Elsewhere, lines whose ballast overlaps (crossings,
  lines drawn over each other) are set a few millimetres apart in height so one lies cleanly over the other.

Every switch is built to fit its routes, whatever curve and frog angle they came out at. The rails are swept along the
routes in code: stock rails, blades, closure and wing rails, the frog's point rails planed to a V, and guard rails. The
blades run to where the routes are 14 cm apart, and long timbers carry both routes until a tie of each would clear the
other. What has a look of its own comes from a kit made in Blender (`Tools/Blender/turnouts.py` makes
`Models/Track/switch_kit_r.glb` and `_l.glb`), each part fitted in place: the point machine stands by the points, the frog
casting stretches across the V at whatever angle it is, and the stretchers and throw rod stretch between the blades as they
move. `turnout_N.png` is a picture of each standard size, for the Switches tool.

Diamonds are built from the tracks that cross (`World/Diamonds.cs`), so they fit any angle down to 8 degrees. Each of the
four places a rail crosses a rail is a frog: outside the other track the two rails run on and meet; inside it, each stops a
flangeway short and starts again planed to a point. A casting from the kit lies under each frog, stretched along both
tracks, and on a shallow crossing a guard rail lies opposite each frog where it clears the other rails. The two tracks are
fitted to straights independently, so neither is bent to carry the other through the crossing.

Crossovers are laid out as one design, not a switch at a time (`World/CrossoverFitting.cs`, before the ways become
lines): two turnouts of the same size point to point at the tracks' measured spacing, each one's lead curve out to its
frog and a straight from frog to frog, `2F + (D - 3.02 m) / angle` long (a No. 10 at 4.5 m spacing is about 66 m). It's
centred where the mapper drew it, and both switch points slide along their tracks to where the design puts them, the
crossover's track redrawn between. The size is the one nearest the mapped length among those the tracks' class takes
(No. 6 to 10 between yard tracks, No. 10 to 20 otherwise) that don't reach over another junction; picking a size for
either switch with the Switches tool picks it for both. A crossover with no size that fits is left as mapped and
marked to fix, with why.

For this the rail ways are cut at every junction first (OpenStreetMap often runs one way on through a switch, or ends
a crossover's way somewhere past its second switch), and put back together after, so a track still runs on through its
switches as one line.

Ladder links: two switches joining tracks that converge (4 to 35 degrees apart, the switches at most 120 m apart; as
from a ladder's lead to a yard track) are fitted as a pair too: each switch's lead curve out to its frog where it was
mapped, and a smooth curve between the frogs, the biggest size whose curve there is no sharper than 0.6 of its lead
curve. Two switches further apart than that are a yard track with a switch at each end, each fitted on its own.

Ladder throats: a switch whose diverging track runs into the next switch (a ladder's lead) needs only 3 m past its
frog before it, not the 12 m it needs to ease back onto mapped track; and the next switch along the straight route may
start just past its timbers. A short track at a switch (its toe) doesn't stop it being a switch; only the routes it
lays out need room. Two switches drawn at one node, point to point, are each laid out on their own.

## Junctions

Every junction is read as a piece of track layout, from the tracks around it rather than its node alone, whenever the
track is laid:

- Tags first: OpenStreetMap nodes tagged `railway=railway_crossing` are diamonds, and `railway:switch=double_slip`,
  `single_slip` or `three_way` are slips and three-way switches. Builds fetch these nodes (with their `railway:*` tags,
  `ref` and `name`) beside the ways, into `Nodes.json.gz` in the route's folder; routes built before need a
  Regenerate to get them. Plenty of areas have none tagged, and there the shape decides.
- Crossovers and ladder links: a switch whose diverging track runs into another switch's diverging track is a pair:
  a crossover where the two straight tracks are side by side, a ladder link where they converge. Two crossovers
  overlapping between the same tracks are a scissors crossover.
- A fan, one track splitting into three or more at a single node, is taken apart first (`World/FanFitting.cs`): one route's switch
  stays at the node and the others leave up to 40 m along the straightest route, by a switch of their own. Only the track as fitted
  changes, so that second switch can't have its points dragged.
- Then each node alone: three tracks shaped like a switch are a turnout; four straight through one another, a diamond;
  two switches drawn at the one node, or anything else, are marked as such.

A junction needs fixing (red) when it has no real design: a switch or crossover with no room for a standard size, a
shape nothing matches, or a layout with no template yet (scissors crossovers, slips, three-way switches).

Diamonds are built in code from the two tracks as they really run (`World/Diamonds.cs`, `World/NetworkMeshes.Diamonds.cs`), at
whatever angle they cross and on curves too, so they always meet the rails: doubled rails with a flangeway right through,
heavy bolted bars, corner castings with pockets. The second track's ties give way where they'd pass through the first's.
Diamonds under 8 degrees (which need moveable-point crossings) and on multiple-track or street track are left to fix.

Through a turnout every track always lays its own ballast bed, just under the normal route's widened one where they overlap, so
close switches can't leave a hole. A reverse route's tie is only left out where the normal route's long timber really is there.

### Switches (0)

Click a switch to pick it (every switch is marked: filled where it's modelled; every junction that needs fixing is
ringed red). With none picked, the side panel lists what needs fixing; clicking one flies there and picks it. The side panel shows its size now, and
each standard size by its model's picture, with its lead curve, frog distance and angle; the size the fitting would
choose by itself is marked, and sizes with no room before the next junction are greyed out. Click a picture to use that
size, Automatic to let the fitting choose again, or As mapped to leave the junction as OpenStreetMap drew it. The choice
is saved with the route (`Switches.json`, by the switch's OpenStreetMap node) and the track is laid again at once.

T throws the switch nearest the pointer: its blades swing over to the other route in 3 seconds. Turnouts on
multiple-track lines, street track, crossings and slips are left as plain track for now.

## Saved track

The track is solved once and saved with the route (`Track.bin.gz`): every line's shape, its straights and arcs, and what
was worked out about its switches. After that the route opens to what's saved, so the track doesn't move because the fitting
code changed. It's solved again only if the map data, a hand-picked switch size or the built tiles changed, on an edit
(of the map data, with Edit Track or the Pen), with File > Solve Track Again, or with `--refit`.

## Where the heights come from

A tile's heights come from the USGS's 3D Elevation Program where it has them: bare earth, 1 m lidar in much of the US and coarser
elsewhere. Where it has none, including outside the US, they come from the AWS Terrain Tiles, fading from one to the other inside a
tile over 100 m. If the service can't be reached, that tile uses the Terrain Tiles and the build says so; building it again retries.
Downloads are cached under the user's data folder, about 16 MB a tile at 2 m. `Tile.json` records each tile's `HeightSource`.
Build all of a route's tiles at once: tiles from the two sources can differ by a metre or two where they meet. Bridge decks aren't in
bare-earth data, so built bridges carry lines over the gaps.

## Mapped track is a mark on the ground

Mapped track isn't built. Everything else is generated (terrain, roads, bridges, cover), and each mapped track is a 2.8 m strip of
gravel on the ground where it would go, in 2D and 3D, with no rails, ties, switches, signals or trains. A strip lies on the ground
as it is and never reshapes it, so what's under it is the real land until track is laid there. The strips are lines like roads, so
nothing that works on track sees them. Track only exists once it's laid by hand, and mapped ways are all guides to lay
it along. The fitting that used to build mapped track is on the `generation` branch as it last stood; the same code still fits
hand-laid track.

In the 2D Editor, the side panel's Background picks Off, Map (OpenStreetMap) or Satellite (USGS aerial imagery, US only, sharpest
at zoom 16), laid over the ground at the opacity set. Tiles are cached under the user's data folder. The Planner shows the same choice.

## Laying yards by hand

With the Lay Yard tool (=), click along a guide: the shortest way along the guides between clicks comes out as straights and
curves, fitted as hand-laid track is, and Enter lays it. A click right at a guide's node that's on built track joins there, and one
on built track joins into it. Laid track is left exactly as laid by the fitting, and a guide fades where track runs along it, with the
side panel saying how much is done. Clicks off any guide go straight between them.

A click on built track, or on a guide within 2.5 m of it, joins it there, so a branch comes off a track you've already laid
as a real switch. The path is led off along the track it leaves (and on to one it arrives at) by a curve no tighter than
80 m if there's room, as the preview shows.

The tool's panel picks how it lays: Follow, as above, or one of two that work from built track in two clicks. Each shows what
it would lay, in red with the reason if it can't, and the second click lays it; Backspace starts over and Ctrl+Z undoes it.

- Crossover: click one track, then the other. Clicking further along on the second makes it lean that way; the middle of the
  two clicks is its middle. Automatic takes the first of No. 8, 7, 6, 10, 15, 20 that fits on yard track, and of No. 10, 15, 20,
  8, 7, 6 otherwise. It's refused too near another junction or a track's end, or between tracks not within 4 degrees of parallel.
- Ladder: click where the lead leaves a track, then where the yard tracks end, on the side they go. The panel sets how many
  tracks, their spacing and the size (No. 6 to 10). The lead leaves by a switch, crosses at the frog angle with a switch for
  each track, and curves in to be the last track itself. All of them are straight and end level with the second click, whatever
  the track they leave does past there. A click within 8 m of a track's loose end carries that track on straight as the
  ladder's first track, on top of the number set in the panel.

A ladder lays one curve to its size's frog angle, so its switches come out that size. Laid crossovers and ladders are laid
track: area clearing leaves them alone.

## Editing switches

In the Switches tool (0), a picked switch shows a white circle where its diverging curve ends and a cross at its frog. Drag
the circle along the diverging track: nearer makes it shorter and sharper, further makes it longer and gentler, and the
drag shows the size it comes to and the curve's radius (red, and it won't take, if it's tighter than 70 m or the routes never
part). Drag the switch's diamond along the track it stands on to move its points, as far as 15 m short of the next junction
or the track's end. Either way the frog rebuilds where the routes part. Curve ends are kept in SwitchCurves.json; moved
points change the ways themselves. Automatic, or picking a size, puts the curve end back. A slip's switches and a ladder link's are
laid out together and can't be dragged.

A picked crossover shows one white circle at its middle instead. Dragging it along the tracks moves both switches the same way
and as far as they both can go (15 m short of the next junction or either track's end), and the crossover is laid out again there.

## Parallel track

The Parallel tool (P) lays any number of tracks at once. It draws as Pen does, from a control point, a track's end or open ground:
click for a corner, drag for a curve, Backspace takes the last point back, and Enter lays them. The path drawn is the first track,
joined to whatever it starts or ends on, and the others go beside it, to its right going the way it was drawn; F puts them on the
left. An end of one of the others that comes within 1.5 m of a track's loose end joins it, so a pair of tracks can be carried on
together. A first click on a built track, away from its points, runs them beside that track instead, following its curves: then
click level with where they end, on the side they go. The panel sets how many (2 to begin with, up to 500, a guard
against a mistyped count) and their spacing (3.5 to 12 m, 4.6 m to begin with). They're shown as you move, in red with which track and why if any can't be laid: none of them are
laid then. None is laid within 3 m of another track or across one, or tighter than the sharpest curve allowed; offsetting a curve
by many tracks tightens the inner ones. Backspace or Esc starts over, and Ctrl+Z takes them all back at once.

## Objects

The Objects tool (O) lays premade track and puts signals, hand throws, grade crossings and diamonds on the route. Pick one in the panel
and click; each click places another, and clicking one already placed picks it instead. Esc lets go of what's picked.

Track pieces are real track, drawn in place before they're laid, in red with why if they can't be:

- Switches: No. 6, 7, 8, 10, 15 and 20, left and right. On a track, the track is the through route and only the diverging route is laid.
- Crossovers: the same sizes, leaning left or right, as two tracks and the crossover. Clicked on a track with another beside it within
  12 m, only the crossover is laid between them.
- Three-way switches: the same sizes, with the first route to the left or right. They're two sets of points one after the other, the
  second clear of the first's blades and done before its frog. On a track, the track is the through route.
- Scissors crossovers: No. 6 to 15 (a No. 20's crossing would be under 8 degrees). Two crossovers and the diamond where they cross, as
  two tracks wide enough to keep that diamond clear of the frogs. Clicked on a track with another beside it, it's laid between them if
  they're far enough apart.
- Single and double slips: tracks crossing at 10, 15 or 20 degrees, with a route from one to the other beside the diamond (a double
  has one each side). They're built from ordinary switches either side of the diamond, so they run longer than a compact slip with its
  points inside the crossing.
- Diamonds: two tracks crossing at 15 to 90 degrees.
- Ladders: 3, 4 or 6 tracks with No. 6, 8 or 10 switches. On a track, the lead leaves it by a switch.
- Straights and Curves: clicked near a track's loose end, they carry on from it, so pieces chain.

R turns a piece 15 degrees and Shift+R back; holding R and rolling the wheel turns it 1 degree a notch, or 5 with Shift. On a track
that decides which way the piece faces. V mirrors it. The panel sets the
spacing crossovers and ladders are built to. Mapped nodes tagged as slips or three-way switches still show as needing fixing.

The rest:

- Signal: snaps 3.6 m off the nearest track within 15 m, on the side clicked, facing trains that have it on their right.
- Hand Throw: goes on the nearest switch within 20 m, in place of its power machine, and its target and lever turn as the switch
  is thrown.
- Grade Crossing: snaps onto the nearest track and takes the line and width of a road crossing it within 15 m, or lies square
  across the track, 8 m wide, if there's none. One crossing takes in every track the road meets, within 12 m of one another: one
  deck level with the rail tops from outside the first to outside the last, ramps down to the road or ground, and a mast each end
  with a crossbuck, flashers and a raised gate. Clicking another of its tracks picks it rather than making a second.
- Diamond: joins two built tracks where they cross, within 15 m, with a control point in both. Refused under 8 degrees. Edit Track
  moves that point, and the assembly is rebuilt to whatever the tracks then make.
- Utility Poles and Light Poles: three of each (telegraph, power and telephone poles; street light, yard mast and platform lamp). Anywhere
  puts one where it's clicked. Road and Track set it off the nearest one within 65 m, on the side clicked, by the distance set: from a
  road's edge, or a track's centre. Dragging one goes by the same setting. A utility pole lines up with the road or track; a light faces it.
- Ties: loose ties with no rails, for dead ends and lifted track. Single tie is one bare tie (0.495 m apart by default, so it follows any
  curve); Tie section is 4.95 m of plated ties with no ballast, and on ballast the same on a bed of its own. Put down on Anywhere within
  8 m of a track's open end, a tie or a pen point goes on the end, and lines up with the way the track was going. Lay these with Pen as a rule.
- Pen, on a pole or ties: as the Pen tool, click for a corner, drag for a curve, and Enter lays the whole path at the Gap, as one undo.
  Backspace takes the last point back and Esc drops it. It doesn't snap to roads or tracks, and it has the pointer to itself, so to pick
  something go back to One or Fence. Picking anything laid in a run offers Delete Its Run. A single click lays up to 2000 ties.
- Fence, on a pole or ties: the first click stands the first pole and each one after adds poles at the Gap, up to that click and no further, and
  goes on from the last. On Anywhere that's a straight line to each click, so it can turn corners; on Road or Track it follows the line,
  from one way to the next where they join. Each click is one undo, a click lays at most 500 poles, and Esc ends the run. The line's
  shown as you go, with where each pole would be and how many.

Click an object's marker to pick it, then drag to move it (it snaps again), R and Shift+R to turn it 15 degrees, R and the wheel to turn it 1 degree a notch (one undo for the whole turn), V to turn it round
(a hand throw: to the switch's other side), Delete to remove it. They're kept in Objects.json in the route's folder, saved as
they change, and Ctrl+Z undoes them. Signals don't show aspects or tie into circuits yet, and crossings don't work their gates.

A crossing's deck is built again whenever the track changes, so it follows the track. Signals and masts stay where they were
placed, and a hand throw whose switch has gone does nothing. Signals, stands and masts are from `Tools/Blender/lineside.py`.
Poles are from `Tools/Blender/poles.py`; like signals, they aren't drawn past the track's near radius (1 km). Their wood is Wood062, at 4K if
`Content/Textures/Wood/Wood062_4K-JPG.zip` is there, and otherwise the size the ties use. They have no wires yet, and the lights don't light.

## Shaping track by hand

The Shape Track tool (-) works on the track itself, straights and arcs, rather than the map data. Click a line to select it:
its handles show, with the map's line beside it in green. Drag a handle and the track between the handles either side is
laid in again through it, in one smooth curve each way. Click the line between handles to add one, Delete takes the one under
the pointer out, and Shift-click two then L makes the line exactly straight between them. Handles at junctions and line ends
stay put. A dragged handle snaps to the straight between its neighbours and to the map's line (Alt turns that off), and the
preview turns red for a curve tighter than 70 m. Undo and redo work as for any edit.

Hand shapes are kept apart from the solved track (`TrackEdits.bin.gz`, written on Save) and laid over it every time, so they
survive Solve Track Again and edits elsewhere. Back to Solved in the side panel drops one. A shape for a line whose map data
has since changed no longer applies, and the log says so.

## Keeping the track right

Track quality is measured on the track as built, whatever made it that way (World/TrackQuality.cs): any bend sharper than
60 m radius over 8 m, and any step in height along a line or between lines at a node. What's found goes on the Needs
Fixing list, and `--profilestats` reports it with coordinates.

- Track is stored as what it really is (World/Alignment.cs): a list of straights and circular arcs for each rail line. The
  line's points are only samples of it, so a straight stays exactly straight through every later step.
- The mapped track is fitted to that first (World/TrackAlignment.cs): it's cut where it strays more than 1.25 m from a
  straight (tighter if that's refused), a best-fit line goes through each piece in the direction it shares with the
  straights around it (within 150 m and 0.5 degrees, so tracks side by side come out parallel), and an arc joins each to the
  next. Junctions on it move with it. The data is the truth: a fit is refused if any of it would lie over 1.5 m from the mapped line, a repair over 3 m, and a switch's diverging curve over 3 m. `--profilestats` reports how far off the track came out. A line is fitted after the lines its ends lie on, from where those put
  them, easing over to a moved junction first, so nothing is dragged out of shape. No straight is shorter than 15 m and no curve tighter than 80 m, and a track doesn't share a direction with one it joins, or the two would never part. A fit that would bring it within 3.3 m of an unconnected track that wasn't that
  close already is retried tighter, then in halves, and only then left as mapped.
- A line running through a switch's toe and on as its diverging route arrives running the way it leaves: its approach is laid in again as a curve where the two differ, so there's no kink at the points.
- A switch's diverging route runs at the normal route's height as far as just past the frog, easing back to its own over
  the next 30 m (or, where a junction comes first, by that junction).
- Heights at junctions are solved over the whole network: every node has one height, and the nodes along each line are
  drawn together until no stretch between two is steeper than that line's grade allows.
- Sharp bends are repaired twice (World/TrackFairing.cs): on the mapped track before the switches are laid out, then
  around the switch models after. A repair is one smooth curve over the shortest stretch that's gentle enough. It never
  runs over a model or a node other lines meet at (before the switches are laid out, a track may carry a node along), and
  it's refused if it would bring the track closer than 3.6 m to another that wasn't already.

## Ground under the track

Each line flattens the ground under its formation, with cut and fill slopes out to the ground. Between two rail lines
on opposite sides of a spot, within 45 m of each other and at much the same height (a yard, tracks side by side, a
wye's legs), the ground is graded flat from one's formation to the other's instead, as a yard is graded, so their cuts
and fills don't leave ridges and trenches between them.

## Track grades (G)

Colours every rail line by its grade: green under 1%, yellow 1–2%, red over 2%. Each colour is the grade over 25 m
either side, not a single segment's. Hovering a line shows its grade over 100 m and its elevation. Works in both
editors; also under View → Track Grades.

## Generation audit

`--audit file` measures the finished track and writes every problem it finds, by check, with its place and details (`World/TrackAudit.cs`):
the junction issues, wiggles outside switches, kinks, tracks that don't meet at a junction, kinks where two tracks join, gaps between
track ends, tiny pieces, tracks crossing with no junction, tracks too close together (split into those the map itself has that close and
those the fitting made so), switches on top of one another, missing ties, track off the map, steep grades, track far above or below the
ground, roads crossing track with no grade crossing, and placed objects with nothing to stand on. Ends at the edge of the built area
don't count. FIX lines are wrong to look at or drive over; note lines are worth knowing about.

## Junction scorecard

`--profilestats` ends with the junction scorecard: how many junctions were read as each kind, how many were laid out to
a standard design, and what's left to fix, by reason. `Tools/junction-scorecard.sh <build folder> <route>...` prints it
for each route given; run it before and after a change to the junction fitting to see what the change did everywhere.

## Dev flags

| Flag | Does |
|---|---|
| `--route NAME` | Open the builder on that route |
| `--view` / `--view2d` | Open straight into the 3D / 2D Editor |
| `--camera LAT LON HEIGHT YAW PITCH` | Start the editor camera there |
| `--throw LAT LON` | Throw the switch nearest there |
| `--look LAT LON ZOOM` | Open the Planner's map there |
| `--regenerate` | Regenerate every built tile as the Planner opens |
| `--switch LAT LON SIZE` | Pick the switch nearest there and set its size (-1 automatic, 0 as mapped) |
| `--clean` | No overlays |
| `--tool N` | Start the editor with tool N |
| `--trackop LAT LON RADIUS OP` | Select the control points within RADIUS m and apply `select`, `straighten`, `smooth`, `simplify` or `join` |
| `--lay LAT LON LAT LON` | Lay track between two points |
| `--sculpt TOOL STEPS` | Apply a terrain tool at the middle of the screen |
| `--fly SPEED` | Fly straight ahead at SPEED m/s, logging frame times and terrain streaming |
| `--skybox NAME` | Use that sky |
| `--panels` | Open Frame Statistics, Profiler and Threading |

## Threads

Background work runs on BRUTAL's job system (Brutal.Concurrency), through `JobStream` and `FrameJobs` in
`App/Jobs.cs`:

- `JobStream`: a stream of background jobs in batches. Whenever the last batch has finished, its owner hands over the
  jobs it most wants done now, so work that's no longer wanted is never started and the nearest goes first. Terrain
  patches (60% of the cores), track mesh chunks (40%) and track circuits use one each. Background work gets all the
  cores but two.
- `FrameJobs.For`: work split across the cores within a frame and waited for, such as projecting the grade overlay.

Disk reads stay on .NET tasks, so they don't hold up a runner.
