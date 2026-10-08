"""Process checks for the managed bot diagnostic tool; no application server is started.

Build the tool separately, then run either:
  LANPONG_DIAGNOSTICS_DLL=/absolute/output/LanPong.BotDiagnostics.dll python3 FILE
  python3 FILE --diagnostics-dll /absolute/output/LanPong.BotDiagnostics.dll
The script uses the installed dotnet host and only standard-library Python modules.
"""

import argparse
import hashlib
import json
import math
import os
import platform
import socket
import subprocess
import sys
import tempfile
from pathlib import Path


MODEL_SHA256 = "5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a"
MODEL_RELATIVE_PATH = Path("Models") / "hard-v1.onnx"
SAMPLES = 100
WARMUP = 2


def diagnostic_environment(extra=None):
    # Keep the caller's runtime search paths, but make each configuration fixture
    # independent of unrelated bot/host provider settings in the shell.
    host_keys = {
        "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_CONTENTROOT", "ASPNETCORE_URLS",
        "DOTNET_ENVIRONMENT", "DOTNET_CONTENTROOT", "DOTNET_URLS",
    }
    environment = {
        key: value for key, value in os.environ.items()
        if key.upper() not in host_keys
        and not key.upper().startswith(("BOTS__", "BOTS:"))
    }
    environment["ASPNETCORE_ENVIRONMENT"] = "Production"
    environment.update(extra or {})
    return environment


def assert_success(result):
    assert result.returncode == 0, (
        f"Diagnostic tool failed (exit {result.returncode}).\n"
        f"stdout:\n{result.stdout}\nstderr:\n{result.stderr}"
    )


def finite_nonnegative(value):
    return type(value) in (int, float) and math.isfinite(value) and value >= 0


def verify_report(result, bot_id, cadence, expected_hash=None):
    assert_success(result)
    assert len(result.stdout.strip().splitlines()) == 1, result.stdout
    report = json.loads(result.stdout)
    assert report["requestedBotId"] == report["effectiveBotId"] == bot_id, report
    assert report["strategyId"] == ("onnx" if expected_hash else "tracker"), report
    assert report["cadenceTicks"] == cadence, report
    assert type(report["dynamicCodeSupported"]) is bool, report
    assert report["verifiedModelSha256"] == expected_hash, report
    assert report["frozenModelDigest"] is (True if expected_hash else None), report
    assert report["samples"] == SAMPLES and report["warmup"] == WARMUP, report
    assert report["scope"] and report["runtime"] and report["architecture"], report
    assert finite_nonnegative(report["preparationMilliseconds"]), report
    workloads = report["workloads"]
    assert [workload["name"] for workload in workloads] == ["consecutive", "cadence-spaced"], report
    previous_tick = 0
    for workload, stride in zip(workloads, (1, cadence)):
        assert workload["count"] == SAMPLES and workload["tickStride"] == stride, workload
        assert type(workload["firstTick"]) is type(workload["lastTick"]) is int, workload
        assert previous_tick < workload["firstTick"] < workload["lastTick"], workload
        if stride == cadence:
            assert workload["firstTick"] % cadence == 0, workload
            assert workload["lastTick"] % cadence == 0, workload
        previous_tick = workload["lastTick"]
        assert all(type(workload[key]) is int and 0 <= workload[key] < 2 ** 64
                   for key in ("batchChecksum", "sampledChecksum")), workload
        for measurement in (workload, workload["timestampFloor"]):
            assert finite_nonnegative(measurement["batchMeanMilliseconds"]), measurement
            assert type(measurement["batchAllocatedBytes"]) is int and measurement["batchAllocatedBytes"] >= 0, measurement
            latency = measurement["latency"]
            values = [latency[key] for key in
                      ("medianMilliseconds", "p95Milliseconds", "p99Milliseconds", "worstMilliseconds")]
            assert all(finite_nonnegative(value) for value in values) and values == sorted(values), latency
            assert type(latency["allocatedBytes"]) is int and latency["allocatedBytes"] >= 0, latency
    return report


def verify_rejection(result, expected_detail):
    assert result.returncode != 0, (result.stdout, result.stderr)
    assert not result.stdout.strip(), result.stdout
    assert expected_detail in result.stderr, result.stderr


def verify_onnx_smoke(dll):
    model = dll.parent / "Models" / "aot-smoke.onnx"
    assert model.is_file() and model.stat().st_size > 0, f"Tool smoke model is missing: {model}"
    library_name = {
        "Darwin": "libonnxruntime.dylib",
        "Linux": "libonnxruntime.so",
        "Windows": "onnxruntime.dll",
    }[platform.system()]
    libraries = [path for path in dll.parent.rglob(library_name) if path.is_file()]
    assert libraries, f"Tool ONNX Runtime native library is missing: {library_name}"
    # The smoke model resolves beside the tool even when cwd is elsewhere.
    with tempfile.TemporaryDirectory(prefix="lanpong-tool-smoke-") as cwd:
        result = subprocess.run(
            ["dotnet", str(dll), "--onnx-smoke"], cwd=cwd,
            env=diagnostic_environment(), capture_output=True, text=True,
            timeout=30, check=False,
        )
    assert_success(result)
    assert result.stdout.strip() == "ONNX smoke passed: 2 -> 3", result.stdout
    print(f"PASS: managed tool tiny ONNX inference returned 2 -> 3 with {libraries[0].name}")


def verify_configured_benchmark(dll):
    output_root = dll.parent
    settings_path = output_root / "appsettings.json"
    assert settings_path.is_file(), f"Tool configuration is missing: {settings_path}"
    settings = json.loads(settings_path.read_text(encoding="utf-8"))
    bots = settings["Bots"]
    entries = bots["Entries"]
    by_id = {entry["Id"]: entry for entry in entries}
    assert {"lada", "iskra", "vektor"} <= by_id.keys(), bots
    indices = {entry["Id"]: index for index, entry in enumerate(entries)}
    model = output_root / MODEL_RELATIVE_PATH
    assert model.is_file() and model.stat().st_size > 0, f"Tool trained model is missing: {model}"
    assert by_id["vektor"]["Onnx"]["ModelPath"] == MODEL_RELATIVE_PATH.as_posix(), by_id["vektor"]
    assert by_id["vektor"]["Onnx"]["ExpectedSha256"] == MODEL_SHA256, by_id["vektor"]
    assert hashlib.sha256(model.read_bytes()).hexdigest() == MODEL_SHA256, model
    assert by_id["vektor"]["FallbackBotId"] == "lada", by_id["vektor"]
    assert by_id["lada"]["Enabled"] and by_id["lada"]["StrategyId"] == "tracker", by_id["lada"]
    default_id = bots["DefaultBotId"]
    default_cadence = by_id[default_id]["Tracker"]["ObservationIntervalTicks"]

    # Every configured diagnostic is given an occupied endpoint. Accidental
    # application startup cannot bind it, and no test creates an app process.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as occupied:
        occupied.bind(("127.0.0.1", 0))
        occupied.listen()
        url = f"http://127.0.0.1:{occupied.getsockname()[1]}"
        with tempfile.TemporaryDirectory(prefix="lanpong-tool-providers-") as temporary:
            diagnostic_cwd = Path(temporary)

            def run(arguments, extra_env=None, content_root=output_root, cwd=diagnostic_cwd):
                root_arguments = [] if content_root is None else ["--contentRoot", str(content_root)]
                return subprocess.run(
                    ["dotnet", str(dll), "--bot-benchmark", *arguments,
                     "--benchmark-samples", str(SAMPLES), "--benchmark-warmup", str(WARMUP),
                     *root_arguments, "--urls", url],
                    cwd=cwd, env=diagnostic_environment(extra_env), capture_output=True,
                    text=True, timeout=60, check=False,
                )

            # No explicit root uses the normal cwd, and no ID uses the catalog default.
            verify_report(run([], content_root=None, cwd=output_root), default_id, default_cadence)
            iskra_cadence = f"--Bots:Entries:{indices['iskra']}:Tracker:ObservationIntervalTicks"
            lada_cadence = f"--Bots:Entries:{indices['lada']}:Tracker:ObservationIntervalTicks"
            model_path = f"--Bots:Entries:{indices['vektor']}:Onnx:ModelPath"
            model_hash = f"--Bots:Entries:{indices['vektor']}:Onnx:ExpectedSha256"
            verify_report(run([iskra_cadence, "4"], {"Bots__DefaultBotId": "iskra"}), "iskra", 4)
            verify_report(run(["lada", lada_cadence, "3"], {"Bots__DefaultBotId": "iskra"}), "lada", 3)
            verify_report(run(["vektor"]), "vektor",
                          by_id["vektor"]["Onnx"]["InferenceCadenceTicks"], MODEL_SHA256)

            # Load environment-specific JSON from a root distinct from cwd and
            # tool output. The model's unique relative path exists only in this root.
            provider_root = diagnostic_cwd / "configuration-provider"
            provider_root.mkdir()
            (provider_root / "appsettings.json").write_bytes(settings_path.read_bytes())
            (provider_root / "Models").mkdir()
            (provider_root / "Models" / "provider-policy.onnx").write_bytes(model.read_bytes())
            (provider_root / "appsettings.DiagnosticProviderSmoke.json").write_text(json.dumps({
                "Bots": {"DefaultBotId": "iskra", "Entries": {
                    str(indices["iskra"]): {"Tracker": {"ObservationIntervalTicks": 6}},
                    str(indices["vektor"]): {"Onnx": {
                        "ModelPath": "Models/provider-policy.onnx", "InferenceCadenceTicks": 11,
                    }},
                }}
            }), encoding="utf-8")
            provider_environment = {
                "ASPNETCORE_ENVIRONMENT": "DiagnosticProviderSmoke",
                "ASPNETCORE_CONTENTROOT": str(provider_root),
            }
            verify_report(run([], provider_environment, content_root=None), "iskra", 6)
            verify_report(run(["vektor"], provider_environment, content_root=None), "vektor", 11, MODEL_SHA256)
            verify_report(run(["--environment", "Production"], provider_environment,
                              content_root=None), default_id, default_cadence)

            # Play may use the explicit healthy fallback, but a selected-model
            # diagnostic must reject the changed identity instead of measuring it.
            missing = diagnostic_cwd / "missing-selected-model.onnx"
            verify_rejection(run(["vektor", model_path, str(missing)]), "fallback")
            corrupt = diagnostic_cwd / "corrupt-selected-model.onnx"
            corrupt_bytes = bytearray(model.read_bytes())
            corrupt_bytes[len(corrupt_bytes) // 2] ^= 0xFF
            corrupt.write_bytes(corrupt_bytes)
            verify_rejection(run(["vektor", model_path, str(corrupt)]), "fallback")
            # A matching digest cannot make the tiny one-value model conform to
            # the production observation[1,10]/logits[1,3] schema.
            tiny = output_root / "Models" / "aot-smoke.onnx"
            verify_rejection(run(["vektor", model_path, str(tiny), model_hash,
                                  hashlib.sha256(tiny.read_bytes()).hexdigest()]), "fallback")
            verify_rejection(run(["--Bots:DefaultBotId", "unknown-diagnostic-bot"]), "Bots:DefaultBotId")
            verify_rejection(run([iskra_cadence, "0"]), iskra_cadence[2:])
            # Failures are local to their processes; a fresh normal selection remains healthy.
            verify_report(run(["vektor"]), "vektor",
                          by_id["vektor"]["Onnx"]["InferenceCadenceTicks"], MODEL_SHA256)
    print("PASS: managed configured tool honors default/explicit IDs and normal providers, "
          "verifies model identity, rejects fallback/invalid settings, and starts no HTTP listener")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--diagnostics-dll", default=os.environ.get("LANPONG_DIAGNOSTICS_DLL"))
    arguments = parser.parse_args(argv)
    if not arguments.diagnostics_dll:
        parser.error("Set LANPONG_DIAGNOSTICS_DLL or pass --diagnostics-dll")
    dll = Path(arguments.diagnostics_dll).resolve()
    if not dll.is_file() or dll.suffix.lower() != ".dll":
        raise FileNotFoundError(f"Managed diagnostic DLL does not exist: {dll}")
    verify_onnx_smoke(dll)
    verify_configured_benchmark(dll)
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"Bot diagnostic smoke failed: {error}", file=sys.stderr)
        raise SystemExit(1)
