# Core boundaries refactor

Layers that only talk downward: **Sim** (trains, brakes, rail graph) → **World** (the loaded route and the trains on it) →
**Renderer** (draws a plain per-frame description) → **Screens and tools** (talk to the world, hand the renderer data).

## Where it stands

- **The drive screen is the editor in disguise.** `DriveScreen` builds `new EditorView(route, middle) { Driving = true }`
  (`Highball/Screens/DriveScreen.cs:47`), and the editor is littered with `Driving` branches and statics to support it.
- **`EditorView` owns everything.** The renderer, terrain and track meshes, streaming, rail graph and saved stock,
  multiplayer, train stepping, cameras, all 15 tools behind `if (_tool == ...)` chains, about 860 lines of scripted dev
  operations (`ShapeOp`, `:1807`) and 37 `DevTools` hooks.
- **The Consist panel runs the railway.** `TrainPanel` owns `Railway` (`TrainPanel.cs:32`). It steps it with frame
  pacing, builds the vehicle draws, lamps, exhaust smoke, outlines, picking and 3D engine sound, and reads the horn, bell
  and sand keys itself.
- **The renderer takes editor types and side channels.**
  - `Render(cmd, size, Camera, Terrain?, NetworkMeshes?, models, outlines)`.
  - Screens also set `SceneLights`, `SceneSmoke`, `SceneHaze` and the outline colours as properties.
  - Four screens (editor/drive, consist editor, lighting scene, app) each wire up their own renderer and lighting panel.
- **Duplicated code.**
  - Vehicle body and truck draws: three copies.
  - Vehicle model caches: two.
  - Locomotive lamp maths: two.
- **The grade overlay** recomputes colours and projects every rail point each frame, though neither changes unless the
  track does.
- **Fine as it is.** `RailWerkz.Sim` is clean. `NetworkMeshes` and `NetworkLines` are big but cohesive and threaded:
  put them behind small interfaces rather than splitting them.

## Target types

| Type | Responsibility | Takes code from |
|---|---|---|
| `World/RouteWorld` | The one place a route is loaded and stepped: terrain, track meshes, switches, rail graph, `Railway`, stock save and restore, signals, multiplayer. `Update(camera)` streams; `Step(dt)` runs the trains. | EditorView setup and streaming, TrainPanel stepping |
| `Trains/TrainVisuals` | Turns the railway into draws, lamps, smoke, haze, outlines and pick targets | TrainPanel |
| `Trains/TrainAudio` | Engine and train sound | TrainPanel |
| `TrainPanel` | Just the window: tabs, controls, tags, coupling marks | (shrinks) |
| `World/FrameScene` | What the renderer draws this frame: camera, ground and track (behind small interfaces), models, outlines and style, lights, smoke, haze, overlay lines | renderer side channels |
| `World/WorldView` | Shared screen plumbing: renderer, lighting panel, scene, composite | four hand-wired copies |
| `VehicleDraws`, `LocoLamps` | Shared helpers | three and two duplicates |
| `IEditorTool` + one class per tool | Each tool's update, draw, panel and escape | EditorView's `if (_tool == ...)` chains |
| `EditorCamera`, `DriveCamera`, `EditorScript` | Cameras and the scripted dev operations | EditorView |

`EditorView` ends up as the composition root: active tool, undo, menus, building the frame scene. `DriveScreen` uses
`RouteWorld`, `DriveCamera`, `TrainPanel` and `WorldView` directly.

## Steps (each one commit, each builds and runs)

Baseline screenshots and logs before step 1, compared after every step:
- A: drive
- B: editor with a train placed and picked
- C: consist editor
- D: lighting scene at night
- E: tool scripts
- F: host and join

1. Move `ShapeOp` into its own partial file (no logic change).
2. Pull the cameras out into `EditorCamera` and `DriveCamera`.
3. Add `FrameScene`; the renderer and shadows take it; delete the side-channel properties.
4. Extract `WorldView` and use it in all four screens.
5. Extract `TrainVisuals` from TrainPanel.
6. Add the shared `VehicleDraws` and `LocoLamps`, used by the consist editor and lighting scene.
7. Extract `TrainAudio`.
8. Introduce `RouteWorld`: loading, streaming, rail graph, stock, signals, `Railway` and its stepping.
9. Point `DriveScreen` at `RouteWorld` and drop the editor from driving; remove the `Driving` branches.
10. Add `IEditorTool` and `ToolContext`, then move tools one per commit, Place and Bridges last.
11. Grade overlay: cache colours per track version, then later draw it in the renderer from world-space lines.
12. Optional: move the ImGui windows (`LightingPanel`, `MaterialsWindow`) out of `World/`.

## Risks

- **Frame order matters.** Step, then pose, then follow camera, then streaming, or a followed train judders. Check this
  with the `--train` smoothness log.
- **Frame pacing** moves with the stepping, not with the drawing.
- **DevTools hooks** are one-shot statics, so each moves together with the code it drives.
- **Undo** stays in EditorView and is passed to tools; it isn't redesigned.
- **Multiplayer** attach stays next to the rail graph rebuild.
