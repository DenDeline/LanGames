# Historical Hard runtime and acceptance (original bot plan steps 7–8)

This document records the original Simple/Hard runtime and version 7 browser
acceptance. Its model, training/evaluator results, commands and measurements
remain historical evidence. The current app selects configured catalog entries
by `botId` and uses version 9 browser/API/catalog and UDP contracts. See the
[current tooling, contracts and migration guide](../bot-runtime-cleanup/current-guide.md)
and [catalog configuration guide](../bot-catalog/configuration.md) for current
runtime/operator behavior. The [configured-runtime measurements](../bot-catalog/performance.md)
are also historical. `HardLocalOpponentController`, `SimpleLocalOpponentController`,
`--hard-smoke` and `--hard-benchmark` were removed; their original commands below
are unavailable in the current app. `--onnx-smoke` and `--bot-benchmark` now belong
to the separate managed `tools/LanPong.BotDiagnostics` tool, not the published app.

At the original acceptance, the app packaged the frozen [Hard v1 model](BOT_MODEL.md) at
`Models/hard-v1.onnx` beside the executable. The expected SHA-256 is
`5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a`.
The then-production controller checked this hash and the exact float32
`observation[1,10]` and `logits[1,3]` metadata before using the model. It loaded
one ONNX Runtime CPU session on the first Hard start, warmed it once, and reused
the session and bound input/output buffers across decisions and rematches.
Ordinary app startup and LAN play did not load the model.

Hard decided on absolute engine ticks divisible by nine, held its last axis
between decisions, and returned a neutral axis during countdown. The output
classes map to up/stay/down, with stay winning a logit tie. These rules match
the training evaluator's policy-owned countdown protocol. If loading or
inference failed, the controller switched to Simple for the rest of its lifetime
and recorded the failure reason. The local game clock continued, and an internal
session status reported that fallback to its caller. The browser offered
separate Simple and Hard actions. Version 7 HTTP/WebSocket snapshots distinguished
the requested Hard mode from the actual Simple controller after fallback and
included a fallback flag. The page kept a visible fallback notice through play,
game over, and rematch; leaving cleared it.

## Historical runtime checks and legacy diagnostics

The original reproduction commands below ran from the repository root, after
restoring the .NET dependencies. This is a historical command block, including
the deleted legacy diagnostic entry points. Current integration scripts cover
the v9 contracts, and running them today does not reproduce the old Simple/Hard
UI acceptance. Use the [current guide](../bot-runtime-cleanup/current-guide.md)
for supported tool, publication and smoke commands.

```bash
dotnet test --solution LanPong.slnx -c Release --no-restore
dotnet run --project src/LanPong/LanPong.csproj -c Release -- --hard-smoke
dotnet run --project src/LanPong/LanPong.csproj -c Release -- --hard-benchmark
dotnet publish src/LanPong/LanPong.csproj -c Release -r osx-arm64 \
  -o .artifacts/publish/hard-step8-osx-arm64 --no-restore
LANPONG_TEST_BINARY="$PWD/.artifacts/publish/hard-step8-osx-arm64/LanPong" \
  python3 tests/integration/published_smoke_test.py
LANPONG_TEST_BINARY="$PWD/.artifacts/publish/hard-step8-osx-arm64/LanPong" \
  python3 tests/integration/integration_test.py
.artifacts/publish/hard-step8-osx-arm64/LanPong --hard-benchmark
```

The original published smoke checked the actual model's SHA-256 and inference,
the existing tiny Native AOT ONNX probe, and HTTP startup. The release workflow
configured smoke for macOS ARM64, Linux x64, and Windows x64; the local
execution evidence below is macOS ARM64. The legacy benchmark performed
1,000 warmup decisions and measures 10,000 model decisions
on finite synthetic engine states, one per nine-tick cadence. Allocation is
measured with `GC.GetAllocatedBytesForCurrentThread` and is **managed**
allocation on that thread, not native ONNX memory use. The path is an explicit
diagnostic command; normal game ticks have no timing instrumentation.

At original Step 7 on the development macOS ARM64 host, the Native AOT executable
passed the published smoke and the full two-process local/LAN integration suite. Its
10,000-decision benchmark measured p95 **0.0025 ms**,
p99 **0.0026 ms**, worst **0.0405 ms**, and **0 managed bytes per decision**.
The engine tick budget is 16.6667 ms. These are local measurements, not a
cross-platform latency guarantee. The published executable was 18,579,032
bytes, `libonnxruntime.dylib` was 43,879,424 bytes, and the model was 30,393
bytes. The Native AOT publish reported an IL3053 warning for MessagePack and
an IL2104 trim warning; it reported no ONNX Runtime warnings. The original Step 7 macOS
publish also included two Windows PE DLLs, which Step 8 removed from Unix
publishes after verifying the macOS binary works with only
`libonnxruntime.dylib`.

## Historical development gameplay check and current evaluator adapter

At the version 7 acceptance, the training-data tool's `--backend production`
ran `HardLocalOpponentController` through the exact .NET game engine and recorded
fallback per match. That class has since been removed. Today this backend uses
`EvaluatedModelPolicy.CreateProduction` to prepare the strict configured
`OnnxLocalOpponentController` with the canonical frozen model hash and nine-tick
cadence. It requires an explicit `--student-model` path; loading or inference
failure aborts evaluation without implicit Simple fallback. It does not resolve
catalog entries or follow their configured fallback chains. The default backend
remains the offline student. See the [current guide](../bot-runtime-cleanup/current-guide.md)
for this adapter and preserved policy-parity evidence. The original development
commands and results are retained below:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- evaluate-model \
  --output .artifacts/hard-step7-paired-dev.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --backend production --seed 20261020 --matches 100
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate-model \
  --output .artifacts/hard-step7-direct-policies-dev.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --backend production --seed 20261020 --matches 100 \
  --countdown-mode policies
```

| Development protocol, seed `20261020` | Production Hard | Simple | Other checks |
| --- | ---: | ---: | --- |
| 100 paired left-opponent scenarios | 100 wins | 58 wins | Score margin +643 versus +79; paired margin 88 better, 2 worse, 10 tied; zero fallbacks |
| 100 direct side-swapped seed pairs, policy-owned countdowns | 200 wins | 0 wins | 200/200 completed, zero caps and fallbacks; 100/100 wins on each side |

The paired Hard win rate has a descriptive Wilson 95% interval of
`[0.963, 1]`. The direct scheduled-game rate has a descriptive interval of
`[0.981, 1]`; its two side assignments share each seed, so the 200 results
are not independent scenarios. The direct run reached 45 distinct opening
checkpoints and 45 distinct playing trajectories on each side. The offline
and production backends produced identical trajectory hashes, ticks, and
scores across this development run's 200 direct games.

## Historical playable Hard and final version 7 acceptance

The final gate ran after the version 7 browser and server contract, native
telemetry guard, and integration checks were implemented. It used the same
production Hard controller then used by the app and the reserved master seed `20261107`.
The original commands below ran from the repository root and are retained as
historical provenance; today's production backend uses the strict adapter above:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate-model \
  --output training/results/hard-v1-final-policies-20261107.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --backend production --seed 20261107 --matches 100 \
  --countdown-mode policies
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate-model \
  --output training/results/hard-v1-final-seeded-targets-20261107.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --backend production --seed 20261107 --matches 100 \
  --countdown-mode seeded-targets
```

The [primary policy-owned countdown report](../../training/results/hard-v1-final-policies-20261107.json)
and [separate seeded-target stress report](../../training/results/hard-v1-final-seeded-targets-20261107.json)
include every seed, side assignment, completion, score, fallback flag, and
trajectory hash. In both reports the backend is `production`, the model SHA-256
is the frozen hash above, and `usedModelThroughout` is true.

| Protocol | Hard wins / scheduled | Completed | Caps | Fallbacks | Right / left wins |
| --- | ---: | ---: | ---: | ---: | ---: |
| Policy-owned countdowns (primary) | **200/200** | 200/200 | 0 | 0 | 100/100 each |
| Seeded-target countdowns (stress) | 200/200 | 200/200 | 0 | 0 | 100/100 each |

All 100 primary seed pairs had a Hard win on both sides; every pair had one
left and one right assignment with the same seed and opening checkpoint. The
primary scheduled-game win rate is 100%, above the 65% gate, with any capped
game counted as a non-win. Its descriptive game-level Wilson 95% interval is
`[0.981, 1]`, but the two games in a seed pair are dependent. Treating the
100 distinct seed pairs as units, the descriptive Wilson 95% interval for
**winning both sides of a pair** is `[0.963, 1]`. The primary run had 46
distinct opening checkpoints and 46 distinct playing trajectories on each
side: its 100 seed pairs reduce to 46 distinct paired trajectory tuples. As a
repetition sensitivity check, counting each distinct paired trajectory once
gives 46/46 successful pairs and a descriptive Wilson 95% interval of
`[0.923, 1]`. This is not an independent-trial guarantee. The stress run had
the same 46 openings and 100 distinct playing trajectories per side; it does
not replace the normal countdown protocol.

The original final macOS ARM64 Native AOT executable passed real-model and tiny-model
published smoke, HTTP startup, and the full published two-process Hard/Simple
and LAN integration suite. Its 10,000-decision benchmark measured p95
**0.0034 ms**, p99 **0.0035 ms**, worst **0.0315 ms**, and **0 managed bytes per
decision** against the 16.6667 ms tick budget. The published directory
contained 139,838,226 file bytes, including debug symbols; the release-style
`.tar.gz` was 31,106,656 bytes. The executable was 18,645,480 bytes, native
macOS ONNX Runtime library 43,879,424 bytes, and model 30,393 bytes. Removing
the two Windows PE DLLs saved 16,750,192 file bytes. The same release workflow
also configured real-model published smoke on Linux x64 and Windows x64; those
target executions were not available on this macOS host.

One pre-fix full integration run intermittently aborted while the ONNX Runtime
1DS telemetry worker shut down. The macOS crash report showed its native
worker stack, and [ONNX Runtime's telemetry documentation](https://github.com/microsoft/onnxruntime/blob/main/docs/Privacy.md#disabling-telemetry)
specifies `ORT_DISABLE_TELEMETRY=1` before initialization to avoid starting
that worker. A module initializer now sets this in the native POSIX environment
before loading ONNX Runtime on macOS and Linux. The published and SDK full
integration suites subsequently exited cleanly without an external setting.
