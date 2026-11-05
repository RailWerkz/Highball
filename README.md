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

## The lighting test scene

```bash
Highball --skipsplash --menu lighting
```

The SD40 on a short length of track beside rows of test spheres (metal over non-metal, rough to polished), with
sliders for the sun and exposure and a sky picker. Drag to orbit, wheel to zoom. To compare the same view after a
shader change, fix it with `--lighting noon|golden|night [csx|source]` and `--orbit YAW PITCH DISTANCE`, then use `--shot`.

This scene and the terrain use physically based light (`Content/Shaders/lighting.glsl`): each direct light goes through
`shadeLight`, and there are no local lights yet. Track and roads keep their own shaders. Everything receives the sun's
shadows (`Content/Shaders/shadows.glsl`, `SunShadows`); terrain and whole models cast them, track and roads don't yet.
`--noshadows` turns them off, to see what they cost.

## Licences

BRUTAL: see `Vendor/BRUTAL_LICENSE.txt` and `Vendor/BRUTAL_NOTICE.txt`. FastNoiseLite and NLayer: MIT. JetBrains
Mono: OFL. EMD SD40 model by Heataker: CC BY 4.0, modified; see `Content/Models/Locomotives/SD40/CREDITS.txt`.
