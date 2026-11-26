namespace VisualControlsBinder.Games;

public sealed record GameAction(string Map, string Id, string Category, string Section, string Name, bool IsAxis = false);

public enum BindingActivation { Default, Tap, DoubleTap, Hold }

public sealed record GameBinding(string Map, string Action, string Input)
{
    public BindingActivation Activation { get; init; }
    public string? NativeActivation { get; init; }
}

internal static class BindingActivationText
{
    public static string Label(this BindingActivation activation) => activation switch
    {
        BindingActivation.Tap => "Tap",
        BindingActivation.DoubleTap => "Double Tap",
        BindingActivation.Hold => "Hold",
        _ => "Default"
    };
}
