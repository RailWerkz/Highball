# Performance and engine plan

Goal: **120+ fps on an RTX 4060 Ti at High, 90+ at Ultra**, with 1M+ triangle vehicles, and a route format that scales to the
whole USA, feeding both single player and multiplayer.

Branch: `perf/renderer`. Every step is measured with the benchmark below and committed on its own.

## Benchmark

Two fixed views on the World route with the 65-car CSX/BNSF autorack train parked, 3440x1440, run from `bin/Agent` with a
read-only copy of the player's settings (`--settingsfile`), `--gputimes`, 24 s in, at 18:30 (sun 4.6° up, the worst case
for shadows) and at noon:

- **A, trackside:** 2 m up, 40 m ahead of the lead locomotive, looking back along it.
- **B, aerial:** 80 m up, 150 m ahead, looking down the train and the junction.

```
Highball --settingsfile ultra.json --skipsplash --route World --view --camera 42.3825740 -83.4670200 2.0 196 -1 \
  --skytime 2026-05-15 18.5 --novsync --gputimes --shot 24 A.png --quit 26
Highball ... --camera 42.3835200 -83.4667000 80 196 -20 ...
```

Back up `Content/Routes/World/Stock.json` around every run (the game rewrites it on exit). Dev flags such as
`--novegetation` save into the `--settingsfile` copy, so keep that copy read-only.

## Where it started (Ultra, 8x MSAA + TAA, native 3440x1440)

| | A | B |
|---|---|---|
| GPU | 18.7 ms (53 fps) | 18.9 ms |
| Triangles | 31.7 M | 23.7 M |
| Sun shadows | 4.85 ms, 21.4 M tris | 3.0 ms |

CPU work was ~3–5 ms; the rest of the frame was waiting on the GPU. **Draw calls (1,500) were never the bottleneck.**

## Phase 1: done

| Step | What | A (ms GPU) | B (ms GPU) |
|---|---|---|---|
| Baseline | Ultra, 8x MSAA + TAA | 18.7 | 18.9 |
| AA | MSAA capped at 2x while TAA is on (8x cost ~4 ms for nothing visible) | | |
| Shadow LOD | Casters drawn at the LOD a cascade's texel can show | | |
| Cached cascades | Cascades 2–3 drawn on alternate frames, reused (re-based) between; redrawn at once if the sun or view drifts | 14.8 | 13.0 |
| Plant prepass | Alpha-tested depth first, then shaded once a pixel (equal depth) | 13.3 | ~13 |
| Terrain noise | The broad fbm patterns read from a baked tiling texture (1.5 ms of ALU) | 13.0 | 12.1 |
| **Temporal upscaling** | World drawn at Render resolution (default 75%), TAA reconstructs full size | 10.5 | 10.0 |
| Caster reach | Shadows thrown from each caster's real top down to the cascade's measured lowest ground | 10.5 | 9.9 |
| Motion | Only stock that actually moved is redrawn for motion vectors | | |

**After phase 1, High preset (2048 shadows) with 75% render resolution: 94–98 fps at dusk, 112–114 fps at noon** (from
~50 fps on Ultra). Ultra: ~88–93 fps. After phase 2's still cache and GPU culling, High at dusk trackside is 8.9 ms of
GPU (112 fps of GPU time, from 10.2 ms), and the render thread takes 0.55 ms of CPU.

What's left in a frame (High, noon, trackside): shadows 1.9, plants 1.1, clouds 1.0, post 1.1, terrain 0.9, models 0.7,
AO 0.6, ties 0.4, roads 0.3.

## Phase 2: shadows (you asked for this one)

**Done:**
- **Still/moving split** for cascades 0, 1 and the track's. What stands still (ground, track, standing stock and
  objects) is drawn into a cached layer 12% wider than the view needs, kept while the view stays inside it, the sun
  holds and a signature of what stands still there (terrain patches, track chunks, standing models) is unchanged; each
  frame it's copied into the cascade and plants, moving stock, switch blades and crossing arms are drawn over it.
  Trackside at noon: sun shadows 1.66 -> 1.04 ms. Flying at 25 m/s it breaks even (redrawn every few frames).
- **GPU caster culling** for plants (below), with every cascade's view culled in one dispatch.

**Not done: virtual shadow maps.** Cascades now cost 1.0-1.5 ms (from 4.85) with the still cache; VSM's own costs
(marking pages from a depth the world doesn't have until after shadows, allocating them, drawing casters per page) would
likely be slower on this content until casters are culled per page on the GPU. Its win is quality: one texel size
everywhere and no seams between cascades. Ray-traced sun shadows (`VK_KHR_ray_query`) for Ultra the same.

## Phase 3: GPU-driven drawing

**Done: plants culled on the GPU** (`plant_cull.comp`). One thread a cell runs Gather's tests (and SunShadows' caster
reach) for the camera's view and every shadow cascade in one dispatch; each kind's kept cells are drawn from one
`vkCmdDrawIndexedIndirectCount`. SunShadows plans its layers first, queues their plant passes, culls them all with the
camera's, then draws, so the GPU doesn't stop between cascades (culling a cascade at a time cost 0.35 ms of stalls).
Render thread CPU 1.9 -> 0.55 ms; GPU unchanged within noise. CPU culling stays as the fallback (no drawIndirectCount,
or `HIGHBALL_CPU_CULLING`).

**Done: plants' depth before the ground.** The alpha-tested plant depth is drawn first, so ground and track hidden under
grass are never shaded; the shaded plants stay after the track's fades (equal depth test). Wind phases are taken once a
frame so both passes bend the grass alike. Trackside: 8.60 -> 8.22 ms of GPU.

**Measured, not done:** terrain and track multi-draw. Terrain records in 0.01 ms of CPU and costs no GPU time for its
draws; merging them would lower the draw count and nothing else. The culling pass and `GpuRing` are there to extend to
models, ties and plates when instance counts make it pay.

## Phase 4: geometry for 1M+ triangle models

Locomotives are 1.03 M / 257 k / 68 k triangles (LOD0/1/2), and their LODs are well proportioned.

**Done:** shadow and motion draws merge a model's solid parts into runs of the index buffer (a glass part in the middle
used to send the whole model back to a draw a part).

**Done: bindless model materials.** Every model texture sits in one descriptor array (`MaterialTable`); each draw's
material is a row in a per-frame table that `model.frag` reads by `gl_DrawID`, so all of a model batch's parts are one
indirect call. Trackside: models 141 -> 9 draws, 0.42 -> 0.35 ms.

**Not done: meshlets and cluster LOD.** Models now cost 0.35 ms with the authored LOD0/1/2; a cluster hierarchy pays off
with many 1M-triangle models in view at once, which the routes don't have yet. It needs meshoptimizer (not in the repo)
or a cooker of our own, a mesh-shader tier and a compute fallback.

## Phase 5: route data for the whole USA

**Already built:** `--export` writes `REGION.hbpack` (tiles: decimetre heights, 2D-predicted, zigzag, Brotli, land cover,
the track files), `REGION.hbserver` (rail graph with heights and signals) and a manifest with per-tile hashes; verifies
everything read back; installs for Single Player. Full-resolution tiles already stream from the pack on background
tasks and are evicted when far.

**Done:**
- Pack format v2: residuals as byte planes, Brotli quality 9, tiles encoded in parallel. World 32.2 -> 23.5 MB (-27%).
- The track a player's game solves is kept in its pack cache between sessions (checked against a stamp of its inputs):
  World opens in 4.3 s, not 5.8.

- Pack format v3: ground more than 300 m from rail kept every 10 m wherever that's within 1 m
  (`PackFile.TerrainTolerance`), every 2 m elsewhere; tile borders judged on their own samples so neighbours match
  exactly. World 23.5 -> 8.3 MB (terrain ~63 KB a tile; the mmo.md target is 40-60 KB). Lossless at 1 dm it doesn't
  pay: lidar ground has sub-decimetre detail in 96% of 100 m blocks.

**Measured, not done:** shipping the solved track in packs. It's 10 MB for World's 66 km, 18 times the network it's
solved from, so it would cost far more in download than the second it saves.

**Not done: region streaming.** Opening lays out all of a region's track (World: 4.3 s for 66 km). At that rate a
quarter of the country's track would take tens of minutes, so streaming needs the track laid out (or loaded compactly)
a window at a time round the train, with tiles and graph joined across window and zone borders. That's a redesign of
`NetworkLines`' solve, not a loader change.

## Phase 6: single player and multiplayer from exported routes

**Already built:** Single Player drives installed packs; `--host` / `--join` over RakNet; the headless
`RailWerkz.Server` loads `.hbserver`.

**Done: the server can run trains itself.** A cut put on the track with CutAdded's new flag is placed by its first
state, then stepped on the server's own Railway at the simulation's 120 Hz; its player sends only Control (reverser,
notch, brakes, sand) and the server sends every player its state 20 times a second. Switches it trails through are set
for everyone. Protocol 3; `--serverruns` turns it on. Tested hosting World: notch 8 took the autorack train from 4 to
45 mph on the server, the game showing it where the server said.

**Done:** server-run trains send their driver the brake gauges (main, equalizing, independent, every car's pipe and
cylinder); couples and splits become new server-made cuts driven by whoever drove what they came from; a menu option
(Multiplayer > Trains run by). Tested: notch 8 then brake 0.6, braked on the server from 31 to 26 mph, the cab at
72 psi. Couples and splits on the server aren't tested yet; engine start-up state isn't mirrored.

Next: AI and timetabled trains run by the server; interest management for many players.

## Risks
- Visual parity: every phase compares A/B screenshots against the previous step.
- Temporal upscaling is a little softer than native at geometric edges; textures get a mip bias of log2(scale) so their
  detail matches native (applies to textures loaded after a Render resolution change).
- Mesh shaders and VSM need fallbacks that are tested, not just compiled.
