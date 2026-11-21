using Brutal.ImGuiApi;
using Brutal.Numerics;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The Build tab: making up a consist right here, as the Consist Editor does it. Vehicles from the catalog, by kind, go on the
/// end of the consist (or after the row picked); rows move, turn, change paint and load, or come out. It's saved to the same
/// library the Consist Editor uses, and put on the track from here; a single vehicle can be put down on its own.
public sealed partial class TrainPanel
{
    /// The consist being made up here, and whether it's changed since it was saved.
    private Consist _build = new() { Name = "New consist" };
    private bool _buildChanged;
    private int _buildRow = -1;
    private VehicleKind _catalogKind = VehicleKind.Locomotive;
    private int _catalogPick;

    /// The paint the catalog's picked vehicle goes on in, by livery id; its first when it's not been chosen.
    private readonly Dictionary<string, string> _catalogLivery = [];
    private ImInputString _buildName = new(48, "New consist"u8);
    private string _buildNameFor = "";

    /// What the next click on the track puts down; null for the consist picked on the Place tab.
    private Consist? _toPlace;

    private static readonly VehicleKind[] Kinds = Enum.GetValues<VehicleKind>();

    private void BuildTab()
    {
        // --- The consist being made up. ---
        if (_buildNameFor != _build.Name)
        {
            _buildName = new ImInputString(48, _build.Name);
            _buildNameFor = _build.Name;
        }

        Kit.BeginCard("Consist", _buildChanged ? "unsaved" : _build.SavedAt is not null ? "saved" : "new", _buildChanged ? Caution : TextDim);
        Kit.Caption("Name");
        ImGui.SetNextItemWidth(Kit.Width);
        if (ImGui.InputText("##buildname", _buildName, ImGuiInputTextFlags.None))
        {
            _build.Name = _buildName.ToString().Trim();
            _buildNameFor = _build.Name;
            _buildChanged = true;
        }

        // Start from a saved one, or afresh.
        Kit.Caption("Start from");
        ImGui.PushItemWidth(Kit.Width - S(88f));
        if (ImGui.BeginCombo("##buildfrom", "a saved consist..."))
        {
            foreach (var saved in _consists)
                if (ImGui.Selectable($"{saved.Name}  ({saved.Cars.Count})"))
                {
                    _build = saved.Clone();
                    _buildChanged = false;
                    _buildRow = -1;
                }
            ImGui.EndCombo();
        }

        ImGui.PopItemWidth();
        ImGui.SameLine();
        if (Kit.Button("build.new", "New", S(80f)))
        {
            _build = new Consist { Name = FreshName() };
            _buildChanged = false;
            _buildRow = -1;
        }

        var stats = _build.Cars.Count == 0 ? "empty"
            : $"{_build.Cars.Count} vehicles  ·  {UnitText.Length(_build.Length)}  ·  {UnitText.Mass(_build.Mass)}  ·  {_build.Locomotives} engines";
        Kit.Note(stats, TextFaint);
        Kit.EndCard();

        // --- Its vehicles, from the front. Nothing here moves as the list grows or a row's picked: the tools are always there
        // (dimmed with no row picked), and the list scrolls in a box of its own height under them. ---
        Kit.BeginCard("Vehicles, front to rear", $"{_build.Cars.Count}");
        var has = _buildRow >= 0 && _buildRow < _build.Cars.Count;
        var row = has ? _buildRow : -1;
        var car = has ? _build.Cars[row] : null;
        var canLoad = car?.Def is { CanLoad: true };
        var hit = Kit.Buttons("build.row1", ["Up", "Down", "Turn", "Paint"],
            [has && row > 0, has && row < _build.Cars.Count - 1, has, (car?.Def?.Liveries.Count ?? 0) > 1]);
        switch (hit)
        {
            case 0:
                Swap(row, row - 1);
                break;
            case 1:
                Swap(row, row + 1);
                break;
            case 2:
                Edit(row, c => c.Settled() with { Reversed = !c.Reversed });
                break;
            case 3:
                Edit(row, NextLivery);
                break;
        }

        var hit2 = Kit.Buttons("build.row2", [car is { Fill: > 0.0 } ? "Empty it" : "Load it", "Copy", "Remove"], [canLoad, has, has]);
        if (hit2 == 0)
            Edit(row, c => c.WithFill(c.Fill > 0.0 ? 0.0 : 1.0));
        else if (hit2 == 1)
        {
            _build.Cars.Insert(row + 1, car!);
            PickBuildRow(row + 1);
            _buildChanged = true;
        }
        else if (hit2 == 2)
        {
            _build.Cars.RemoveAt(row);
            PickBuildRow(Math.Min(row, _build.Cars.Count - 1));
            _buildChanged = true;
        }

        // What a car's seen carrying once loaded (a well car's stacks), stepped through; always there, dimmed when there's
        // nothing to pick.
        var cargoes = car?.Def?.Cargoes ?? [];
        var cargoAt = cargoes.Count > 0 ? Math.Max(0, cargoes.ToList().FindIndex(x => x.Id == car!.CargoId)) : -1;
        var cargoOn = cargoes.Count > 1;
        var cargoText = cargoes.Count == 0 ? "No cargo to pick" : cargoes[cargoAt].Name;
        // (The name in the middle steps on too.)
        var step = Kit.Buttons("build.cargo", ["<", cargoText, ">"], [cargoOn, cargoOn, cargoOn], [1f, 6f, 1f]);
        if (step >= 0 && cargoOn)
        {
            var next = cargoes[(cargoAt + (step == 0 ? cargoes.Count - 1 : 1)) % cargoes.Count].Id;
            Edit(row, c => c.Settled() with { Cargo = next });
        }

        Kit.Note(car?.Def?.FindLivery(car.Livery) is { } paint ? $"Paint: {paint.Name}" : has ? "Paint: -" : "Pick a vehicle to change it", TextFaint);

        ImGui.PushStyleColor(ImGuiCol.ChildBg, new float4(0f, 0f, 0f, 0f));
        if (ImGui.BeginChild("##buildcars", new float2(Kit.Width, BuildRows * S(31f) + S(2f)), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
        {
            var inner = ImGui.GetContentRegionAvail().X;
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0f, 0f));
            if (_build.Cars.Count == 0)
                Kit.Note("  No vehicles: add them from the catalog.");
            for (var i = 0; i < _build.Cars.Count; i++)
            {
                var c = _build.Cars[i];
                var def = c.Def;
                var name = def?.Name ?? $"{c.Vehicle} (missing)";
                var detail = (c.Reversed ? "turned · " : "") + (def is { CanLoad: true } ? c.Fill > 0.0 ? "loaded" : "empty" : def?.Kind == VehicleKind.Locomotive ? "loco" : "");
                if (Kit.Row($"buildcar{i}", $"{i + 1}", name, detail.ToUpperInvariant(), i == _buildRow, inner))
                    _buildRow = i == _buildRow ? -1 : i;
                if (i == _buildRow && _buildScrollTo)
                {
                    ImGui.SetScrollHereY(0.5f);
                    _buildScrollTo = false;
                }
            }

            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        ImGui.Dummy(new float2(1f, S(4f)));
        var save = Kit.Buttons("build.save", ["Save", Placing && _toPlace is not null ? "Click the track" : "Place on track"],
            [_build.Cars.Count > 0 && _build.Name.Length > 0, _build.Cars.Count > 0 && CanPlace]);
        if (save == 0)
            SaveBuild();
        else if (save == 1)
            StartPlacing(_build.Clone());
        Kit.EndCard();

        // --- The catalog: the same, its list in a box of its own height, the paint and add buttons always under it. ---
        Kit.BeginCard("Catalog");
        var kindNames = Kinds.Select(k => k == VehicleKind.Locomotive ? "Locos" : k.Plural()).ToArray();
        var kind = ToolPanel.Mode("build.kind", kindNames, Array.IndexOf(Kinds, _catalogKind), 4);
        if (kind >= 0 && Kinds[kind] != _catalogKind)
        {
            _catalogKind = Kinds[kind];
            _catalogPick = 0;
        }

        var vehicles = VehicleCatalog.OfKind(_catalogKind).ToList();
        ImGui.PushStyleColor(ImGuiCol.ChildBg, new float4(0f, 0f, 0f, 0f));
        if (ImGui.BeginChild("##catalog", new float2(Kit.Width, CatalogRows * S(31f) + S(2f)), ImGuiChildFlags.Borders, ImGuiWindowFlags.None))
        {
            var inner = ImGui.GetContentRegionAvail().X;
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new float2(0f, 0f));
            if (vehicles.Count == 0)
                Kit.Note("  None in the catalog.");
            for (var i = 0; i < vehicles.Count; i++)
                if (Kit.Row($"catalog{i}", $"{i + 1}", vehicles[i].Name, UnitText.Length(vehicles[i].Length), i == _catalogPick, inner))
                    _catalogPick = i;
            ImGui.PopStyleVar();
        }

        ImGui.EndChild();
        ImGui.PopStyleColor();

        var picked = _catalogPick >= 0 && _catalogPick < vehicles.Count ? vehicles[_catalogPick] : null;
        // Its paint: a button each (up to four across), dimmed when it comes in only the one.
        var livery = picked is null ? null : picked.FindLivery(_catalogLivery.GetValueOrDefault(picked.Id)) ?? picked.Liveries.FirstOrDefault();
        ImGui.Dummy(new float2(1f, S(4f)));
        Kit.Caption("Paint");
        var liveries = picked?.Liveries ?? [];
        var names = liveries.Count > 0 ? liveries.Select(l => l.Name).ToArray() : ["-"];
        var at = Math.Max(0, liveries.ToList().FindIndex(l => l.Id == livery?.Id));
        var chosen = ToolPanel.Mode("build.livery", names, at, Math.Clamp(names.Length, 1, 4), liveries.Count > 1);
        if (picked is not null && chosen >= 0 && chosen != at && chosen < liveries.Count)
        {
            livery = liveries[chosen];
            _catalogLivery[picked.Id] = livery.Id;
        }

        ImGui.Dummy(new float2(1f, S(4f)));
        var add = Kit.Buttons("build.add", ["Add to consist", "Place just this one"], [picked is not null, picked is not null && CanPlace]);
        if (add == 0 && picked is not null)
        {
            var into = _buildRow >= 0 ? _buildRow + 1 : _build.Cars.Count;
            _build.Cars.Insert(into, new ConsistCar(picked.Id, livery?.Id));
            PickBuildRow(into);
            _buildChanged = true;
        }
        else if (add == 1 && picked is not null)
            StartPlacing(new Consist { Name = picked.Name, Cars = [new ConsistCar(picked.Id, livery?.Id)] });

        Kit.EndCard();
    }

    /// How many rows the consist's and the catalog's lists show before they scroll.
    private const int BuildRows = 6;
    private const int CatalogRows = 4;

    /// The row picked, to be scrolled into view on the next draw.
    private bool _buildScrollTo;

    private void PickBuildRow(int row)
    {
        _buildRow = row;
        _buildScrollTo = row >= 0;
    }

    private bool CanPlace => _graph is { Edges.Count: > 0 };

    /// Waits for a click on the track to put this consist down.
    private void StartPlacing(Consist consist)
    {
        if (Placing && _toPlace is not null)
        {
            Placing = false;
            _toPlace = null;
            return;
        }

        _toPlace = consist;
        Placing = true;
    }

    private void Swap(int a, int b)
    {
        (_build.Cars[a], _build.Cars[b]) = (_build.Cars[b], _build.Cars[a]);
        PickBuildRow(b);
        _buildChanged = true;
    }

    private void Edit(int row, Func<ConsistCar, ConsistCar> change)
    {
        _build.Cars[row] = change(_build.Cars[row]);
        _buildChanged = true;
    }

    private static ConsistCar NextLivery(ConsistCar car)
    {
        if (car.Def is not { } def || def.Liveries.Count < 2)
            return car;
        var at = Math.Max(0, def.Liveries.ToList().FindIndex(l => l.Id == def.FindLivery(car.Livery)?.Id));
        return car.Settled() with { Livery = def.Liveries[(at + 1) % def.Liveries.Count].Id };
    }

    /// Saves the consist made up here to the library (as the Consist Editor would), and picks it on the Place tab.
    private void SaveBuild()
    {
        _build.Save();
        _buildChanged = false;
        _consists = Consist.All();
        _picked = Math.Max(0, _consists.FindIndex(c => c.Name == _build.Name));
        _notice = $"Saved {_build.Name}";
    }

    private string FreshName()
    {
        var taken = _consists.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = 1; ; n++)
            if (!taken.Contains($"Consist {n}"))
                return $"Consist {n}";
    }
}
