namespace VisualControlsBinder.Games;

internal sealed record RoutedBindingRead(IReadOnlyList<GameBinding> Bindings, string? Status = null)
{
    public static IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> Read(
        IReadOnlyCollection<GremlinOutput> outputs, Func<GremlinOutput, string> inputFor,
        IEnumerable<GameBinding> bindings, bool normalizeInput = false)
    {
        var byInput = bindings.ToLookup(binding => normalizeInput ? binding.Input.Trim() : binding.Input,
            normalizeInput ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new Dictionary<GremlinOutput, RoutedBindingRead>(outputs.Count);
        foreach (var output in outputs)
        {
            try { result[output] = new RoutedBindingRead(byInput[inputFor(output)].ToArray()); }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException)
            {
                result[output] = new RoutedBindingRead([], error.Message);
            }
        }
        return result;
    }

    public static IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> Unavailable(
        IReadOnlyCollection<GremlinOutput> outputs, string status) =>
        outputs.ToDictionary(output => output, _ => new RoutedBindingRead([], status));
}
