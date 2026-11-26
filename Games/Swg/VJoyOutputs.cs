using System.IO;
using System.Runtime.InteropServices;

namespace VisualControlsBinder.Games;

internal static class VJoyOutputs
{
    private static readonly object Sync = new();
    private static NativeApi? sdk;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DriverEnabled();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeviceExists(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CountButtons(uint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AxisExists(uint device, uint usage);

    public static IReadOnlyList<GremlinOutput>? Read()
    {
        lock (Sync)
        {
            var api = sdk ??= FindApi();
            if (api is null || api.Enabled() == 0)
                return null;

            var outputs = new List<GremlinOutput>();
            for (var deviceId = 1; deviceId <= 16; deviceId++)
            {
                if (api.Exists((uint)deviceId) == 0)
                    continue;

                var buttonCount = Math.Clamp(api.Buttons((uint)deviceId), 0, 128);
                for (var button = 1; button <= buttonCount; button++)
                    outputs.Add(new GremlinOutput(deviceId, "button", button));

                for (var axis = 1; axis <= 8; axis++)
                    if (api.Axis((uint)deviceId, (uint)(0x30 + axis - 1)) == 1)
                        outputs.Add(new GremlinOutput(deviceId, "axis", axis));
            }

            return outputs;
        }
    }

    private static NativeApi? FindApi()
    {
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] sdkPaths =
        [
            Path.Combine(programFiles, "vJoy", "x64", "vJoyInterface.dll"),
            Path.Combine(programFiles, "vJoy", "amd64", "vJoyInterface.dll")
        ];
        foreach (string sdkPath in sdkPaths)
        {
            if (!File.Exists(sdkPath) || !NativeLibrary.TryLoad(sdkPath, out IntPtr module))
            {
                continue;
            }

            if (NativeLibrary.TryGetExport(module, "vJoyEnabled", out IntPtr enabled)
                && NativeLibrary.TryGetExport(module, "isVJDExists", out IntPtr configured)
                && NativeLibrary.TryGetExport(module, "GetVJDButtonNumber", out IntPtr count)
                && NativeLibrary.TryGetExport(module, "GetVJDAxisExist", out IntPtr exists))
            {
                // An initialized vendor DLL must stay loaded while profiles are repeatedly read.
                return new NativeApi(
                    Marshal.GetDelegateForFunctionPointer<DriverEnabled>(enabled),
                    Marshal.GetDelegateForFunctionPointer<DeviceExists>(configured),
                    Marshal.GetDelegateForFunctionPointer<CountButtons>(count),
                    Marshal.GetDelegateForFunctionPointer<AxisExists>(exists));
            }

            NativeLibrary.Free(module);
        }
        return null;
    }

    private sealed record NativeApi(DriverEnabled Enabled, DeviceExists Exists, CountButtons Buttons, AxisExists Axis);
}
