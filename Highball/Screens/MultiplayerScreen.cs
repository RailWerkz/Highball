using Brutal.ImGuiApi;
using RailWerkz.Core.Settings;
using RailWerkz.UI;
using RailWerkz.UI.Anim;
using static RailWerkz.UI.Style;

namespace Highball.Screens;

/// Host or join a session: typed fields for the name and address, left and right for the port.
public sealed class MultiplayerScreen : MenuScreen
{
    private enum Field { None, Name, Address }

    private Field _editing;
    private string _buffer = string.Empty;

    public MultiplayerScreen() : base("MULTIPLAYER")
    {
        Menu.Look = MenuLook.Rows;
        Menu.RowWidth = 640f;
        var s = Settings.Current;

        Menu.Add(new MenuItem { Label = "Driver name", Value = () => Shown(Field.Name, s.DriverName), OnSelect = () => BeginEdit(Field.Name, s.DriverName) })
            .Add(new MenuItem { Label = "Host address", Value = () => Shown(Field.Address, s.LastHost), OnSelect = () => BeginEdit(Field.Address, s.LastHost) })
            .Add(new MenuItem { Label = "Port", Value = () => s.Port.ToString(), OnChange = d => s.Port = Math.Clamp(s.Port + d, 1024, 65535), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Host a session", OnSelect = () => Notify("Not available yet.") })
            .Add(new MenuItem { Label = "Join session", OnSelect = () => Notify("Not available yet."), SeparatorAfter = true })
            .Add(new MenuItem { Label = "Back", OnSelect = Back });
    }

    private string Shown(Field field, string value) =>
        _editing == field ? _buffer + "_" : value;

    private void BeginEdit(Field field, string value)
    {
        _editing = field;
        _buffer = value;
    }

    private void EndEdit(bool keep)
    {
        var s = Settings.Current;
        if (keep && _buffer.Trim().Length > 0)
        {
            if (_editing == Field.Name)
                s.DriverName = _buffer.Trim().ToUpperInvariant();
            else if (_editing == Field.Address)
                s.LastHost = _buffer.Trim();
            Settings.Save();
            Sfx.Select();
        }
        else
        {
            Sfx.Back();
        }

        _editing = Field.None;
    }

    protected override void Back()
    {
        Settings.Save();
        base.Back();
    }

    public override void Update(float dt)
    {
        if (_editing == Field.None)
        {
            Menu.Update();
            return;
        }

        Func<char, bool> allow = _editing == Field.Address ? c => char.IsLetterOrDigit(c) || c is '.' or '-' or ':' : c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_';
        if (Keyboard.Edit(ref _buffer, _editing == Field.Name ? 20 : 64, allow))
            EndEdit(true);
        else if (ImGui.IsKeyPressed(ImGuiKey.Escape))
            EndEdit(false);
    }

    protected override (string Key, string Action)[] HintKeys() => _editing != Field.None
        ? [("ENTER", "Save"), ("ESC", "Cancel")]
        : [("ENTER", "Select"), ("← →", "Change"), ("ESC", "Back")];
}
