# Content

Everything the game loads, one folder per kind. Every asset has a folder of its own with a small JSON file that says what it is,
so a new one is added by dropping in a folder rather than by writing code.

```
Content/
  Materials/<Category>/<Name>/        PBR surfaces (track, roads, crossings, paints)
      material.json                   name, category, sizes, source, licence
      preview.png                     (optional) thumbnail
      1K/  2K/                        albedo, normal, roughness, ao, height, metalness (.jpg or .png)
  Meshes/
      Track/                          track pieces: ties, plates, switch kits, turnouts, the bridge kit
      Lineside/<Kind>/                placed beside the track: Signals, HandThrows, Crossings, Poles, Ties, RoadSigns
          asset.json                  id, name, its models, and each model's paint slots and the material each is drawn with
      Vehicles/
          Locomotives/<Model>/<Road>/ vehicle.json, assembly.json, CREDITS.txt, LOD0/ (LOD1/, LOD2/ made by Tools/Blender/lods.py)
          Freight/<Type>/             vehicle.json, LOD0/
          Couplers/                   the coupler every vehicle shares
          Legacy/                     the first freight cars, switched off (vehicle.json.disabled)
      Vegetation/  Characters/
  Terrain/<Layer>/                    the ground's layers (DDS): Grass, BareSand, Cliffs...; Clutter/, Modifiers/
  Sky/Skyboxes/  Sky/Planets/         skyboxes (horizontal-cross PNGs), the sun and moon
  Presets/Track.json                  which materials and paints the track and lineside are drawn with (the Material Editor)
  Audio/  Fonts/  Shaders/  Routes/  Packs/
```

## Rules

- **A material is named by its folder under Materials**: `Metal/Metal021`, `Ground/Rocks022`. That name is what routes, presets
  and asset.json files store. Map files inside a size folder use the standard names above.
- **Sizes**: 1K and 2K. The game picks by its texture quality and falls back to the nearest size there is.
- **What an asset's made from stays beside it, in `Source/`** (Blender files, part models, downloaded originals). Git and the
  build leave every `Source/` folder out.
- **Downloads and unpacked third-party packs go in `ContentSource/`** at the repository root (ignored), never in Content.
- **Lineside models name their parts after a paint** (`Fixed_Black`, `Moving_Red`). asset.json lists each model's slots;
  `Tools/Content/write_assets.py` refreshes the list after a model changes, keeping any material already set. A slot is either
  a shared paint's name (`"Black": "Black"`, edited in the Material Editor) or a paint of the model's own
  (`"Black": {"texture": "Metal/Metal024", "color": [r, g, b], "strength": 0.3, "metal": false, "roughness": 0.45}`), which the
  Inspector's Materials card sets and saves (`LinesidePaints`). Only the shared seven (Black, White, Galv, Yellow, Red, Green,
  Concrete) can be swapped; lenses, wood and ballast are drawn by the shader's own rules. Paints share at most 16 textures.

## Adding things

- **A texture**: drop the download (zip) into `Materials/<Category>/` and run `python Tools/Textures/tidy.py`. It becomes
  `Materials/<Category>/<Name>/` with its material.json, and the original zip goes to `TextureSources/`.
- **A vehicle**: a folder under `Meshes/Vehicles/` with a vehicle.json whose model paths are relative to `Meshes/`.
- **A lineside model**: export it into `Meshes/Lineside/<Kind>/` (the scripts in `Tools/Blender` do), then run
  `python Tools/Content/write_assets.py`.
