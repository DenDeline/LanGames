using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LanPong.Bots.Inference;

internal static partial class OnnxRuntimeStartup
{
    private const string DisableTelemetryVariable = "ORT_DISABLE_TELEMETRY";

    // macOS ONNX Runtime's telemetry worker can abort during process shutdown.
    // POSIX telemetry is disabled for this process before the ORT library loads.
    [ModuleInitializer]
    internal static void DisablePosixTelemetry()
    {
        // On macOS Environment.SetEnvironmentVariable updates .NET's view but not
        // libc getenv(), which is what ONNX Runtime checks during native startup.
        // Force the native value even when an inherited value enables telemetry.
        if (OperatingSystem.IsMacOS())
        {
            if (SetEnvMac(DisableTelemetryVariable, "1", 1) != 0)
                throw new InvalidOperationException("Cannot disable ONNX Runtime telemetry.");
        }
        else if (OperatingSystem.IsLinux())
        {
            if (SetEnvLinux(DisableTelemetryVariable, "1", 1) != 0)
                throw new InvalidOperationException("Cannot disable ONNX Runtime telemetry.");
        }
        else return;

        Environment.SetEnvironmentVariable(DisableTelemetryVariable, "1");
    }

    // POSIX setenv copies both UTF-8 strings; no marshalled pointer outlives the call.
    [LibraryImport("libSystem.B.dylib", EntryPoint = "setenv",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnvMac(string name, string value, int overwrite);

    [LibraryImport("libc.so.6", EntryPoint = "setenv",
        StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnvLinux(string name, string value, int overwrite);
}
