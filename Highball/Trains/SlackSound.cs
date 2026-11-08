using RailWerkz.Core.Audio;

namespace Highball.Trains;

/// The couplers taking up slack, from Content/Audio/Couplers, heard at each coupling as it happens.
///
/// A coupling sits loose in its knuckle play (TrainBody.FreeSlack each way) until the cars either side close up or draw
/// apart enough to put it on its draft gears. That moment is the sound: run-in as it closes in buff, stretch as it opens in
/// draft, louder and harder the faster the cars met. The speed is theirs just before, while it was still loose, since the
/// gears have soaked some of it up by the time it's seen engaged. Each coupling makes its own, so a run-in or a stretch
/// travels down the train car by car. A coupling must come back loose before it sounds again, and not too soon after.
public sealed class SlackSound
{
    private const string Folder = "Couplers";
    private const float Reference = 8f;
    private const float Range = 700f;
    private const double Height = 0.85;  // the couplers' height over the rail tops, metres
    /// Closing or opening slower than this, metres a second, makes no sound; then the bands up to the hardest.
    private const double Quietest = 0.04;
    private const double Nudge = 0.12;
    private const double Firm = 0.45;
    private const double Hardest = 1.1;
    /// Back under this much of the knuckle play counts as loose again; and the least time between one coupling's sounds.
    private const double Loose = 0.8;
    private const float Rest = 0.5f;

    private static readonly string[] Buff = SoundFiles.Matching(Folder, "buff");
    private static readonly string[] RunInGentle = SoundFiles.Matching(Folder, "slackRunin_gentle");
    private static readonly string[] RunIn = SoundFiles.Matching(Folder, "slackRunin0");
    private static readonly string[] Bunch = SoundFiles.Matching(Folder, "run8_slackBunch");
    private static readonly string[] StretchGentle = SoundFiles.Matching(Folder, "stretch");
    private static readonly string[] Stretch = SoundFiles.Matching(Folder, "slackStretch");

    private readonly Random _random = new();
    private TrainBody? _for;
    private int[] _state = [];      // each coupling: -1 bunched on its gears, 0 loose, 1 stretched on them
    private double[] _closing = []; // how fast it was closing (negative) or opening, last frame
    private float[] _since = [];

    /// Every frame, after the train's drawn: each car's truck centres where they're drawn, and the listener's position.
    public void Update(float dt, TrainBody body, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        var n = body.Extension.Length;
        if (!ReferenceEquals(body, _for) || _state.Length != n)
        {
            // A new train starts wherever its couplings are, without sounding them.
            _for = body;
            _state = new int[n];
            _closing = new double[n];
            _since = new float[n];
            for (var j = 0; j < n; j++)
            {
                _state[j] = State(body.Extension[j], 0);
                _since[j] = Rest;
            }

            return;
        }

        for (var j = 0; j < n; j++)
        {
            _since[j] += dt;
            var was = _state[j];
            var now = State(body.Extension[j], was);
            var speed = Math.Abs(_closing[j]);
            _closing[j] = body.Velocity[j] - body.Velocity[j + 1];
            _state[j] = now;
            if (now == 0 || now == was || _since[j] < Rest || speed < Quietest || trucks.Count != body.Count)
                continue;

            _since[j] = 0f;
            var hard = (float)Math.Clamp((speed - Quietest) / (Hardest - Quietest), 0.0, 1.0);
            var from = now < 0
                ? speed < Nudge && Buff.Length > 0 ? Buff : speed < Firm ? RunInGentle : speed < Hardest || Bunch.Length == 0 ? RunIn : Bunch
                : speed < Firm ? StretchGentle : Stretch;
            if (from.Length == 0 || SoundFiles.Shot(from[_random.Next(from.Length)]) is not { } shot)
                continue;
            var volume = 0.35f + 0.65f * MathF.Sqrt(hard);
            var pitch = 0.94f + 0.12f * (float)_random.NextDouble();
            AudioEngine.Play(shot, volume: volume, pitch: pitch, at: At(body, j, trucks, listener));
        }
    }

    private static int State(double extension, int was)
    {
        if (extension > TrainBody.FreeSlack)
            return 1;
        if (extension < -TrainBody.FreeSlack)
            return -1;
        return Math.Abs(extension) < TrainBody.FreeSlack * Loose ? 0 : was;
    }

    /// Between car j's rear truck and car j + 1's front one, at coupler height, moving with the train.
    private static AudioEngine.Placement At(TrainBody body, int j, IReadOnlyList<((double X, double Y, double Z) Front, (double X, double Y, double Z) Rear)> trucks, (double X, double Y, double Z) listener)
    {
        var (a, b) = (trucks[j].Rear, trucks[j + 1].Front);
        var (dx, dy, dz) = (trucks[j].Front.X - a.X, trucks[j].Front.Y - a.Y, trucks[j].Front.Z - a.Z);
        var length = Math.Max(Math.Sqrt(dx * dx + dy * dy + dz * dz), 1e-6);
        var v = (body.Velocity[j] + body.Velocity[j + 1]) * 0.5;
        return new AudioEngine.Placement(
            new float3((float)((a.X + b.X) * 0.5 - listener.X), (float)((a.Y + b.Y) * 0.5 + Height - listener.Y), (float)((a.Z + b.Z) * 0.5 - listener.Z)),
            new float3((float)(dx / length * v), (float)(dy / length * v), (float)(dz / length * v)), Reference, Range);
    }
}
