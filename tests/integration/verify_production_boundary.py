"""Verify the evaluated production project and restored graph exclude developer tools."""

import argparse
import json
import re
import subprocess
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "src" / "LanPong"
TOOL = ROOT / "tools" / "LanPong.BotDiagnostics"
FORBIDDEN = ("lanpong.botdiagnostics", "lanpong.trainingdata", "lanpong.tests",
             "lanpong.benchmarks", "benchmarkdotnet", "tunit", "testhost",
             "microsoft.net.test.sdk")
DIAGNOSTIC_TYPES = re.compile(r"\b(?:ConfiguredBotDiagnostics|OnnxSmoke|BotBenchmark\w*|BenchmarkDotNet)\b")


def evaluate(project, runtime=None):
    arguments = ["dotnet", "msbuild", str(project), "-property:Configuration=Release",
                 "-getItem:Compile,Content,ProjectReference,PackageReference,FrameworkReference",
                 "-getProperty:ProjectAssetsFile,MSBuildProjectDirectory,Configuration,RuntimeIdentifier"]
    if runtime:
        arguments.append(f"-property:RuntimeIdentifier={runtime}")
    result = subprocess.run(
        arguments,
        cwd=ROOT, capture_output=True, text=True, check=True, timeout=30,
    )
    evaluated = json.loads(result.stdout)
    assert evaluated["Properties"]["Configuration"] == "Release", evaluated["Properties"]
    if runtime:
        assert evaluated["Properties"]["RuntimeIdentifier"] == runtime, evaluated["Properties"]
    return evaluated


def verify(runtime=None):
    app = evaluate(APP / "LanPong.csproj", runtime)
    tool = evaluate(TOOL / "LanPong.BotDiagnostics.csproj")
    items = app["Items"]
    assert not items["ProjectReference"], items["ProjectReference"]
    for item in items["Compile"]:
        source = Path(item["FullPath"])
        assert source.is_relative_to(APP), source
        assert not DIAGNOSTIC_TYPES.search(source.read_text()), source
    for item in items["Content"]:
        identity = item["Identity"].lower()
        assert "aot-smoke" not in identity and not any(name in identity for name in FORBIDDEN), item
    for item in items["PackageReference"]:
        assert not any(name in item["Identity"].lower() for name in FORBIDDEN), item

    assets_path = Path(app["Properties"]["ProjectAssetsFile"])
    if not assets_path.is_absolute():
        assets_path = APP / assets_path
    assets = json.loads(assets_path.read_text())
    if runtime:
        assert any(target.endswith("/" + runtime) for target in assets["targets"]), assets["targets"].keys()
    for name, library in assets["libraries"].items():
        assert library["type"] != "project", (name, library)
        assert not any(marker in name.lower() for marker in FORBIDDEN), name
    for target in assets["targets"].values():
        for name, dependency in target.items():
            assert not any(marker in name.lower() for marker in FORBIDDEN), name
            for edge in dependency.get("dependencies", {}):
                assert not any(marker in edge.lower() for marker in FORBIDDEN), (name, edge)

    program = (APP / "Program.cs").read_text()
    for dispatch in ("--onnx-smoke", "--bot-benchmark", "--benchmark-samples", "--benchmark-warmup"):
        assert dispatch not in program, dispatch
    assert not DIAGNOSTIC_TYPES.search(program)
    assert not (APP / "Models" / "aot-smoke.onnx").exists()
    assert not (APP / "ConfiguredBotDiagnostics.cs").exists()
    assert not (APP / "OnnxSmoke.cs").exists()
    assert (APP / "Bots" / "Inference" / "OnnxRuntimeStartup.cs").exists()

    references = tool["Items"]["ProjectReference"]
    assert len(references) == 1 and Path(references[0]["FullPath"]) == APP / "LanPong.csproj", references
    assert any(item["Identity"] == "Microsoft.AspNetCore.App"
               for item in tool["Items"]["FrameworkReference"])
    assert (TOOL / "Models" / "aot-smoke.onnx").is_file()
    tool_program = (TOOL / "Program.cs").read_text()
    assert "OnnxRuntimeStartup.DisablePosixTelemetry()" in tool_program

    print("PASS: production Compile/Content/direct references and restored dependency graph exclude diagnostic/report types, tool/test/training/benchmark dependencies and the tiny model")
    print("PASS: production dispatch is absent; diagnostics reference the app one way and explicitly initialize the shared native telemetry guard")
    return {"application": app, "diagnostics": tool,
            "resolvedApplicationLibraries": sorted(assets["libraries"]),
            "resolvedTargetNames": sorted(assets["targets"]), "projectAssetsFile": str(assets_path)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--runtime", help="Evaluate the application with the publication runtime identifier.")
    args = parser.parse_args()
    proof = verify(args.runtime)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(proof, indent=2) + "\n")


if __name__ == "__main__":
    main()
