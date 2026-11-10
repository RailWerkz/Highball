# HIGHBALL

A train simulator by **RailWerkz**. Written in C# on [BRUTAL](https://github.com/ahwootech/BRUTAL), which provides the
GLFW, OpenGL, Dear ImGui, OpenAL, RakNet, stb and glTF bindings.

**Status: 0.0.1.** The RailWerkz framework, a splash that runs the startup checks, and the menus: main, single
player, multiplayer, route builder, options, credits. Driving, sessions and the Route Builder are not in yet.

## Running

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). BRUTAL's NuGet packages bundle their native
libraries for Windows x64, Linux x64 and macOS arm64.

```bash
dotnet run --project Highball -c Release
```

Or run `Tools/Run.ps1`.

## Layout

```
Content/                 Everything the game loads
  Audio/Music/           MP3s, in one folder per track: Menu/, Credits/, Loading/, Driving/ (played in name order)
  Fonts/                 JetBrains Mono. Add Inter-Regular/Medium/SemiBold/Bold.ttf here and the interface uses Inter
  Routes/TheRoute/       A route: Route.json (name, description, origin, era...), and later its terrain and track
  Changelog.json         Releases, shown on the main menu
Vendor/                  FastNoiseLite (MIT), BRUTAL's licence and notice
RailWerkz.Core/          Time, content paths, logging, settings, maths, OpenAL audio, MP3 music streaming, SoundForge
RailWerkz.OpenGL/        Shaders, meshes, textures, cubemaps, render targets, glTF models and skeletons
RailWerkz.Network/       RakNet sessions and message buffers
RailWerkz.UI/            The interface toolkit:
  Anim/                  Animation engine modelled on ImAnim: easing presets, cubic-bezier, tweens with
                         crossfade/cut/queue, springs, stagger, oscillators, noise and shake
  Rgba, Style            Colours (blended in OKLAB), the palette and accent, fonts, scale
  Canvas, Widgets, Menu  Draw-list primitives (glass, shadows, gradients), buttons, fields, pills, the animated menu
  Startup                The step-at-a-time startup runner behind the splash
Highball/                The game
  App/                   Window and loop, screen stack, input, dev flags
  Routes/                Route manifests
  Screens/               Splash, main menu, single player, multiplayer, route builder, options, credits
```

## The interface

Screens draw on one ImGui window covering the display. `Tween.Float(id, channel, target, duration, ease)` returns
the animated value; its state is keyed by id and channel and dropped when unused. **Reduce motion** in Options turns
animation off.

Keyboard: arrows or W/S, Enter, Esc, left/right to change a value, Q/E for tabs. The mouse works too.

## Dev flags

```bash
Highball --skipsplash --menu options --shot 3 options.png --quit 3.5
```

`--menu single|multi|builder|options|credits|lighting` (open that screen after the main menu), `--skipsplash`,
`--shot SECONDS FILE.png [SECONDS FILE.png ...]`, `--quit SECONDS`, `--novsync`. Runs that take screenshots or quit
on a timer never write the player's settings.

Settings are saved to `%APPDATA%/Highball/Settings.json`, and the log is written next to them.

## Track materials

In the Route Builder's 3D Editor, **View > Track Materials** (or **M**) lists every texture the track and lineside parts are
drawn with: ballast, rail top, rail sides, ties, tie plates, pole wood, bridge steel, and the paints that signals, hand throws,
crossing masts and poles are made of (each with its own texture, colour, texture strength, roughness and metal). Pick any
texture under `Content/Textures` (ambientCG `Name_2K-JPG.zip` sets, a zip, or a folder of `_Color`/`_NormalGL`/`_Roughness`
maps), or type a path to one anywhere on disk; it shows on the track a moment later. **Save** writes
`Content/Materials/Track.json`, which the game also reloads if it's edited while running. `--materials` opens the window.

## The lighting test scene

```bash
Highball --skipsplash --menu lighting
```

The SD40-2 on a short length of track beside rows of test spheres (metal over non-metal, rough to polished), with
the Lighting window (sky, date and time, exposure). Drag to orbit, wheel to zoom. To compare the same view after a
shader change, fix it with `--skytime YYYY-MM-DD HOUR`, `--lighting dawn|noon|golden|dusk|night [csx|source]` and
`--orbit YAW PITCH DISTANCE`, then use `--shot`. The Route Builder has the same window under its Lighting menu, with the
sun worked out from the route's place on Earth.

This scene and the terrain use physically based light (`Content/Shaders/lighting.glsl`): each direct light goes through
`shadeLight`, and there are no local lights yet. Track and roads go through it too, and cast shadows: beds, roads and bridges in every cascade, rails and ties only in the nearer two. Metal reflects the sky above the horizon and the land cover's colour round the camera below it (no reflection probes yet). Everything receives the sun's
shadows (`Content/Shaders/shadows.glsl`, `SunShadows`); terrain, plants and whole models cast them too.
Lamps go through `LocalLights`, a grid over the view that says which reach which pixels: yard lights placed along yard track by
`YardLights`, signals, and an engine's headlights, each with a glow that bloom picks up. `Weather` (`--weather Rain` or `Snow`, or the
Lighting window) wets or snows on every surface through `weather.glsl`, thickens the air and draws what's falling round the camera.

The picture is then finished by `PostProcess`: temporal anti-aliasing (`--notaa`), bloom (`--nobloom`), exposure measured from the image,
god rays and the sun's glare (`--norays`), and a look (`ColorGrade`, `--grade`). `--noshadows` turns them off, to see what they cost. Past the cascades, `TerrainHorizon` keeps how high the land rises round each
point in eight directions, over 41 km round the camera, so mountains shade the land at any distance. `AmbientOcclusion` darkens
what the sky can't reach, from the depth alone, sparing the sun's light (`--noao`, or the Lighting window, turns it off).

The procedural sky has volumetric clouds (`Highball/World/Clouds.cs`, `Content/Shaders/Clouds/`), adapted from Kitten Space
Agency's cloud shaders. They march after the world, as far as its depth, so they hold from under, inside and above them. The
Lighting window picks a type (`CloudType`: the ten basic types and a few mixes), or `--cloudtype Cumulus`, and `--clouds AMOUNT`
sets how much of it. The moon (`MoonPosition`) is where it really is
for the date, with its real phase, and lights the world once the sun has gone. The stars are invented but turn with the real sky.

Over the terrain, `Vegetation` places grass, scrub, stones and trees from the land cover as the camera moves, keeping off track and
roads (`--novegetation`, or the Lighting window, turns them off). The scrub's texture and the stones are generated; the grass cards
are from `Content/Textures/Terrain/GroundClutter`, and the trees from `Content/Models/Vegetation`, only where the map shows forest.

## Licences

BRUTAL: see `Vendor/BRUTAL_LICENSE.txt` and `Vendor/BRUTAL_NOTICE.txt`. FastNoiseLite and NLayer: MIT. JetBrains
Mono: OFL. EMD SD40 model by Heataker: CC BY 4.0, modified; see `Content/Models/Locomotives/SD40/CREDITS.txt`.
