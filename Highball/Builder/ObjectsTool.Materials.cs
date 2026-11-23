using System.Globalization;
using Brutal.ImGuiApi;
using Highball.World;
using RailWerkz.Core.Logging;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Inspector's Materials card for the object picked: each model it's built from, and for each paint its parts are named for
/// (a slot), what that's drawn with: a shared paint (the Material Editor's Black, Galv...) or a paint of the model's own, with its own
/// texture. Slots belong to the model, so every object built from it changes together; they're saved to the model's asset.json
/// (LinesidePaints). A grade crossing has its own paints already, in Track.json (the Material Editor's Grade crossings).
internal sealed partial class ObjectsTool
{
    private const string OwnPaint = "Own paint";

    private void MaterialsPanel(PlacedObject picked)
    {
        if (picked.Kind == PlacedKind.GradeCrossing)
        {
            CrossingMaterials();
            return;
        }

        var models = NetworkMeshes.ModelsOf(picked)
            .Select(m => (Model: m, Slots: LinesidePaints.All.Where(s => s.Model == m).ToList()))
            .Where(m => m.Slots.Count > 0)
            .ToList();
        if (models.Count == 0)
            return;

        Kit.BeginCard("Materials", "Every one built from the same model changes with it");
        foreach (var (model, slots) in models)
        {
            if (models.Count > 1)
                Kit.Caption(ModelLabel(model));
            foreach (var slot in slots)
                SlotRow(slot);
        }

        foreach (var asset in models.SelectMany(m => m.Slots).Select(s => s.Asset).Distinct().Where(a => a.Unsaved))
        {
            Kit.Separator();
            Kit.Note($"{asset.Name}: not saved", Caution);
            switch (Kit.Buttons("mat.save." + asset.Folder, ["Save", "Revert"]))
            {
                case 0:
                    try
                    {
                        LinesidePaints.Save(asset);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        Log.Warn($"Lineside paints: couldn't save {asset.Path}: {e.Message}");
                    }

                    break;
                case 1:
                    LinesidePaints.Revert(asset);
                    break;
            }
        }

        Kit.EndCard();
    }

    /// One slot: what it's drawn with, and a paint of its own's controls.
    private static void SlotRow(LinesidePaints.Slot slot)
    {
        ImGui.PushID($"{slot.Model}/{slot.Name}");
        var current = slot.Own is null ? slot.Link : OwnPaint;
        ImGui.AlignTextToFramePadding();
        ImGui.Text(slot.Name);
        ImGui.SameLine(S(90f));
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##link"u8, current == OwnPaint ? OwnPaint : "Shared: " + current, ImGuiComboFlags.None))
        {
            foreach (var shared in LinesidePaints.Shareable)
                if (ImGui.Selectable("Shared: " + shared, current == shared, ImGuiSelectableFlags.None, null))
                    LinesidePaints.Link(slot, shared);
            ImGui.Separator();
            if (ImGui.Selectable(OwnPaint, current == OwnPaint, ImGuiSelectableFlags.None, null) && slot.Own is null)
                LinesidePaints.SetOwn(slot, LinesidePaints.PaintOf(slot));
            ImGui.EndCombo();
        }

        ImGui.SetItemTooltip(slot.Own is null
            ? $"Drawn with the shared {slot.Link} (Material Editor): changing that changes every model using it"
            : "This model's own: a texture and colour of its own");

        if (slot.Own is { } own)
        {
            // Begun as the shared paint it's named for: "default" is that.
            var shared = TrackMaterials.Current.PaintOf(Array.FindIndex(TrackMaterials.Paints, p => p.Name == slot.Name));
            ImGui.Indent(S(8f));
            MaterialsWindow.Instance.PaintEditor("own", $"{slot.Name} ({ModelLabel(slot.Model)})", slot.Model, own, shared,
                false, paint => LinesidePaints.SetOwn(slot, paint));
            ImGui.Unindent(S(8f));
        }

        ImGui.PopID();
    }

    /// A grade crossing's own paints (Track.json), as the Material Editor has them.
    private static void CrossingMaterials()
    {
        Kit.BeginCard("Materials", "Every grade crossing; saved with the Material Editor");
        var materials = TrackMaterials.Current;
        for (var i = TrackMaterials.SharedPaints; i < TrackMaterials.Paints.Length; i++)
        {
            var index = i;
            var (name, fallback) = TrackMaterials.Paints[i];
            MaterialsWindow.Instance.PaintEditor(name, name, "A grade crossing's own paint", materials.PaintOf(i), fallback, false,
                paint => materials.SetPaint(index, paint));
        }

        if (TrackMaterials.Unsaved)
        {
            Kit.Note("Track materials: not saved", Caution);
            if (Kit.Button("mat.crossing.save", "Save"))
                materials.Save();
        }

        Kit.EndCard();
    }

    /// A model's file as a name: "Lineside/Signals/head_triple.glb" is "Head triple".
    private static string ModelLabel(string model)
    {
        var name = Path.GetFileNameWithoutExtension(model).Replace('_', ' ');
        return name.Length == 0 ? model : char.ToUpper(name[0], CultureInfo.InvariantCulture) + name[1..];
    }
}
