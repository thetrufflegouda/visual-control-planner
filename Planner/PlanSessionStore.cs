using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace VisualControlsBinder.Planner;

internal sealed record PlanSession(BindingPlan Plan, string? FilePath, string? SavedFingerprint,
    string? GameId, string? DeviceId, string? Layer, string? ControlId)
{
    public int Version { get; init; } = 1;
}

internal sealed class PlanSessionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private string? _revision;

    public PlanSessionStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisualControlsBinder", "working-plan.json"));
    }

    public PlanSession? Load()
    {
        byte[]? bytes = ReadSnapshot();
        _revision = Revision(bytes);
        if (bytes is null)
            return null;

        var session = JsonSerializer.Deserialize<PlanSession>(bytes, Json);
        if (session is not { Version: 1, Plan: not null })
            throw new InvalidDataException("The previous working session uses an unsupported format.");
        bool wasSaved = session.SavedFingerprint is { } saved && session.Plan.Fingerprint() == saved;
        BindingPlan.Normalize(session.Plan);
        return wasSaved ? session with { SavedFingerprint = session.Plan.Fingerprint() } : session;
    }

    public bool IsSnapshotPath(string path) => Path.GetFullPath(path).Equals(_path, StringComparison.OrdinalIgnoreCase);

    public void Save(PlanSession session, bool hasUnsavedChanges = true)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(session, Json);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        using var writeLock = LockForWrite(_path);
        string? current = Revision(ReadSnapshot());
        if (current != _revision)
        {
            if (hasUnsavedChanges)
                throw new InvalidOperationException(
                    "Another window saved the working session since this window loaded it. Use Save plan to keep your changes before closing.");
            return;
        }

        WriteAtomically(_path, bytes);
        _revision = Revision(bytes);
    }

    private byte[]? ReadSnapshot()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var snapshot = new MemoryStream();
            file.CopyTo(snapshot);
            return snapshot.ToArray();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string? Revision(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));

    internal static FileStream LockForWrite(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException error) when (attempt < 9 && (error.HResult & 0xffff) is 32 or 33)
            {
                Thread.Sleep(20);
            }
        }
    }

    internal static void WriteAtomically(string path, ReadOnlySpan<byte> bytes)
    {
        string target = Path.GetFullPath(path);
        string stage = Path.Combine(Path.GetDirectoryName(target)!, $".visual-controls-{Guid.NewGuid():N}.tmp");
        using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            file.Write(bytes);
            file.Flush(flushToDisk: true);
        }
        File.Move(stage, target, overwrite: true);
    }
}
