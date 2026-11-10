using System.Buffers.Binary;

namespace RailWerkz.Core.Audio;

/// PCM sound data in memory: 16-bit, mono or stereo.
public sealed class Sound(short[] samples, int channels, int sampleRate)
{
    public short[] Samples { get; } = samples;
    public int Channels { get; } = channels;
    public int SampleRate { get; } = sampleRate;
    public float Duration => Samples.Length / (float)(Channels * SampleRate);

    /// The same folded down to one channel (the channels averaged), for placing in the world: OpenAL only places mono.
    /// The sound with everything above corner Hz turned up by gainDb (a first-order high shelf), then driven driveDb into a soft
    /// limiter (tanh), so it's louder without its peaks clipping: for recordings made from far off, whose bite has gone, and that
    /// already peak at full scale.
    public Sound Brighten(float gainDb, float corner, float driveDb = 0f)
    {
        var ch = Channels;
        var frames = Samples.Length / ch;
        var k = MathF.Pow(10f, gainDb / 20f) - 1f;
        // A one-pole low-pass at corner; the rest is the part above it, added back k times over.
        var a = MathF.Exp(-2f * MathF.PI * corner / SampleRate);
        var output = new float[Samples.Length];
        var peak = 0f;
        for (var c = 0; c < ch; c++)
        {
            var low = 0f;
            for (var f = 0; f < frames; f++)
            {
                var x = Samples[f * ch + c] / 32768f;
                low = (1f - a) * x + a * low;
                var y = x + (x - low) * k;
                output[f * ch + c] = y;
                peak = MathF.Max(peak, MathF.Abs(y));
            }
        }

        var drive = MathF.Pow(10f, driveDb / 20f) / MathF.Max(peak, 1e-6f);
        var result = new short[Samples.Length];
        for (var i = 0; i < output.Length; i++)
            result[i] = (short)Math.Clamp(MathF.Tanh(output[i] * drive) * 0.98f * 32768f, -32768f, 32767f);
        return new Sound(result, ch, SampleRate);
    }

    public Sound ToMono()
    {
        if (Channels == 1)
            return this;

        var frames = Samples.Length / Channels;
        var mono = new short[frames];
        for (var f = 0; f < frames; f++)
        {
            var sum = 0;
            for (var c = 0; c < Channels; c++)
                sum += Samples[f * Channels + c];
            mono[f] = (short)(sum / Channels);
        }

        return new Sound(mono, 1, SampleRate);
    }
}

/// Reads RIFF WAV files: 8/16-bit PCM and 32-bit float (the M61 loops), converted to 16-bit.
public static class Wav
{
    public static Sound Load(string path)
    {
        var data = File.ReadAllBytes(path);

        if (data.Length < 12 || BinaryPrimitives.ReadUInt32BigEndian(data) != 0x52494646) // "RIFF"
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a WAV file");

        int format = 1, channels = 1, rate = 44100, bits = 16;
        var offset = 12;
        ReadOnlySpan<byte> pcm = default;

        while (offset + 8 <= data.Length)
        {
            var id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            var body = offset + 8;
            size = Math.Min(size, data.Length - body);

            if (id == 0x666D7420) // "fmt "
            {
                format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(body + 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 14));

                if (format == 0xFFFE && size >= 26) // WAVE_FORMAT_EXTENSIBLE: the real format is in the sub-format GUID
                    format = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(body + 24));
            }
            else if (id == 0x64617461) // "data"
            {
                pcm = data.AsSpan(body, size);
            }

            offset = body + size + (size & 1);
        }

        short[] samples;

        if (format == 3 && bits == 32)
        {
            samples = new short[pcm.Length / 4];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)Math.Clamp(BinaryPrimitives.ReadSingleLittleEndian(pcm[(i * 4)..]) * 32767f, -32768f, 32767f);
        }
        else if (format == 1 && bits == 16)
        {
            samples = new short[pcm.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm[(i * 2)..]);
        }
        else if (format == 1 && bits == 8)
        {
            samples = new short[pcm.Length];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)((pcm[i] - 128) << 8);
        }
        else if (format == 1 && bits == 24)
        {
            samples = new short[pcm.Length / 3];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)(pcm[i * 3 + 1] | (pcm[i * 3 + 2] << 8));
        }
        else
        {
            throw new NotSupportedException($"{Path.GetFileName(path)}: WAV format {format}, {bits}-bit isn't supported");
        }

        // OpenAL only spatializes mono; everything above stereo is folded down.
        if (channels > 2)
        {
            var frames = samples.Length / channels;
            var stereo = new short[frames * 2];
            for (var f = 0; f < frames; f++)
            {
                stereo[f * 2] = samples[f * channels];
                stereo[f * 2 + 1] = samples[f * channels + 1];
            }
            samples = stereo;
            channels = 2;
        }

        return new Sound(samples, channels, rate);
    }
}
