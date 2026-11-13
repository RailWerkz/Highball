# HIGHBALL

A North American freight train simulator by **RailWerkz**, written in C# on .NET 10 with its own Vulkan renderer.

Routes are built from real places: elevation, land cover and the railway itself come from public map data, and the
track is fitted into what real track is (straights, curves, turnouts) rather than drawn as mapped. Trains are simulated
car by car: couplers with slack, the AAR air brake down the pipe, distributed power by radio, wheel slip and sand.

**Status: 0.0.1, in development.** The Route Builder (Planner, 2D and 3D Editors) works and is where trains are driven
today: stock is placed, coupled, driven and saved with the route from the Consist panel. Signalling logic, sessions and
multiplayer are not in yet.

- [Running](#running)
- [The framework](#the-framework)
- [The renderer](#the-renderer)
- [Lighting and atmosphere](#lighting-and-atmosphere)
- [Train simulation](#train-simulation)
- [Routes from real places](#routes-from-real-places)
- [Track](#track)
- [Audio](#audio)
- [The Route Builder](#the-route-builder)
- [Layout](#layout)
- [Dev flags](#dev-flags)
- [Licences](#licences)

## Running

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and a GPU with Vulkan 1.3. BRUTAL's NuGet packages
bundle their native libraries for Windows x64, Linux x64 and macOS arm64. Shaders are compiled from GLSL to SPIR-V at
build time with `glslc` (from the Vulkan SDK).

```bash
dotnet run --project Highball -c Release
```

Or run `Tools/Run.ps1`. Settings are saved to `%APPDATA%/Highball/Settings.json`, and the log is written next to them.

Routes (`Content/Routes/`) and the train and lineside audio (`Content/Audio/`) are kept on each machine and are not in git.
The vehicles' lower-detail models aren't either: make them once with Blender (the game draws full detail without them):

```bash
blender -b --factory-startup --python Tools/Blender/lods.py -- Content
```

## The framework

Everything is built on [BRUTAL](https://github.com/ahwootech/BRUTAL), which provides the native bindings as NuGet
packages: **GLFW** (window and input), **Vulkan** with **VMA** (memory), **Dear ImGui** with its backends, **OpenAL**,
**RakNet**, **stb**, **glTF**, a numerics library (`float3`, `double3`, quaternions) and a job system. NLayer decodes the
music's MP3s, and FastNoiseLite makes noise.

On top of that sit the RailWerkz libraries, which know nothing about trains:

| Library | What it does |
|---|---|
| `RailWerkz.Core` | Time, content paths, logging, settings, units, OpenAL audio (voices, loops, 3D placement, streamed music) |
| `RailWerkz.Vulkan` | The GPU: device and swapchain, frames in flight, buffers, meshes, textures, render targets, shadow maps, pipelines, glTF models and skeletons, GPU timers |
| `RailWerkz.UI` | The interface toolkit on ImGui: an animation engine (easing, tweens, springs, stagger), OKLAB colour, the palette, draw-list primitives (glass, shadows, gradients), widgets and menus |
| `RailWerkz.Network` | RakNet sessions and message buffers |

`Highball` is the game: the app loop and screens, the world and its renderer, routes, track, signalling, trains and
the Route Builder.

The interface is immediate mode. Screens draw on one ImGui window covering the display; panels in the editors are cards
built from `PanelKit`. `Tween.Float(id, channel, target, duration, ease)` returns an animated value whose state is keyed
by id and dropped when unused, and **Reduce motion** in Options turns animation off.

## The renderer

A forward renderer on **Vulkan 1.3**, written for this game (`Highball/World/WorldRenderer.cs`, `RailWerkz.Vulkan`).

- **Vulkan 1.3 core:** dynamic rendering and synchronization2, with the descriptor-indexing pieces of 1.2. Memory
  comes from VMA. Anything the GPU may still be reading is released through a deferred queue that waits for its frame
  slot to come round.
- **HDR and reverse-Z:** the world is drawn into a floating-point target with reverse-Z depth, so a 41 km view keeps its
  precision at the far end. Everything is placed relative to the camera in doubles, so nothing jitters 100 km from the
  route's origin. Texture coordinates wrap on a period that is a whole number of every texture's repeat, so they don't
  step either.
- **Anti-aliasing:** MSAA (up to 8x) for geometry and alpha-to-coverage for cut-out leaves and grass, plus temporal
  anti-aliasing with motion vectors. Moving trains are drawn a second time into a motion image, so TAA doesn't smear them
  with the ground they pass over.
- **Terrain:** a quadtree of 64 x 64 patches over a 2 m grid, 2 m spacing near the camera rising to 512 m far off. Edges
  between levels are stitched so seams never crack, and patches are built nearest first on the job system, ahead of a
  moving camera.
- **Track and roads:** cross-sections swept along each line (a 1:1.5 ballast trapezoid, a 136 lb rail profile, crowned
  roads, bridge decks on girders and piers), built in square chunks on worker threads and streamed by distance:
  - **Ties and tie plates** are instanced models per chunk, the full model near the camera, a decimated tie and a block
    plate further out.
  - **Past 1 km** the ballast shader paints the sleepers in.
  - **A depth pre-pass** draws the track first, so the expensive ballast shader runs once per pixel.
  - **Hardware clip distances** cut the rails off cleanly at their reach.
- **Models:** glTF, flattened per material, with skinning for characters. Locomotives and rolling stock are assembled
  from parts (body, trucks, wheelsets, doors) built by the Blender scripts in `Tools/Blender`.
- **Vegetation:** grass, pebbles, scrub, boulders and trees placed from the land cover in cells built on background
  threads, seeded by position so they look the same from anywhere, kept off track and roads, and moved by wind in the
  vertex shader.
- **Quality levels:** low to ultra set terrain detail, shadow map size and vegetation reach.

The finished picture goes through `PostProcess`, all compute passes:
- temporal anti-aliasing;
- bloom (a downsample and upsample chain);
- exposure measured from the image;
- god rays and the sun's glare;
- a colour grade (warmth, contrast, saturation);
- the ACES filmic tonemap.

## Lighting and atmosphere

- **Physically based shading** (`Content/Shaders/lighting.glsl`): metal-roughness surfaces with a GGX specular and
  Schlick Fresnel, linear HDR throughout. Metal reflects the sky above the horizon and the land cover's colour below it.
- **The sun and moon are where they really are** for the route's latitude, longitude, date and time (`SunPosition`,
  `MoonPosition`), with the moon's real phase. Once the sun has set the moon lights the world. The stars turn with the
  real sky.
- **A procedural sky from light scattering** (`Atmosphere`): Rayleigh and Mie single scattering worked out on the CPU
  as the sun moves, with an ozone layer to keep twilight blue. Skyboxes can stand in for it and set the light themselves.
- **Volumetric clouds** (`Clouds.cs`, `Content/Shaders/Clouds/`), adapted from Kitten Space Agency's cloud shaders:
  - ten cloud types and a few mixes;
  - marched in compute at half size, in a checkerboard settled over frames, out to the world's depth, so they hold
    from under, inside or above them;
  - their shadows are mapped onto the ground.
- **Sun shadows** (`SunShadows`, `shadows.glsl`):
  - **Four cascades** out to 1.5 km. Each is a sphere around its slice of the view, so it doesn't change size as the
    camera turns, and it moves in whole texels, so edges don't crawl.
  - **A fifth cascade for the track only** (rails, ties, plates, switch blades) carries their sharp shadows on the
    ballast out to 120 m without blurring everything else.
  - **Casters are culled** when their shadow can't reach anything in view.
- **Mountains shade the land at any distance:** past the cascades, `TerrainHorizon` stores how high the land rises around
  each point in eight directions, over 41 km around the camera.
- **Ambient occlusion** traced from depth at half size and settled over frames. It darkens only the sky's share of a
  pixel's light, never the sun's.
- **Local lights** (`LocalLights`, `local_lights.glsl`): a clustered grid over the view, sliced by the log of distance,
  holding up to 255 lamps, 15 per cell. Headlights and ditch lights (which flash with the horn), signal lenses, and
  lineside light poles shining down are all spot lights with a glow that bloom picks up.
- **Weather** (`Weather`, `weather.glsl`): rain or snow falling around the camera, ground that takes a while to wet and
  longer to dry, snow that builds up and melts, and thicker air.

To look at lighting on its own: `Highball --skipsplash --menu lighting` opens a test scene (a locomotive beside rows of
test spheres, rough to polished, metal over non-metal) with the Lighting window. Fix a view with
`--skytime YYYY-MM-DD HOUR`, `--lighting dawn|noon|golden|dusk|night` and `--orbit YAW PITCH DISTANCE`, then `--shot`.

## Train simulation

Trains run on a fixed step of 1/120 s in doubles (`Highball/Trains`). Every car is its own mass with its own speed, on
its own grade and curve.

- **Exact track:** a train's place is an edge and a distance along it on the rail graph (`RailGraph`), built from the
  laid track's straights and arcs and the rail-top heights. A switch is a vertex that sends a train down the route it's
  set for, and running through one set against it forces it over.
- **Couplers with slack** (`TrainBody`): knuckle play, then two friction draft gears in series that stiffen as they close
  and give back less as they open, then solid. Slack runs in and out as it does on the real thing:
  - a train bunched by its brakes stretches one car at a time as the engines load up;
  - a heavy train's rear runs in when the head end brakes first;
  - the couplers can be heard taking up the slack car by car.

  The body is substepped finely enough for the stiffest coupling and lightest car.
- **Resistance:** rolling and air resistance by the Davis equation and curve resistance by AREMA's figures. Brake shoe
  force is set by a car's empty weight, so a loaded car stops slower.
- **The AAR air brake** (`AirBrakes`):
  - The brake pipe runs the length of the train, charged from the main reservoir by the lead's brake valve, with an
    equalizing reservoir.
  - Air flows car to car as a diffusion, so a reduction or release takes time to reach the rear, and a long train
    recharges slowly.
  - Each car has a control valve with auxiliary and emergency reservoirs, its own brake cylinder, quick service and
    emergency venting.
  - Angle cocks and hoses sit between cars: uncouple with the hoses joined and the cocks open, and the train goes into
    emergency.
  - There is an independent brake, a bleed rod, and handbrakes that wind on over a few seconds.
- **Distributed power** (`DistributedPower`), Locotrol style: locomotive consists through the train, each linked to the
  lead by a 450 MHz radio data link. Each message gets through, or doesn't, by the path over the actual terrain and the
  earth's curve. Remotes run synchronous or independent, and a remote that loses comm for 45 seconds idles and cuts out
  its brake valve.
- **Locomotives** (`LocoSystems`):
  - **Electrics:** battery, breakers and computer boot; priming and cranking the engine, with the battery volts sagging
    and the lamps dimming as it turns over.
  - **Control:** the isolation switch, eight throttle notches, and a dynamic brake with an extended-range curve.
  - **Traction:** limited by adhesion, which falls with speed (Curtius and Kniffler).
    - Ask for more than the wheels can hold and they slip, pulling less until the wheel slip control cuts the power back
      and they grip again. AC units hold right at the edge; DC units slip harder and longer.
    - Sand adds grip.
  - **Supplies:** fuel, lube oil, cooling water and sand, from each unit's real capacities.
    - A locomotive weighs its dry weight plus what's aboard.
    - Fuel burns with the power it makes, and oil and water go with it.
    - Running short trips the engine's protection.
- **Many cuts on the track** (up to 10,000 vehicles): cuts that meet couple under 15 mph and are removed above it, and an
  uncoupled cut drawn away from its train becomes a cut of its own. Control is taken and released from the Consist
  panel, and a released cut is left braked. Placed stock is saved with the route.
- **Car bodies** sag with their load and roll on their springs from the track's unevenness and from leaning out on curves
  (drawn only; the dynamics don't see it).

Consists are made up in the Consist Editor or in the Consist panel's Build tab, from vehicles described by a
`vehicle.json` each. Adding a vehicle needs no code.

## Routes from real places

A route is built tile by tile in the Planner (`Highball/Routes/RouteGenerator.cs`):

- **Elevation:** USGS 3D Elevation Program 1 m bare-earth lidar where it exists, and the AWS Terrain Tiles everywhere
  else, eased to meet neighbouring tiles.
- **Land cover:** ESA WorldCover 2021 at 10 m, read straight from its cloud-optimised GeoTIFFs by range requests. It
  decides the ground textures, the trees and the plants.
- **Roads and railways:** OpenStreetMap through the Overpass API, merged into the route's network.
- **One projection per route:** a Transverse Mercator plane (Krüger's series on WGS84) centred on the route's origin, so
  areas built at different times meet exactly. Saved positions (signals, objects, stock) are kept as latitude and
  longitude, so they don't depend on the projection.

Downloads are cached, so rebuilding a tile costs nothing. Every tile keeps a coarse copy of its heights in memory, and
the full 2 m heights near the camera stream in and out on worker threads.

## Track

Mapped railways are clicked by hand over aerial photos and wander a metre or so either way; a train would show every
bit of it. So the track is rebuilt into what real track is:

- **Alignment** (`TrackAlignment`): each line is cut where it strays from a straight, fitted with best-fit straights,
  and joined by circular curves as large as fit between them. Tracks side by side are fitted together, so a yard's tracks
  come out parallel.
- **Profiles:** each line's formation height is the ground it's laid on, smoothed, carried straight over bridges and
  through tunnels, and agreeing with every line it meets. The ground is then reshaped to carry it.
- **Turnouts** (`Turnouts`): worked out from the real geometry where lines meet. Each has switch blades, closure rails,
  a frog with proper separation and guard rails, and long timbers carrying both tracks. Crossovers share long timbers
  across both tracks, and slips, diamonds and three-way switches are handled too.
- **Beds:** mainline, low (yards and branches), sunken (industrial) and street track. A switch between beds of different
  heights gets one merged ballast surface.
- **Rail classes** (main, branch, industrial, yard, transit, narrow gauge, disused), set per track, each in its own
  colour on the overlay. **G** cycles Grade, Class, and Class over the mapped railway.
- **Bridges and grade crossings**, with roads cut back to a crossing's pad.
- **Signalling foundations:** track circuits with insulated joints and stable ids (`Signalling/TrackCircuits.cs`).
  Signals, hand throws and crossing masts are placed; signal logic is still to come.

## Audio

OpenAL with a pool of 64 voices, sounds placed in the world with Doppler (`RailWerkz.Core/Audio/AudioEngine.cs`):

- **Prime movers** (`EngineSound`): the startup recording, idle and notch 8 loops crossfaded and pitched as the revs
  follow the throttle at the engine's own pace, an exhaust chug that comes in with the load, the air compressor cutting
  in and out with the governor, and the dynamic brake's grid blowers. The nearest eight locomotives are heard.
- **Running gear** (`RunningGear`): traction motor whine; rolling loops on the nearest trucks that crossfade from slow
  rolling to axle roll; rail joint clanks; flange squeal on curves; brake squeal; wheel slip.
- **Couplers** (`SlackSound`): run-in and stretch at each coupling as it takes up slack, travelling down the train.
- **Horn and bell** heard from where they are on the lead locomotive. Horns are an attack, a looped sustain and a
  release, brightened and soft-limited on load.
- **Music** streamed from MP3, per screen.

## The Route Builder

`docs/route-builder.md` covers it in full. In short:

- **Planner:** pick tiles over OpenStreetMap and build them with lidar or standard heights, at the road and rail detail
  chosen.
- **2D and 3D Editors:** fly over or through the route and use the tools:
  - sculpt and paint the ground;
  - lay track with the pen or stamps (switches, crossovers, ladders, slips, diamonds), edit its points and fit it
    parallel;
  - set switches, track classes and bed heights;
  - place signals, hand throws, crossings, light poles and bridges;
  - draw roads;
  - edit track circuits.

  Undo and redo cover the edits, and Ctrl+S saves.
- **Consist panel:** build consists, place stock and single vehicles, drive (reverser, throttle, dynamic brake,
  automatic and independent brakes, sander), and work each unit's switches, distributed power, couplings, hoses,
  angle cocks and handbrakes. The brakes and couplers can be graphed.
- **Track materials** (View > Track Materials, or **M**): pick the texture, colour, roughness and metal of every track
  and lineside material. They show live and save to `Content/Materials/Track.json`.
- **Debug windows:** frame statistics with CPU and GPU timings, and a Camera window (**F7**) that gives a launch line
  for the exact view, sky and clouds on screen.

## Layout

```
Content/                 Everything the game loads
  Audio/                 Music, engines, horns, bells, trucks, couplers (not in git)
  Fonts/                 JetBrains Mono, and Inter for the interface if it's there
  Materials/             Track.json: the track and lineside materials
  Models/                Locomotives, rolling stock, couplers, track parts, lineside, vegetation (glTF, vehicle.json)
  Routes/                Each route's tiles, network, objects, signalling and stock (not in git)
  Shaders/               GLSL, compiled to SPIR-V at build time; Post/, Clouds/ and Occlusion/ are compute passes
  Textures/              Ground, ballast, metal and concrete sets, sky and planets
Vendor/                  FastNoiseLite (MIT), BRUTAL's licence and notice
RailWerkz.Core/          Time, content paths, logging, settings, maths, OpenAL audio, music streaming
RailWerkz.Vulkan/        The GPU, buffers, meshes, textures, render targets, shadow maps, pipelines, glTF models
RailWerkz.Network/       RakNet sessions and message buffers
RailWerkz.UI/            The interface toolkit: animation, colour, canvas, widgets, menus, the startup runner
Highball/                The game
  App/                   Window and loop, screen stack, input, dev flags, debug windows
  Builder/               The Route Builder: Planner, 2D and 3D Editors, their tools, the Consist panel
  Routes/                Route manifests, the projection, tile generation from map data
  Screens/               Splash, menus, Route Builder, Consist Editor, options, credits
  Signalling/            Track circuits and the track graph
  Trains/                Vehicles, consists, the rail graph, train physics, air brakes, distributed power, sounds
  World/                 The renderer, terrain, sky, clouds, shadows, lights, weather, vegetation, track meshes
Tools/                   Blender scripts for rolling stock, track parts, lineside and bridges; texture tools
docs/                    The Route Builder guide
```

## Dev flags

```bash
Highball --skipsplash --menu options --shot 3 options.png --quit 3.5
```

- `--menu single|multi|builder|options|credits|lighting`: open that screen after the main menu.
- `--skipsplash`.
- `--shot SECONDS FILE.png [SECONDS FILE.png ...]` and `--quit SECONDS`: screenshots and a timed quit. Runs that use
  these never write the player's settings.
- `--novsync`.
- `--route NAME --view --camera LAT LON ABOVE_GROUND YAW PITCH`: open a route's 3D Editor at a place. The Camera window
  (F7) gives the whole line for the current view.
- `--train CONSIST LAT LON NOTCH`: place a saved consist there and drive it.
- `--skytime`, `--cloudtype`, `--clouds`, `--weather`: fix the sky and weather.
- Effects off, to see what they cost: `--noshadows`, `--notaa`, `--nobloom`, `--norays`, `--noao`, `--novegetation`.

## Licences

BRUTAL: see `Vendor/BRUTAL_LICENSE.txt` and `Vendor/BRUTAL_NOTICE.txt`. FastNoiseLite and NLayer: MIT. JetBrains
Mono: OFL. Volumetric clouds adapted from Kitten Space Agency's cloud shaders. Locomotive and coupler models: see the
`CREDITS.txt` beside each one under `Content/Models` (the EMD SD40 by Heataker, CC BY 4.0, modified).
