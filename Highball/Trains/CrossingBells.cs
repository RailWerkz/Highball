using Highball.World;
using RailWerkz.Core.Audio;
using RailWerkz.Core.IO;
using RailWerkz.Core.Utils;

namespace Highball.Trains;

/// Rings every working grade crossing's bell where it stands: when its warning comes on, one of its kind's recordings
/// (Audio/Crossings: xing_ebell*.wav electronic, xing_mbell*.wav mechanical) picked at random, looped until it goes off. Each
/// recording is one ring of the bell, so looped it rings on at its own beat. Heard as the trains are, from the camera.
public sealed class CrossingBells : IDisposable
{
    /// Full volume within this many metres, falling off beyond; not heard past Range.
    private const float Reference = 12f;
    private const float Range = 900f;
    private const float Height = 3.4f;

    private readonly Dictionary<long, AudioEngine.LoopHandle> _ringing = [];
    private static readonly HashSet<string> Loaded = [];
    private static string[]? _electronic, _mechanical;

    /// A kind's recordings, as names under Content/Audio, found the first time they're asked for.
    private static string[] Recordings(CrossingBell kind)
    {
        string[] Find(string pattern)
        {
            try
            {
                return [.. Directory.GetFiles(ContentPath.Audio("Crossings"), pattern).Order().Select(f => Path.Combine("Crossings", Path.GetFileName(f)))];
            }
            catch (IOException)
            {
                return [];
            }
        }

        return kind == CrossingBell.Mechanical ? _mechanical ??= Find("xing_mbell*.wav") : _electronic ??= Find("xing_ebell*.wav");
    }

    /// After the listener's set this frame: starts the bells of crossings that have come on, stops those that have gone off,
    /// and keeps the rest placed where they are from the camera.
    public void Update(RouteWorld world, Camera camera)
    {
        if (world.Source is not { } source)
        {
            Stop();
            return;
        }

        var objects = source.Objects;
        foreach (var site in world.CrossingSites)
        {
            var on = world.Crossings.IsActive(site.Id);
            var y = source.HeightAt(site.X, site.Z) + Height;
            var at = new AudioEngine.Placement(new float3((float)(site.X - camera.X), (float)(y - camera.Y), (float)(site.Z - camera.Z)), default, Reference, Range);
            if (_ringing.TryGetValue(site.Id, out var handle))
            {
                if (on)
                    AudioEngine.Move(handle, at);
                else
                {
                    AudioEngine.FadeLoop(handle, 0.25f);
                    _ringing.Remove(site.Id);
                }
            }
            else if (on && objects.FirstOrDefault(o => o.Id == site.Id) is { } crossing)
            {
                var choices = Recordings((CrossingBell)crossing.Bell);
                if (choices.Length == 0)
                    continue;
                var path = choices[Random.Shared.Next(choices.Length)];
                var name = "crossing:" + path;
                if (!Loaded.Contains(name) && AudioEngine.Load(name, path, mono: true))
                    Loaded.Add(name);
                _ringing[site.Id] = AudioEngine.PlayLoop(name, AudioCategory.Effects, 1f, at);
            }
        }

        // Crossings taken out while ringing.
        foreach (var id in _ringing.Keys.Where(id => world.CrossingSites.All(s => s.Id != id)).ToList())
        {
            AudioEngine.StopLoop(_ringing[id]);
            _ringing.Remove(id);
        }
    }

    private void Stop()
    {
        foreach (var handle in _ringing.Values)
            AudioEngine.StopLoop(handle);
        _ringing.Clear();
    }

    public void Dispose() => Stop();
}
