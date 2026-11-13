using System.Globalization;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.VulkanApi;
using Highball.App;
using Highball.Routes;
using Highball.World;
using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.Core.Utils;
using RailWerkz.UI;
using RailWerkz.Vulkan;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The scripted dev operations (--shapeops and the other track ops), kept out of the editor proper.
public sealed partial class EditorView
{
    /// A scripted Shape tool step (--shapeops), for testing.
    private void ShapeOp(TerrainSource source, string op)
    {
        var parts = op.Split(',');
        double Number(int i) => double.Parse(parts[i], CultureInfo.InvariantCulture);
        (double X, double Z) At(int i)
        {
            var p = _route.Projection!.Forward(new GeoPoint(Number(i), Number(i + 1)));
            return (p.East, -p.North);
        }

        var lines = source.Lines!;
        (NetworkLine Line, List<int> Handles, int Point)? Nearest((double X, double Z) at, bool free)
        {
            (NetworkLine, List<int>, int)? found = null;
            var best = 90000.0;
            foreach (var line in lines.Lines.Where(l => l.Rail && !l.Disused && l.Alignment is not null))
            {
                if (at.X < line.MinX - 20 || at.X > line.MaxX + 20 || at.Z < line.MinZ - 20 || at.Z > line.MaxZ + 20)
                    continue;
                var handles = TrackShaping.Handles(line, lines.IsJunction, []);
                for (var p = 0; p < handles.Count; p++)
                {
                    var q = handles[p];
                    var d = (line.X[q] - at.X) * (line.X[q] - at.X) + (line.Z[q] - at.Z) * (line.Z[q] - at.Z);
                    if (d < best && (!free || (p > 0 && p < handles.Count - 1 && !line.Nodes.Any(n => n.Point == q && lines.IsJunction(n.Node)))))
                        (best, found) = (d, (line, handles, q));
                }
            }

            return found;
        }

        void Report(string what, NetworkLine line, TrackShaping.Result? result)
        {
            Log.Info(result is null ? $"Shape {what}: refused" : string.Create(CultureInfo.InvariantCulture, $"Shape {what}: line {line.Index}, tightest curve {result.Radius:0} m, {result.OffMap:0.00} m off the map, {result.Shape.Pieces.Count} pieces"));
            if (result is not null)
                CommitShape(source, line, result.Shape);
        }

        switch (parts[0])
        {
            case "clearyard" or "restoreyard":
            {
                if (_guides!.Areas.MinBy(a => a.Ways.Min(w => source.Network.Ways[w].Lat.Select((la, i) => Math.Abs(la - Number(1)) + Math.Abs(source.Network.Ways[w].Lon[i] - Number(2))).Min())) is { } area)
                {
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Yard {parts[0]}: {area.Ways.Count} ways, {area.Length:0} m, {area.Junctions} junctions, of {_guides.Areas.Count} yards"));
                    Committed(_tracks!.SetGuide(area.Ways, parts[0] == "clearyard"));
                }

                break;
            }

            case "switchcurve" or "switchslide":
            {
                // Through the handles: a press on one, a drag this far along its track, and letting go.
                var (sx, sz) = At(1);
                if (lines.Turnouts.Where(t => SwitchHandles.Editable(lines, t)).MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is not { } t)
                    break;
                _tool = Tool.Switches;
                _switch = t.Node;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (t.X, source.HeightAt(t.X, t.Z) + 300.0, t.Z + 40.0, -1.3f);
                var metres = (float)Number(3);
                var curve = parts[0] == "switchcurve";
                (double X, double Z)? grab = curve ? SwitchHandles.CurveHandle(lines, t) : (t.X, t.Z);
                if (grab is not { } g || ProjectGround(g.X, g.Z) is not { } press)
                {
                    Log.Warn($"Switch {t.Node}: no handle");
                    break;
                }

                var (tx, tz, _, _, _, _, _) = curve ? t.Reverse.At(metres) : metres >= 0f ? t.Normal.At(metres) : t.Toe.At(-metres);
                var target = ProjectGround(tx, tz) ?? press;
                var before = lines.SwitchOptions.GetValueOrDefault(t.Node);
                _handles.Update(lines, t, press, (g.X, g.Z), ProjectGround, S(1f), true, true);
                _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, true);
                var preview = _handles.Curve;
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0},{t.Z:0}: No. {t.Number}, curve ends {before?.Reach:0} m; drag {parts[0]} {metres:0} m: preview No. {preview?.Number}, radius {preview?.Radius:0}, fits {preview?.Fits}, slid {_handles.Slid:0}"));
                SwitchChange(lines, t, _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, false));
                break;
            }

            case "alongview":
            {
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                _tool = Tool.Switches;
                (_camera.X, _camera.Z, _camera.Pitch) = (ax, az, -0.08f);
                _camera.Y = source.HeightAt(ax, az) + Number(5);
                _camera.Yaw = MathF.Atan2((float)(bx - ax), (float)(az - bz));
                break;
            }

            case "switchshow":
            {
                var (sx, sz) = At(1);
                if (lines.Turnouts.MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is { } t)
                {
                    var options = lines.SwitchOptions.GetValueOrDefault(t.Node);
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0.0},{t.Z:0.0}: No. {t.Number}, curve ends {options?.Reach:0.0} m, by hand {options?.ReachByHand}, frog {t.FrogNormal:0.0} m, normal line {t.Normal.Line.Index}{(t.Normal.Line.Exact ? " laid" : "")}, reverse line {t.Reverse.Line.Index}{(t.Reverse.Line.Exact ? " laid" : "")}, {lines.Turnouts.Count} switches"));
                }

                break;
            }

            case "boxdrag":
            {
                // Through the tool itself: a press, a drag and letting go, at the screen places of these two ground points.
                var (bx, bz) = At(1);
                var (ex, ez) = At(3);
                if (ProjectGround(bx, bz) is not { } press || ProjectGround(ex, ez) is not { } release)
                {
                    Log.Warn("Box: not in view");
                    break;
                }

                _tool = Tool.Follow;
                BoxUpdate(press, true, true);
                BoxUpdate(release, false, true);
                BoxUpdate(release, false, false);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Box: {_area is not null}, {_areaLength.Built:0} m built, {_areaLength.Cleared:0} m cleared"));
                break;
            }

            case "boxclear" or "boxgenerate":
            {
                var before = source.Network.Ways.Count;
                BoxSet(parts[0] == "boxclear");
                Log.Info($"Box {parts[0]}: {before} ways before, {source.Network.Ways.Count} after");
                break;
            }

            case "parallelpen":
            {
                // parallelpen,TRACKS,SPACING,SIDE,lat,lon,lat,lon...: a path drawn with Parallel through those places, each on a
                // control point within 3 m if there is one, and Enter.
                _tool = Tool.Parallel;
                (_parallel.ParallelTracks, _parallel.Spacing, _parallel.Side) = ((int)Number(1), (float)Number(2), (int)Number(3));
                var drawn = new List<LayPoint>();
                for (var i = 4; i + 1 < parts.Length; i += 2)
                {
                    var (px, pz) = At(i);
                    drawn.Add(_tracks!.NodesNear(px, pz, 3.0).FirstOrDefault() is var near && near != 0
                        ? new LayPoint(_tracks.PositionOf(near).X, _tracks.PositionOf(near).Z, Node: near) : new LayPoint(px, pz));
                }

                var planned = _parallel.PenParallel(_tracks!, drawn, PanelLength, laying: true);
                var joined = planned.Ways.Count(w => w.Points[0].Node is not null || w.Points[^1].Node is not null);
                Log.Info($"Parallel pen: {planned.Summary}, problem: {planned.Problem ?? "none"}, {planned.Ways.Count} ways, {joined} joined, points {string.Join("/", planned.Ways.Select(w => w.Points.Count))}");
                if (planned.Problem is null)
                    Committed(_tracks!.LayWays(planned.Ways));
                break;
            }

            case "bridgetrack":
            {
                // bridgetrack,lat,lon,SIDE: the bridge there picked, and a track laid onto it on that side (1 right, -1 left).
                var (bx0, bz0) = At(1);
                var picked = _network?.BridgeAt(bx0, bz0, 30.0);
                var before = source.Network.Ways.Count;
                if (picked is not null)
                    LayTrackOnBridge(picked, (int)Number(3));
                Log.Info($"Bridge track: bridge {picked?.Key.ToString() ?? "none"}, {source.Network.Ways.Count - before} ways added, problem {_bridgeProblem ?? "none"}");
                break;
            }

            case "parallel" or "crossover" or "ladder":
            {
                // Through the tool itself: a click on the track, the pointer and a second click at the other place.
                _tool = parts[0] == "parallel" ? Tool.Parallel : Tool.Follow;
                var yard = ActiveYard;
                if (parts[0] != "parallel")
                    yard.SetMode(parts[0] == "crossover" ? YardMode.Crossover : YardMode.Ladder);
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                if (parts[0] == "parallel")
                    (yard.Spacing, yard.ParallelTracks) = ((float)Number(5), parts.Length > 6 ? (int)Number(6) : 1);
                else if (parts[0] == "crossover")
                    yard.Size = (int)Number(5);
                else
                    (yard.Tracks, yard.Spacing, yard.Size) = ((int)Number(5), (float)Number(6), (int)Number(7));
                var started = yard.Start(_tracks!, ax, az);
                yard.Update(_tracks!, lines, (bx, bz), PanelLength);
                var planned = yard.Plan;
                Log.Info($"Yard {parts[0]}: started {started}, {planned?.Summary}, problem: {planned?.Problem ?? "none"}, {planned?.Ways.Count} ways");
                YardPress(lines, bx, bz);
                if (parts[0] != "parallel")
                    yard.SetMode(YardMode.Follow);
                break;
            }

            case "place":
            {
                // Through the tool: the mode, and a click on the ground here.
                _tool = Tool.Place;
                _place.Mode = parts[1] switch { "signal" => PlaceMode.Signal, "handthrow" => PlaceMode.HandThrow, "crossing" => PlaceMode.GradeCrossing, _ => PlaceMode.Diamond };
                var (px, pz) = At(2);
                _place.Selected = null;
                _place.Update(lines, _tracks!, (px, pz), PanelLength);
                var note = _place.Note;
                PlacePress(source, lines, px, pz);
                var made = source.Objects.FirstOrDefault(o => o.Id == _place.Selected);
                Log.Info($"Place {parts[1]}: snapped {_place.Snapped is not null}, {note}, refused: {_switchRefused ?? "no"}, placed {made?.Kind}, {source.Objects.Count} objects");
                break;
            }

            case "pole":
            {
                // Through the tool: pole,kind,snap,offset,gap (0: no fence), then a lat,lon for each click.
                _tool = Tool.Place;
                _place.Mode = PlaceMode.Pole;
                _place.PoleKind = Enum.Parse<PlacedKind>(parts[1]);
                _place.Snap = Enum.Parse<PoleSnap>(parts[2]);
                if (_place.Snap == PoleSnap.Road)
                    _place.RoadOffset = (float)Number(3);
                else
                    _place.TrackOffset = (float)Number(3);
                _place.Drawn = Number(4) > 0.0 ? PoleDraw.Fence : PoleDraw.One;
                if (Number(4) > 0.0)
                    _place.Gap = (float)Number(4);
                (_place.RunFrom, _place.RunFirst) = (null, null);
                for (var i = 5; i + 1 < parts.Length; i += 2)
                {
                    var (px, pz) = At(i);
                    _switchRefused = null;
                    _place.Update(lines, _tracks!, (px, pz), PanelLength);
                    var note = _place.Note;
                    var before = source.Objects.Count;
                    PolePress(lines, px, pz);
                    Log.Info($"Pole {parts[1]} {parts[2]}: {note}, refused: {_switchRefused ?? "no"}, {source.Objects.Count - before} made, {source.Objects.Count} objects");
                }

                foreach (var made in source.Objects.Where(o => o.Kind.IsPole()).TakeLast(4))
                {
                    var (poleX, poleZ) = PlacedAtWorld(made);
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Pole {made.Kind} at {poleX:0.0} {poleZ:0.0} heading {made.Heading:0.0}"));
                }

                (_place.RunFrom, _place.RunFirst) = (null, null);
                break;
            }

            case "polepen":
            {
                // Through the tool: polepen,kind,gap, then a lat,lon,outx,outz for each point of the path, and it's laid.
                _tool = Tool.Place;
                _place.Mode = PlaceMode.Pole;
                _place.PoleKind = Enum.Parse<PlacedKind>(parts[1]);
                _place.Drawn = PoleDraw.Pen;
                _place.Gap = (float)Number(2);
                _place.Pen.Clear();
                for (var i = 3; i + 3 < parts.Length; i += 4)
                {
                    var (px, pz) = At(i);
                    var snapped = _place.PenSnap(lines, (px, pz))!.Value;
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Pole pen point {px:0.0} {pz:0.0} goes at {snapped.X:0.0} {snapped.Z:0.0}"));
                    _place.Pen.Add(snapped, Number(i + 2), Number(i + 3));
                }

                _switchRefused = null;
                var before = source.Objects.Count;
                PenLay();
                Log.Info($"Pole pen {parts[1]}: refused: {_switchRefused ?? "no"}, {source.Objects.Count - before} made, {source.Objects.Count} objects");
                foreach (var made in source.Objects.Where(o => o.Kind.IsPole()).TakeLast(3))
                {
                    var (poleX, poleZ) = PlacedAtWorld(made);
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Pole {made.Kind} at {poleX:0.0} {poleZ:0.0} heading {made.Heading:0.0}"));
                }

                break;
            }

            case "stamp":
            {
                // Through the tool: a piece from the palette, how it's turned and mirrored, and a click on the ground here.
                _tool = Tool.Place;
                _place.Mode = PlaceMode.Track;
                _place.Rebuild(Settings.Current.Units == UnitSystem.Imperial);
                _place.Stamp = _place.Catalogue.FirstOrDefault(c => c.Id == parts[1]);
                var (sx, sz) = At(2);
                (_place.Heading, _place.Mirror) = (parts.Length > 4 ? Number(4) * Math.PI / 180.0 : 0.0, parts.Length > 5 && parts[5] == "m");
                _place.Update(lines, _tracks!, (sx, sz), PanelLength);
                var plan = _place.Plan;
                Log.Info($"Stamp {parts[1]}: {plan?.Summary}, problem: {plan?.Problem ?? "none"}, {plan?.Ways.Count} ways, {plan?.Switches.Length} switches");
                LayStamp(lines, sx, sz);
                break;
            }

            case "wireframe":
                _renderer.Wireframe = parts[1] == "on";
                break;

            case "stampcheck":
            {
                // What's come of the pieces laid: every switch with its size, and every junction with a problem.
                var (cx, cz) = At(1);
                var within = Number(3);
                foreach (var t in lines.Turnouts.Where(t => Math.Abs(t.X - cx) < within && Math.Abs(t.Z - cz) < within))
                    Log.Info($"Stamp check: switch {t.Node} No. {t.Number?.ToString() ?? "none"}, modelled {t.Modelled}");
                foreach (var l in lines.Layouts.Where(l => Math.Abs(l.X - cx) < within && Math.Abs(l.Z - cz) < within))
                    Log.Info($"Stamp check: {TrackLayouts.Name(l.Kind, l.Number ?? 0)} at {_route.Projection!.Inverse(new RoutePoint(l.X, -l.Z)).Lat:0.0000}, issue: {l.Issue ?? "none"}");
                break;
            }

            case "placeturn" or "placeflip" or "placedelete" or "placemove":
            {
                if (source.Objects.LastOrDefault() is not { } last)
                    break;
                if (parts[0] == "placeturn")
                    PlaceTurn(last, Number(1));
                else if (parts[0] == "placeflip")
                    PlaceFlip(last);
                else if (parts[0] == "placedelete")
                    PlacedChange(last, null);
                else
                {
                    var (toX, toZ) = At(1);
                    PlaceMove(lines, last, toX, toZ);
                }

                Log.Info($"Place {parts[0]}: {source.Objects.Count} objects");
                break;
            }

            case "throw":
            {
                var (tx, tz) = At(1);
                _network!.Throw(tx, tz, 40.0);
                break;
            }

            case "laycurve":
            {
                var (fromX, fromZ) = At(1);
                var (endX, endZ) = At(3);
                var (midX, midZ) = ((fromX + endX) / 2.0, (fromZ + endZ) / 2.0);
                var (ux, uz) = (endX - fromX, endZ - fromZ);
                var length = Math.Sqrt(ux * ux + uz * uz);
                var bow = Number(5);
                var radius = (length * length / 4.0 + bow * bow) / (2.0 * Math.Abs(bow));
                var (cx, cz) = (midX - uz / length * (radius - Math.Abs(bow)) * Math.Sign(bow), midZ + ux / length * (radius - Math.Abs(bow)) * Math.Sign(bow));
                var (a0, a1) = (Math.Atan2(fromZ - cz, fromX - cx), Math.Atan2(endZ - cz, endX - cx));
                var sweep = Math.IEEERemainder(a1 - a0, 2.0 * Math.PI);
                var count = Math.Max(2, (int)(radius * Math.Abs(sweep) / 2.0));
                Committed(_tracks!.Lay([.. Enumerable.Range(0, count + 1).Select(k => new LayPoint(cx + radius * Math.Cos(a0 + sweep * k / count), cz + radius * Math.Sin(a0 + sweep * k / count)))], lead: false, exact: true));
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Yard curve: radius {radius:0} m"));
                break;
            }

            case "laystraight":
            {
                var (fromX, fromZ) = At(1);
                var (endX, endZ) = At(3);
                var count = Math.Max(2, (int)(Math.Sqrt((endX - fromX) * (endX - fromX) + (endZ - fromZ) * (endZ - fromZ)) / 2.0));
                Committed(_tracks!.Lay([.. Enumerable.Range(0, count + 1).Select(k => new LayPoint(fromX + (endX - fromX) * k / count, fromZ + (endZ - fromZ) * k / count))], lead: false, exact: true));
                break;
            }

            case "roadprofile":
            {
                // roadprofile,LAT,LON: the road nearest there, its ground and height every point 60 m either side.
                var (px, pz) = At(1);
                var road = lines.Lines.Where(l => !l.Rail && !l.Mark).MinBy(l => Enumerable.Range(0, l.Count).Min(i => (l.X[i] - px) * (l.X[i] - px) + (l.Z[i] - pz) * (l.Z[i] - pz)));
                if (road is null)
                    break;
                var at = Enumerable.Range(0, road.Count).MinBy(i => (road.X[i] - px) * (road.X[i] - px) + (road.Z[i] - pz) * (road.Z[i] - pz));
                Log.Info($"Road {road.Index}: class {(RoadClass)road.Class}, {road.Count} points, nodes at {string.Join(",", road.Nodes.Select(n => $"{n.Point}{(lines.IsJunction(n.Node) ? "J" : "")}"))}");
                for (var i = 0; i < road.Count; i++)
                    if (MathF.Abs(road.S[i] - road.S[at]) < 60f)
                        Log.Info(string.Create(CultureInfo.InvariantCulture, $"  {i} s {road.S[i]:0.0} x {road.X[i]:0.00} z {road.Z[i]:0.00} shift {(road.Shift.Length > i ? road.Shift[i] : 0f):0.00} ground {road.Ground[i]:0.00} y {road.Y[i]:0.00} {(i < road.Structures.Length ? road.Structures[i] : Structure.Ground)}"));
                break;
            }

            case "layercross":
            {
                // layercross,LAT,LON,RADIUS: road bridges crossing each other near there, each one's layer and deck height at the crossing.
                var (cx, cz) = At(1);
                var r = Number(3);
                var near = lines.Lines.Where(l => !l.Rail && !l.Mark && l.Structures.Contains(Structure.Bridge) && Enumerable.Range(0, l.Count).Any(i => (l.X[i] - cx) * (l.X[i] - cx) + (l.Z[i] - cz) * (l.Z[i] - cz) < r * r)).ToList();
                var (bad, total) = (0, 0);
                for (var a = 0; a < near.Count; a++)
                    for (var b = a + 1; b < near.Count; b++)
                        for (var i = 0; i + 1 < near[a].Count; i++)
                            for (var j = 0; j + 1 < near[b].Count; j++)
                            {
                                var (A, B) = (near[a], near[b]);
                                var (ux, uz, vx, vz) = (A.X[i + 1] - A.X[i], A.Z[i + 1] - A.Z[i], B.X[j + 1] - B.X[j], B.Z[j + 1] - B.Z[j]);
                                var cross = ux * vz - uz * vx;
                                if (Math.Abs(cross) < 1e-9)
                                    continue;
                                var (wx, wz) = (B.X[j] - A.X[i], B.Z[j] - A.Z[i]);
                                var (t, q) = ((wx * vz - wz * vx) / cross, (wx * uz - wz * ux) / cross);
                                if (t < 0 || t > 1 || q < 0 || q > 1 || A.Nodes.Any(n => B.Nodes.Any(m => m.Node == n.Node)))
                                    continue;
                                var (ya, yb) = (A.Y[i] + (A.Y[i + 1] - A.Y[i]) * (float)t, B.Y[j] + (B.Y[j + 1] - B.Y[j]) * (float)q);
                                var (la, lb) = (i < A.Layers.Length ? A.Layers[i] : -9, j < B.Layers.Length ? B.Layers[j] : -9);
                                total++;
                                if (MathF.Abs(ya - yb) < 6f)
                                {
                                    bad++;
                                    var g = _route.Projection!.Inverse(new RoutePoint(A.X[i] + ux * t, -(A.Z[i] + uz * t)));
                                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"  clash at {g.Lat:0.000000},{g.Lon:0.000000}: road {A.Index} layer {la} y {ya:0.0} {A.Structures[i]} vs road {B.Index} layer {lb} y {yb:0.0} {B.Structures[j]}"));
                                }
                            }

                Log.Info($"Layer crossings: {total} crossings between {near.Count} decks, {bad} under 6 m apart");
                break;
            }

            case "bridgegroup":
            {
                // bridgegroup,LAT,LON: the bridge nearest there: its reference, members (offset, half) and reach across.
                var (cx, cz) = At(1);
                if (_network?.BridgeAt(cx, cz, 30.0) is { } g)
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Bridge group {g.Key}: reference {g.Reference.Index}, left {g.Left:0.0} right {g.Right:0.0}, members {string.Join("; ", g.Members.Select(m => $"{m.Line.Index} off {m.Offset:0.0} half {m.Half:0.0}"))}, covered {string.Join(",", (g.Covered ?? []).Select(c => $"{c.From:0.0}..{c.To:0.0}"))}"));
                break;
            }

            case "bridgeends":
            {
                // bridgeends,LAT,LON,RADIUS: each bridge end near there: its line, kind, run, floor, what it stands beside.
                var (cx, cz) = At(1);
                var radius = Number(3);
                foreach (var l in lines.Lines.Where(l => l.Structures.Contains(Structure.Bridge)))
                    foreach (var e in lines.BridgeEndsOf(l))
                        if (Math.Pow(l.X[e.Point] - cx, 2) + Math.Pow(l.Z[e.Point] - cz, 2) < radius * radius)
                        {
                            var g = _route.Projection!.Inverse(new RoutePoint(l.X[e.Point], -l.Z[e.Point]));
                            Log.Info(string.Create(CultureInfo.InvariantCulture, $"Bridge end: line {l.Index} point {e.Point} ({(e.Start ? "start" : "end")}) at {g.Lat:0.000000},{g.Lon:0.000000}: {e.Kind}, run {e.Run:0.0} m, floor {e.Floor:0.0}, deck {l.Y[e.Point]:0.0}, wing {e.Wing:0.0}, beside {(e.Under is { } u ? u.Index.ToString() : "-")} at {e.UnderS:0} side {e.Side}"));
                        }

                break;
            }

            case "roadaudit":
            {
                // roadaudit: every road measured (NetworkLines.AuditRoads), the counts and the worst places logged.
                var report = lines.AuditRoads();
                Log.Info($"Road audit: {string.Join("; ", report.Counts.OrderBy(c => c.Key).Select(c => $"{c.Key}: {c.Value}"))}");
                foreach (var f in report.Worst)
                {
                    var g = _route.Projection!.Inverse(new RoutePoint(f.X, -f.Z));
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"  {f.Kind}: {f.Value:0.00} at {g.Lat:0.000000},{g.Lon:0.000000} (road {f.Line})"));
                }

                break;
            }

            case "roadsnear":
            {
                // roadsnear,LAT,LON,RADIUS: the road lines passing within RADIUS metres, logged: how wide, which way, where they end.
                var (cx, cz) = At(1);
                var radius = Number(3);
                foreach (var l in lines.Lines.Where(l => !l.Rail && !l.Mark && Enumerable.Range(0, l.Count).Any(p => (l.X[p] - cx) * (l.X[p] - cx) + (l.Z[p] - cz) * (l.Z[p] - cz) < radius * radius)))
                {
                    string End(int p)
                    {
                        var g = _route.Projection!.Inverse(new RoutePoint(l.X[p], -l.Z[p]));
                        var node = l.Nodes.FirstOrDefault(n => n.Point == p).Node;
                        var others = lines.Lines.Count(o => o != l && o.Nodes.Any(n => n.Node == node));
                        var on = lines.Continuing(l, p) is { } c ? string.Create(CultureInfo.InvariantCulture, $"carries on into {c.Line.Index}, {Math.Sqrt(Math.Pow(c.Line.X[c.Point] - l.X[p], 2) + Math.Pow(c.Line.Z[c.Point] - l.Z[p], 2)):0.00} m apart") : "carries on into nothing";
                        return string.Create(CultureInfo.InvariantCulture, $"{g.Lat:0.000000},{g.Lon:0.000000} node {node} ({others} others, {on})");
                    }

                    string NodeUsers(long node) => "(node: " + string.Join(",", lines.Lines.Where(o => o.Nodes.Any(n => n.Node == node)).Select(o =>
                    {
                        var at = o.Nodes.First(n => n.Node == node).Point;
                        return $"{(o.Rail ? "rail" : o.Mark ? "mark" : "road")}{o.Index}{(at == 0 || at == o.Count - 1 ? " end" : " through")}";
                    })) + ")";
                    var (wobble, worst) = (0.0, 0);
                    var wobbles = new List<string>();
                    for (var p = 1; p + 1 < l.Count; p++)
                    {
                        var w = Math.Abs((l.X[p] - l.X[p - 1]) * (l.Z[p + 1] - l.Z[p - 1]) - (l.Z[p] - l.Z[p - 1]) * (l.X[p + 1] - l.X[p - 1])) / Math.Max(Math.Sqrt(Math.Pow(l.X[p + 1] - l.X[p - 1], 2) + Math.Pow(l.Z[p + 1] - l.Z[p - 1], 2)), 1e-6);
                        if (w > 0.05)
                            wobbles.Add(string.Create(CultureInfo.InvariantCulture, $"{p}@{l.S[p]:0}m:{w * 100:0}cm{(l.Structures[p] != l.Structures[p - 1] ? "(structure)" : "")}{(l.Nodes.Any(n => n.Point == p) ? NodeUsers(l.Nodes.First(n => n.Point == p).Node) : "")}"));
                        if (w > wobble)
                            (wobble, worst) = (w, p);
                    }

                    if (wobbles.Count > 0)
                        Log.Info($"Road {l.Index} wobbles: {string.Join(" ", wobbles.Take(30))}");

                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Road {l.Index}: wobble {wobble * 100:0.0} cm, class {(RoadClass)l.Class}, {l.Lanes} lanes, {l.Width:0.0} m wide{(l.Oneway ? ", one way" : "")}, {l.S[^1]:0} m, {l.Count} points, {l.Structures.Count(s => s == Structure.Bridge)} bridge segments; from {End(0)} to {End(l.Count - 1)}"));
                }

                break;
            }

            case "linesdump":
            {
                // Every built track's line, with its junctions, for working out a hand re-lay.
                using var dump = new StreamWriter(parts[1]);
                foreach (var l in lines.Lines.Where(l => l.Rail && !l.Disused && l.Alignment is not null))
                {
                    var handles = TrackShaping.Handles(l, lines.IsJunction, []);
                    var junctions = l.Nodes.Where(n => lines.IsJunction(n.Node)).Select(n => n.Point).ToHashSet();
                    dump.WriteLine(string.Create(CultureInfo.InvariantCulture, $"L {l.Index} {l.Count} {l.S[^1]:0.0} {(l.Exact ? 1 : 0)} {string.Join(';', handles)} {string.Join(';', junctions)}"));
                    for (var p = 0; p < l.Count; p++)
                    {
                        var g = _route.Projection!.Inverse(new RoutePoint(l.X[p], -l.Z[p]));
                        dump.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{l.X[p]:0.000} {l.Z[p]:0.000} {l.S[p]:0.00} {g.Lat:0.00000000} {g.Lon:0.00000000}"));
                    }
                }

                Log.Info("Lines dumped");
                break;
            }

            case "road":
            {
                // road,KIND,MODE,lat,lon,lat,lon[,lat,lon...]: each point a click.
                _tool = Tool.Road;
                _road.Clear();
                (_road.Kind, _road.Mode) = (int.Parse(parts[1], CultureInfo.InvariantCulture), Enum.Parse<RoadMode>(parts[2], true));
                for (var i = 3; i + 1 < parts.Length; i += 2)
                {
                    var (rx, rz) = At(i);
                    if (_road.Click(Roads.Snap(rx, rz, 5.0)) is { } laid)
                        Committed(Roads.Lay(laid, RoadTool.Kinds[_road.Kind]));
                }

                Log.Info($"Road: laid, {source.Network.Ways.Count} ways");
                break;
            }

            case "roadoff":
            {
                var (rx, rz) = At(1);
                var picked = Roads.Nearest(rx, rz, 40.0, RoadEditor.IsRoad);
                Log.Info($"Road off: {picked?.Way}");
                if (picked is { } off)
                    Committed(Roads.Remove(off.Way));
                break;
            }

            case "bridge":
            {
                var (ax, az) = At(1);
                var (bx, bz) = At(3);
                bool Any(NetworkWay w) => !w.Tags.ContainsKey(RouteNetwork.RemovedTag);
                if (Roads.Nearest(ax, az, 40.0, Any) is { } first && Roads.Nearest(bx, bz, 40.0, Any) is { } second)
                {
                    var made = Roads.AddBridge(first, second, out var refused);
                    Log.Info($"Bridge: {(made is null ? refused ?? "nothing" : $"{made.Ways.Count} ways changed")}");
                    Committed(made);
                }

                break;
            }

            case "flattensample":
            {
                // flattensample,lat,lon: as F over that place with Flatten in hand.
                var (fx0, fz0) = At(1);
                SampleFlatten(source, fx0, fz0);
                Log.Info($"Flatten sample: {FlattenSampleText}");
                break;
            }

            case "lens":
                // lens,ZOOM: as rolling the wheel to that zoom in the 3D Editor.
                _camera.Zoom = Math.Clamp((float)Number(1), 1f, MaxLensZoom);
                break;

            case "splitat":
            {
                // splitat,lat,lon: the control point nearest there selected, and U.
                var (sx0, sz0) = At(1);
                _selection.Clear();
                foreach (var near in _tracks!.NodesNear(sx0, sz0, 3.0).Take(1))
                    _selection.Add(near);
                SplitTrack(_tracks);
                Log.Info($"Split: refused {_switchRefused ?? "no"}, {source.Network.Ways.Count} ways");
                break;
            }

            case "bridgeclick":
            {
                // bridgeclick,lat,lon: as a click with Place bridge in hand.
                var (cx, cz) = At(1);
                BridgeClick(cx, cz);
                Log.Info($"Bridge click: {(_bridgeFrom is null ? "finished" : "begun")} {_switchRefused}");
                break;
            }

            case "unbridge":
            {
                var (ax, az) = At(1);
                var found = Roads.Nearest(ax, az, 40.0, RoadEditor.IsBridge);
                Log.Info($"Unbridge: {found?.Way}");
                if (found is { } bridged)
                    Committed(Roads.RemoveBridge(bridged.Way));
                break;
            }

            case "bed":
            {
                // bed,lat,lon,KIND[,HEIGHT]: the rail way nearest there.
                var (bx0, bz0) = At(1);
                var hit = Roads.Nearest(bx0, bz0, 60.0, w => w.Rail);
                Log.Info($"Bed: {parts[3]} on way {hit?.Way}");
                if (hit is { } on)
                    Committed(_tracks!.SetBed([on.Way], parts[3], parts.Length > 4 ? (float)Number(4) : null));
                break;
            }

            case "background":
                (_showBackground, MapTiles.Background, _backgroundOpacity) = (parts[1] != "off", parts[1] == "satellite" ? MapBackground.Satellite : MapBackground.Map, 1f);
                SetTopDown(true);
                break;
            case "yardlist":
            {
                var (lx, lz) = At(1);
                foreach (var l in lines.Layouts.Where(l => (l.X - lx) * (l.X - lx) + (l.Z - lz) * (l.Z - lz) < Number(3) * Number(3)))
                    Log.Info($"Layout {l.Kind} at {l.X:0.0},{l.Z:0.0}: {l.Issue}");
                foreach (var d in lines.CrossingAssemblies)
                {
                    var g = _route.Projection!.Inverse(new RoutePoint(d.X, -d.Z));
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Diamond at {g.Lat:0.000000},{g.Lon:0.000000}: {d.Degrees:0.0} degrees, along {d.Range[0].To - d.Range[0].From:0.0} and {d.Range[1].To - d.Range[1].From:0.0} m"));
                }

                foreach (var t in lines.Turnouts.Where(t => (t.X - lx) * (t.X - lx) + (t.Z - lz) * (t.Z - lz) < Number(3) * Number(3)))
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Switch {t.Node} at {t.X:0.0},{t.Z:0.0}: No. {t.Number}, frog {t.FrogNormal:0.0} m, {lines.Layouts.FirstOrDefault(l => l.Nodes.Contains(t.Node))?.Kind}{(t.Reverse.Line.Exact ? ", laid" : "")}"));
                break;
            }

            case "crossoverslide":
            {
                // Through the handles: a press on the crossover's middle, a drag this far along it, and letting go.
                var (sx, sz) = At(1);
                if (lines.Turnouts.Where(t => SwitchHandles.Partner(lines, t) is not null).MinBy(t => (t.X - sx) * (t.X - sx) + (t.Z - sz) * (t.Z - sz)) is not { } t || SwitchHandles.CrossoverHandle(lines, t) is not { } middle)
                {
                    Log.Warn("Crossover: none here");
                    break;
                }

                _tool = Tool.Switches;
                _switch = t.Node;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (middle.X, source.HeightAt(middle.X, middle.Z) + 300.0, middle.Z + 40.0, -1.3f);
                var partner = SwitchHandles.Partner(lines, t)!;
                var (ux, uz) = (partner.X - t.X, partner.Z - t.Z);
                var norm = Math.Max(Math.Sqrt(ux * ux + uz * uz), 1e-9);
                var metres = Number(3);
                var (tx, tz) = (middle.X + ux / norm * metres, middle.Z + uz / norm * metres);
                if (ProjectGround(middle.X, middle.Z) is not { } press)
                    break;
                var target = ProjectGround(tx, tz) ?? press;
                _handles.Update(lines, t, press, middle, ProjectGround, S(1f), true, true);
                _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, true);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Crossover {t.Node} and {partner.Node}: No. {t.Number}, drag {metres:0} m, slid {_handles.Slid:0.0} m, {lines.Layouts.Count(l => l.Kind == JunctionKind.Crossover)} crossovers"));
                SwitchChange(lines, t, _handles.Update(lines, t, target, (tx, tz), ProjectGround, S(1f), false, false));
                break;
            }

            case "follow":
            {
                _tool = Tool.Follow;
                _follow.Clear();
                for (var k = 1; k + 1 < parts.Length; k += 2)
                {
                    var (fx, fz) = At(k);
                    if (FollowClick(_guides!.PlaceNear(fx, fz, 60.0), null) is { } placed)
                        _follow.Add(placed);
                }

                _follow.Update(_guides!, null);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Follow: {_follow.Length:0} m, tightest curve {_follow.Radius:0} m, fitted {!_follow.Unfitted}, {_follow.Preview?.Count} pieces"));
                FollowLay();
                break;
            }

            case "parallelfollow":
            {
                // parallelfollow,TRACKS,SPACING,SIDE,lat,lon,lat,lon...: Parallel clicked along the guides at those places, and Enter.
                _tool = Tool.Parallel;
                _parallelFollowing = true;
                (_parallel.ParallelTracks, _parallel.Spacing, _parallel.Side) = ((int)Number(1), (float)Number(2), (int)Number(3));
                _parallelFollow.Clear();
                for (var k = 4; k + 1 < parts.Length; k += 2)
                {
                    var (fx, fz) = At(k);
                    // (The first may be a track's loose end, as a click on it is.)
                    if (k == 4 && _tracks!.LooseEndNear(fx, fz, 15.0) is { } loose)
                    {
                        var (ex, ez) = _tracks.PositionOf(loose);
                        _parallelFollow.Add(new FollowTool.Click(ex, ez, null, new LayPoint(ex, ez, Node: loose), _tracks.RunsAt(loose)));
                        Log.Info($"Parallel follow: from loose end {loose}");
                    }
                    else if (FollowClick(_guides!.PlaceNear(fx, fz, 60.0), null) is { } placed)
                        _parallelFollow.Add(placed);
                }

                _parallelFollow.Update(_guides!, null);
                var plan = _parallelFollow.Preview is { } pieces && _parallelFollow.Points() is { } first ? _parallel.FollowParallel(_tracks!, pieces, first, PanelLength) : null;
                Log.Info($"Parallel follow: {_parallelFollow.Length:0} m, {plan?.Summary}, problem {plan?.Problem ?? "none"}, points {string.Join("/", plan?.Ways.Select(w => w.Points.Count) ?? [])}, joined {plan?.Ways.Count(w => w.Points[0].Node is not null || w.Points[^1].Node is not null)}");
                if (plan is { Problem: null })
                    Committed(_tracks!.LayWays(plan.Ways));
                _parallelFollow.Clear();
                break;
            }

            case "view":
                _tool = Tool.Follow;
                (_camera.X, _camera.Z) = At(1);
                _camera.Pitch = -1.3f;
                _camera.Z += 8.0;
                _camera.Y = source.HeightAt(_camera.X, _camera.Z) + Number(3);
                break;
            case "drag" when Nearest(At(1), true) is { } h:
            {
                // Through the tool itself: a click on the line, on its handle, a drag to the new place, and letting go.
                var y = h.Line.Y[h.Point] + NetworkLine.BallastHeight + 0.2;
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (h.Line.X[h.Point], y + 40.0, h.Line.Z[h.Point] + 8.0, -1.3f);
                if (Project(h.Line.X[h.Point], y, h.Line.Z[h.Point]) is not { } screen)
                {
                    Log.Warn("Shape drag: the handle isn't in view");
                    break;
                }

                var target = (h.Line.X[h.Point] + Number(3), h.Line.Z[h.Point] - Number(4));
                _tool = Tool.Shape;
                (NetworkLine Line, Alignment Shape)? Step(bool pressed, bool held) =>
                    _shape.Update(lines, screen, target, Project, S(1f), _camera.X, _camera.Z, ViewReach, pressed, held, false, false, false, false, false, false);
                Step(true, true);
                var picked = _shape.LineIn(lines);
                Step(false, false);
                Step(true, true);
                Step(false, true);
                var preview = _shape.Preview;
                var change = Step(false, false);
                Log.Info(string.Create(CultureInfo.InvariantCulture, $"Shape drag: picked line {picked?.Index}, wanted {h.Line.Index}, tightest curve {preview?.Radius:0} m, {preview?.OffMap:0.00} m off the map, {(change is null ? "nothing to commit" : "committed")}"));
                if (change is { } made)
                    CommitShape(source, made.Line, made.Shape);
                break;
            }

            case "show" when Nearest(At(1), true) is { } h:
                _tool = Tool.Shape;
                _shape.Show(h.Line.Key);
                (_camera.X, _camera.Y, _camera.Z, _camera.Pitch) = (h.Line.X[h.Point], h.Line.Y[h.Point] + 60.0, h.Line.Z[h.Point] + 8.0, -1.3f);
                Log.Info($"Shape show: line {h.Line.Index}, handle at point {h.Point} of {h.Line.Count}, {h.Handles.Count} handles");
                break;
            case "move" when Nearest(At(1), true) is { } h:
                var (mx, mz) = (h.Line.X[h.Point] + Number(3), h.Line.Z[h.Point] - Number(4));
                Report("move", h.Line, TrackShaping.Move(h.Line, h.Handles, h.Point, mx, mz));
                break;
            case "straighten" when Nearest(At(1), true) is { } h && h.Handles.IndexOf(h.Point) + 2 < h.Handles.Count:
                var next = h.Handles[h.Handles.IndexOf(h.Point) + 2];
                Report("straighten", h.Line, TrackShaping.Straighten(h.Line, h.Handles, h.Point, next, p => h.Line.Nodes.Any(n => n.Point == p && lines.IsJunction(n.Node))));
                break;
            case "ease":
            {
                // ease,LAT,LON,LAT,LON: one smooth curve between the line's points nearest the two places (as K does in Shape).
                var (ea, eb) = (At(1), At(3));
                int Closest(NetworkLine l, (double X, double Z) p) => Enumerable.Range(0, l.Count).MinBy(i => (l.X[i] - p.X) * (l.X[i] - p.X) + (l.Z[i] - p.Z) * (l.Z[i] - p.Z));
                double Away(NetworkLine l, (double X, double Z) p) { var i = Closest(l, p); return Math.Sqrt((l.X[i] - p.X) * (l.X[i] - p.X) + (l.Z[i] - p.Z) * (l.Z[i] - p.Z)); }
                var on = lines.Lines.Where(l => l.Rail && !l.Disused && l.Alignment is not null).MinBy(l => Away(l, ea) + 0.01 * Away(l, eb));
                if (on is null)
                    break;
                var (pa, pb) = (Closest(on, ea), Closest(on, eb));
                double Jump(Alignment shape, double u0, double u1)
                {
                    // Curvature sampled every 2 m from 30 m before to 30 m after, and the biggest change between samples.
                    var (jump, previous) = (0.0, (double?)null);
                    for (var u = Math.Max(u0 - 30, 2.0); u < Math.Min(u1 + 30, shape.Length - 2.0); u += 2.0)
                    {
                        var k = Math.IEEERemainder(shape.HeadingAt(u + 1.0) - shape.HeadingAt(u - 1.0), 2.0 * Math.PI) / 2.0;
                        if (previous is { } p)
                            jump = Math.Max(jump, Math.Abs(k - p));
                        previous = k;
                    }

                    return jump;
                }

                var was = Jump(on.Alignment!, on.Alignment!.U[Math.Min(pa, pb)], on.Alignment.U[Math.Max(pa, pb)]);
                var eased = TrackShaping.Ease(on, pa, pb, p => on.Nodes.Any(n => n.Point == p && lines.IsJunction(n.Node)));
                Log.Info($"Ease: line {on.Index}, points {pa} and {pb} of {on.Count}, junctions at " + string.Join(", ", on.Nodes.Where(n => lines.IsJunction(n.Node)).Select(n =>
                {
                    var g = _route.Projection!.Inverse(new RoutePoint(on.X[n.Point], -on.Z[n.Point]));
                    return string.Create(CultureInfo.InvariantCulture, $"{n.Point} ({g.Lat:0.000000},{g.Lon:0.000000}, {on.S[n.Point]:0} m)");
                })));
                if (eased is not null)
                    Log.Info(string.Create(CultureInfo.InvariantCulture, $"Ease: points {pa}..{pb}, biggest curvature step {was * 1000:0.00} /km before, {Jump(eased.Shape, eased.Shape.U[Math.Min(pa, pb)], eased.Shape.U[Math.Max(pa, pb)]) * 1000:0.00} /km after"));
                Report("ease", on, eased);
                break;
            }

            case "remove" when Nearest(At(1), true) is { } h:
                Report("remove", h.Line, TrackShaping.Remove(h.Line, h.Handles, h.Point));
                break;
            case "followtest" when _guides is { } guides:
                // followtest,LAT,LON,LAT,LON: Follow along the guides between the two places, logged, laid nowhere.
                var (fa, fb) = (At(1), At(3));
                if (guides.PlaceNear(fa.X, fa.Z, 60.0) is not { } ga || guides.PlaceNear(fb.X, fb.Z, 60.0) is not { } gb)
                {
                    Log.Warn("Follow test: no guide there");
                    break;
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();
                _follow.Clear();
                _follow.Add(new FollowTool.Click(ga.X, ga.Z, ga, new LayPoint(ga.X, ga.Z)));
                var end = new FollowTool.Click(gb.X, gb.Z, gb, new LayPoint(gb.X, gb.Z));
                _follow.Update(guides, end);
                _follow.Add(end);
                _follow.Update(guides, null);
                var fitted = watch.ElapsedMilliseconds;
                var followed = _follow.Points();
                Log.Info($"Follow test: {_follow.Length:0} m, {(_follow.Unfitted ? "partly as mapped" : "fitted")}, {_follow.Preview?.Count ?? 0} pieces, tightest {_follow.Radius:0} m; " +
                    $"{followed?.Count ?? 0} points (every 2 m would be {Math.Ceiling(_follow.Length / 2.0) + 1:0}); fit {fitted} ms, points {watch.ElapsedMilliseconds - fitted} ms");
                break;
            case "bridgepick":
                // bridgepick,LAT,LON: Structures, Edit bridge, with the bridge nearest there picked.
                var bridgeAt = At(1);
                (_tool, _place.Mode) = (Tool.Place, PlaceMode.EditBridge);
                _bridgeKey = _network?.BridgeAt(bridgeAt.X, bridgeAt.Z, 30.0)?.Key;
                Log.Info($"Bridge pick: {_bridgeKey?.ToString() ?? "none"}");
                break;
            case "crossingcut":
                // crossingcut,LAT,LON: the grade crossing nearest there has its roads cut back to its ends.
                var cutAt = At(1);
                if (source.Objects.Where(o => o.Kind == PlacedKind.GradeCrossing).MinBy(o =>
                    {
                        var (ox, oz) = PlacedAtWorld(o);
                        return (ox - cutAt.X) * (ox - cutAt.X) + (oz - cutAt.Z) * (oz - cutAt.Z);
                    }) is { } cut)
                {
                    CrossingPlace(lines, cut, cut);
                    var cutRoads = _undo.TryPeek(out var top) && top is CrossingEdit { Roads: { } r } ? r.Ways : [];
                    Log.Info($"Crossing cut: {cut.Id}: {string.Join("; ", cutRoads.Select(w => $"way {w.Id} {w.Before?.Nodes.Length ?? 0} -> {w.After?.Nodes.Length ?? 0} points"))}");
                }

                break;
            case "reset" when Nearest(At(1), false) is { } h:
                CommitShape(source, h.Line, null);
                Log.Info($"Shape reset: line {h.Line.Index}");
                break;
            case "undo":
                if (_undo.TryPop(out var undone))
                {
                    Redo(undone, undo: true);
                    _redo.Push(undone);
                }

                break;
            case "redo":
                if (_redo.TryPop(out var redone))
                {
                    Redo(redone, undo: false);
                    _undo.Push(redone);
                }

                break;
            case "save":
                Save(source);
                Log.Info("Shape: saved");
                break;
            default:
                Log.Warn($"Shape op: nothing to do for {op}");
                break;
        }
    }
}
