using System.Diagnostics;
using System.IO;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public sealed record EliteDangerousBinding(string Action, string Device, string Key, bool HasModifiers = false);
public sealed record EliteDangerousChange(bool Changed, EliteDangerousBinding? Replaced = null);

public sealed class EliteDangerousProfile
{
    private readonly XDocument _document;
    private byte[] _sourceBytes;

    private EliteDangerousProfile(string path, byte[] bytes, XDocument document)
    {
        SourcePath = path;
        _sourceBytes = bytes;
        _document = document;
    }

    public string SourcePath { get; }

    public IReadOnlyList<GameAction> Actions => _document.Root!.Elements()
        .Where(action => Slots(action).Any()).Select(EliteDangerousGame.Describe).ToArray();

    public IReadOnlyList<EliteDangerousBinding> Bindings => _document.Root!.Elements()
        .SelectMany(action => Slots(action).Select(slot => ReadBinding(action.Name.LocalName, slot)))
        .OfType<EliteDangerousBinding>().ToArray();

    public static EliteDangerousProfile Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(fullPath);
        using var source = new MemoryStream(bytes);
        var document = XDocument.Load(source, LoadOptions.PreserveWhitespace);
        if (document.Root?.Name != "Root" || !document.Root.Elements().Any(action => Slots(action).Any()))
            throw new InvalidDataException("Choose an Elite Dangerous controls .binds file.");
        return new EliteDangerousProfile(fullPath, bytes, document);
    }

    public EliteDangerousChange Apply(GameAction action, string device, string key, bool remove = false)
    {
        XElement? element = _document.Root!.Elements().SingleOrDefault(item => item.Name.LocalName == action.Id);
        if (element is null)
            throw new InvalidOperationException($"{action.Name} is unavailable in this preset. Choose a preset for the game version that contains it.");

        XElement[] slots = Slots(element).ToArray();
        var matching = slots.FirstOrDefault(slot => ReadBinding(action.Id, slot) is { HasModifiers: false } binding
            && binding.Device.Equals(device, StringComparison.OrdinalIgnoreCase) && binding.Key == key);
        if (remove)
        {
            if (matching is null)
                return new EliteDangerousChange(false);
            SetBinding(matching, "{NoDevice}", "");
            return new EliteDangerousChange(true);
        }

        if (matching is not null)
            return new EliteDangerousChange(false);
        XElement? destination = element.Element("Binding") ?? slots.FirstOrDefault(slot => ReadBinding(action.Id, slot) is null);
        if (destination is null)
            throw new InvalidOperationException($"{action.Name} already has two inputs. Remove one before assigning another.");
        EliteDangerousBinding? replaced = ReadBinding(action.Id, destination);
        if (replaced?.HasModifiers == true)
            throw new InvalidOperationException($"{action.Name} uses an axis chord in the selected preset. Change that chord in the game's controls.");

        SetBinding(destination, device, key);
        return new EliteDangerousChange(true, replaced);
    }

    public void SaveAs(string path)
    {
        if (!Path.GetExtension(path).Equals(".binds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the new Elite Dangerous preset with a .binds extension.", nameof(path));
        path = Path.GetFullPath(path);
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException($"Choose an existing folder for the Elite Dangerous preset: {path}.");
        var copy = new XDocument(_document);
        copy.Root!.SetAttributeValue("PresetName", EliteDangerousGame.PresetFileName(path));
        NativeBindingWrites.Commit([new NativeBindingWrite(path, null, Serialize(copy))]);
    }

    public void SaveChanges()
    {
        byte[] bytes = Serialize(_document);
        NativeBindingWrites.Commit([new NativeBindingWrite(SourcePath, _sourceBytes, bytes)], EnsureGameClosed, createBackups: false);
        _sourceBytes = bytes;
    }

    private void EnsureGameClosed()
    {
        string install = Path.TrimEndingDirectorySeparator(EliteDangerousGame.DefaultInstallDirectory) + Path.DirectorySeparatorChar;
        string userPresets = Path.TrimEndingDirectorySeparator(EliteDangerousGame.BindingsDirectory) + Path.DirectorySeparatorChar;
        if (!SourcePath.StartsWith(userPresets, StringComparison.OrdinalIgnoreCase)
            && !SourcePath.StartsWith(install, StringComparison.OrdinalIgnoreCase))
            return;

        foreach (string name in new[] { "EliteDangerous64", "EliteDangerous" })
        {
            var games = Process.GetProcessesByName(name);
            foreach (var game in games)
                game.Dispose();
            if (games.Length > 0)
                throw new InvalidOperationException("Close Elite Dangerous before updating its loaded preset. You can still export a new copy.");
        }
    }

    private static IEnumerable<XElement> Slots(XElement action) =>
        action.Elements().Where(element => element.Name.LocalName is "Binding" or "Primary" or "Secondary");

    private static EliteDangerousBinding? ReadBinding(string action, XElement slot) =>
        (string?)slot.Attribute("Device") is { } device && (string?)slot.Attribute("Key") is { Length: > 0 } key
            ? new EliteDangerousBinding(action, device, key, slot.Elements("Modifier").Any()) : null;

    private static void SetBinding(XElement slot, string device, string key)
    {
        slot.SetAttributeValue("Device", device);
        slot.SetAttributeValue("Key", key);
    }

    private static byte[] Serialize(XDocument document)
    {
        using var destination = new MemoryStream();
        document.Save(destination, SaveOptions.DisableFormatting);
        return destination.ToArray();
    }
}
