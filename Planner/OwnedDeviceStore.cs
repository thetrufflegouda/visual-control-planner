using System.IO;
using System.Text.Json;

namespace VisualControlsBinder.Planner;

internal sealed class OwnedDeviceStore(string? path = null)
{
    private readonly string _path = Path.GetFullPath(path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisualControlsBinder", "my-devices.json"));

    public HashSet<string> Load()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            var ids = JsonSerializer.Deserialize<string[]>(file);
            if (ids is null || ids.Any(string.IsNullOrWhiteSpace))
                throw new InvalidDataException("The saved device list has an invalid entry.");
            return new HashSet<string>(ids, StringComparer.Ordinal);
        }
        catch (FileNotFoundException) { return new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return new(StringComparer.Ordinal); }
    }

    public HashSet<string> SetOwned(string deviceId, bool owned)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var writeLock = PlanSessionStore.LockForWrite(_path);
        var ids = Load();
        bool changed = owned ? ids.Add(deviceId) : ids.Remove(deviceId);
        if (changed)
            PlanSessionStore.WriteAtomically(_path, JsonSerializer.SerializeToUtf8Bytes(ids.Order(StringComparer.Ordinal)));
        return ids;
    }
}
