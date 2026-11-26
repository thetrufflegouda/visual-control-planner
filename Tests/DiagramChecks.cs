using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

internal static class DiagramChecks
{
    internal static void RoutedWarThunderShowsSavedBindings(string folder)
    {
        const string game = "war-thunder";
        string path = Path.Combine(folder, "controls.blk");
        File.WriteAllText(path, """
            controls{
              version:i=5
              deviceMapping{
                joystick{
                  devId:t="1234:bead"
                  name:t="vJoy Device"
                  connected:b=yes
                  buttonsOffset:i=20
                  buttonsCount:i=2
                  axesOffset:i=0
                  axesCount:i=0
                }
              }
              hotkeys{
                ID_FIRE{
                  joyButton:i=20
                }
                ID_FIRE{
                  joyButton:i=20
                  activationType:i=2
                }
                ID_RADAR{
                  joyButton:i=20
                }
              }
              axes{
              }
            }
            """);
        var plan = new BindingPlan();
        var device = TestLayout();
        var fire = new GameAction("hotkeys", "ID_FIRE", "", "", "Fire");
        var radar = new GameAction("hotkeys", "ID_RADAR", "", "", "Radar");
        var salute = new GameAction("hotkeys", "ID_SALUTE", "", "", "Salute");
        var output = new GremlinOutput(1, "button", 1);
        var unused = new GremlinOutput(1, "button", 2);
        GremlinOutput[] outputs = [output, unused];
        var trigger = new ControlAddress(device.Id, "fire", "Normal");
        var shifted = trigger with { Layer = GremlinShiftSetting.Layer };
        GremlinRouteView[] routes =
        [
            new(device.Id, trigger, InputSignal.Button(1), new(output, null), false, false, false, device.Name),
            new(device.Id, shifted, InputSignal.Button(1), new(output, null), true, false, false, device.Name),
            new(null, null, InputSignal.Button(3), new(unused, null), false, false, false, "Unpaired Stick")
        ];

        var before = ReadDiagram();
        Require(before.Count(row => row.Action == fire) == 4, "Both saved timings must appear on the base and inherited routes.");
        Require(before.Count(row => row.Action == radar) == 2, "A shared output must show all of its saved actions.");
        Require(before.Any(row => row.Address == shifted && row.Inherited && row.Label == "Fire · Hold"),
            "An inherited route must retain the binding label and timing.");
        Require(before.Any(row => row.Address is null && row.Route == unused && row.Action is null),
            "An unplaced route without a game action must remain visible.");

        plan.Edits.Add(new BindingEdit(game, trigger, salute) { ViaVJoy = true, RoutedOutput = output });
        var pending = ReadDiagram();
        Require(pending.Any(row => row.Address == trigger && row.Route == output && row.Label == "Salute · Tap"),
            "A pending action must appear on its planned route.");
        Require(pending.Count(row => row.Action is not null) == 7, "Pending additions must not hide saved actions on the output.");

        var profile = new WarThunderVJoyBindings(WarThunderProfile.Load(path), outputs);
        Require(profile.Apply(salute, output, BindingActivation.Tap, null), "The fixture must save the pending action.");
        NativeBindingWrites.Commit(profile.ToWrites());
        plan.Edits.Clear();
        var after = ReadDiagram();
        Require(after.Any(row => row.Address == trigger && row.Label == "Salute · Tap") &&
            after.Any(row => row.Address == shifted && row.Label == "Salute · Tap" && row.Inherited),
            "After saving and reopening, the diagram must still show the action on both routes.");

        plan.Edits.Add(new BindingEdit(game, trigger, fire, "joyButton:20")
            { ViaVJoy = true, RoutedOutput = output, Activation = BindingActivation.Tap });
        var removal = ReadDiagram();
        Require(removal.Count(row => row.Action == fire) == 2 && removal.All(row => row.Label != "Fire · Tap"),
            "A pending Tap removal must leave the saved Hold action visible.");

        IReadOnlyList<DiagramBinding> ReadDiagram()
        {
            var saved = new WarThunderVJoyBindings(WarThunderProfile.Load(path), outputs).Read(outputs);
            return DiagramBindings.ForGremlin(game, plan, [device], routes, saved, [fire, radar, salute], null,
                device => device.MatchesReferenceProduct((device.VendorId, device.ProductId)), _ => true, _ => true).Rows;
        }
    }

    internal static void StarCitizenDiagramKeepsDistinctBindings(string folder)
    {
        const string game = "star-citizen";
        string path = Path.Combine(folder, "layout_test_exported.xml");
        string otherPath = Path.Combine(folder, "layout_copy_exported.xml");
        File.WriteAllText(path, $$"""
            <ActionMaps profileName="Test">
              <options type="joystick" instance="2" Product="vJoy Device {{StarCitizenVJoyBindings.ProductId:B}}" />
              <actionmap name="flight">
                <action name="fire">
                  <rebind input="js2_button1" activationMode="tap" />
                  <rebind input="js2_button1" activationMode="delayed_press" />
                  <rebind input="js2_button3" activationMode="tap" />
                  <rebind input="js1_button1" activationMode="tap" />
                </action>
              </actionmap>
            </ActionMaps>
            """);
        File.Copy(path, otherPath);
        var source = StarCitizenGame.LoadProfile(path);
        var plan = new BindingPlan();
        var device = TestLayout();
        var trigger = new ControlAddress(device.Id, "fire", "Normal");
        var fire = new GameAction("flight", "fire", "", "", "Fire");
        var output = new GremlinOutput(1, "button", 1);
        GremlinRouteView[] routes = [new(device.Id, trigger, InputSignal.Button(1), new(output, null), false, false, false, device.Name)];

        var same = Project(path);
        Require(same.Count == 4 && same.Count(row => row.Route == output) == 2 &&
            same.Any(row => row.Input == "js2_button3" && row.Address is null) &&
            same.Any(row => row.Input == "js1_button1" && row.Address == trigger),
            "Only bindings already shown through a route in the same file should lose their duplicate native row.");
        Require(Project(otherPath).Count == 6, "Bindings in separately selected files must remain separate even when they match.");

        plan.Edits.Add(new BindingEdit(game, trigger, fire, "js2_button1")
        {
            ViaVJoy = true, RoutedOutput = output, Activation = BindingActivation.Hold, NativeActivation = "delayed_press"
        });
        var partial = Project(path);
        Require(partial.Count(row => row.Route == output) == 1 && partial.Any(row => row.Input == "js2_button1" &&
            row.Route is null && row.Activation == BindingActivation.Hold),
            "A routed Tap row does not represent a native Hold row for the same input and action.");

        IReadOnlyList<DiagramBinding> Project(string routedPath)
        {
            var routedProfile = StarCitizenGame.LoadProfile(routedPath);
            var saved = new StarCitizenVJoyBindings(routedProfile, [1]).Read([output]);
            var (routedRows, represented) = DiagramBindings.ForGremlin(game, plan, [device], routes, saved, [fire], null,
                _ => true, _ => true, _ => true);
            return DiagramBindings.ForStarCitizen(game, plan, [device], source, [fire], _ => 1, _ => true,
                includeStaged: false, routedSource: (routedProfile.SourcePath, represented)).Concat(routedRows).ToArray();
        }
    }

    internal static void NativeHintsRequireCompatibleProduct(string _)
    {
        var plan = new BindingPlan();
        var device = TestLayout();
        var action = new GameAction("hotkeys", "ID_FIRE", "", "", "Fire");
        NativeDiagramBinding[] saved = [new(device.Id, "button1", "joyButton:20", action, BindingActivation.Tap)];
        var trigger = new ControlAddress(device.Id, "fire", "Normal");

        Require(Project((3, 4)) is { Address: null, Reference: false },
            "A binding on an incompatible controller must stay unplaced until learned.");
        Require(Project((1, 5)) is { Reference: true } alternate && alternate.Address == trigger,
            "A supported alternate product may use the layout hint.");
        Require(Project((1, 2)) is { Reference: true } offline && offline.Address == trigger,
            "An offline controller using the layout product may use its hint.");

        plan.Remember(trigger, InputSignal.Button(1));
        Require(Project((3, 4)) is { Reference: false } learned && learned.Address == trigger,
            "Learning the input must place the binding even on an incompatible model.");

        DiagramBinding Project((int Vendor, int Product) ids) => DiagramBindings.ForNative("war-thunder", plan, [device], saved,
            item => item.MatchesReferenceProduct(ids)).Single();
    }

    private static HardwareDefinition TestLayout() => new("test-stick", "Test Stick", 1, 2, [],
        [new("fire", "Trigger", "button", "button1", null)]) { AlternateProductIds = [5] };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
