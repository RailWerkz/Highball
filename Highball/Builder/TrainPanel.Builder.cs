using Brutal.ImGuiApi;
using Highball.Trains;
using RailWerkz.UI;
using static RailWerkz.UI.Style;

namespace Highball.Builder;

/// The trains as the Route Builder shows them, in its docked layout:
///   Inspector         the vehicle or coupling picked; with neither, the consist to place and the cuts on the track
///   Drive             a debugging window (Window > Drive), shut till it's asked for: the controls in a row, then control,
///                     running and power. Driving's for keys and the cab; this is only for trying things in the builder.
///   Consist Builder   its own window, opened from the Inspector or the Window menu
///   Outliner          the cuts and their vehicles, listed (Cuts, FocusCut, PickVehicle)
/// The camera, the name tags and the air graphs are in the builder's View menu.
public sealed partial class TrainPanel
{
    /// The Consist Builder window is open.
    public bool ConsistBuilderOpen;

    /// The Drive window is open (it's for debugging, so shut till it's asked for).
    public bool DriveOpen;

    /// Where the Drive and Consist Builder windows dock when first opened (the Inspector's node), if set.
    public ImGuiID DockAt;

    /// The Inspector's contents while the Train tool's in hand.
    public void BuilderInspector()
    {
        if (_notice is not null)
        {
            Kit.Status(Caution, _notice);
            if (ImGui.IsItemClicked())
                _notice = null;
        }

        _showVehicleTab = _showCouplingTab = false;
        var train = Train;
        if (train is not null && (Selected >= 0 || SelectedCoupling is not null))
        {
            if (Kit.Button("train.back", "< All trains"))
            {
                Select(-1);
                SelectCoupling(null);
                return;
            }

            ImGui.Dummy(new float2(1f, S(2f)));
            if (Selected >= 0)
            {
                VehicleTab(train);
            }
            else
                CouplingTab(train);
            return;
        }

        Setup(train, view: false);
        Kit.BeginCard("Consists");
        if (Kit.Button("train.builder", ConsistBuilderOpen ? "Close Consist Builder" : "Open Consist Builder"))
            ConsistBuilderOpen = !ConsistBuilderOpen;
        Kit.EndCard();
    }

    /// The Consist Builder window (Window > Consist Builder).
    public void ConsistBuilderWindow()
    {
        if (!ConsistBuilderOpen)
            return;
        var (min, max) = Highball.App.Workspace.Viewport;
        ImGui.SetNextWindowSize(new float2(S(380f), MathF.Min(S(720f), (max.Y - min.Y) * 0.8f)), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new float2(max.X - S(12f), min.Y + S(12f)), ImGuiCond.FirstUseEver, new float2(1f, 0f));
        if (DockAt != default)
            ImGui.SetNextWindowDockID(DockAt, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Consist Builder"u8, ref ConsistBuilderOpen))
            BuildTab();
        ImGui.End();
    }

    /// The Drive window, while a cut's driven: every control in a row along its top, then the cut's control (handbrakes,
    /// release, horn and bell) and how it's running beside its power.
    public void DriveWindow()
    {
        if (!DriveOpen)
            return;
        if (DockAt != default)
            ImGui.SetNextWindowDockID(DockAt, ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Drive (debug)###drive"u8, ref DriveOpen))
        {
            if (Controlled is not { } train)
            {
                ImGui.TextDisabled("No train driven. Take control of one from the Train tool.");
                ImGui.End();
                return;
            }

            ImGui.Text(train.Name);
            DriveStrip();
            if (ImGui.BeginTable("##drive.columns"u8, 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
            {
                ImGui.TableNextColumn();
                DriveTab(train);
                ImGui.TableNextColumn();
                PowerTab(train);
                ImGui.EndTable();
            }
        }

        ImGui.End();
    }

    /// The cuts on the track, for the Outliner: each one's train, whether it's driven, and whether it's the one looked at.
    public IEnumerable<(Train Train, bool Driving, bool Focused)> Cuts =>
        _cuts.Select(c => (c.Train, ReferenceEquals(c, _controlled), ReferenceEquals(c, _focus)));

    /// Looks at the index'th cut (from the Outliner).
    public void LookAtCut(int index)
    {
        var cuts = _cuts;
        if (index >= 0 && index < cuts.Count)
            Focus(cuts[index]);
        Open = true;
    }

    /// Picks a vehicle of the index'th cut (from the Outliner).
    public void PickVehicle(int index, int car)
    {
        LookAtCut(index);
        Select(car);
    }
}
