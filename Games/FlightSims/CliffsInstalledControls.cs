using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace VisualControlsBinder.Games;

internal static class CliffsInstalledControls
{
    private static readonly IReadOnlyDictionary<ushort, OperandType> Operands = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .GroupBy(op => (ushort)op.Value)
        .ToDictionary(group => group.Key, group => group.First().OperandType);

    private static readonly HashSet<string> CommandMaps = new(StringComparer.Ordinal)
    {
        "common", "commonMove", "pilot", "pilotMove", "turret", "turretMove", "vehicle", "vehicleMove",
        "view", "viewSet", "viewPan", "viewSnap", "timeSpeed", "orders", "gui", "chat", "builder"
    };

    public static string GameAssembly(string installDirectory) => Path.Combine(installDirectory, "parts", "core", "maddox.dll");

    public static IReadOnlyList<(string Map, string Command)> Read(string installDirectory)
    {
        try
        {
            using var source = File.OpenRead(GameAssembly(installDirectory));
            using var executable = new PEReader(source);
            var metadata = executable.GetMetadataReader();
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var methods = metadata.GetTypeDefinition(typeHandle).GetMethods();
                foreach (var handle in methods)
                {
                    var body = GetBody(executable, metadata.GetMethodDefinition(handle));
                    if (body is null || body.Size < 1000)
                        continue;
                    var strings = UserStrings(body, metadata);
                    // The method name is obfuscated. The menu's pilot map and registered command IDs are stable.
                    if (strings.FirstOrDefault() == "pilot" && strings.Contains("trigger01") &&
                        strings.Contains("thtl_plus") && strings.Contains("engselect_all"))
                        return RegisteredCommands(executable, metadata, methods);
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException or InvalidOperationException)
        {
            // Old and customized editions can still supply actions through their controls INIs.
        }
        return [];
    }

    private static (string Map, string Command)[] RegisteredCommands(PEReader executable, MetadataReader metadata,
        MethodDefinitionHandleCollection methods)
    {
        var commands = new List<(string, string)>();
        foreach (var handle in methods)
        {
            var body = GetBody(executable, metadata.GetMethodDefinition(handle));
            if (body is null)
                continue;
            var strings = UserStrings(body, metadata);
            if (strings.FirstOrDefault() is not { } map || !CommandMaps.Contains(map))
                continue;
            // These registration methods contain literal command IDs, numeric ordering labels, and headings.
            foreach (var value in strings.Skip(1))
                if (value.Length > 0 && value != "Config")
                    commands.Add((map, value));
        }
        return commands.ToArray();
    }

    private static MethodBodyBlock? GetBody(PEReader executable, MethodDefinition method) =>
        method.RelativeVirtualAddress == 0 || (method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.IL
            ? null : executable.GetMethodBody(method.RelativeVirtualAddress);

    private static IReadOnlyList<string> UserStrings(MethodBodyBlock body, MetadataReader metadata)
    {
        var strings = new List<string>();
        var code = body.GetILReader();
        while (code.RemainingBytes > 0)
        {
            ushort opcode = code.ReadByte();
            if (opcode == 0xfe)
                opcode = (ushort)(0xfe00 | code.ReadByte());
            if (!Operands.TryGetValue(opcode, out var operand))
                throw new BadImageFormatException("Unknown opcode in the Cliffs of Dover game assembly.");
            if (operand == OperandType.InlineString)
            {
                int token = code.ReadInt32() & 0x00ff_ffff;
                strings.Add(metadata.GetUserString(MetadataTokens.UserStringHandle(token)).Trim());
                continue;
            }
            int size = operand switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => checked(code.ReadInt32() * sizeof(int)),
                _ => 4
            };
            if (size < 0 || size > code.RemainingBytes)
                throw new BadImageFormatException("Invalid operand in the Cliffs of Dover game assembly.");
            code.Offset += size;
        }
        return strings;
    }
}
