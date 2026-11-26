using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

internal static class PlanPrivacyChecks
{
    public static void ShareablePlanKeepsBindings(string folder)
    {
        const string game = "star-citizen";
        var instance = Guid.Parse("ab000000-0000-0000-0000-000000000001");
        var product = Guid.Parse("ab000000-0000-0000-0000-000000000002");
        string profile = Path.Combine(folder, "private-game-profile.xml");
        string gremlin = Path.Combine(folder, "private-gremlin-profile.xml");
        var device = new PlannedDevice("stick", instance, product, "stick#2") { BaseLayoutId = "base" };
        var address = new ControlAddress(device.Id, "trigger", "Normal");
        var action = new GameAction("flight", "fire", "Flight", "", "Fire");
        var output = new GremlinOutput(1, "button", 3);
        var plan = new BindingPlan
        {
            Devices = [device],
            Edits =
            [
                new(game, address, action) { Activation = BindingActivation.Hold, ViaVJoy = true, RoutedOutput = output, RoutedProfile = profile },
                new(game, address, action, "js1_button1") { ViaVJoy = true, RoutedOutput = output, RoutedProfile = profile }
            ],
            CustomActions = [action],
            GameFolders = { [game] = Path.Combine(folder, "private-install") },
            SourceProfiles = { [game] = profile },
            CharacterProfiles = { ["swg-legends"] = Path.Combine(folder, "private-character_space.inp") },
            OutputModes = { [game] = OutputMode.JoystickGremlin },
            GremlinProfiles = { [game] = gremlin },
            Aircraft = { ["dcs"] = "F-16C_50" },
            NativeJoysticks = { ["swg-legends"] = device.Id },
            VirtualRoutes = [new(game, address, 1, output)],
            RemovedVirtualRoutes = [new(game, address, output, "button1", instance)],
            DcsProfiles = [new("F-16C_50", device.Id, Path.Combine(folder, "private-module.diff.lua"))],
            GremlinShifts = [new(game, address, GremlinShiftBehavior.Momentary) { BaseMode = "private-template-mode" }],
            RoutedProfiles = [new(game, gremlin, profile)]
        };
        plan.Remember(address, InputSignal.Button(1));
        string original = plan.Fingerprint();
        string sharedPath = Path.Combine(folder, "Share.vbind.json");
        plan.CreateShareableCopy().Save(sharedPath);

        string json = File.ReadAllText(sharedPath);
        Require(!json.Contains("private-", StringComparison.Ordinal) &&
                !json.Contains(instance.ToString(), StringComparison.OrdinalIgnoreCase) &&
                !json.Contains(product.ToString(), StringComparison.OrdinalIgnoreCase),
            "A shareable plan must omit personal references and controller identifiers.");
        var shared = BindingPlan.Read(sharedPath);
        Require(shared.Devices.Single() == (device with { InstanceId = null, ProductId = null }), "Keep the slot and assembly without its pairing.");
        Require(shared.GetSignal(address) == InputSignal.Button(1), "Keep the learned input.");
        Require(shared.Edits is [{ RemoveInput: null, RoutedProfile: null, Activation: BindingActivation.Hold } edit] &&
                edit.Action == action && edit.RoutedOutput == output && edit.ViaVJoy,
            "Keep the planned action, activation, and route without carrying local removals.");
        Require(shared.VirtualRoutes.SequenceEqual(plan.VirtualRoutes) && shared.CustomActions.SequenceEqual(plan.CustomActions) &&
                shared.OutputModes[game] == OutputMode.JoystickGremlin && shared.Aircraft["dcs"] == "F-16C_50" &&
                shared.NativeJoysticks["swg-legends"] == device.Id &&
                shared.GremlinShifts is [{ BaseMode: null, Behavior: GremlinShiftBehavior.Momentary }],
            "Keep the portable output, aircraft, and shift choices.");
        Require(shared.RemovedVirtualRoutes is null && shared.DcsProfiles is null && shared.RoutedProfiles is null &&
                !shared.HasNetworkOrDevicePaths() && plan.Fingerprint() == original,
            "Sharing must leave the user's working plan and profile choices intact.");

        string localPath = Path.Combine(folder, "Local.vbind.json");
        plan.Save(localPath);
        Require(BindingPlan.Read(localPath).Fingerprint() == original, "A local save must still reopen with its settings.");
    }

    public static void DetectPlanNetworkReferences(string folder)
    {
        const string remote = "\\\\example.invalid\\profiles\\controls.xml";
        const string devicePath = "\\\\?\\UNC\\example.invalid\\profiles\\controls.diff.lua";
        var edit = new BindingEdit("star-citizen", new("stick", "trigger", "Normal"), new("flight", "fire", "Flight", "", "Fire"));
        BindingPlan[] remotePlans =
        [
            new() { GameFolders = { ["dcs"] = remote } },
            new() { SourceProfiles = { ["elite"] = "//example.invalid/profiles/preset.binds" } },
            new() { CharacterProfiles = { ["swg-legends"] = remote } },
            new() { GremlinProfiles = { ["star-citizen"] = @"\\.\pipe\plan-privacy-check" } },
            new() { DcsProfiles = [new("module", "stick", devicePath)] },
            new() { RoutedProfiles = [new("star-citizen", remote, "game.xml")] },
            new() { RoutedProfiles = [new("star-citizen", "gremlin.xml", remote)] },
            new() { Edits = [edit with { RoutedProfile = remote }] }
        ];
        string path = Path.Combine(folder, "Imported.vbind.json");
        foreach (var plan in remotePlans)
        {
            plan.Save(path);
            var imported = BindingPlan.Read(path);
            Require(imported.HasNetworkOrDevicePaths(), "Find network or device paths in every plan field that holds a path.");
            Require(!imported.CreateShareableCopy().HasNetworkOrDevicePaths(), "The copy must discard those paths.");
        }
        Require(!new BindingPlan { SourceProfiles = { ["star-citizen"] = Path.Combine(folder, "controls.xml") } }
                .HasNetworkOrDevicePaths(), "Ordinary local paths should open normally.");
    }

    private static void Require(bool passed, string message)
    {
        if (!passed)
            throw new InvalidOperationException(message);
    }
}
