using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

internal static class MsfsCloudBindingWrites
{
    private const string HelperName = "xgamesaveutil.exe";
    // XGameSave's 256 MB quota is encoded as base64 in the helper's export.
    private const int MaxSaveXmlLength = 384 * 1024 * 1024;
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static string? TryGetUnavailableReason(bool is2024)
    {
        try
        {
            _ = FindContext(is2024);
            _ = FindHelper();
            return null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            return error.Message;
        }
    }

    public static string? Commit(IReadOnlyList<NativeBindingWrite> changes, bool is2024)
    {
        if (changes.Count == 0)
            return null;
        var context = FindContext(is2024);
        var helper = FindHelper();
        var pending = changes.Where(change => change.Original is null || !change.Content.AsSpan().SequenceEqual(change.Original)).ToArray();
        if (pending.Length == 0)
            return null;

        MsfsBindingProfile.EnsureGameClosed();
        CheckOriginalFiles(context, pending);
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VisualControlsBinder", "Backups", is2024 ? "MSFS2024" : "MSFS2020", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string backup = Path.Combine(folder, "before-export.xml");
        RunHelper(helper, Arguments("export", backup, is2024, context.Scid), folder);
        XDocument import = BuildImport(ReadSave(backup), pending, context.Scid);
        string target = Path.Combine(folder, "binding-containers-only.xml");
        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            import.Save(output, SaveOptions.DisableFormatting);

        MsfsBindingProfile.EnsureGameClosed();
        CheckOriginalFiles(context, pending);
        try
        {
            RunHelper(helper, Arguments("import", target, is2024, context.Scid), folder);
            string verified = Path.Combine(folder, "after-export.xml");
            MsfsBindingProfile.EnsureGameClosed();
            RunHelper(helper, Arguments("export", verified, is2024, context.Scid), folder);
            VerifyImport(import, ReadSave(verified), context.Scid);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            throw new IOException($"The Xbox Game Save update could not be verified. Recovery files are in {folder}. {error.Message}", error);
        }
        return folder;
    }

    internal static XDocument BuildImport(XDocument exported, IReadOnlyList<NativeBindingWrite> changes, Guid scid)
    {
        if (changes.Count == 0 || changes.Any(change => change.Original is null))
            throw new InvalidOperationException("Choose existing Xbox controller presets before exporting.");
        var import = new XDocument(exported);
        var containers = GetContainers(import, scid);
        var entries = containers.Values.SelectMany(container => GetBlobs(container).Values.Select(blob => (Container: container, Blob: blob)))
            .Select(item => (item.Container, item.Blob, Data: ReadBlob(item.Blob))).ToArray();
        var edited = new HashSet<XElement>();
        var kept = new HashSet<XElement>();
        foreach (var change in changes)
        {
            var matches = entries.Where(item => item.Data.AsSpan().SequenceEqual(change.Original)).Take(2).ToArray();
            if (matches.Length != 1 || !edited.Add(matches[0].Blob))
                throw new InvalidDataException("A selected control profile does not match exactly one Xbox save blob. Reload the profile and try again.");
            var (container, blob, _) = matches[0];
            kept.Add(container);
            blob.ReplaceNodes(new XCData(Convert.ToBase64String(change.Content)));
        }
        foreach (var container in containers.Values.Where(container => !kept.Contains(container)))
            container.Remove();
        return import;
    }

    internal static string[] Arguments(string verb, string file, bool is2024, Guid scid) =>
        verb is "export" or "import" && scid != Guid.Empty
            ? [verb, file, $"/pfn:{Package(is2024)}", $"/scid:{scid:D}"]
            : throw new ArgumentException("Invalid Xbox Game Save command.");

    private static void VerifyImport(XDocument import, XDocument verified, Guid scid)
    {
        var actual = GetContainers(verified, scid);
        foreach (var (name, expectedContainer) in GetContainers(import, scid))
        {
            if (!actual.TryGetValue(name, out var actualContainer))
                throw new InvalidDataException("The updated Xbox save container was missing after import.");
            var expectedBlobs = GetBlobs(expectedContainer);
            var actualBlobs = GetBlobs(actualContainer);
            if (expectedBlobs.Count != actualBlobs.Count || expectedBlobs.Any(expected =>
                !actualBlobs.TryGetValue(expected.Key, out var blob) || !ReadBlob(expected.Value).AsSpan().SequenceEqual(ReadBlob(blob))))
                throw new InvalidDataException("The Xbox save container did not round-trip with its complete contents.");
        }
    }

    private static Dictionary<string, XElement> GetContainers(XDocument document, Guid scid)
    {
        var root = document.Root;
        if (root?.Name != "XbConnectedStorageSpace")
            throw new InvalidDataException("The Xbox Game Save helper produced an unexpected export format.");
        if ((string?)root.Element("ContextDescription")?.Element("Title")?.Attribute("scid") is { } exported &&
            (!Guid.TryParse(exported, out var id) || id != scid))
            throw new InvalidDataException("The Xbox Game Save export belongs to another title.");
        var containers = root.Element("Data")?.Element("Containers")
            ?? throw new InvalidDataException("The Xbox Game Save export did not contain a container index.");
        return NamedElements(containers, "Container");
    }

    private static Dictionary<string, XElement> GetBlobs(XElement container)
    {
        var blobs = container.Element("Blobs") ?? throw new InvalidDataException("An Xbox save container did not list its blobs.");
        return NamedElements(blobs, "Blob");
    }

    private static Dictionary<string, XElement> NamedElements(XElement parent, string elementName)
    {
        var result = new Dictionary<string, XElement>(IgnoreCase);
        foreach (var entry in parent.Elements(elementName))
        {
            string? name = (string?)entry.Attribute("name");
            if (string.IsNullOrWhiteSpace(name) || !result.TryAdd(name, entry))
                throw new InvalidDataException("The Xbox Game Save export contains an ambiguous object name.");
        }
        return result;
    }

    private static byte[] ReadBlob(XElement blob)
    {
        if (blob.HasElements)
            throw new InvalidDataException("An Xbox save blob was not encoded as base64.");
        try { return Convert.FromBase64String(blob.Value); }
        catch (FormatException error) { throw new InvalidDataException("An Xbox save blob contains invalid base64.", error); }
    }

    private static XDocument ReadSave(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSaveXmlLength
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException error) { throw new InvalidDataException("Could not read the Xbox Game Save export.", error); }
    }

    private static string Package(bool is2024) =>
        is2024 ? "Microsoft.Limitless_8wekyb3d8bbwe" : "Microsoft.FlightSimulator_8wekyb3d8bbwe";

    private static SaveContext FindContext(bool is2024)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", Package(is2024), "SystemAppData", "wgs");
        if (!Directory.Exists(root))
            throw new InvalidOperationException("No local Microsoft Flight Simulator Xbox saves are available.");
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var matches = Directory.EnumerateDirectories(root, "*", options)
            .Select(ReadContext).OfType<SaveContext>().Take(2).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidOperationException("VCP needs exactly one local Xbox save context for this edition of Microsoft Flight Simulator.");
    }

    private static SaveContext? ReadContext(string path)
    {
        string[] name = Path.GetFileName(path).Split('_');
        return name is [var account, var service] && IsHex(account, 16) && IsHex(service, 32) &&
            Guid.TryParseExact(service, "N", out var scid) && scid != Guid.Empty &&
            File.Exists(Path.Combine(path, "containers.index")) ? new SaveContext(path, scid) : null;
    }

    private static void CheckOriginalFiles(SaveContext context, IReadOnlyList<NativeBindingWrite> changes)
    {
        var checkedPaths = new HashSet<string>(IgnoreCase);
        foreach (var change in changes)
        {
            string path = Path.GetFullPath(change.Path);
            string[] relative = Path.GetRelativePath(context.Root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relative is not [var container, var blob] || !IsHex(container, 32) || !IsHex(blob, 32) ||
                change.Original is null || !checkedPaths.Add(path) ||
                (File.GetAttributes(Path.GetDirectoryName(path)!) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A selected file is not an existing controller blob in this Microsoft Flight Simulator Xbox save context.");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length != change.Original.Length)
                throw StaleProfile();
            byte[] bytes = new byte[change.Original.Length];
            stream.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(change.Original))
                throw StaleProfile();
        }
    }

    private static IOException StaleProfile() => new("An Xbox controller profile changed on disk. Reload it before exporting.");

    private static bool IsHex(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigit);

    private static string FindHelper()
    {
        foreach (string folder in HelperFolders().Distinct(IgnoreCase))
        {
            string candidate = Path.Combine(folder, HelperName);
            if (File.Exists(candidate) && IsMicrosoftSigned(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Microsoft's signed xgamesaveutil.exe from the Windows PC Game Development Kit is required to update Xbox saves.");
    }

    private static IEnumerable<string> HelperFolders()
    {
        foreach (string name in new[] { "GameDK", "GameDKCoreLatest" })
            if (AbsoluteFolder(Environment.GetEnvironmentVariable(name)) is { } sdk)
                yield return Path.Combine(sdk, "bin");
        foreach (var location in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            if (AbsoluteFolder(Environment.GetFolderPath(location)) is { } programs)
                yield return Path.Combine(programs, "Microsoft GDK", "bin");
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (AbsoluteFolder(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))) is { } directory)
                yield return directory;
    }

    private static string? AbsoluteFolder(string? folder)
    {
        try
        {
            return folder is not null && Path.IsPathFullyQualified(folder)
                ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) : null;
        }
        catch (ArgumentException) { return null; }
    }

    private static void RunHelper(string executable, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            using var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!IsMicrosoftSigned(executable))
                throw new InvalidOperationException("The Xbox Game Save helper could not be verified.");
            using var process = Process.Start(start) ?? throw new IOException("Could not start Microsoft's Xbox Game Save helper.");
            var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            process.WaitForExit();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new IOException($"Microsoft's Xbox Game Save helper reported an error (0x{process.ExitCode:X8}).");
        }
        catch (Win32Exception error) { throw new IOException("Could not start Microsoft's Xbox Game Save helper.", error); }
    }

    private static bool IsMicrosoftSigned(string executable)
    {
        IntPtr filename = Marshal.StringToHGlobalUni(executable);
        IntPtr fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = filename };
            Marshal.StructureToPtr(file, fileInfo, false);
            var data = new TrustData
            {
                Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1,
                File = fileInfo, ProviderFlags = 0x1010
            };
            var policy = new Guid("00aac56b-cd44-11d0-8cc2-00c04fc295ee");
            if (WinVerifyTrust(new IntPtr(-1), ref policy, ref data) != 0)
                return false;
            using var signer = X509Certificate.CreateFromSignedFile(executable);
            using var certificate = new X509Certificate2(signer);
            return IgnoreCase.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), "Microsoft Corporation");
        }
        catch (CryptographicException) { return false; }
        finally
        {
            Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filename);
        }
    }

    private sealed record SaveContext(string Root, Guid Scid);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public IntPtr Path, Handle, Subject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyData, SipData;
        public uint UiChoice, RevocationChecks, UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData, Url;
        public uint ProviderFlags, UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid policy, ref TrustData data);
}
