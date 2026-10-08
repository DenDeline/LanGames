"""Reject development settings, symbols and developer tooling from app publications."""

import sys
from pathlib import Path


publish_dir = Path(sys.argv[1])
if not publish_dir.is_dir():
    raise SystemExit(f"Publish directory does not exist: {publish_dir}")

excluded_names = {"appsettings.development.json", ".ds_store", "aot-smoke.onnx"}
excluded_suffixes = {".pdb", ".dbg", ".dsym"}
developer_prefixes = {
    "lanpong.botdiagnostics", "lanpong.trainingdata", "lanpong.tests", "lanpong.benchmarks",
    "benchmarkdotnet", "configuredbotdiagnostics", "onnxsmoke", "tunit", "testhost",
    "microsoft.net.test.sdk", "botbenchmarkoptions", "botbenchmarkcommand",
    "botbenchmarklatency", "botbenchmarkfloor", "botbenchmarkworkload",
    "botbenchmarkreport", "botbenchmarkjsonserializercontext",
}
result_directories = {"benchmarkdotnet.artifacts", "testresults"}


def is_developer_payload(path):
    # Known assembly/package/report stems match only at a filename boundary.
    # Custom models, static files and framework assemblies may legitimately
    # contain words such as training, reports or diagnostics in their names.
    parts = [part.lower() for part in path.relative_to(publish_dir).parts]
    return any(part in result_directories
               or any(part == prefix or part.startswith(prefix + ".")
                      for prefix in developer_prefixes)
               for part in parts)


unexpected = [
    path.relative_to(publish_dir)
    for path in publish_dir.rglob("*")
    if path.name.lower() in excluded_names or path.suffix.lower() in excluded_suffixes
    or is_developer_payload(path)
]

if unexpected:
    raise SystemExit("Unexpected release files: " + ", ".join(map(str, unexpected)))

print("Release publication contains no development settings, symbols, diagnostics/test/training/benchmark payloads or tiny smoke fixture")
