namespace RailWerkz.UI;

/// The work done behind the splash screen, one step a frame so the screen keeps animating: every line is a real
/// check (the GPU, the audio device, the shaders, the routes...). A step that returns Wait is asked again next
/// frame, up to its patience; then it's asked with last = true and must give a final answer.
public sealed class Startup
{
    public enum Status { Ok, Warn, Fail, Wait }

    public readonly record struct Result(Status State, string Value);

    public sealed record Step(string Label, Func<bool, Result> Run, double Patience = 0.0);

    private readonly List<Step> _steps;
    private readonly Result[] _results;
    private double _stepStart = -1.0;

    public Startup(IEnumerable<Step> steps)
    {
        _steps = [.. steps];
        _results = new Result[_steps.Count];
    }

    public int Count => _steps.Count;
    public int Current { get; private set; }
    public bool Done => Current >= _steps.Count;

    /// 0..1 through the steps.
    public float Progress => _steps.Count == 0 ? 1f : Current / (float)_steps.Count;

    public string Label(int i) => _steps[i].Label;
    public Result ResultOf(int i) => _results[i];

    /// The step running now, for the "Loading shaders…" line.
    public string CurrentLabel => Done ? "Ready" : _steps[Current].Label;

    /// Runs (or re-asks) the current step. now is the interface clock.
    public void Advance(double now)
    {
        if (Done)
            return;

        if (_stepStart < 0.0)
            _stepStart = now;

        var step = _steps[Current];
        var last = now - _stepStart >= step.Patience;
        Result result;

        try
        {
            result = step.Run(last);
        }
        catch (Exception e)
        {
            Core.Logging.Log.Error($"{step.Label}: {e}");
            result = new Result(Status.Fail, e.Message);
        }

        if (result.State == Status.Wait && !last)
            return;

        if (result.State == Status.Wait)
            result = result with { State = Status.Warn };

        _results[Current] = result;
        Core.Logging.Log.Info($"{step.Label}: {result.State} {result.Value}");
        Current++;
        _stepStart = -1.0;
    }
}
