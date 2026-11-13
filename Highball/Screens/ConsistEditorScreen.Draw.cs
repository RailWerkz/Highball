using Brutal.ImGuiApi;
using Highball.App;
using Highball.Trains;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

public sealed partial class ConsistEditorScreen
{
    /// A press on a row or a catalog vehicle, which becomes a drag once the pointer moves far enough.
    private sealed class DragState
    {
        public int Row = -1;
        public VehicleDef? Vehicle;
        public float2 From;
        public bool Moving;
    }

    private DragState? _drag;
    private float2 _screen;
    private float2 _stageMin, _stageMax;
    private bool _reveal;
    private float _listScroll, _catalogScroll;
    private (float Top, float Bottom, float Left, float Right) _list;
    private (double At, string Key) _lastClick = (-10.0, "");

    private static float RowHeight => S(56f);
    private static float CardHeight => S(92f);
    private static float ChipHeight => S(18f);
    private static float ChipRowStep => S(22f);

    /// Where each of a vehicle's paint chips goes on its card: in rows from the card's left, a new row whenever the next won't
    /// fit before the ADD button, so a vehicle with many paints gets a taller card rather than losing some.
    private static List<(Livery Livery, float2 Min, float2 Max)> ChipLayout(VehicleDef def, float2 cardMin, float cardRight)
    {
        var chips = new List<(Livery, float2, float2)>();
        var left = cardMin.X + S(14f);
        var limit = cardRight - S(60f);
        var x = left;
        var y = cardMin.Y + S(66f);
        foreach (var livery in def.Liveries)
        {
            var w = Canvas.SpacedWidth(Medium, S(10f), livery.Name.ToUpperInvariant(), 0.12f) + S(14f);
            if (x > left && x + w > limit)
            {
                x = left;
                y += ChipRowStep;
            }

            chips.Add((livery, new float2(x, y), new float2(x + w, y + ChipHeight)));
            x += w + S(6f);
        }

        return chips;
    }

    /// The card's height: one row of chips fits the usual height; each further row adds one.
    private static float CardHeightFor(VehicleDef def, float left, float right)
    {
        var chips = ChipLayout(def, new float2(left, 0f), right);
        return chips.Count == 0 ? CardHeight : MathF.Max(CardHeight, chips[^1].Max.Y + (CardHeight - S(66f) - ChipHeight));
    }

    private bool DoubleClick(string key)
    {
        var now = Tween.Now;
        var twice = _lastClick.Key == key && now - _lastClick.At < 0.35;
        _lastClick = twice ? (-10.0, "") : (now, key);
        return twice;
    }

    private static bool Inside(float2 p, float2 min, float2 max) => p.X >= min.X && p.X < max.X && p.Y >= min.Y && p.Y < max.Y;

    public override void Draw(ImDrawListPtr draw, float2 size)
    {
        _screen = size;
        var margin = S(40f);
        var top = S(112f);
        var bottom = size.Y - S(76f);
        var leftWidth = MathF.Min(S(400f), size.X * 0.27f);
        var rightWidth = MathF.Min(S(500f), size.X * 0.33f);
        var leftMin = new float2(margin, top);
        var leftMax = new float2(margin + leftWidth, bottom);
        var rightMin = new float2(size.X - margin - rightWidth, top);
        var rightMax = new float2(size.X - margin, bottom);
        _stageMin = new float2(leftMax.X, top);
        _stageMax = new float2(rightMin.X, bottom - CaptionHeight(rightMin.X - leftMax.X - S(64f)) - S(12f));

        // A shade behind the title and the hints, so they read over a bright sky.
        Canvas.VGradient(draw, new float2(0f, 0f), new float2(size.X, S(150f)), Style.Background.WithAlpha(0.75f), Style.Background.WithAlpha(0f));
        Canvas.VGradient(draw, new float2(0f, size.Y - S(90f)), size, Style.Background.WithAlpha(0f), Style.Background.WithAlpha(0.8f));

        Canvas.Text(draw, Heavy, S(34f), new float2(margin, S(38f)), Style.Text, "CONSIST EDITOR");
        var state = _dirty ? "UNSAVED CHANGES" : _consist.SavedAt is null ? "NOT SAVED YET" : "SAVED";
        var stateColor = _dirty ? Caution : _consist.SavedAt is null ? TextFaint : Style.Clear;
        Canvas.Lamp(draw, new float2(margin + S(4f), S(92f)), S(3f), stateColor);
        Canvas.Spaced(draw, Bold, S(11f), new float2(margin + S(16f), S(85f)), Rgba.Mix(Style.Text, stateColor, 0.5f), state, 0.16f);
        if (_consist.Missing is > 0 and var missing)
        {
            var at = margin + S(16f) + Canvas.SpacedWidth(Bold, S(11f), state, 0.16f) + S(28f);
            Canvas.Lamp(draw, new float2(at, S(92f)), S(3f), Stop);
            Canvas.Spaced(draw, Bold, S(11f), new float2(at + S(12f), S(85f)), Stop, missing == 1 ? "1 VEHICLE NOT IN THE CATALOG" : $"{missing} VEHICLES NOT IN THE CATALOG", 0.16f);
        }

        var mouse = ImGui.GetIO().MousePos;
        var pressed = Keyboard.Clicked();

        DrawCatalog(draw, leftMin, leftMax, mouse, pressed);
        DrawConsist(draw, rightMin, rightMax, mouse, pressed);
        DrawCaption(draw, new float2(leftMax.X + S(32f), top), new float2(rightMin.X - S(32f), bottom));
        FinishDrag(draw, mouse);

        Canvas.Hints(draw, new float2(margin, size.Y - S(30f)),
            ("DRAG", "Orbit / reorder"), ("WASD", "Move view"), ("Q E", "Down, up"), ("ENTER", "Add"), ("DEL", "Remove"), ("CTRL+↑↓", "Move"), ("F", "Flip"),
            ("CTRL+Z", "Undo"), ("CTRL+S", "Save"), ("ESC", "Back"));
        DrawToast(draw, size);
    }

    // --- The catalog: kinds, then the vehicles of the one picked. ---

    private void DrawCatalog(ImDrawListPtr draw, float2 min, float2 max, float2 mouse, bool pressed)
    {
        var content = Canvas.Plate(draw, min, max, 1f, "CATALOG");
        var left = min.X + S(12f);
        var right = max.X - S(12f);
        var y = content.Y - S(10f);
        var navRow = S(30f);

        void KindRow(VehicleKind kind, float indent)
        {
            var rowMin = new float2(left, y);
            var rowMax = new float2(right, y + navRow);
            var hovered = !Keyboard.PointerBlocked && _drag is null && Inside(mouse, rowMin, rowMax);
            var picked = kind == _kind;
            var count = VehicleCatalog.OfKind(kind).Count();
            var lit = Tween.Float($"consist.kind.{kind}", "lit", picked ? 1f : hovered ? 0.5f : 0f, 0.15f);
            if (lit > 0.01f)
                draw.AddRectFilled(rowMin, rowMax, Accent.WithAlpha(0.14f * lit));
            if (picked)
                draw.AddRectFilled(rowMin, new float2(rowMin.X + S(3f), rowMax.Y), Accent);

            var color = picked ? Style.Text : count > 0 ? TextDim : TextFaint;
            Canvas.Text(draw, picked ? Bold : Medium, S(15f), new float2(left + S(14f) + indent, y + S(6f)), color, kind.Plural());
            Canvas.Text(draw, Mono, S(13f), new float2(right - S(10f), y + S(7f)), count > 0 ? TextDim : TextFaint, count.ToString(), Align.Right);

            if (hovered)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (pressed && !picked)
                {
                    _kind = kind;
                    _catalogScroll = 0f;
                    if (VehicleCatalog.OfKind(kind).FirstOrDefault() is { } first)
                        Pick(first);
                    Sfx.Select();
                }
            }

            y += navRow;
        }

        KindRow(VehicleKind.Locomotive, 0f);
        y += S(10f);
        Canvas.Spaced(draw, Medium, S(10f), new float2(left + S(14f), y), TextFaint, "ROLLING STOCK", 0.22f);
        y += S(20f);
        foreach (var kind in VehicleKinds.All.Where(k => k.IsRollingStock()))
            KindRow(kind, S(10f));

        y += S(12f);
        draw.AddLine(new float2(min.X, y), new float2(max.X, y), StrokeStrong, 1f);
        draw.AddLine(new float2(min.X, y + S(3f)), new float2(max.X, y + S(3f)), Stroke, 1f);
        y += S(14f);
        Canvas.Spaced(draw, Bold, S(11f), new float2(left + S(12f), y), Style.Text, _kind.Plural().ToUpperInvariant(), 0.2f);
        y += S(24f);

        // The vehicles, scrolling if there are more than fit.
        var listTop = y;
        var listBottom = max.Y - S(20f);
        var vehicles = VehicleCatalog.OfKind(_kind).ToList();
        if (vehicles.Count == 0)
        {
            foreach (var line in Canvas.Wrap($"No {_kind.Plural().ToLowerInvariant()}.", Regular, S(14f), right - left - S(24f)))
            {
                Canvas.Text(draw, Regular, S(14f), new float2(left + S(12f), y), TextFaint, line);
                y += S(20f);
            }

            return;
        }

        var heights = vehicles.Select(v => CardHeightFor(v, left, right)).ToList();
        var total = heights.Sum(h => h + S(8f));
        var view = listBottom - listTop;
        if (Inside(mouse, new float2(min.X, listTop), new float2(max.X, listBottom)) && !Keyboard.PointerBlocked)
            _catalogScroll -= ImGui.GetIO().MouseWheel * CardHeight * 0.6f;
        _catalogScroll = Math.Clamp(_catalogScroll, 0f, MathF.Max(0f, total - view));

        draw.PushClipRect(new float2(min.X, listTop), new float2(max.X, listBottom), true);
        y = listTop - _catalogScroll;
        for (var i = 0; i < vehicles.Count; i++)
        {
            DrawVehicleCard(draw, vehicles[i], new float2(left, y), new float2(right, y + heights[i]), mouse, pressed, listTop, listBottom);
            y += heights[i] + S(8f);
        }

        draw.PopClipRect();
    }

    private void DrawVehicleCard(ImDrawListPtr draw, VehicleDef def, float2 min, float2 max, float2 mouse, bool pressed, float clipTop, float clipBottom)
    {
        var visible = mouse.Y >= clipTop && mouse.Y < clipBottom;
        var hovered = visible && !Keyboard.PointerBlocked && (_drag is null || !_drag.Moving) && Inside(mouse, min, max);
        var picked = _vehicle == def && !_previewRow;
        var id = $"consist.card.{def.Id}";
        var lit = Tween.Float(id, "lit", picked ? 1f : hovered ? 0.6f : 0f, 0.15f);
        draw.AddRectFilled(min, max, Rgba.Lerp(Raised.WithAlpha(0.55f), Accent.WithAlpha(0.16f), lit));
        draw.AddRect(min, max, Rgba.Mix(Stroke, Accent, picked ? 1f : lit * 0.5f), 0f, ImDrawFlags.None, 1f);

        var x = min.X + S(14f);
        Pictogram(draw, def.Kind, new float2(x, min.Y + S(14f)), new float2(x + S(54f), min.Y + S(34f)), picked ? Accent : TextDim, false);
        var textX = x + S(68f);
        Canvas.Text(draw, Bold, S(16f), new float2(textX, min.Y + S(10f)), Style.Text, Ellipsize(def.Name, Bold, S(16f), max.X - textX - S(60f)));
        Canvas.Text(draw, Regular, S(12f), new float2(textX, min.Y + S(31f)), TextDim, Ellipsize(Join(def.Type, def.Builder, def.Built), Regular, S(12f), max.X - textX - S(60f)));

        var specs = def.Power > 0.0
            ? $"{UnitText.Length(def.Length)}  {UnitText.Mass(def.MassEmpty)}  {UnitText.Power(def.Power)}"
            : $"{UnitText.Length(def.Length)}  {UnitText.Mass(def.MassEmpty)} empty";
        Canvas.Text(draw, Mono, S(12f), new float2(x, min.Y + S(48f)), TextDim, Ellipsize(specs, Mono, S(12f), max.X - x - S(60f)));

        // The paint schemes, as chips in as many rows as they take; the one chosen is what ADD puts in.
        var chosen = LiveryFor(def);
        var chipClicked = false;
        foreach (var (livery, chipMin, chipMax) in ChipLayout(def, min, max.X))
        {
            var label = livery.Name.ToUpperInvariant();
            var on = livery.Id == chosen;
            var chipHover = hovered && Inside(mouse, chipMin, chipMax);
            draw.AddRectFilled(chipMin, chipMax, on ? Accent.WithAlpha(0.85f) : chipHover ? RaisedHover : Surface);
            draw.AddRect(chipMin, chipMax, on ? Accent : StrokeStrong, 0f, ImDrawFlags.None, 1f);
            Canvas.Spaced(draw, Medium, S(10f), chipMin + new float2(S(7f), S(3f)), on ? OnAccent : TextDim, label, 0.12f);
            if (chipHover && pressed)
            {
                _liveryOf[def.Id] = livery.Id;
                Pick(def);
                chipClicked = true;
                Sfx.Tick();
            }
        }

        // ADD, on the right: after the selected row, or at the end.
        var addMin = new float2(max.X - S(50f), min.Y + S(10f));
        var addMax = new float2(max.X - S(10f), max.Y - S(10f));
        var addHover = hovered && Inside(mouse, addMin, addMax);
        draw.AddRectFilled(addMin, addMax, addHover ? Accent : Accent.WithAlpha(0.18f));
        draw.AddRect(addMin, addMax, Accent, 0f, ImDrawFlags.None, 1f);
        var plus = (addMin + addMax) * 0.5f;
        var arm = S(7f);
        var ink = addHover ? OnAccent : Accent;
        draw.AddRectFilled(new float2(plus.X - arm, plus.Y - S(1.5f)), new float2(plus.X + arm, plus.Y + S(1.5f)), ink);
        draw.AddRectFilled(new float2(plus.X - S(1.5f), plus.Y - arm), new float2(plus.X + S(1.5f), plus.Y + arm), ink);

        if (!hovered)
            return;

        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (!pressed || chipClicked)
            return;

        if (addHover)
        {
            Insert(def);
            return;
        }

        Pick(def);
        if (DoubleClick(id))
            Insert(def);
        else
            _drag = new DragState { Vehicle = def, From = mouse };
    }

    // --- The consist: name, totals, tools, then its vehicles from the front. ---

    private void DrawConsist(ImDrawListPtr draw, float2 min, float2 max, float2 mouse, bool pressed)
    {
        var content = Canvas.Plate(draw, min, max, 1f, "CONSIST");
        var left = min.X + S(16f);
        var right = max.X - S(16f);
        var width = right - left;
        var y = content.Y - S(8f);

        // The name, edited in place.
        var name = _consist.Name;
        var fieldMin = new float2(left, y);
        Widgets.TextField(draw, "consist.name", "Name", ref name, fieldMin, width, _editingName, 40);
        var fieldMax = fieldMin + new float2(width, S(48f));
        if (!Keyboard.PointerBlocked && Inside(mouse, fieldMin, fieldMax))
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.TextInput);
            if (pressed && !_editingName)
            {
                _editingName = true;
                Sfx.Select();
            }
        }
        else if (pressed && _editingName)
        {
            _editingName = false;
        }

        y = fieldMax.Y + S(14f);

        // What it adds up to.
        var c = _consist;
        (string Label, string Value)[] stats =
        [
            ("Locos / cars", $"{c.Locomotives} / {c.RollingStock}"),
            ("Length", UnitText.Length(c.Length)),
            ("Weight", UnitText.Mass(c.Mass)),
            ("Power", UnitText.Power(c.Power)),
            ("Power/weight", UnitText.PowerToWeight(c.Power, c.Mass)),
            ("Tractive effort", UnitText.Force(c.StartingTractiveEffort)),
        ];
        var column = width / 3f;
        for (var i = 0; i < stats.Length; i++)
        {
            var at = new float2(left + i % 3 * column, y + i / 3 * S(44f));
            Canvas.Spaced(draw, Medium, S(10f), at, TextFaint, stats[i].Label.ToUpperInvariant(), 0.14f);
            Canvas.Text(draw, Mono, S(16f), at + new float2(0f, S(15f)), Style.Text, Ellipsize(stats[i].Value, Mono, S(16f), column - S(8f)));
        }

        y += S(96f);

        // The tools, on the selected row.
        var has = _selected >= 0 && _selected < c.Cars.Count;
        var car = has ? c.Cars[_selected] : null;
        (string Label, bool Enabled, Action Act)[] tools =
        [
            ("Up", has && _selected > 0, () => Move(_selected, _selected - 1)),
            ("Down", has && _selected < c.Cars.Count - 1, () => Move(_selected, _selected + 1)),
            ("Flip", has, () => Flip(_selected)),
            ("Paint", car?.Def is { Liveries.Count: > 1 }, () => CycleLivery(_selected)),
            ("Load", car?.Def is { CanLoad: true }, () => ToggleLoad(_selected)),
            ("Copy", has, () => Duplicate(_selected)),
            ("Del", has, () => Delete(_selected)),
        ];
        var gap = S(4f);
        var toolWidth = (width - gap * (tools.Length - 1)) / tools.Length;
        for (var i = 0; i < tools.Length; i++)
        {
            var at = new float2(left + i * (toolWidth + gap), y);
            if (Widgets.Button(draw, $"consist.tool.{i}", tools[i].Label, at, new float2(toolWidth, S(34f)), ButtonKind.Secondary, tools[i].Enabled))
                tools[i].Act();
        }

        y += S(46f);

        // How full the selected car is loaded: dragged from empty to its rated load, one undo step a drag.
        DrawLoad(draw, has ? car : null, left, right, y);
        y += S(44f);
        if (car?.Def is { Cargoes.Count: > 0 } cargoDef)
        {
            DrawCargo(draw, car, cargoDef, left, right, y);
            y += S(44f);
        }

        // The list.
        var footer = S(58f);
        var listTop = y;
        var listBottom = max.Y - footer - S(10f);
        _list = (listTop, listBottom, left, right);
        draw.AddRectFilled(new float2(left, listTop), new float2(right, listBottom), Style.Background.WithAlpha(0.35f));
        draw.AddRect(new float2(left, listTop), new float2(right, listBottom), Stroke, 0f, ImDrawFlags.None, 1f);
        DrawRows(draw, mouse, pressed);

        // NEW, OPEN, SAVE, DELETE.
        var buttonY = max.Y - footer;
        var quarter = (width - S(24f)) / 4f;
        float2 Slot(int i) => new(left + i * (quarter + S(8f)), buttonY);
        var buttonSize = new float2(quarter, S(40f));
        if (Widgets.Button(draw, "consist.new", "New", Slot(0), buttonSize))
            New();
        if (Widgets.Button(draw, "consist.open", "Open", Slot(1), buttonSize))
        {
            _editingName = false;
            HighballApp.Instance.Push(new OpenConsistScreen(this));
        }

        if (Widgets.Button(draw, "consist.save", "Save", Slot(2), buttonSize, ButtonKind.Primary, _dirty || _consist.SavedAt is null))
            Save();

        // Only a consist that's on disk can be deleted; asks first.
        if (Widgets.Button(draw, "consist.delete", "Delete", Slot(3), buttonSize, ButtonKind.Secondary, _consist.SavedAt is not null))
        {
            _editingName = false;
            HighballApp.Instance.Push(new DeleteConsistScreen(this, _consist, fromList: false));
        }
    }

    private static string LoadText(double fill) => fill <= 0.0 ? "Empty" : fill >= 1.0 ? "Loaded" : $"{fill * 100.0:0}% loaded";

    private void DrawLoad(ImDrawListPtr draw, ConsistCar? car, float left, float right, float y)
    {
        var def = car?.Def;
        var loadable = def is { CanLoad: true };
        var height = S(34f);
        var midY = y + height * 0.5f;
        Canvas.Spaced(draw, Medium, S(10f), new float2(left, midY - S(7f)), TextFaint, "LOAD", 0.14f);

        var readoutWidth = S(150f);
        var sliderMin = new float2(left + S(52f), y);
        var sliderSize = new float2(right - readoutWidth - S(8f) - sliderMin.X, height);
        var fill = (float)(car?.Fill ?? 0.0);
        if (Widgets.Slider(draw, "consist.load", ref fill, sliderMin, sliderSize, loadable, out var started) && car is not null)
        {
            var index = _selected;
            // Snap to the ends, so empty and full are easy to hit.
            var value = fill < 0.02f ? 0.0 : fill > 0.98f ? 1.0 : Math.Round(fill, 2);
            if (started)
                Change(cars => cars[index] = cars[index].WithFill(value));
            else if (Math.Abs(value - car.Fill) > 1e-9)
            {
                _consist.Cars[index] = car.WithFill(value);
                _dirty = true;
            }

            _previewRow = true;
        }

        var readout = !loadable ? car is null ? "No car picked" : "Carries no load"
            : $"{(car!.Fill * 100.0):0}%  {UnitText.Mass(def!.Mass(car.Fill))}";
        Canvas.Text(draw, Mono, S(13f), new float2(right, midY - S(8f)), loadable ? Style.Text : TextFaint, readout, Align.Right);
    }

    /// What the selected car is seen carrying (a well car's stacks), picked once it carries anything; the first unless
    /// another is.
    private void DrawCargo(ImDrawListPtr draw, ConsistCar car, VehicleDef def, float left, float right, float y)
    {
        var height = S(34f);
        Canvas.Spaced(draw, Medium, S(10f), new float2(left, y + height * 0.5f - S(7f)), TextFaint, "CARGO", 0.14f);
        var loaded = car.Fill > 0.0;
        var current = car.CargoId;
        var x0 = left + S(52f);
        var gap = S(4f);
        var width = (right - x0 - gap * (def.Cargoes.Count - 1)) / def.Cargoes.Count;
        for (var i = 0; i < def.Cargoes.Count; i++)
        {
            var cargo = def.Cargoes[i];
            var kind = loaded && cargo.Id == current ? ButtonKind.Primary : ButtonKind.Secondary;
            if (Widgets.Button(draw, $"consist.cargo.{i}", cargo.Name, new float2(x0 + i * (width + gap), y), new float2(width, height), kind, loaded))
                SetCargo(_selected, cargo.Id);
        }
    }

    private void DrawRows(ImDrawListPtr draw, float2 mouse, bool pressed)
    {
        var (listTop, listBottom, left, right) = _list;
        var cars = _consist.Cars;
        var view = listBottom - listTop;
        var total = cars.Count * RowHeight;
        var overList = !Keyboard.PointerBlocked && Inside(mouse, new float2(left, listTop), new float2(right, listBottom));

        if (overList)
            _listScroll -= ImGui.GetIO().MouseWheel * RowHeight;
        if (_reveal && _selected >= 0)
        {
            _listScroll = Math.Clamp(_listScroll, (_selected + 1) * RowHeight - view, _selected * RowHeight);
            _reveal = false;
        }

        // Dragging near either end scrolls the list along.
        if (_drag is { Moving: true } && overList)
        {
            var edge = S(36f);
            if (mouse.Y < listTop + edge)
                _listScroll -= Tween.DeltaTime * 600f * Scale;
            else if (mouse.Y > listBottom - edge)
                _listScroll += Tween.DeltaTime * 600f * Scale;
        }

        _listScroll = Math.Clamp(_listScroll, 0f, MathF.Max(0f, total - view));
        var scroll = Tween.Spring("consist.list", "scroll", _listScroll, 420f, 40f);

        if (cars.Count == 0)
        {
            var y0 = listTop + S(24f);
            foreach (var line in Canvas.Wrap("Empty consist.", Regular, S(14f), right - left - S(40f)))
            {
                Canvas.Text(draw, Regular, S(14f), new float2(left + S(20f), y0), TextFaint, line);
                y0 += S(21f);
            }
        }

        draw.PushClipRect(new float2(left, listTop), new float2(right, listBottom), true);
        var dragging = _drag is { Moving: true, Row: >= 0 } ? _drag.Row : -1;
        for (var i = 0; i < cars.Count; i++)
        {
            var rowMin = new float2(left, listTop + i * RowHeight - scroll);
            var rowMax = new float2(right, rowMin.Y + RowHeight);
            if (rowMax.Y < listTop || rowMin.Y > listBottom)
                continue;

            var car = cars[i];
            var def = car.Def;
            var selected = i == _selected;
            var hovered = overList && _drag is null && Inside(mouse, rowMin, rowMax);
            var lit = Tween.Float($"consist.row.{i}", "lit", selected ? 1f : hovered ? 0.5f : 0f, 0.12f);
            var fade = i == dragging ? 0.35f : 1f;

            if (i % 2 == 1)
                draw.AddRectFilled(rowMin, rowMax, new Rgba(1f, 1f, 1f, 0.02f));
            if (lit > 0.01f)
                draw.AddRectFilled(rowMin, rowMax, Accent.WithAlpha(0.15f * lit));
            if (selected)
                draw.AddRectFilled(rowMin, new float2(rowMin.X + S(3f), rowMax.Y), Accent);
            draw.AddLine(new float2(rowMin.X, rowMax.Y - 0.5f), new float2(rowMax.X, rowMax.Y - 0.5f), Stroke, 1f);

            // Its place in the train, a side view (front to the left), the vehicle, then its length.
            var midY = rowMin.Y + RowHeight * 0.5f;
            Canvas.Text(draw, MonoBold, S(13f), new float2(rowMin.X + S(34f), midY - S(8f)), (selected ? Accent : TextFaint).Fade(fade), (i + 1).ToString(), Align.Right);
            var kind = def?.Kind ?? VehicleKind.Boxcar;
            Pictogram(draw, kind, new float2(rowMin.X + S(46f), midY - S(10f)), new float2(rowMin.X + S(96f), midY + S(8f)), (def is null ? Stop : selected ? Style.Text : TextDim).Fade(fade), car.Reversed);

            var textX = rowMin.X + S(110f);
            var lengthText = def is null ? "" : UnitText.Length(def.Length);
            var lengthWidth = Canvas.TextSize(Mono, S(12f), lengthText).X;
            var room = rowMax.X - S(14f) - lengthWidth - S(12f) - textX;
            Canvas.Text(draw, Bold, S(15f), new float2(textX, rowMin.Y + S(9f)), (def is null ? Stop : Style.Text).Fade(fade), Ellipsize(def?.Name ?? car.Vehicle, Bold, S(15f), room));
            var details = def is null
                ? "Not in the catalog"
                : Join(def.FindLivery(car.Livery)?.Name ?? "", car.Reversed ? "Reversed" : "", def.CanLoad ? LoadText(car.Fill) : "",
                    car.Fill > 0.0 && def.Cargoes.FirstOrDefault(k => k.Id == car.CargoId) is { } cargo ? cargo.Name : "");
            Canvas.Text(draw, Regular, S(12f), new float2(textX, rowMin.Y + S(30f)), TextDim.Fade(fade), Ellipsize(details, Regular, S(12f), room));
            Canvas.Text(draw, Mono, S(12f), new float2(rowMax.X - S(14f), midY - S(7f)), TextDim.Fade(fade), lengthText, Align.Right);

            if (!hovered)
                continue;

            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            if (pressed)
            {
                Select(i);
                Sfx.Hover();
                if (DoubleClick($"row.{i}"))
                    Flip(i);
                else
                    _drag = new DragState { Row = i, From = mouse };
            }
        }

        draw.PopClipRect();
    }

    /// Where a drop at the pointer would go: a gap between rows, 0 the very front.
    private int DropIndex(float2 mouse)
    {
        var (listTop, _, _, _) = _list;
        var scroll = Tween.Spring("consist.list", "scroll", _listScroll, 420f, 40f);
        var at = (mouse.Y - listTop + scroll) / RowHeight;
        return Math.Clamp((int)MathF.Round(at), 0, _consist.Cars.Count);
    }

    private void FinishDrag(ImDrawListPtr draw, float2 mouse)
    {
        if (_drag is not { } drag)
            return;

        if (!drag.Moving && Vector2Length(mouse - drag.From) > S(6f))
            drag.Moving = true;

        var (listTop, listBottom, left, right) = _list;
        var overList = mouse.X >= left - S(40f) && mouse.X <= right + S(40f) && mouse.Y >= listTop && mouse.Y <= listBottom;

        if (drag.Moving)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeAll);
            var label = drag.Vehicle?.Name ?? (drag.Row >= 0 && drag.Row < _consist.Cars.Count ? _consist.Cars[drag.Row].Def?.Name ?? _consist.Cars[drag.Row].Vehicle : "");
            var drop = DropIndex(mouse);
            var useful = overList && !(drag.Row >= 0 && (drop == drag.Row || drop == drag.Row + 1));

            if (useful)
            {
                var scroll = Tween.Spring("consist.list", "scroll", _listScroll, 420f, 40f);
                var lineY = Math.Clamp(listTop + drop * RowHeight - scroll, listTop + 1f, listBottom - 1f);
                draw.AddRectFilled(new float2(left, lineY - S(1.5f)), new float2(right, lineY + S(1.5f)), Accent);
                draw.AddTriangleFilled(new float2(left, lineY - S(6f)), new float2(left + S(8f), lineY), new float2(left, lineY + S(6f)), Accent);
            }

            // What's carried, by the pointer.
            var tagMin = mouse + new float2(S(16f), S(10f));
            var tagWidth = Canvas.TextSize(Bold, S(14f), label).X + S(24f);
            draw.AddRectFilled(tagMin, tagMin + new float2(tagWidth, S(30f)), Surface);
            draw.AddRect(tagMin, tagMin + new float2(tagWidth, S(30f)), useful ? Accent : StrokeStrong, 0f, ImDrawFlags.None, 1f);
            Canvas.Text(draw, Bold, S(14f), tagMin + new float2(S(12f), S(6f)), Style.Text, label);
        }

        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
            return;

        // Let go.
        _drag = null;
        if (!drag.Moving || !overList)
            return;

        var index = DropIndex(mouse);
        if (drag.Vehicle is { } vehicle)
            Insert(vehicle, index);
        else if (drag.Row >= 0)
            Move(drag.Row, index > drag.Row ? index - 1 : index);
    }

    private static float Vector2Length(float2 v) => MathF.Sqrt(v.X * v.X + v.Y * v.Y);

    // --- Under the vehicle shown: what it is. ---

    private static float CaptionWidth(float room) => MathF.Min(room, S(720f));

    /// Six specs to a row when there's room, else three.
    private static int SpecsPerRow(float width) => (width - S(52f)) / 6f >= S(112f) ? 6 : 3;

    private float CaptionHeight(float room)
    {
        if (room < S(320f))
            return 0f;
        var rows = SpecsPerRow(CaptionWidth(room)) == 6 ? 1 : 2;
        return S(92f) + rows * S(40f);
    }

    private void DrawCaption(ImDrawListPtr draw, float2 min, float2 max)
    {
        if (Shown() is not { } shown || max.X - min.X < S(320f))
            return;

        var def = shown.Def;
        var width = CaptionWidth(max.X - min.X);
        var centre = (min.X + max.X) * 0.5f;
        var plateMin = new float2(centre - width * 0.5f, max.Y - CaptionHeight(max.X - min.X));
        var plateMax = new float2(centre + width * 0.5f, max.Y);
        Canvas.Plate(draw, plateMin, plateMax, 0.92f);

        var x = plateMin.X + S(26f);
        var from = shown.Car is null ? "CATALOG" : $"CONSIST  #{_selected + 1}";
        Canvas.Spaced(draw, Medium, S(10f), new float2(x, plateMin.Y + S(16f)), Accent, from, 0.2f);
        Canvas.Text(draw, Heavy, S(24f), new float2(x, plateMin.Y + S(30f)), Style.Text, Ellipsize(def.Name, Heavy, S(24f), width - S(52f)));
        var sub = Join(shown.Livery?.Name ?? "", def.Type, def.Builder, def.Built, shown.Reversed ? "Reversed" : "");
        Canvas.Text(draw, Regular, S(13f), new float2(x, plateMin.Y + S(60f)), TextDim, Ellipsize(sub, Regular, S(13f), width - S(52f)));

        var mass = def.Mass(shown.Car?.Fill ?? 0.0);
        var specs = new List<(string, string)> { ("Length", UnitText.Length(def.Length)), ("Weight", UnitText.Mass(mass)), ("Axles", def.Axles.ToString()) };
        if (def.Power > 0.0)
        {
            specs.Add(("Power", UnitText.Power(def.Power)));
            specs.Add(("Tractive effort", UnitText.Force(def.StartingTractiveEffort)));
        }
        else if (def.CanLoad)
        {
            specs.Add(("Loaded", UnitText.Mass(def.MassLoaded)));
        }

        if (def.MaxSpeed > 0.0)
            specs.Add(("Top speed", UnitText.Speed(def.MaxSpeed)));

        var perRow = SpecsPerRow(width);
        var column = (width - S(52f)) / perRow;
        for (var i = 0; i < specs.Count; i++)
        {
            var at = new float2(x + i % perRow * column, plateMin.Y + S(88f) + i / perRow * S(40f));
            Canvas.Spaced(draw, Medium, S(9f), at, TextFaint, specs[i].Item1.ToUpperInvariant(), 0.14f);
            Canvas.Text(draw, Mono, S(14f), at + new float2(0f, S(14f)), Style.Text, Ellipsize(specs[i].Item2, Mono, S(14f), column - S(6f)));
        }

        if (ModelFor(ShownModel(shown)) is null)
            Canvas.Spaced(draw, Bold, S(12f), new float2(centre, (min.Y + plateMin.Y) * 0.5f), Stop, "MODEL MISSING", 0.2f, Align.Center);
    }

    private void DrawToast(ImDrawListPtr draw, float2 size)
    {
        var age = (float)(Tween.Now - _toastAt);
        const float life = 2.5f;
        if (age > life || _toast.Length == 0)
            return;

        var enter = Easing.Apply(Ease.OutCubic, age / 0.25f);
        var alpha = MathF.Min(enter, Math.Clamp((life - age) / 0.3f, 0f, 1f));
        var text = _toast.ToUpperInvariant();
        var w = Canvas.SpacedWidth(Medium, S(13f), text, 0.08f) + S(72f);
        var h = S(44f);
        var centre = (_stageMin.X + _stageMax.X) * 0.5f;
        var min = new float2(centre - w * 0.5f, S(40f) - (1f - enter) * S(16f));
        Canvas.Plate(draw, min, min + new float2(w, h), alpha);
        Canvas.Lamp(draw, new float2(min.X + S(24f), min.Y + h * 0.5f), S(3.5f), Accent, alpha);
        Canvas.Spaced(draw, Medium, S(13f), new float2(min.X + S(44f), min.Y + h * 0.5f - S(8f)), Style.Text.Fade(alpha), text, 0.08f);
    }

    // --- Small pieces. ---

    private static string Join(params string[] parts) => string.Join("  ·  ", parts.Where(p => p.Length > 0));

    /// The text, cut with an ellipsis to fit width.
    private static string Ellipsize(string text, ImFontPtr font, float size, float width)
    {
        if (width <= 0f)
            return "";
        if (Canvas.TextSize(font, size, text).X <= width)
            return text;
        for (var n = text.Length - 1; n > 0; n--)
        {
            var cut = text[..n].TrimEnd() + "…";
            if (Canvas.TextSize(font, size, cut).X <= width)
                return cut;
        }

        return "";
    }

    /// A vehicle's side, flat and square like the rest of the panel: its body by kind, its trucks under it. The front is
    /// on the left unless it's reversed.
    private static void Pictogram(ImDrawListPtr draw, VehicleKind kind, float2 min, float2 max, Rgba color, bool reversed)
    {
        var w = max.X - min.X;
        var h = max.Y - min.Y;
        float X(float u) => reversed ? max.X - u * w : min.X + u * w;
        float Y(float v) => min.Y + v * h;
        void Box(float u0, float v0, float u1, float v1, Rgba c)
        {
            var (a, b) = (X(u0), X(u1));
            draw.AddRectFilled(new float2(MathF.Min(a, b), Y(v0)), new float2(MathF.Max(a, b), Y(v1)), c);
        }

        var dim = color.WithAlpha(color.A * 0.45f);
        switch (kind)
        {
            case VehicleKind.Locomotive:
                Box(0.04f, 0.42f, 0.96f, 0.80f, color); // frame and long hood's base
                Box(0.24f, 0.18f, 0.94f, 0.80f, color); // long hood
                Box(0.04f, 0.42f, 0.10f, 0.80f, color); // short hood
                Box(0.10f, 0.02f, 0.24f, 0.80f, color); // cab
                Box(0.13f, 0.12f, 0.21f, 0.32f, dim); // its window
                break;
            case VehicleKind.Autorack:
                Box(0.02f, 0.00f, 0.98f, 0.80f, color);
                for (var u = 0.08f; u < 0.92f; u += 0.1f)
                    Box(u, 0.10f, u + 0.05f, 0.62f, dim);
                break;
            case VehicleKind.Boxcar:
            case VehicleKind.Reefer:
                Box(0.04f, 0.10f, 0.96f, 0.80f, color);
                Box(0.42f, 0.18f, 0.58f, 0.74f, dim);
                if (kind == VehicleKind.Reefer)
                    Box(0.08f, 0.18f, 0.16f, 0.30f, dim);
                break;
            case VehicleKind.CoveredHopper:
                Box(0.04f, 0.12f, 0.96f, 0.62f, color);
                Box(0.18f, 0.62f, 0.36f, 0.80f, color);
                Box(0.64f, 0.62f, 0.82f, 0.80f, color);
                break;
            case VehicleKind.OpenHopper:
                Box(0.06f, 0.24f, 0.94f, 0.60f, color);
                Box(0.20f, 0.60f, 0.40f, 0.80f, color);
                Box(0.60f, 0.60f, 0.80f, 0.80f, color);
                break;
            case VehicleKind.Gondola:
                Box(0.03f, 0.44f, 0.97f, 0.80f, color);
                break;
            case VehicleKind.Flatcar:
                Box(0.02f, 0.62f, 0.98f, 0.80f, color);
                break;
            case VehicleKind.TankCar:
                Box(0.06f, 0.20f, 0.94f, 0.68f, color);
                Box(0.46f, 0.08f, 0.54f, 0.20f, color);
                Box(0.10f, 0.68f, 0.90f, 0.80f, color);
                break;
            case VehicleKind.Intermodal:
                Box(0.02f, 0.66f, 0.98f, 0.80f, color);
                Box(0.10f, 0.14f, 0.90f, 0.66f, color);
                Box(0.49f, 0.14f, 0.51f, 0.66f, dim);
                break;
            case VehicleKind.Caboose:
                Box(0.10f, 0.28f, 0.90f, 0.80f, color);
                Box(0.38f, 0.04f, 0.62f, 0.28f, color);
                Box(0.02f, 0.62f, 0.10f, 0.80f, dim);
                Box(0.90f, 0.62f, 0.98f, 0.80f, dim);
                break;
        }

        // The trucks.
        Box(0.10f, 0.84f, 0.30f, 1.00f, color);
        Box(0.70f, 0.84f, 0.90f, 1.00f, color);
    }
}
