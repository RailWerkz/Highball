using Brutal.ImGuiApi;
using Highball.App;
using Highball.World;
using RailWerkz.Core.Logging;
using RailWerkz.UI;
using RailWerkz.Vulkan;

namespace Highball.Builder;

/// The Track Materials window: every part of the track and of what's placed beside it, the texture each is drawn with now, and
/// a list of every texture under Content/Textures (or any path) to swap it for. A change shows on the next frame or two;
/// Save writes it to Content/Materials/Track.json for good (TrackMaterials).
public sealed class MaterialsWindow : IDisposable
{
    public static MaterialsWindow Instance { get; } = new();

    private const float Thumb = 44f;
    private const float SmallThumb = 22f;

    public bool Open;

    private List<TrackMaterials.Choice>? _choices;
    private readonly ImInputString _filter = new(96, ""u8);
    private readonly ImInputString _path = new(512, ""u8);
    private readonly Dictionary<string, Thumbnail> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    private string? _status;

    private sealed class Thumbnail
    {
        public Task<(byte[] Pixels, int Size)?>? Loading;
        public Texture2D? Texture;
        public ImTextureRef? Ref;
        public bool Missing;
    }

    private MaterialsWindow()
    {
        if (DevTools.Materials)
            Open = true;
    }

    public void Toggle() => Open = !Open;

    public void Draw()
    {
        if (!Open)
            return;

        _choices ??= TrackMaterials.Find();
        UploadThumbnails();

        ImGui.SetNextWindowPos(new float2(ImGui.GetIO().DisplaySize.X - 500f, 60f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new float2(480f, 720f), ImGuiCond.FirstUseEver);
        if (WindowBounds.Begin("Track Materials"u8, ref Open))
        {
            Toolbar();
            ImGui.Separator();
            if (ImGui.BeginChild("materials"u8, new float2(0f, 0f), ImGuiChildFlags.None, ImGuiWindowFlags.None))
            {
                var materials = TrackMaterials.Current;
                if (ImGui.CollapsingHeader("Track"u8, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var slot in (TrackMaterials.Slot[])[TrackMaterials.Slot.Ballast, TrackMaterials.Slot.RailTop, TrackMaterials.Slot.RailSide, TrackMaterials.Slot.Ties, TrackMaterials.Slot.Plates])
                        SlotRow(materials, slot);
                }

                if (ImGui.CollapsingHeader("Lineside"u8, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    foreach (var slot in (TrackMaterials.Slot[])[TrackMaterials.Slot.PoleWood, TrackMaterials.Slot.BridgeSteel])
                        SlotRow(materials, slot);
                }

                if (ImGui.CollapsingHeader("Signal lamps"u8, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    for (var i = 0; i < TrackMaterials.Lamps.Length; i++)
                        LampRow(materials, i);
                }

                if (ImGui.CollapsingHeader("Signals, hand throws, crossings and poles"u8, ImGuiTreeNodeFlags.DefaultOpen))
                {
                    for (var i = 0; i < TrackMaterials.Paints.Length; i++)
                        PaintRow(materials, i);
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();
    }

    private void Toolbar()
    {
        var unsaved = TrackMaterials.Unsaved;
        ImGui.BeginDisabled(!unsaved);
        if (ImGui.Button("Save"u8))
        {
            try
            {
                TrackMaterials.Current.Save();
                _status = "Saved to " + TrackMaterials.FilePath;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _status = "Couldn't save: " + e.Message;
                Log.Warn($"Track materials: {e.Message}");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Revert"u8))
        {
            TrackMaterials.Revert();
            _status = "Back to what was saved";
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Game defaults"u8))
        {
            TrackMaterials.ResetToDefaults();
            _status = "Defaults (not saved)";
        }

        ImGui.SameLine();
        if (ImGui.Button("Rescan"u8))
        {
            _choices = TrackMaterials.Find();
            _status = $"{_choices.Count} textures found";
        }

        ImGui.SetItemTooltip("Content/Textures"u8);

        if (TrackTextures.Loading)
            ImGui.TextColored(new float4(0.95f, 0.75f, 0.3f, 1f), "Loading textures..."u8);
        else if (unsaved)
            ImGui.TextColored(new float4(0.95f, 0.75f, 0.3f, 1f), "Changed, not saved"u8);
        else
            ImGui.TextDisabled("Saved"u8);
        if (_status is { } status)
        {
            ImGui.SameLine();
            ImGui.TextDisabled(status);
        }
    }

    private void SlotRow(TrackMaterials materials, TrackMaterials.Slot slot)
    {
        var (_, label, help, fallback) = TrackMaterials.Slots.First(s => s.Slot == slot);
        var id = materials.TextureOf(slot);
        ImGui.PushID(slot.ToString());
        Header(id, label, help, id == fallback, TrackTextures.InUse.TryGetValue(slot.ToString(), out var used) && used.Length == 0);
        if (Picker(id, fallback) is { } picked)
            materials.SetTexture(slot, picked);
        ImGui.EndGroup();
        ImGui.PopID();
        ImGui.Spacing();
    }

    private void PaintRow(TrackMaterials materials, int index)
    {
        var (name, fallback) = TrackMaterials.Paints[index];
        var paint = materials.PaintOf(index);
        var users = TrackMaterials.PaintUsers.TryGetValue(name, out var list) && list.Count > 0 ? "Used by " + string.Join(", ", list) : "Not used by any model";
        ImGui.PushID(name);
        Header(paint.Texture, name, users, paint == fallback || Same(paint, fallback), TrackTextures.InUse.TryGetValue(name, out var used) && used.Length == 0);
        if (Picker(paint.Texture, fallback.Texture) is { } picked)
            materials.SetPaint(index, paint with { Texture = picked });

        // The colour's linear in the file and the shader; the picker shows it as it looks.
        var shown = new float3(ToSrgb(paint.Color[0]), ToSrgb(paint.Color[1]), ToSrgb(paint.Color[2]));
        ImGui.SetNextItemWidth(-90f);
        if (ImGui.ColorEdit3("Colour"u8, ref shown, ImGuiColorEditFlags.Float))
            materials.SetPaint(index, paint with { Color = [ToLinear(shown.X), ToLinear(shown.Y), ToLinear(shown.Z)] });

        var strength = paint.Strength;
        ImGui.SetNextItemWidth(-90f);
        if (ImGui.SliderFloat("Texture"u8, ref strength, 0f, 1f, "%.2f"u8, ImGuiSliderFlags.None))
            materials.SetPaint(index, paint with { Strength = strength });
        ImGui.SetItemTooltip("0 flat paint, 1 full texture"u8);

        var roughness = paint.Roughness;
        ImGui.SetNextItemWidth(-160f);
        if (ImGui.SliderFloat("Roughness"u8, ref roughness, 0f, 1f, "%.2f"u8, ImGuiSliderFlags.None))
            materials.SetPaint(index, paint with { Roughness = roughness });
        ImGui.SetItemTooltip("Before the roughness map (60%)"u8);
        ImGui.SameLine();
        var metal = paint.Metal;
        if (ImGui.Checkbox("Metal"u8, ref metal))
            materials.SetPaint(index, paint with { Metal = metal });
        ImGui.SameLine();
        if (ImGui.SmallButton("Texture's colour"u8))
            materials.SetPaint(index, paint with { Color = [0.5f, 0.5f, 0.5f], Strength = 1f });

        ImGui.EndGroup();
        ImGui.PopID();
        ImGui.Spacing();
    }

    private static void LampRow(TrackMaterials materials, int index)
    {
        var (name, fallback) = TrackMaterials.Lamps[index];
        var lamp = materials.LampOf(index);
        ImGui.PushID("lamp" + name);
        var max = MathF.Max(lamp.Color[0], MathF.Max(lamp.Color[1], lamp.Color[2]));
        ImGui.ColorButton("##swatch"u8, new float4(ToSrgb(lamp.Color[0] / max), ToSrgb(lamp.Color[1] / max), ToSrgb(lamp.Color[2] / max), 1f),
            ImGuiColorEditFlags.NoTooltip, new float2(Thumb, Thumb));
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(name);
        ImGui.SameLine();
        if (SameLamp(lamp, fallback))
            ImGui.TextDisabled("default"u8);
        else
        {
            ImGui.TextColored(new float4(0.5f, 0.8f, 1f, 1f), "changed"u8);
            ImGui.SameLine();
            if (ImGui.SmallButton("Reset"u8))
                materials.SetLamp(index, fallback);
        }

        // The colour's kept with its brightest channel at 1: brightness is set on its own.
        var shown = new float3(ToSrgb(lamp.Color[0]), ToSrgb(lamp.Color[1]), ToSrgb(lamp.Color[2]));
        ImGui.SetNextItemWidth(-110f);
        if (ImGui.ColorEdit3("Colour"u8, ref shown, ImGuiColorEditFlags.Float))
        {
            float[] linear = [ToLinear(shown.X), ToLinear(shown.Y), ToLinear(shown.Z)];
            var top = MathF.Max(linear.Max(), 1e-4f);
            materials.SetLamp(index, lamp with { Color = [linear[0] / top, linear[1] / top, linear[2] / top] });
        }

        float Slider(ImString label, float value, float min, float max, ImString format, ImString tip, bool log = false)
        {
            ImGui.SetNextItemWidth(-110f);
            ImGui.SliderFloat(label, ref value, min, max, format, log ? ImGuiSliderFlags.Logarithmic : ImGuiSliderFlags.None);
            ImGui.SetItemTooltip(tip);
            return value;
        }

        var lens = Slider("Lens glow"u8, lamp.LensBrightness, 0f, TrackTextures.LensScale, "%.1f"u8, "Lit lens brightness"u8);
        var intensity = Slider("Light"u8, lamp.Intensity, 0f, 50f, "%.2f"u8, "Light cast ahead"u8, true);
        var range = Slider("Reach (m)"u8, lamp.Range, 1f, 200f, "%.0f"u8, "Light range"u8, true);
        var beam = Slider("Beam (deg)"u8, lamp.Beam, 5f, 175f, "%.0f"u8, "Half-angle; fades from 2/3"u8);
        var glow = Slider("Halo size (m)"u8, lamp.GlowSize, 0f, 2f, "%.2f"u8, "Seen from afar; 0 for none"u8);
        var glowBright = Slider("Halo brightness"u8, lamp.GlowBrightness, 0f, 20f, "%.2f"u8, "Halo brightness"u8, true);
        var changed = lamp with { LensBrightness = lens, Intensity = intensity, Range = range, Beam = beam, GlowSize = glow, GlowBrightness = glowBright };
        if (!SameLamp(changed, lamp))
            materials.SetLamp(index, changed);

        ImGui.EndGroup();
        ImGui.PopID();
        ImGui.Spacing();
    }

    private static bool SameLamp(TrackMaterials.Lamp a, TrackMaterials.Lamp b) =>
        a.Color.SequenceEqual(b.Color) && a.LensBrightness == b.LensBrightness && a.Intensity == b.Intensity && a.Range == b.Range && a.Beam == b.Beam &&
        a.GlowSize == b.GlowSize && a.GlowBrightness == b.GlowBrightness;

    private static bool Same(TrackMaterials.Paint a, TrackMaterials.Paint b) =>
        a.Texture == b.Texture && a.Color.SequenceEqual(b.Color) && a.Strength == b.Strength && a.Metal == b.Metal && a.Roughness == b.Roughness;

    /// The part's thumbnail, its name, and whether it's as the game has it; leaves a group open beside the thumbnail for the
    /// row's controls.
    private void Header(string id, string label, string help, bool isDefault, bool missing)
    {
        Image(id, Thumb);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(id);
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.Text(label);
        ImGui.SetItemTooltip(help);
        ImGui.SameLine();
        if (missing)
            ImGui.TextColored(new float4(1f, 0.4f, 0.35f, 1f), "not found, drawn grey"u8);
        else if (!isDefault)
            ImGui.TextColored(new float4(0.5f, 0.8f, 1f, 1f), "changed"u8);
        else
            ImGui.TextDisabled("default"u8);
    }

    /// The texture list for one part. Returns the one picked, if one was.
    private string? Picker(string current, string fallback)
    {
        string? picked = null;
        ImGui.SetNextItemWidth(-28f);
        if (ImGui.BeginCombo("##texture"u8, current, ImGuiComboFlags.HeightLarge))
        {
            if (ImGui.IsWindowAppearing())
            {
                ImGui.SetKeyboardFocusHere();
                _path.Clear();
            }

            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##filter"u8, "Filter"u8, _filter, ImGuiInputTextFlags.None, null, IntPtr.Zero);
            var filter = _filter.ToString().Trim();

            if (ImGui.Selectable($"Game default ({fallback})", current == fallback, ImGuiSelectableFlags.None, null))
                picked = fallback;
            ImGui.Separator();

            string? group = null;
            foreach (var choice in _choices!)
            {
                if (filter.Length > 0 && !choice.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (choice.Group != group)
                {
                    group = choice.Group;
                    ImGui.SeparatorText(group);
                }

                Image(choice.Id, SmallThumb);
                ImGui.SameLine();
                var sizes = choice.Sizes.Length > 0 ? "   " + string.Join(" ", choice.Sizes) : "";
                if (ImGui.Selectable($"{choice.Label}{sizes}##{choice.Id}", string.Equals(choice.Id, current, StringComparison.OrdinalIgnoreCase), ImGuiSelectableFlags.None, new float2(0f, SmallThumb)))
                    picked = choice.Id;
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(choice.Id);
            }

            ImGui.SeparatorText("Anywhere else"u8);
            ImGui.TextDisabled("Folder, .zip or Content/Textures path"u8);
            ImGui.SetNextItemWidth(-60f);
            var entered = ImGui.InputTextWithHint("##path"u8, "D:/Textures/MyRock"u8, _path, ImGuiInputTextFlags.EnterReturnsTrue, null, IntPtr.Zero);
            ImGui.SameLine();
            if ((ImGui.Button("Use"u8) || entered) && _path.ToString().Trim().Trim('"') is { Length: > 0 } path)
            {
                picked = path.Replace('\\', '/');
                ImGui.CloseCurrentPopup();
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("x"u8, new float2(22f, 0f)))
            picked = fallback;
        ImGui.SetItemTooltip("Reset"u8);
        return picked != null && picked != current ? picked : null;
    }

    /// A texture's colour map, small; a grey square while it loads or if there's none.
    private void Image(string id, float size)
    {
        if (!_thumbs.TryGetValue(id, out var thumb))
        {
            _thumbs[id] = thumb = new Thumbnail();
            thumb.Loading = Task.Run(() => TrackMaterials.Thumbnail(id));
        }

        if (thumb.Ref is { } texture)
            ImGui.Image(texture, new float2(size, size), null, null);
        else
        {
            var at = ImGui.GetCursorScreenPos();
            ImGui.Dummy(new float2(size, size));
            var draw = ImGui.GetWindowDrawList();
            draw.AddRectFilled(at, at + new float2(size, size), thumb.Missing ? 0xFF3030A0u : 0xFF404040u);
        }
    }

    private void UploadThumbnails()
    {
        // A few a frame, so opening the list doesn't stall.
        var uploaded = 0;
        foreach (var thumb in _thumbs.Values)
        {
            if (uploaded >= 6)
                break;
            if (thumb.Loading is not { IsCompleted: true } task)
                continue;
            thumb.Loading = null;
            if (task.Result is { } result)
            {
                thumb.Texture = Texture2D.FromPixels(result.Size, result.Size, result.Pixels, repeat: false, mipmaps: false, srgb: true, name: "thumbnail");
                thumb.Ref = HighballApp.Instance.AddTexture(thumb.Texture);
                uploaded++;
            }
            else
                thumb.Missing = true;
        }
    }

    private static float ToSrgb(float linear) =>
        linear <= 0.0031308f ? linear * 12.92f : 1.055f * MathF.Pow(Math.Max(linear, 0f), 1f / 2.4f) - 0.055f;

    private static float ToLinear(float srgb) =>
        srgb <= 0.04045f ? srgb / 12.92f : MathF.Pow((srgb + 0.055f) / 1.055f, 2.4f);

    public void Dispose()
    {
        foreach (var thumb in _thumbs.Values)
        {
            if (thumb.Ref is { } texture)
                HighballApp.Instance.RemoveTexture(texture);
            thumb.Texture?.Dispose();
        }

        _thumbs.Clear();
    }
}
