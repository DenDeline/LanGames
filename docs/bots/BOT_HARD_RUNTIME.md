# Production Hard runtime and acceptance (bot plan steps 7–8)

The app packages the frozen [Hard v1 model](BOT_MODEL.md) at
`Models/hard-v1.onnx` beside the executable. The expected SHA-256 is
`5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a`.
The production controller checks this hash and the exact float32
`observation[1,10]` and `logits[1,3]` metadata before using the model. It loads
one ONNX Runtime CPU session on the first Hard start, warms it once, and reuses
the session and bound input/output buffers across decisions and rematches.
Ordinary app startup and LAN play do not load the model.

Hard decides on absolute engine ticks divisible by nine, holds its last axis
between decisions, and returns a neutral axis during countdown. The output
classes map to up/stay/down, with stay winning a logit tie. These rules match
the training evaluator's policy-owned countdown protocol. If loading or
inference fails, the controller switches to Simple for the rest of its lifetime
and records the failure reason. The local game clock continues, and an internal
session status reports that fallback to its caller. The browser now offers
separate Simple and Hard actions. Version 7 HTTP/WebSocket snapshots distinguish
the requested Hard mode from the actual Simple controller after fallback and
include a fallback flag. The page keeps a visible fallback notice through play,
game over, and rematch; leaving clears it.

## Reproduce the runtime checks

From the repository root, after restoring the .NET dependencies:

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

The published smoke checks the actual model's SHA-256 and inference, the
existing tiny Native AOT ONNX probe, and HTTP startup. The release smoke runs
on macOS ARM64, Linux x64, and Windows x64 in the release workflow. The
benchmark performs 1,000 warmup decisions and measures 10,000 model decisions
on finite synthetic engine states, one per nine-tick cadence. Allocation is
measured with `GC.GetAllocatedBytesForCurrentThread` and is **managed**
allocation on that thread, not native ONNX memory use. The path is an explicit
diagnostic command; normal game ticks have no timing instrumentation.

At Step 7 on the development macOS ARM64 host, the Native AOT executable
passed the published smoke and the full two-process local/LAN integration suite. Its
10,000-decision benchmark measured p95 **0.0025 ms**,
p99 **0.0026 ms**, worst **0.0405 ms**, and **0 managed bytes per decision**.
The engine tick budget is 16.6667 ms. These are local measurements, not a
cross-platform latency guarantee. The published executable was 18,579,032
bytes, `libonnxruntime.dylib` was 43,879,424 bytes, and the model was 30,393
bytes. The Native AOT publish reported an IL3053 warning for MessagePack and
an IL2104 trim warning; it reported no ONNX Runtime warnings. The Step 7 macOS
publish also included two Windows PE DLLs, which Step 8 removes from Unix
publishes after verifying the macOS binary works with only
`libonnxruntime.dylib`.

## Development gameplay check with the production controller

The training-data tool accepts `--backend production` to run the **same
controller** used by the app through the exact .NET game engine. It requires
an explicit `--student-model` path, verifies the frozen hash, and records
fallback per match. Production evaluation writes a report and exits with an
error if any match used Simple fallback. Its default backend remains the
offline student for comparison. To reproduce this development check:

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

## Playable Hard and final acceptance

The final gate ran after the version 7 browser and server contract, native
telemetry guard, and integration checks were implemented. It used the same
production Hard controller as the app and the reserved master seed `20261107`.
To reproduce from the repository root:

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

The final macOS ARM64 Native AOT executable passed real-model and tiny-model
published smoke, HTTP startup, and the full published two-process Hard/Simple
and LAN integration suite. Its 10,000-decision benchmark measured p95
**0.0034 ms**, p99 **0.0035 ms**, worst **0.0315 ms**, and **0 managed bytes per
decision** against the 16.6667 ms tick budget. The published directory
contained 139,838,226 file bytes, including debug symbols; the release-style
`.tar.gz` was 31,106,656 bytes. The executable was 18,645,480 bytes, native
macOS ONNX Runtime library 43,879,424 bytes, and model 30,393 bytes. Removing
the two Windows PE DLLs saved 16,750,192 file bytes. The same release workflow
still runs real-model published smoke on Linux x64 and Windows x64; those target
executions were not available on this macOS host.

One pre-fix full integration run intermittently aborted while the ONNX Runtime
1DS telemetry worker shut down. The macOS crash report showed its native
worker stack, and [ONNX Runtime's telemetry documentation](https://github.com/microsoft/onnxruntime/blob/main/docs/Privacy.md#disabling-telemetry)
specifies `ORT_DISABLE_TELEMETRY=1` before initialization to avoid starting
that worker. A module initializer now sets this in the native POSIX environment
before loading ONNX Runtime on macOS and Linux. The published and SDK full
integration suites subsequently exited cleanly without an external setting.
