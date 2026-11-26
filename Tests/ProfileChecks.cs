using System.Xml.Linq;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

var checks = new (string Name, Action<string> Run)[]
{
    ("Star Citizen unbind preserves other inputs", CheckStarCitizenUnbind),
    ("Export rejects a stale profile before writing", CheckStaleProfile),
    ("Export rolls back an earlier write after a destination collision", CheckRollback),
    ("Gremlin disconnect preserves the response curve and other routes", CheckGremlinDisconnect),
    ("Gremlin prioritizes explicit and saved targets", CheckGremlinSavedTargets),
    ("Gremlin reuses routes and finds free outputs in device order", CheckGremlinFreeOutput),
    ("Export rejects a profile replaced after staging", WriterChecks.ReplacedDestination),
    ("Overlapping exports serialize writes in destination order", WriterChecks.OverlappingExports),
    ("SWG exports both keymaps or rolls back the copy", WriterChecks.SwgCopies),
    ("Direct and routed Star Citizen writes use the same stale-file check", WriterChecks.StarCitizenWriters),
    ("IL-2 copies and updates its actions and map as a pair", WriterChecks.Il2ProfilePair),
    ("Elite Dangerous keeps modifiers and rejects stale presets", WriterChecks.ElitePreset),
    ("The War Thunder diagram shows saved routed bindings and timing", DiagramChecks.RoutedWarThunderShowsSavedBindings),
    ("Diagram references require a compatible controller product", DiagramChecks.NativeHintsRequireCompatibleProduct),
    ("Star Citizen diagrams preserve distinct inputs without repeating routed bindings", DiagramChecks.StarCitizenDiagramKeepsDistinctBindings),
    ("Shareable plans keep bindings and omit machine-specific settings", PlanPrivacyChecks.ShareablePlanKeepsBindings),
    ("Network and Windows device paths are identified before loading a plan", PlanPrivacyChecks.DetectPlanNetworkReferences),
};

string runFolder = Path.Combine(AppContext.BaseDirectory, "profile-checks", Guid.NewGuid().ToString("N"));
int failures = 0;
for (int i = 0; i < checks.Length; i++)
{
    var (name, check) = checks[i];
    string folder = Directory.CreateDirectory(Path.Combine(runFolder, (i + 1).ToString())).FullName;
    try
    {
        check(folder);
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}{Environment.NewLine}{error}");
    }
}
Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
if (failures > 0)
    Console.Error.WriteLine($"Profiles from this run: {runFolder}");
return failures == 0 ? 0 : 1;

static void CheckStarCitizenUnbind(string folder)
{
    string path = Path.Combine(folder, "layout_test_exported.xml");
    var product = Guid.Parse("10000000-0000-0000-0000-000000000001");
    File.WriteAllText(path, $$"""
        <ActionMaps profileName="Test">
          <options type="joystick" instance="1" Product="Test Stick {{product:B}}" />
          <actionmap name="flight">
            <action name="fire">
              <rebind input="js1_button1" />
              <rebind input="js2_button2" />
              <rebind input="kb1_space" />
            </action>
          </actionmap>
        </ActionMaps>
        """);
    var loaded = new GameProfiles().ReadNative(GameKind.StarCitizen, folder, path, [], globalIl2: true);
    var profile = loaded.StarCitizen ?? throw new InvalidOperationException("The selected Star Citizen profile did not load.");
    Require(profile.RemoveActivationForDevice("flight", "fire", "js1_button1", product, null), "The selected joystick binding should be removed.");
    profile.SaveChanges();
    var inputs = StarCitizenGame.LoadProfile(path).Bindings.Select(binding => binding.Input);
    Require(inputs.SequenceEqual(["js1_ ", "js2_button2", "kb1_space"]), "The empty override must suppress the default and leave the other inputs intact.");
}

static void CheckStaleProfile(string folder)
{
    string first = Path.Combine(folder, "first.xml");
    string second = Path.Combine(folder, "second.xml");
    File.WriteAllBytes(first, [1]);
    File.WriteAllBytes(second, [3]);

    ExpectIoFailure(() => NativeBindingWrites.Commit([
        new(first, [1], [4]), new(second, [2], [5]),
    ]));
    Require(File.ReadAllBytes(first).SequenceEqual<byte>([1]), "A stale second profile must prevent writing the first.");
    Require(File.ReadAllBytes(second).SequenceEqual<byte>([3]), "A stale profile must retain the current bytes.");
}

static void CheckRollback(string folder)
{
    string first = Path.Combine(folder, "first.xml");
    string second = Path.Combine(folder, "second.xml");
    File.WriteAllBytes(first, [1]);
    int validations = 0;
    ExpectIoFailure(() => NativeBindingWrites.Commit([new(first, [1], [4]), new(second, null, [5])], () =>
    {
        if (++validations == 2)
            File.WriteAllBytes(second, [9]);
    }));

    Require(validations == 2, "The conflicting file should appear after staging.");
    Require(File.ReadAllBytes(first).SequenceEqual<byte>([1]), "The first write should be rolled back.");
    Require(File.ReadAllBytes(second).SequenceEqual<byte>([9]), "The conflicting file must remain intact.");
}

static void CheckGremlinDisconnect(string folder)
{
    string path = Path.Combine(folder, "gremlin.xml");
    var (source, curveId) = WriteGremlinFixture(path);
    var axis = new GremlinOutput(1, "axis", 1);
    var button = new GremlinOutput(1, "button", 2);
    var profile = GremlinProfile.Load(path, static () => null);
    var removal = new GremlinRouteRemoval(source, "axis", 1, "Default", axis);
    Require(profile.ContainsRoute(removal), "The fixture must have an active axis route.");
    var (write, _) = profile.PrepareUpdate([], 128, [removal]);
    var update = write ?? throw new InvalidOperationException("Disconnecting the axis should produce a write.");
    NativeBindingWrites.Commit([update]);

    var updated = GremlinProfile.Load(path, static () => null);
    Require(!updated.ContainsRoute(removal), "The disconnected route must be absent after reloading.");
    Require(updated.GetOutput(source, "button", 2) == button, "The unrelated button route must survive.");
    var document = XDocument.Load(path);
    var axisInput = document.Root!.Element("inputs")!.Elements("input").Single(input => (string?)input.Element("input-type") == "axis");
    string rootId = axisInput.Element("action-configuration")!.Element("root-action")!.Value;
    var rootAction = document.Root.Element("library")!.Elements("action").Single(action => (string?)action.Attribute("id") == rootId);
    Require(rootAction.Element("actions")!.Elements("action-id").Select(reference => reference.Value).SequenceEqual([curveId.ToString()]),
        "The response curve must remain active on the original input.");
}

static void CheckGremlinSavedTargets(string folder)
{
    string path = Path.Combine(folder, "gremlin.xml");
    var (source, _) = WriteGremlinFixture(path);
    var firstButton = new GremlinOutput(1, "button", 1);
    var savedButton = new GremlinOutput(1, "button", 2);
    var savedAxis = new GremlinOutput(1, "axis", 1);
    var savedKey = new SwgKey(0x39);
    var profile = GremlinProfile.Load(path, () => [firstButton, savedButton, savedAxis]);
    var button = (source, "button", 2, "Default");

    Require(profile.ChooseTarget(button, firstButton, new(savedButton, savedKey), [], 2) == new SwgTarget(firstButton, null),
        "The explicit vJoy route must take precedence over saved targets.");
    Require(profile.ChooseTarget(button, null, new(savedButton, savedKey), [], 2) == new SwgTarget(savedButton, null),
        "The saved vJoy route must take precedence over the saved key.");
    Require(profile.ChooseTarget(button, null, new(savedAxis, savedKey), [], 2) == new SwgTarget(null, savedKey),
        "A saved route of the wrong kind should fall back to the saved key for a physical button.");
    Require(profile.ChooseTarget((source, "axis", 1, "Default"), null, new(null, savedKey), [], 2) == new SwgTarget(savedAxis, null),
        "A physical axis should reuse its vJoy route instead of taking a saved key.");
}

static void CheckGremlinFreeOutput(string folder)
{
    string path = Path.Combine(folder, "gremlin.xml");
    var (source, _) = WriteGremlinFixture(path);
    var firstButton = new GremlinOutput(1, "button", 1);
    var current = new GremlinOutput(1, "button", 2);
    var aboveLimit = new GremlinOutput(1, "button", 3);
    var nextDevice = new GremlinOutput(2, "button", 1);
    var profile = GremlinProfile.Load(path, () => [nextDevice, aboveLimit, current, firstButton]);
    var existing = (source, "button", 2, "Default");
    var newInput = (source, "button", 3, "Default");
    var noSavedTarget = new SwgTarget(null, null);

    Require(profile.ChooseTarget(existing, null, noSavedTarget, [], 2).Output == current,
        "Reuse the existing output when it is available.");
    Require(profile.ChooseTarget(existing, null, noSavedTarget, [current], 2).Output == firstButton,
        "A reserved current output must yield to the first free output.");
    Require(profile.ChooseTarget(newInput, null, noSavedTarget, [firstButton], 2).Output == nextDevice,
        "Skip occupied and above-limit outputs before advancing to the next vJoy device.");
    Require(profile.ChooseTarget(newInput, null, noSavedTarget, [firstButton, nextDevice], 2).Output is null,
        "An exhausted profile must report no available output.");
}

static (Guid Source, Guid Curve) WriteGremlinFixture(string path)
{
    var source = Guid.Parse("20000000-0000-0000-0000-000000000001");
    var curveId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    var axisRoute = Route("axis", 1, 1, new XElement("action-id", curveId));
    var buttonRoute = Route("button", 2, 2);
    new XDocument(new XElement("profile", new XAttribute("version", 14),
        new XElement("devices", new XElement("device", new XElement("device-id", source), new XElement("device-name", "Test Stick"))),
        new XElement("modes", new XElement("mode", "Default")),
        new XElement("inputs", axisRoute.Input, buttonRoute.Input),
        new XElement("library", axisRoute.Root, axisRoute.Mapping, buttonRoute.Root, buttonRoute.Mapping,
            new XElement("action", new XAttribute("id", curveId), new XAttribute("type", "response-curve"))))).Save(path);
    return (source, curveId);

    (XElement Input, XElement Root, XElement Mapping) Route(string kind, int output, int input, XElement? transform = null)
    {
        var rootId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();
        return (
            new XElement("input", new XElement("device-id", source), new XElement("input-type", kind), new XElement("mode", "Default"),
                new XElement("input-id", input), new XElement("action-configuration", new XElement("root-action", rootId), new XElement("behavior", kind))),
            new XElement("action", new XAttribute("id", rootId), new XAttribute("type", "root"),
                new XElement("actions", transform, new XElement("action-id", mappingId))),
            new XElement("action", new XAttribute("id", mappingId), new XAttribute("type", "map-to-vjoy"),
                Property("vjoy-device-id", 1), Property("vjoy-input-id", output), Property("vjoy-input-type", kind)));
    }

    static XElement Property(string name, object value) => new("property", new XElement("name", name), new XElement("value", value));
}

static void ExpectIoFailure(Action operation)
{
    try { operation(); }
    catch (IOException) { return; }
    throw new InvalidOperationException("Expected an I/O failure.");
}

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
