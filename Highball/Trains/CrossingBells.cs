using Highball.World;
using RailWerkz.Core.Audio;
using RailWerkz.Core.IO;
using RailWerkz.Core.Utils;

namespace Highball.Trains;

/// Rings every working grade crossing's bell where it stands: when its warning comes on (and until its arms are back up), one of its kind's recordings
/// (Audio/Crossings: xing_ebell*.wav electronic, xing_mbell*.wav mechanical) picked at random, looped until it goes off. Each
/// recording is one ring of the bell, so looped it rings on at its own beat. Heard as the trains are, from the camera. How
/// loud, how high, how far: AudioTuning.Crossings, tuned in the Audio Editor, read live.
public sealed class CrossingBells : IDisposable
{
    private static AudioTuning.CrossingTuning Tuning => AudioTuning.Current.Crossings;

    private readonly Dictionary<long, (AudioEngine.LoopHandle Handle, CrossingBell Kind)> _ringing = [];
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

        var tuning = Tuning;
        var objects = source.Objects;
        foreach (var site in world.CrossingSites)
        {
            // Ringing while its flashers flash: from when its warning starts until its arms are back up.
            var on = world.Network?.CrossingLit(site.Id) ?? false;
            var y = source.HeightAt(site.X, site.Z) + tuning.Height;
            var at = new AudioEngine.Placement(new float3((float)(site.X - camera.X), (float)(y - camera.Y), (float)(site.Z - camera.Z)), default,
                MathF.Max(tuning.FullWithin, 0.1f), MathF.Max(tuning.Range, tuning.FullWithin));
            if (_ringing.TryGetValue(site.Id, out var ringing))
            {
                if (on)
                {
                    AudioEngine.Move(ringing.Handle, at);
                    var (level, pitch) = Sound(ringing.Kind);
                    AudioEngine.SetLoopVolume(ringing.Handle, level, pitch);
                }
                else
                {
                    AudioEngine.FadeLoop(ringing.Handle, MathF.Max(tuning.FadeOut, 0.01f));
                    _ringing.Remove(site.Id);
                }
            }
            else if (on && objects.FirstOrDefault(o => o.Id == site.Id) is { } crossing)
            {
                var kind = (CrossingBell)crossing.Bell;
                if (Pick(kind) is not { } name)
                    continue;
                var (level, pitch) = Sound(kind);
                var handle = AudioEngine.PlayLoop(name, AudioCategory.Effects, level, at);
                AudioEngine.SetLoopVolume(handle, level, pitch);
                _ringing[site.Id] = (handle, kind);
            }
        }

        // Crossings taken out while ringing.
        foreach (var id in _ringing.Keys.Where(id => world.CrossingSites.All(s => s.Id != id)).ToList())
        {
            AudioEngine.StopLoop(_ringing[id].Handle);
            _ringing.Remove(id);
        }
    }

    /// A kind's level and pitch as tuned.
    private static (float Level, float Pitch) Sound(CrossingBell kind) => kind == CrossingBell.Mechanical
        ? (Tuning.Mechanical, Tuning.MechanicalPitch)
        : (Tuning.Electronic, Tuning.ElectronicPitch);

    /// One of a kind's recordings at random, loaded; null if it has none.
    private static string? Pick(CrossingBell kind)
    {
        var choices = Recordings(kind);
        if (choices.Length == 0)
            return null;
        var path = choices[Random.Shared.Next(choices.Length)];
        var name = "crossing:" + path;
        if (!Loaded.Contains(name) && AudioEngine.Load(name, path, mono: true))
            Loaded.Add(name);
        return name;
    }

    /// For the Audio Editor: one ring of a kind, as tuned, heard as if standing at the crossing (at full volume).
    public static void Preview(CrossingBell kind)
    {
        if (Pick(kind) is not { } name)
            return;
        var (level, pitch) = Sound(kind);
        AudioEngine.Play(name, AudioCategory.Effects, level, pitch);
    }

    private void Stop()
    {
        foreach (var (handle, _) in _ringing.Values)
            AudioEngine.StopLoop(handle);
        _ringing.Clear();
    }

    public void Dispose() => Stop();
}
