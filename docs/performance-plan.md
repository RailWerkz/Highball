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

**Now, High preset (2048 shadows) with 75% render resolution: 94–98 fps at dusk, 112–114 fps at noon** (from ~50 fps on
Ultra). Ultra: ~88–93 fps.

What's left in a frame (High, noon, trackside): shadows 1.9, plants 1.1, clouds 1.0, post 1.1, terrain 0.9, models 0.7,
AO 0.6, ties 0.4, roads 0.3.

## Phase 2: shadows (you asked for this one)

Cascaded shadow maps were the largest single cost and the place most likely to show artefacts. Done so far: texel-based
LOD, cached far cascades, geometric caster culling. Next, in order:

1. **Static/dynamic split for the near cascades.** Terrain, track and placed objects cached per cascade and only re-drawn
   when the cascade scrolls a set number of texels or the sun moves; trains and wind-moved plants drawn on top each frame.
2. **Per-cascade GPU culling** (shares phase 3's culling pass): instances and track cells culled against the light
   frustum and the receiver bounds on the GPU, so the CPU stops walking every caster five times (1.1 ms CPU today).
3. **Virtual shadow maps** (16k² virtual, 128² pages, only pages the camera samples are drawn, static pages cached across
   frames) as the long-term replacement for cascades on GPUs with sparse binding or a page-table fallback. One filter
   width in world space everywhere, so there's no visible step in sharpness where cascades meet.
4. Optional **ray-traced sun shadows** (`VK_KHR_ray_query`) for RTX-class cards as an Ultra option, with VSM as the
   fallback.

## Phase 3: GPU-driven drawing

- One geometry pool (`BufferArena` grows into it), bindless material table, instance table on the GPU.
- Compute culling (frustum + two-phase Hi-Z occlusion) per view and per shadow cascade, writing
  `vkCmdDrawIndexedIndirectCount`. Plants first (the biggest instance counts), then models, ties and plates.
- Depth prepass from the culled set for everything with an expensive shader.

## Phase 4: geometry for 1M+ triangle models

Today's locomotives are 1.03 M / 257 k / 68 k triangles (LOD0/1/2), and their LODs are well proportioned. Next:
- Offline cooker (meshoptimizer): meshlets, a cluster LOD hierarchy, vertex quantisation.
- Renderer tiers: mesh shaders (task-shader cluster culling), compute cluster culling + MDI, and today's path as the
  reference.
- Material merging: 61–64 materials per locomotive means 60+ draws per vehicle; texture arrays / bindless cut that to a
  handful.

## Phase 5: route data for the whole USA

**Already built:** `--export` (and the editor's export) writes `REGION.hbpack` (tiles: decimetre heights, 2D-predicted,
zigzag, Brotli, plus land cover and the track files), `REGION.hbserver` (rail graph with heights and signals) and a
manifest with per-tile hashes for patches, verifies everything read back, and installs the packs for Single Player.
The World route today: 115 tiles, 33.7 MB (~293 KB a tile).

**Done:** pack format v2: residuals as byte planes, Brotli quality 9 (`PackFile.TileQuality`; 11 saves another ~7% at 20x
the time), tiles encoded in parallel. World: 32.2 → 23.5 MB (−27%), exported in 6 s, every tile verified on read-back,
v1 packs still read, Single Player drives it.

Next: the visual track and objects stored per tile
(`docs/mmo.md`), and streaming tiles from packs on a background thread with transfer-queue uploads.

## Phase 6: single player and multiplayer from exported routes

**Already built:** Single Player drives installed packs; `--host` / `--join` run sessions over RakNet; `RailWerkz.Server`
exists. The design for the national world (zones, 2 km tiles, regions, servers) is in `docs/mmo.md`.

Next: finish `docs/refactor-plan.md` (RouteWorld owns the route and trains, DriveScreen stops being the editor), then the
authoritative headless server loading `.hbserver`, with the same code path for a local single-player server.

## Risks
- Visual parity: every phase compares A/B screenshots against the previous step.
- Temporal upscaling is a little softer than native at geometric edges; textures get a mip bias of log2(scale) so their
  detail matches native (applies to textures loaded after a Render resolution change).
- Mesh shaders and VSM need fallbacks that are tested, not just compiled.
