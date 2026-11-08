using Brutal.ImGuiApi;
using Highball.App;
using Highball.Trains;
using RailWerkz.UI;

namespace Highball.Screens;

/// Every saved consist: Enter opens one in the editor, Del deletes it (once confirmed).
public sealed class OpenConsistScreen : MenuScreen
{
    private readonly ConsistEditorScreen _editor;
    private readonly List<Consist> _consists = Consist.All();

    public OpenConsistScreen(ConsistEditorScreen editor) : base("OPEN CONSIST")
    {
        _editor = editor;
        for (var i = 0; i < _consists.Count; i++)
        {
            var consist = _consists[i];
            Menu.Add(new MenuItem
            {
                Label = consist.Name.ToUpperInvariant(),
                OnSelect = () =>
                {
                    _editor.Open(consist);
                    HighballApp.Instance.Pop();
                },
                SeparatorAfter = i == _consists.Count - 1,
            });
        }

        if (_consists.Count == 0)
            Menu.Add(new MenuItem { Label = "NO CONSISTS", Enabled = () => false, SeparatorAfter = true });

        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
    }

    public override void Update(float dt)
    {
        Menu.Update();
        if (ImGui.IsKeyPressed(ImGuiKey.Delete) && Menu.Selected < _consists.Count && _consists.Count > 0)
            HighballApp.Instance.Push(new DeleteConsistScreen(_editor, _consists[Menu.Selected]));
    }

    protected override void DrawRight(Brutal.ImGuiApi.ImDrawListPtr draw, float2 size)
    {
        if (_consists.Count == 0 || Menu.Selected >= _consists.Count)
            return;

        var c = _consists[Menu.Selected];
        var x = MenuX(size) + Style.S(120f);
        var y = MenuTop(size) + Style.S(84f);
        Canvas.Text(draw, Style.Bold, Style.S(18f), new float2(x, y), Style.Text, $"{c.Locomotives} locomotives, {c.RollingStock} cars");
        Canvas.Text(draw, Style.Mono, Style.S(14f), new float2(x, y + Style.S(30f)), Style.TextDim,
            $"{UnitText.Length(c.Length)}   {UnitText.Mass(c.Mass)}   {UnitText.Power(c.Power)}");
    }

    protected override (string Key, string Action)[] HintKeys() => _consists.Count > 0
        ? [("ENTER", "Open"), ("DEL", "Delete"), ("ESC", "Back")]
        : [("ESC", "Back")];
}

/// Deletes a saved consist's file, once confirmed.
public sealed class DeleteConsistScreen : MenuScreen
{
    public DeleteConsistScreen(ConsistEditorScreen editor, Consist consist) : base($"DELETE {consist.Name.ToUpperInvariant()}?")
    {
        // Cancel first, so the highlight starts on it and a stray Enter deletes nothing.
        Menu.Add(new MenuItem { Label = "CANCEL", OnSelect = Back })
            .Add(new MenuItem
            {
                Label = "DELETE",
                OnSelect = () =>
                {
                    consist.Delete();
                    HighballApp.Instance.Pop();
                    HighballApp.Instance.Replace(new OpenConsistScreen(editor));
                },
            });
    }
}
