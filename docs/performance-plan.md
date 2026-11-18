# Performance and engine plan

Goal: **120+ fps on an RTX 4060 Ti at High, 90+ at Ultra**, with 1M+ triangle vehicles, and a route format that scales to the
whole USA, feeding both single player and multiplayer.

## What the profiler actually says (2026-11-18, Ultra, CSX train, Barstow-style scene)

| | |
|---|---|
| Frame | 21.2 ms CPU, **19.9 ms GPU**. CPU spends 15.5 ms in Present waiting on the GPU. |
| Bound by | **The GPU.** CPU recording of the whole frame is 4.7 ms. 1,583 draws is not what's slow. |
| Triangles | **34.2 M per frame**, of which **18.4 M are sun shadows** (4 cascades of 3.8–5.1 M each). |
| Models | 3.77 M tris drawn **six times**: main pass, motion vectors pass, four shadow cascades. |
| Settings | Quality 3: 4096² shadows x 4 cascades, **8x MSAA and TAA together**, 19.3 km view distance. |

GPU time by pass (avg ms): shadows 4.0, roads and rails 2.4, clouds 1.9, models 1.8, plants 1.7, cloud march 1.6,
terrain 1.6, ties 1.3, after-the-world 1.3, motion 0.8, AO 0.7, track depth 0.5.

Conclusions:
- The fix is **fewer triangles and fewer pixels shaded per frame**, not fewer API calls. Draw calls get collapsed too, but
  as a side effect of moving culling and LOD onto the GPU.
- 8x MSAA on top of TAA is paying twice for anti-aliasing on 34 M triangles, and thin geometry (rails, ties, wires) under
  MSAA is the worst case for quad overdraw. That's most of the 2.4 ms in "roads and rails".
- Shadow casters don't drop LOD per cascade, and every cascade is redrawn every frame.
- Vehicle LOD2 (from 220 m) is still ~25–28% of LOD0. There's no LOD3 or impostor, so a long consist far away costs millions
  of triangles for a few hundred pixels.

## Not a ground-up rewrite

A from-scratch rewrite of 76k lines with no automated tests would leave the Route Builder broken for weeks and lose the
track fitting, bridges and sim work that's correct today. Instead: **replace the renderer core behind `FrameScene`, finish
the layering in `refactor-plan.md`, and add a cooked route format.** Every phase builds, runs, and is measured against
the baseline before the next starts.

## Phases

### 0. Measurement (first, ~1 hr)
- Fixed benchmark: `--bench <route> <camera path>` flies a scripted path for 30 s with a train in view and writes
  per-pass GPU/CPU avg, p99 and triangle counts to CSV. Runs at Low/High/Ultra.
- Baseline committed in `docs/perf/`. Every later step reports its delta against it.

### 1. Quick wins in the current renderer (hours; expected ~20 ms → ~11–13 ms GPU)
1. **AA:** TAA by default; MSAA 0/2/4 as an option, never stacked at 8x with TAA. Ultra = TAA + 2x at most.
2. **Shadow LOD:** casters pick their LOD from cascade texel size, not camera distance. Cascades 2–3 use LOD2/impostors.
3. **Shadow caching:** far cascades (2, 3) re-render every 2nd/4th frame or only when the sun or casters move; static
   geometry cached, dynamic (trains) composited on top.
4. **Merge the motion pass:** write velocity as a second colour target in the main pass for moving models instead of drawing
   them again (-0.8 ms, -3.7 M tris).
5. **Vehicle LOD3 + octahedral impostors** past ~600 m, and tighter LOD distances scaled by screen size, not metres.
6. **Ties:** past ~150 m, stop drawing tie instances and let the track texture carry them.
7. Clouds and cloud march at half resolution with temporal reprojection (~-1.5 ms).

### 2. GPU-driven renderer (the core rewrite; ~1–2 sessions)
- **One geometry pool:** every static mesh, vehicle and track chunk in shared vertex/index buffers (`BufferArena` grows into this).
- **Bindless materials:** descriptor indexing, one material table SSBO. One pipeline per shading model, not per object.
- **Instance table on the GPU:** transforms, bounds, LOD ranges, material IDs. CPU only uploads what changed.
- **Compute culling:** frustum + Hi-Z occlusion (last frame's depth pyramid, two-phase) per instance, picks LOD, writes
  `vkCmdDrawIndexedIndirectCount` commands. Same pass reruns per shadow cascade with the light frustum.
- **Depth prepass from the culled set**, then shading with equal-depth test: no overdraw in the expensive shaders.
- Result: ~20–60 indirect draws per frame regardless of scene size; CPU recording well under 1 ms.

### 3. Geometry for 1M+ triangle models (~1–2 sessions)
- **Offline cooker** (`Tools/Cook`): meshoptimizer for meshlets (64 verts / 124 tris), cluster LOD hierarchy (group,
  simplify, lock borders — a Nanite-style DAG), vertex quantisation, meshopt compression.
- **Renderer tiers** (the "multiple renderers"):
  - **Tier A — mesh shaders** (`VK_EXT_mesh_shader`, RTX 20+/RX 6000+): task shader culls clusters (frustum, backface
    cone, Hi-Z), picks the LOD cut by screen-space error. Million-poly locos cost what's on screen.
  - **Tier B — compute cluster culling + MDI** for GPUs without mesh shaders. Same data, same results.
  - **Tier C — classic instanced LOD path** (today's, cleaned up) as the safe fallback and the reference for screenshots.
- Shadows use the same cluster culling with a coarser error target.

### 4. Shadows at scale
- Move to **virtual shadow maps** (16k² sparse page table, only pages the camera samples are drawn, static pages cached
  across frames) on Tier A/B; cached CSM on Tier C.

### 5. Route data for the whole USA
- Split **editable project** (what the builder saves today) from a **cooked route package**:
  - World partitioned into fixed tiles (e.g. 2 km) in a quadtree, each tile one file chunk with a header for streaming.
  - Heights quantised + delta + zstd; track/road/bridge meshes cooked to clusters (phase 3 format); instance lists for
    plants, poles, ties; the rail graph as its own compact layer the sim and server load without any geometry.
  - Content-addressed shared assets (meshes, textures as BC7/BC5 KTX2) so a 5,000-mile route doesn't duplicate them.
- Streaming on a background thread with an async transfer queue upload; double-precision tile origins (already used).

### 6. Game structure: builder, single player, multiplayer
- Finish `refactor-plan.md` steps (RouteWorld owns the route and trains; DriveScreen stops being EditorView).
- **Runtime** loads only cooked packages. **Single player = local server in-process**; **multiplayer = the same server
  headless** (`RailWerkz.Server`) with the authoritative sim (`RailWerkz.Sim`, already clean) and client prediction for the
  local cab. One code path for both.
- Builder → "Export route" produces the cooked package used by both.

## Order and checkpoints

| Step | Ships | Check |
|---|---|---|
| 0 | Benchmark + baseline | CSV committed |
| 1 | Quick wins | GPU ≤ 13 ms on the benchmark at Ultra, screenshots match baseline |
| 2 | GPU-driven Tier C/B | ≤ 60 draws, CPU record < 1 ms, GPU ≤ 9 ms High |
| 3 | Cooker + mesh shader Tier A | A 1M+ tri loco ×20 in view at 120+ fps High |
| 4 | Virtual shadows | Shadows ≤ 1.5 ms |
| 5 | Cooked route format | A multi-route package streams with no hitches (p99 < 12 ms) |
| 6 | SP/MP from cooked routes | Host and join drive the same exported route |

## Risks
- Visual parity: every phase compares launch-line screenshots against the baseline (`--noshadows` first, then with).
- Mesh shaders need driver support: Tier B/C must stay working and be tested, not just compiled.
- The cluster LOD DAG is the hardest piece; Tier B with discrete LODs + impostors already gets most of the win if it slips.
