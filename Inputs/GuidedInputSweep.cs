using VisualControlsBinder.Hardware;

namespace VisualControlsBinder.Inputs;

internal sealed class GuidedInputSweep(
    IReadOnlyList<HardwareControl> steps,
    IEnumerable<string> learnedTokens)
{
    internal sealed record Step(HardwareControl Control, InputSignal? Signal, string? PreviousToken);

    private readonly HashSet<string> _used = new(learnedTokens, StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<InputSignal> _candidates = [];
    private readonly Stack<Step> _completed = new();

    public HardwareControl? Current => _completed.Count < steps.Count ? steps[_completed.Count] : null;
    public int Number => Math.Min(_completed.Count + 1, Count);
    public int Count => steps.Count;
    public bool CanGoBack => _completed.Count > 0;
    public int Learned => _completed.Count(step => step.Signal is not null);
    public int Skipped => _completed.Count - Learned;
    public IReadOnlyCollection<InputSignal> Candidates => _candidates;

    public void Observe(IEnumerable<InputSignal> signals)
    {
        foreach (var signal in signals)
        {
            if (!_used.Contains(signal.Token))
                _candidates.Add(signal);
        }
    }

    public void ClearCandidates() => _candidates.Clear();

    public void Advance(InputSignal? signal, string? previousToken = null)
    {
        var control = Current ?? throw new InvalidOperationException("The input sweep is complete.");
        if (signal is not null && !_used.Add(signal.Token))
            throw new InvalidOperationException("That input is already learned in this layer.");
        _completed.Push(new Step(control, signal, previousToken));
        _candidates.Clear();
    }

    public Step? Back()
    {
        _candidates.Clear();
        if (!_completed.TryPop(out var step))
            return null;
        if (step.Signal is { } signal)
            _used.Remove(signal.Token);
        return step;
    }
}
