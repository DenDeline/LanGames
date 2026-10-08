using LanPong.BotDiagnostics;
using LanPong.Bots.Inference;
using Microsoft.Extensions.Options;

const string help = """
LanPong developer bot diagnostics (no gameplay or HTTP listener is started).

Commands:
  --onnx-smoke
      Run the tiny packaged ONNX probe: 2 -> 3.
  --bot-benchmark [botId] [--benchmark-samples N] [--benchmark-warmup N]
      Measure the configured bot; omitted botId selects the catalog default.
      Samples: 100..100000 (default 20000). Warmup: 2..100000 (default 20000).
  --help, -h
      Show this help.

Configured probes accept the normal ASP.NET configuration arguments and providers.
Content root defaults to the working directory, subject to normal provider precedence.
Explicit relative content roots resolve against the tool's binary directory;
use an absolute path when launching from the repository root.
From the repository root, for example:
  dotnet run --project tools/LanPong.BotDiagnostics -c Release -- --onnx-smoke
  dotnet run --project tools/LanPong.BotDiagnostics -c Release -- --bot-benchmark vektor --contentRoot "$PWD/tools/LanPong.BotDiagnostics/bin/Release/net10.0"
Use an application content root instead when measuring a custom catalog.
""";

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    Console.WriteLine(help);
    return 0;
}

try
{
    // A tool-only ORT access must not depend on when the referenced app module initializes.
    OnnxRuntimeStartup.DisablePosixTelemetry();
    if (args is ["--onnx-smoke"])
    {
        OnnxSmoke.Run();
        return 0;
    }
    if (ConfiguredBotDiagnostics.ParseCommand(args) is { } benchmark)
    {
        ConfiguredBotDiagnostics.Run(benchmark, Console.Out);
        return 0;
    }
    Console.Error.WriteLine("Unknown diagnostic command. Choose --onnx-smoke or --bot-benchmark.");
    Console.Error.WriteLine(help);
    return 2;
}
catch (Exception error)
{
    Console.Error.WriteLine(error is ArgumentException or InvalidOperationException or OptionsValidationException
        ? $"Bot diagnostics failed: {error.Message}"
        : "Bot diagnostics failed during configuration or inference.");
    return 1;
}
