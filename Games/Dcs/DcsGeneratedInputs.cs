namespace VisualControlsBinder.Games;

internal static class DcsGeneratedInputs
{
    // These input tables describe generated Kiowa controls with literal labels and states.
    // Read the specific declarations referenced by the selected joystick profile.
    private static readonly string[] Plain =
    [
        "createPushButtonBindings", "createPushTToggleBindings", "createPressAndHoldButtonBindings"
    ];
    private static readonly string[] WithStates =
    [
        "createDialBindings", "createSelectorDialBindings", "createTwoPosTumblerSwitchBindings",
        "createThreePosTumblerSwitchBindings", "createThreePosSpringAndTumblerSwitchBindings",
        "createThreePosTumblerAndSpringSwitchBindings", "createThreePosSpringSwitchBindings",
        "createThreePosSpringSwitchSingleValueBindings"
    ];

    public static IEnumerable<(string Name, string[] Categories)> Read(string path, ISet<string> tables)
    {
        var lua = DcsLua.Read(path);
        foreach (var helper in Plain.Concat(WithStates))
        {
            bool hasStates = WithStates.Contains(helper);
            int categoryIndex = hasStates ? 5 : 4;
            foreach (var (_, args) in lua.Calls(helper))
            {
                if (args.Length <= categoryIndex || args[0].Length != 1 || !tables.Contains(lua[args[0].Start].Text) ||
                    lua.Label(args[1]) is not { } name)
                    continue;
                var categories = lua.Labels(args[categoryIndex]);
                var states = hasStates ? ReadStates(lua, args[2]) : [];
                foreach (string label in Expand(helper, name, states))
                    yield return (label, categories);
            }
        }
    }

    private static string[] ReadStates(DcsLua lua, DcsLua.Slice states)
    {
        if (!lua[states.Start].Is("{"))
            return [];
        return lua.Parts(states.Start)
            .Select(item => lua[item.Start].Is("{") ? lua.Parts(item.Start).Select(lua.Label).FirstOrDefault() : lua.Label(item))
            .OfType<string>().ToArray();
    }

    private static IEnumerable<string> Expand(string helper, string name, string[] states)
    {
        if (Plain.Contains(helper))
            return [name];
        if (states.Length == 0)
            return [];

        var positions = states.Select(state => $"{name} - {state}");
        if (helper == "createSelectorDialBindings")
            return positions.Concat([$"{name} - STEP CW", $"{name} - STEP CCW"]);
        if (helper == "createDialBindings" || states.Length < 2)
            return positions;

        string center = states[1];
        var endpoints = states.Where((_, index) => index != 1).ToArray();
        IEnumerable<string> toggles = helper switch
        {
            "createThreePosSpringAndTumblerSwitchBindings" => endpoints.TakeLast(1),
            "createThreePosTumblerAndSpringSwitchBindings" => endpoints.Take(1),
            "createThreePosSpringSwitchBindings" or "createThreePosSpringSwitchSingleValueBindings" => [],
            _ => endpoints
        };
        return positions.Concat(endpoints.Select(state => $"{name}: {state} <> {center}"))
            .Concat(toggles.Select(state => $"{name} - {state}/{center}"));
    }
}
