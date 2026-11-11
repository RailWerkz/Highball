using Highball.App;
using Highball.Builder;
using Highball.Routes;
using RailWerkz.Core.Logging;
using RailWerkz.Core.Settings;
using RailWerkz.UI;

namespace Highball.Screens;

/// Pick a world to drive: every pack installed under Content/Packs (the Route Builder's export installs them there).
public sealed class SinglePlayerScreen : MenuScreen
{
    public SinglePlayerScreen() : base("SINGLE PLAYER")
    {
        var packs = PackSource.Installed();

        for (var i = 0; i < packs.Count; i++)
        {
            var (world, region, path) = packs[i];
            var name = WorldName(path) ?? world;
            var label = region == "World" ? name : $"{name} · {region}";
            Menu.Add(new MenuItem { Label = label.ToUpperInvariant(), Detail = path, OnSelect = () => Choose(world, region, path), SeparatorAfter = i == packs.Count - 1 });
        }

        if (packs.Count == 0)
            Menu.Add(new MenuItem { Label = "NO WORLDS INSTALLED", Enabled = () => false, SeparatorAfter = true });

        Menu.Add(new MenuItem { Label = "BACK", OnSelect = Back });
        Menu.Selected = Math.Max(0, packs.FindIndex(p => $"{p.World}/{p.Region}" == Settings.Current.LastRoute));
    }

    /// The world's name as its manifest gives it, beside the pack.
    private static string? WorldName(string pack)
    {
        try
        {
            var manifest = Path.Combine(Path.GetDirectoryName(pack)!, "manifest.json");
            return File.Exists(manifest) && System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest)).RootElement.TryGetProperty("world", out var world) ? world.GetString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Choose(string world, string region, string path)
    {
        Settings.Current.LastRoute = $"{world}/{region}";
        Settings.Save();
        try
        {
            HighballApp.Instance.Push(BuilderScreen.Play(PackSource.Open(path)));
        }
        catch (Exception e)
        {
            Log.Error($"Single Player: {path}: {e}");
            Notify($"Couldn't open it: {e.Message}");
        }
    }
}
