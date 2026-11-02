using RailWerkz.Core.IO;
using RailWerkz.Core.Logging;
using NLayer;

namespace RailWerkz.Core.Audio;

/// Decodes the music from Content/Audio/Music (MP3, through NLayer) into stereo 16-bit blocks for the engine to queue.
/// Changing track fades the old one out and the new one in; looping tracks run through their files and start again.
public sealed class MusicStream : IDisposable
{
    private const float FadeOut = 1.2f;
    private const float FadeIn = 1.5f;

    private MusicTrack _track = MusicTrack.None;
    private MusicTrack _next = MusicTrack.None;
    private bool _switching;
    private float _fade;          // 0 silent, 1 full

    private MpegFile? _file;
    private int _fileIndex;
    private float[] _decoded = [];

    /// The rate of the file playing (or that last played), which the next block is at.
    public int SampleRate { get; private set; } = 44100;

    public MusicTrack Track => _switching ? _next : _track;

    public void Play(MusicTrack track)
    {
        if (track == Track)
            return;

        _next = track;
        _switching = true;
    }

    /// Fills interleaved stereo 16-bit samples. Returns how many frames were written: fewer than asked when the
    /// file changes mid-block, so every block is at one sample rate.
    public int Render(Span<short> stereo)
    {
        var frames = stereo.Length / 2;
        var dt = 1f / SampleRate;

        if (_switching && _fade <= 0f)
            Switch();

        var channels = _file?.Channels ?? 2;
        if (_decoded.Length < frames * channels)
            _decoded = new float[frames * channels];

        var got = _file is null ? 0 : _file.ReadSamples(_decoded, 0, frames * channels) / channels;
        if (_file is not null && got == 0)
        {
            // End of the file: on to the next (or the same one again), or silence for a track that plays once.
            var track = Music.For(_track);
            Open(track.Loop ? (_fileIndex + 1) % track.Files.Length : -1);
            return 0;
        }

        var written = _file is null ? frames : got;
        for (var f = 0; f < written; f++)
        {
            if (_switching)
                _fade = MathF.Max(0f, _fade - dt / FadeOut);
            else if (_fade < 1f)
                _fade = MathF.Min(1f, _fade + dt / FadeIn);

            float l = 0f, r = 0f;
            if (f < got)
            {
                l = _decoded[f * channels];
                r = channels > 1 ? _decoded[f * channels + 1] : l;
            }

            stereo[f * 2] = (short)(Math.Clamp(l * _fade, -1f, 1f) * short.MaxValue);
            stereo[f * 2 + 1] = (short)(Math.Clamp(r * _fade, -1f, 1f) * short.MaxValue);

            // Faded out: stop here so the new track starts on a fresh block (it may be at another rate).
            if (_switching && _fade <= 0f)
                return f + 1;
        }

        return written;
    }

    private void Switch()
    {
        _track = _next;
        _switching = false;
        Open(0);
    }

    private void Open(int index)
    {
        _file?.Dispose();
        _file = null;
        _fileIndex = index;

        var files = Music.For(_track).Files;
        if (index < 0 || index >= files.Length)
            return;

        var path = ContentPath.Audio($"Music/{files[index]}");
        try
        {
            _file = new MpegFile(path);
            SampleRate = _file.SampleRate;
            Log.Info($"Music: {_track} plays {files[index]}");
        }
        catch (Exception e)
        {
            Log.Warn($"Music {files[index]}: {e.Message}");
        }
    }

    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
    }
}
