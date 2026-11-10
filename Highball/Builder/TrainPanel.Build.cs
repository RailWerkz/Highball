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

        // --- Its vehicles, from the front. ---
        Kit.BeginCard("Vehicles, front to rear", $"{_build.Cars.Count}");
        if (_build.Cars.Count == 0)
            Kit.Note("Add vehicles from the catalog below.");
        for (var i = 0; i < _build.Cars.Count; i++)
        {
            var car = _build.Cars[i];
            var def = car.Def;
            var name = def?.Name ?? $"{car.Vehicle} (missing)";
            var detail = (car.Reversed ? "turned · " : "") + (def is { CanLoad: true } ? car.Fill > 0.0 ? "loaded" : "empty" : def?.Kind == VehicleKind.Locomotive ? "loco" : "");
            if (ToolPanel.Item($"buildcar{i}", null, $"{i + 1}. {name}", detail.Length > 0 ? detail.ToUpperInvariant() : null, i == _buildRow,
                    swatch: def?.Kind == VehicleKind.Locomotive ? Caution : def is null ? Stop : TextFaint))
                _buildRow = i == _buildRow ? -1 : i;
        }

        if (_buildRow >= 0 && _buildRow < _build.Cars.Count)
        {
            ImGui.Dummy(new float2(1f, S(4f)));
            var row = _buildRow;
            var car = _build.Cars[row];
            var canLoad = car.Def is { CanLoad: true };
            var hit = Kit.Buttons("build.row1", ["Up", "Down", "Turn", "Paint"],
                [row > 0, row < _build.Cars.Count - 1, true, (car.Def?.Liveries.Count ?? 0) > 1]);
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

            var hit2 = Kit.Buttons("build.row2", [car.Fill > 0.0 ? "Empty it" : "Load it", "Copy", "Remove"], [canLoad, true, true]);
            if (hit2 == 0)
                Edit(row, c => c.WithFill(c.Fill > 0.0 ? 0.0 : 1.0));
            else if (hit2 == 1)
            {
                _build.Cars.Insert(row + 1, car);
                _buildRow = row + 1;
                _buildChanged = true;
            }
            else if (hit2 == 2)
            {
                _build.Cars.RemoveAt(row);
                _buildRow = Math.Min(row, _build.Cars.Count - 1);
                _buildChanged = true;
            }

            if (car.Def?.FindLivery(car.Livery) is { } livery)
                Kit.Note($"Paint: {livery.Name}", TextFaint);
        }

        ImGui.Dummy(new float2(1f, S(4f)));
        var save = Kit.Buttons("build.save", ["Save", Placing && _toPlace is not null ? "Click the track" : "Place on track"],
            [_build.Cars.Count > 0 && _build.Name.Length > 0, _build.Cars.Count > 0 && CanPlace]);
        if (save == 0)
            SaveBuild();
        else if (save == 1)
            StartPlacing(_build.Clone());
        Kit.EndCard();

        // --- The catalog. ---
        Kit.BeginCard("Catalog");
        var kindNames = Kinds.Select(k => k == VehicleKind.Locomotive ? "Locos" : System.Text.RegularExpressions.Regex.Replace(k.ToString(), "(?<=[a-z])(?=[A-Z])", " ")).ToArray();
        var kind = ToolPanel.Mode("build.kind", kindNames, Array.IndexOf(Kinds, _catalogKind), 4);
        if (kind >= 0 && Kinds[kind] != _catalogKind)
        {
            _catalogKind = Kinds[kind];
            _catalogPick = 0;
        }

        var vehicles = VehicleCatalog.OfKind(_catalogKind).ToList();
        if (vehicles.Count == 0)
            Kit.Note("None of these in the catalog.");
        for (var i = 0; i < vehicles.Count; i++)
        {
            var v = vehicles[i];
            if (ToolPanel.Item($"catalog{i}", null, v.Name, UnitText.Length(v.Length), i == _catalogPick))
                _catalogPick = i;
        }

        if (_catalogPick >= 0 && _catalogPick < vehicles.Count)
        {
            var picked = vehicles[_catalogPick];
            ImGui.Dummy(new float2(1f, S(4f)));
            var add = Kit.Buttons("build.add", ["Add to consist", "Place just this one"], [true, CanPlace]);
            if (add == 0)
            {
                var at = _buildRow >= 0 ? _buildRow + 1 : _build.Cars.Count;
                _build.Cars.Insert(at, new ConsistCar(picked.Id, picked.Liveries.FirstOrDefault()?.Id));
                _buildRow = at;
                _buildChanged = true;
            }
            else if (add == 1)
                StartPlacing(new Consist { Name = picked.Name, Cars = [new ConsistCar(picked.Id, picked.Liveries.FirstOrDefault()?.Id)] });
        }

        Kit.EndCard();
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
        _buildRow = b;
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
