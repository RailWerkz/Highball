using Highball.App;
using Highball.Builder;
using Highball.Multiplayer;
using Highball.Routes;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Screens;

/// Host a session: every installed world or region that has its server track beside it, hosted on this machine on the
/// port set in Multiplayer, and joined.
public sealed class HostScreen : MenuScreen
{
    public HostScreen() : base("HOST A SESSION")
    {
        var packs = PackSource.Installed().Where(p => File.Exists(ServerFile(p.Path))).ToList();
        for (var i = 0; i < packs.Count; i++)
        {
            var (world, region, path) = packs[i];
            Menu.Add(new MenuItem { Label = (region == "World" ? world : $"{world} · {region}").ToUpperInvariant(), Detail = ServerFile(path), OnSelect = () => Host(world, path), SeparatorAfter = i == packs.Count - 1 });
        }

        if (packs.Count == 0)
            Menu.Add(new MenuItem { Label = "NO WORLDS INSTALLED WITH SERVER TRACK", Enabled = () => false, SeparatorAfter = true });
        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
    }

    /// The server track beside a pack: REGION.hbserver.
    public static string ServerFile(string pack) => Path.ChangeExtension(pack, ".hbserver");

    private void Host(string world, string pack)
    {
        try
        {
            var s = Settings.Current;
            var net = NetPlay.Host(ServerFile(pack), world, s.Port, s.DriverName);
            HighballApp.Instance.Replace(new ConnectingScreen(() => net));
        }
        catch (Exception e)
        {
            Log.Error($"Host: {e}");
            Notify($"Couldn't host: {e.Message}");
        }
    }
}

/// Waits for the server's welcome, then opens the world and region it says (if it's installed) to play. Joining keeps
/// trying for a minute while there's no answer, so a player can join before the host has started.
public sealed class ConnectingScreen : MenuScreen
{
    private const double RetryFor = 60.0;
    private const double RetryEvery = 3.0;

    private readonly Func<NetPlay> _connect;
    private NetPlay _net;
    private string _status = "Connecting...";
    private bool _done;
    private double _elapsed, _failedAt = -1.0;
    private int _tries = 1;

    public ConnectingScreen(Func<NetPlay> connect) : this(connect, connect())
    {
    }

    private ConnectingScreen(Func<NetPlay> connect, NetPlay net) : base(net.Hosting ? "HOSTING" : "JOINING")
    {
        _connect = connect;
        _net = net;
        Menu.Look = MenuLook.Rows;
        Menu.RowWidth = 760f;
        Menu.Add(new MenuItem { Label = "Status", Value = () => _status, SeparatorAfter = true })
            .Add(new MenuItem { Label = "Cancel", OnSelect = Back });
        Menu.Selected = 1;
    }

    protected override void Back()
    {
        if (!_done)
            _net.Dispose();
        _done = true;
        base.Back();
    }

    public override void Update(float dt)
    {
        base.Update(dt);
        if (_done)
            return;
        _elapsed += dt;
        _net.Poll();
        switch (_net.State)
        {
            case NetPlay.Stage.Connecting:
                _status = _tries > 1 ? $"Waiting for the server... (try {_tries})" : "Connecting...";
                break;
            case NetPlay.Stage.Failed:
                // No answer yet: try again a while, in case the host hasn't started.
                var noAnswer = _net.Problem?.StartsWith("Couldn't connect", StringComparison.Ordinal) == true;
                if (noAnswer && !_net.Hosting && _elapsed < RetryFor)
                {
                    if (_failedAt < 0.0)
                        _failedAt = _elapsed;
                    _status = $"No answer, trying again... (try {_tries})";
                    if (_elapsed - _failedAt >= RetryEvery)
                    {
                        _net.Dispose();
                        _net = _connect();
                        _tries++;
                        _failedAt = -1.0;
                    }
                }
                else
                    _status = _net.Problem ?? "Failed";

                break;
            case NetPlay.Stage.Welcomed:
                var found = PackSource.Installed().FirstOrDefault(p => p.World.Equals(_net.World, StringComparison.OrdinalIgnoreCase) && p.Region.Equals(_net.Region, StringComparison.OrdinalIgnoreCase));
                if (found.Path is null)
                {
                    _status = $"You don't have {_net.World} · {_net.Region} installed";
                    _net.Dispose();
                    _done = true;
                    return;
                }

                try
                {
                    _done = true;
                    HighballApp.Instance.Replace(BuilderScreen.Play(PackSource.Open(found.Path), _net));
                }
                catch (Exception e)
                {
                    Log.Error($"Joining: {e}");
                    _status = $"Couldn't open {found.Path}: {e.Message}";
                    _net.Dispose();
                }

                break;
        }
    }
}
