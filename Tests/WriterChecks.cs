using System.Text;
using System.Xml.Linq;
using VisualControlsBinder.Games;

internal static class WriterChecks
{
    public static void ReplacedDestination(string folder)
    {
        string first = Path.Combine(folder, "first.xml");
        string second = Path.Combine(folder, "second.xml");
        string external = Path.Combine(folder, "external.xml");
        File.WriteAllText(first, "original one");
        File.WriteAllText(second, "original two");
        File.WriteAllText(external, "external two");

        int validations = 0;
        ExpectIoFailure(() => NativeBindingWrites.Commit([
            new(first, "original one"u8.ToArray(), "planned one"u8.ToArray()),
            new(second, "original two"u8.ToArray(), "planned two"u8.ToArray())
        ], () =>
        {
            if (++validations == 2)
                File.Replace(external, second, null);
        }));
        Check(File.ReadAllText(first) == "original one", "The previously written file should be restored.");
        Check(File.ReadAllText(second) == "external two", "The replacement at the active path must survive.");
    }

    public static void OverlappingExports(string folder)
    {
        string first = Path.Combine(folder, "first.xml");
        string second = Path.Combine(folder, "second.xml");
        File.WriteAllBytes(first, [1]);
        File.WriteAllBytes(second, [1]);
        using var staged = new ManualResetEventSlim();
        using var contenderStarted = new ManualResetEventSlim();
        var timeout = TimeSpan.FromSeconds(10);
        int validations = 0;
        var winner = Task.Run(() => NativeBindingWrites.Commit([new(first, [1], [2]), new(second, [1], [2])], () =>
        {
            if (++validations != 2)
                return;
            staged.Set();
            Check(contenderStarted.Wait(timeout), "The overlapping export failed to start.");
        }));
        Check(staged.Wait(timeout), "The first export did not reach its staged writes.");
        // Reverse the same pair and use different path spelling to exercise the destination identities.
        var contender = Task.Run(() => ExpectIoFailure(() => NativeBindingWrites.Commit([
            new(Path.Combine(folder, ".", "second.xml"), [1], [3]), new(first, [1], [3])
        ], contenderStarted.Set)));
        Task.WhenAll(winner, contender).GetAwaiter().GetResult();

        Check(File.ReadAllBytes(first).SequenceEqual<byte>([2]) && File.ReadAllBytes(second).SequenceEqual<byte>([2]),
            "The overlapping export must not mix results or replace the winner.");
        var backups = Directory.GetFiles(folder, "*.bak");
        Check(backups.Length == 2 && backups.All(path => File.ReadAllBytes(path).SequenceEqual<byte>([1])),
            "Only the winning export should create backups of the original contents.");
    }

    public static void SwgCopies(string folder)
    {
        string ground = Path.Combine(folder, "pilot.inp");
        string space = Path.Combine(folder, "pilot_space.inp");
        // Minimal IFF keymaps, with distinct supported versions so swapped or rewritten copies are visible.
        File.WriteAllBytes(ground, "FORM\0\0\0\u0010IMAPFORM\0\0\0\u00040006"u8.ToArray());
        File.WriteAllBytes(space, "FORM\0\0\0\u0010IMAPFORM\0\0\0\u00040007"u8.ToArray());
        var profile = SwgNativeProfile.Load([ground, space]);
        string copies = Directory.CreateDirectory(Path.Combine(folder, "copies")).FullName;
        profile.SaveAs(copies);
        foreach (string source in new[] { ground, space })
            Check(File.ReadAllBytes(Path.Combine(copies, Path.GetFileName(source))).SequenceEqual(File.ReadAllBytes(source)),
                "SWG copies must keep their game-recognized names and exact contents.");
        ExpectIoFailure(() => profile.SaveAs(copies));

        string competing = Directory.CreateDirectory(Path.Combine(folder, "collision")).FullName;
        string collision = Path.Combine(competing, Path.GetFileName(space));
        int validations = 0;
        ExpectIoFailure(() => NativeBindingWrites.Commit(profile.ToWrites(competing), () =>
        {
            if (++validations == 2)
                File.WriteAllText(collision, "another profile");
        }));
        Check(!File.Exists(Path.Combine(competing, Path.GetFileName(ground))), "A failed SWG copy must not leave half a pair.");
        Check(File.ReadAllText(collision) == "another profile", "The pre-existing Space keymap must survive.");
    }

    public static void StarCitizenWriters(string folder)
    {
        string path = Path.Combine(folder, "layout_test_exported.xml");
        File.WriteAllText(path, "<ActionMaps><actionmap name=\"flight\"><action name=\"fire\" /></actionmap></ActionMaps>");
        var direct = StarCitizenGame.LoadProfile(path);
        var routed = StarCitizenGame.LoadProfile(path);
        direct.Apply("flight", "fire", "js1_button1");
        routed.Apply("flight", "fire", "js1_button2");
        direct.SaveChanges();
        direct.SaveChanges();
        ExpectIoFailure(() => NativeBindingWrites.Commit([routed.ToWrite()!]));
        Check(!Directory.EnumerateFiles(folder, "*.bak").Any(), "Direct saves should retain their backup behavior.");
        var fresh = StarCitizenGame.LoadProfile(path);
        fresh.Apply("flight", "fire", "js1_button3");
        NativeBindingWrites.Commit([fresh.ToWrite()!]);
        direct.Apply("flight", "fire", "js1_button4");
        ExpectIoFailure(direct.SaveChanges);
        var inputs = StarCitizenGame.LoadProfile(path).Bindings.Select(binding => binding.Input).ToArray();
        Check(inputs.SequenceEqual(["js1_button1", "js1_button3"]), "The stale direct save must preserve both committed edits.");
        Check(Directory.GetFiles(folder, "*.bak").Length == 1, "The routed save should keep its original backup.");
    }

    public static void Il2ProfilePair(string folder)
    {
        string actionsPath = Path.Combine(folder, "global.actions");
        string mapPath = Path.Combine(folder, "current.map");
        const string actions = "&actions=action,command,invert|\r\nfire, joy0_b0, 0|\r\ngear, joy0_b1, 0|\r\n";
        const string fire = "action(\"fire\",\"Event\",\"joy7_b0\");\n";
        const string map = "KEYSET(\"keyset_main\");\n" + fire + "action(\"gear\",\"Event\",\"joy7_b1\");\n"
            + "// KEYSET ACTIONS END\nKEYSET(\"other\");\naction(\"fire\",\"Event\",\"other_key\");\n";
        File.WriteAllText(actionsPath, actions, new UTF8Encoding(true));
        File.WriteAllText(mapPath, map);
        var profile = Il2Profile.Load(actionsPath);
        var fireAction = new GameAction("Key Mapping", "fire", "", "", "Fire");
        Check(profile.Apply(fireAction, "joy0_b2"), "The planned button should change both IL-2 files.");

        string copy = Path.Combine(folder, "Offline.actions");
        profile.SaveAs(copy);
        Check(File.ReadAllText(actionsPath) == actions && File.ReadAllText(mapPath) == map, "Saving a copy must leave the loaded pair untouched.");
        string expectedActions = actions.Replace("fire, joy0_b0, 0|", "fire, joy0_b0, 0|\r\nfire, joy0_b2, 0|");
        string expectedMap = map.Replace(fire, fire + "action(\"fire\",\"Event\",\"joy7_b2\");\n");
        byte[] actionBytes = [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(expectedActions)];
        byte[] mapBytes = Encoding.UTF8.GetBytes(expectedMap);
        Check(File.ReadAllBytes(copy).SequenceEqual(actionBytes) && File.ReadAllBytes(Path.ChangeExtension(copy, ".map")).SequenceEqual(mapBytes),
            "The named pair should keep both encodings, line endings, keysets and translated joystick slots.");
        ExpectIoFailure(() => profile.SaveAs(copy));

        profile.SaveChanges();
        Check(File.ReadAllBytes(actionsPath).SequenceEqual(actionBytes) && File.ReadAllBytes(mapPath).SequenceEqual(mapBytes),
            "Updating global.actions must update current.map with the same edits.");
        using (File.Open(mapPath, FileMode.Open, FileAccess.Read, FileShare.None))
            profile.SaveChanges(); // A byte-identical pair needs no disk access.
        Check(profile.Apply(fireAction, "joy0_b2", remove: true), "The loaded profile should accept another edit after saving.");
        File.AppendAllText(mapPath, "// external change\n");
        ExpectIoFailure(profile.SaveChanges);
        Check(File.ReadAllBytes(actionsPath).SequenceEqual(actionBytes) && File.ReadAllText(mapPath) == expectedMap + "// external change\n",
            "An externally changed companion must prevent writing either file.");
        Check(!Directory.EnumerateFiles(folder, "*.bak").Any(), "Direct IL-2 updates should retain their backup behavior.");
    }

    public static void ElitePreset(string folder)
    {
        string source = Path.Combine(folder, "Original.4.2.binds");
        const string xml = """
            <?xml version="1.0" encoding="utf-16"?>
            <Root PresetName="Original">
              <Fire><Primary Device="Keyboard" Key="Key_Space" /><Secondary Device="{NoDevice}" Key="" /></Fire>
              <Gear><Primary Device="Stick" Key="Joy_9"><Modifier Device="Keyboard" Key="Key_Shift" /></Primary></Gear>
            </Root>
            """;
        File.WriteAllText(source, xml, Encoding.Unicode);
        var profile = EliteDangerousProfile.Load(source);
        var stale = EliteDangerousProfile.Load(source);
        var action = profile.Actions.Single(item => item.Id == "Fire");
        Check(profile.Apply(action, "Stick", "Joy_2").Changed, "The empty secondary slot should accept the joystick.");
        string copy = Path.Combine(folder, "New preset.4.2.binds");
        profile.SaveAs(copy);
        ExpectIoFailure(() => profile.SaveAs(copy));
        Check((string?)XDocument.Load(copy).Root?.Attribute("PresetName") == "New preset" && File.ReadAllText(source) == xml,
            "The new filename should set the copy's preset name and leave the source untouched.");
        profile.SaveChanges();
        byte[] saved = File.ReadAllBytes(source);
        Check(saved.AsSpan().StartsWith(Encoding.Unicode.Preamble), "The UTF-16 preset should retain its encoding.");
        var bindings = EliteDangerousProfile.Load(source).Bindings;
        Check(bindings.Any(binding => binding is { Action: "Fire", Device: "Keyboard", Key: "Key_Space" })
            && bindings.Any(binding => binding is { Action: "Fire", Device: "Stick", Key: "Joy_2" })
            && bindings.Any(binding => binding is { Action: "Gear", Key: "Joy_9", HasModifiers: true }),
            "The joystick, keyboard binding and unrelated chord must survive reload.");
        ExpectIoFailure(stale.SaveChanges); // Elite validates even when the in-memory preset has no new edits.
        Check(File.ReadAllBytes(source).SequenceEqual(saved), "A stale Elite writer must leave the active profile intact.");
        Check(profile.Apply(action, "Stick", "Joy_2", remove: true).Changed, "The saved preset should accept a subsequent edit.");
        profile.SaveChanges();
        Check(EliteDangerousProfile.Load(source).Bindings.All(binding => binding.Key != "Joy_2")
            && !Directory.EnumerateFiles(folder, "*.bak").Any(), "A second direct save must use the updated baseline without creating backups.");
    }

    private static void ExpectIoFailure(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected the writer to reject the operation.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
