using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VisualControlsBinder.Games;

internal sealed record NativeBindingWrite(string Path, byte[]? Original, byte[] Content);

internal static class NativeBindingWrites
{
    public static void Commit(IReadOnlyList<NativeBindingWrite> writes, Action? ensureGameClosed = null,
        bool createBackups = true)
    {
        if (writes.Count == 0)
            return;
        writes = writes.Select(write => write with { Path = Path.GetFullPath(write.Path) }).ToArray();
        var paths = writes.Select(write => write.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != writes.Count)
            throw new InvalidOperationException("Two profiles would write to the same binding file.");
        ensureGameClosed?.Invoke();
        var mutexes = new List<Mutex>();
        var locked = new List<FileStream>();
        var committed = new List<(NativeBindingWrite Write, string Stage)>();
        try
        {
            // Acquire every destination in the same order even when two exports list their profiles differently.
            foreach (string path in paths)
                mutexes.Add(LockDestination(path));
            foreach (var write in writes)
            {
                if (write.Original is null)
                {
                    if (File.Exists(write.Path) || Directory.Exists(write.Path))
                        throw new IOException($"A binding file already exists at {write.Path}. Reload before exporting.");
                    continue;
                }
                var file = new FileStream(write.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                locked.Add(file);
                VerifyContents(file, write.Original);
            }

            var staged = writes.Select(write => (Write: write, Stage: Stage(write.Path, write.Content))).ToArray();
            ensureGameClosed?.Invoke();
            foreach (var (write, stage) in staged)
            {
                if (write.Original is null)
                    File.Move(stage, write.Path);
                else
                {
                    // A file open with delete sharing can still be replaced by another program during preview.
                    using var current = File.Open(write.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    VerifyContents(current, write.Original);
                    File.Replace(stage, write.Path, createBackups ? $"{write.Path}.{Guid.NewGuid():N}.bak" : null);
                }
                committed.Add((write, stage));
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var recoveryErrors = new List<Exception>();
            foreach (var (write, stage) in committed.AsEnumerable().Reverse())
            {
                try
                {
                    using var current = File.Open(write.Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                    VerifyContents(current, write.Content);
                    if (write.Original is null)
                        File.Move(write.Path, stage);
                    else
                        File.Replace(Stage(write.Path, write.Original), write.Path, null);
                }
                catch (Exception recovery) when (recovery is IOException or UnauthorizedAccessException)
                {
                    recoveryErrors.Add(recovery);
                }
            }
            if (recoveryErrors.Count > 0)
            {
                string guidance = createBackups && committed.Any(item => item.Write.Original is not null)
                    ? " Check the .bak files beside the updated profiles for the originals."
                    : $" Check the affected profiles: {string.Join(", ", committed.Select(item => item.Write.Path))}.";
                throw new IOException("Export failed and the changes could not be rolled back." + guidance,
                    new AggregateException([error, .. recoveryErrors]));
            }
            throw;
        }
        finally
        {
            foreach (var file in locked)
                file.Dispose();
            foreach (var mutex in mutexes)
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    private static Mutex LockDestination(string path)
    {
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
        var mutex = new Mutex(false, $@"Global\VisualControlsBinder.Binding.{key}");
        try
        {
            if (!mutex.WaitOne(TimeSpan.FromSeconds(5)))
                throw new IOException($"Another export is saving {path}. Try again after it finishes.");
        }
        catch (AbandonedMutexException) { } // The caller owns the mutex and will verify the file.
        catch
        {
            mutex.Dispose();
            throw;
        }
        return mutex;
    }

    private static void VerifyContents(FileStream file, ReadOnlySpan<byte> expected)
    {
        if (file.Length == expected.Length)
        {
            Span<byte> buffer = stackalloc byte[8192];
            while (!expected.IsEmpty)
            {
                var next = buffer[..Math.Min(expected.Length, buffer.Length)];
                file.ReadExactly(next);
                if (!next.SequenceEqual(expected[..next.Length]))
                    break;
                expected = expected[next.Length..];
            }
            if (expected.IsEmpty)
                return;
        }
        throw new IOException($"The binding file changed on disk: {file.Name}. Reload before exporting.");
    }

    private static string Stage(string target, byte[] content)
    {
        string folder = Path.GetDirectoryName(target) ?? throw new ArgumentException("The binding file needs a destination directory.");
        Directory.CreateDirectory(folder);
        string stage = Path.Combine(folder, $".vcp-{Guid.NewGuid():N}.tmp");
        using var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(content);
        file.Flush(flushToDisk: true);
        return stage;
    }
}
