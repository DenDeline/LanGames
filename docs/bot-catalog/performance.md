# Configured bot runtime measurements

Historical report from the completed catalog work, before the runtime cleanup.
All 18 captures, tables, the preparation outlier, provenance and original command
blocks below are retained. Their application diagnostic dispatch and source layout
describe that revision; the published app no longer accepts those diagnostic
commands. The current managed tool, v9 contracts and publication checks are in the
[current tooling and migration guide](../bot-runtime-cleanup/current-guide.md),
with operator settings in [configuration.md](configuration.md). No new timing
measurements or speedup claims are added here.

Measured on 2026-10-08 for step 6. No steady gameplay optimization was justified: the [focused static review](performance-review.md) found no actionable hot-path allocation or lookup, and every measured policy window reported zero calling-thread managed bytes. This change adds a diagnostic and verification rather than an artificial runtime rewrite. These are scoped observations, not allocation freedom for native memory or a complete game tick, a latency bound, or a before/after speedup claim.

## Configuration and execution

`--bot-benchmark [id]` loads the same `WebApplication.CreateBuilder` configuration providers, content root, validated catalog, registered factories, and `BotRuntime` as the application. Omitting the ID selects `Bots:DefaultBotId`. Diagnostic arguments are removed before configuration binding; normal command-line/environment/JSON overrides remain available. A disposable web application is built to own the configuration watchers, content-root file provider, and service provider, then disposed on success or failure. It is never started: no hosted service or network listener starts, and `PongPeer` is not registered. Building the host also resolves configuration so its disposal is tracked, following the [framework's host-ownership path](https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/Microsoft.Extensions.Hosting/src/HostBuilder.cs#L325-L335).

Preparation must retain the requested identity and a playable session without fallback. An ONNX measurement requires the digest actually verified by its prepared controller to equal the configured digest. A fallback, later identity change, exhausted session, or illegal axis aborts with a nonzero exit and no JSON result. Known configuration-validation failures retain actionable option names; unexpected preparation errors have a generic message.

The shipped profiles were used unchanged:

| ID | Strategy | Cadence in ticks | Relevant configuration |
| --- | --- | ---: | --- |
| `lada` | tracker | 9 | Activation X 0.72; lookahead 0.25 s; dead zone 0.018 |
| `iskra` | tracker | 5 | Activation X 0.5; lookahead 0.45 s; dead zone 0.012 |
| `vektor` | ONNX | 9 | `Models/hard-v1.onnx`; explicit Lada fallback |

All six Vektor reports retained `requestedBotId = effectiveBotId = vektor` and verified the frozen model SHA-256 `5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a`, with `frozenModelDigest = true`.

Host: Apple M5 Pro, macOS 27.0, ARM64, .NET SDK 10.0.400 and runtime 10.0.11. Managed capture invoked `dotnet src/LanPong/bin/Release/net10.0/LanPong.dll` with the diagnostic arguments. Native capture invoked `.artifacts/bot-catalog-step6/publish/LanPong` directly, from `dotnet publish src/LanPong/LanPong.csproj -c Release -r osx-arm64 -o .artifacts/bot-catalog-step6/publish`. The project has `PublishAot=true`, and `.artifacts/bot-catalog-step6/logs/publish.log` records `Generating native code`. These invocation and build records identify managed versus Native AOT execution. Both reported `.NET 10.0.11`, `Arm64`, and `dynamicCodeSupported = false`. The managed runtime configuration explicitly disables that capability because this project enables `PublishAot`; the report flag is not used to infer execution mode.

## Method and scope

Each of the three profiles ran in three fresh processes per mode, serially, with 20,000 warmup calls and 20,000 samples for each measured batch and sampled window in each workload. The local preview server was stopped, and no build or test workload ran during measurement. This was a normal desktop session; scheduler activity, thermal state, CPU affinity, and system caches were not controlled. Managed processes ran first, then native processes. Fresh processes do not reset OS caches or establish fully cold native-library loading.

Inputs are prebuilt deterministic, varying `Playing` states, with changing ball/paddle positions and velocities. Actual session calls have strictly increasing ticks across warmup and both workloads. `consecutive` advances one tick per call, including cached decisions. `cadence-spaced` advances by the configured cadence, beginning at the next cadence multiple, to exercise each decision opportunity. Inputs do not simulate a complete match or feed actions back into game physics.

The diagnostic warms the actual batch/sampled helpers and timer/allocation instrumentation. Configuration, DI, preparation, state/sample array allocation, sorting, JSON formatting, and disposal are outside the measured policy windows. Preparation is reported separately and includes prepared fallback resources; it does not include configuration/DI setup or the whole process startup.

Batched means use two timestamps around N calls and include loop/state access, dispatch, identity/axis guards, and checksum work. Per-call samples time the `GetAxis` dispatch with a timestamp pair; guards and checksums follow each sample outside its timestamp pair. Allocation counters span the entire respective loop. The timer/no-op floor uses the same warmed helpers, guards, and checksum with a no-inline trivial axis function. Floor values are raw and are never subtracted. The no-op body is a reference for harness cost, not an exact matched policy baseline.

`GC.GetAllocatedBytesForCurrentThread` deltas measure managed allocations only on the calling thread. They exclude native ONNX Runtime memory, other threads, the game clock, browser work, setup, and cleanup. A zero delta does not establish leak freedom. Slow native disposal and failure-transition costs were not measured here.

## Results

Times below are **microseconds per call**. Batch values are the median of the three process means, followed by their minimum–maximum range. Median/p95/p99 columns are the median of the three per-process statistics; worst is the maximum observed individual sample across all three processes. These aggregated percentiles are not a pooled-sample distribution. JSON reports retain each run's statistics without table rounding.

| Mode | ID | Workload | Batch mean (range), µs | Call median | p95 | p99 | Worst |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| managed | lada | consecutive | 0.031 (0.030–0.033) | 0.041 | 0.042 | 0.084 | 9.750 |
| managed | lada | cadence-spaced | 0.046 (0.044–0.049) | 0.042 | 0.083 | 0.084 | 0.209 |
| managed | iskra | consecutive | 0.035 (0.033–0.040) | 0.041 | 0.083 | 0.084 | 15.375 |
| managed | iskra | cadence-spaced | 0.054 (0.049–0.061) | 0.042 | 0.084 | 0.084 | 0.250 |
| managed | vektor | consecutive | 0.316 (0.309–0.317) | 0.042 | 2.458 | 2.542 | 37.917 |
| managed | vektor | cadence-spaced | 2.468 (2.436–2.477) | 2.417 | 2.542 | 2.709 | 180.917 |
| native | lada | consecutive | 0.011 (0.011–0.013) | 0.000 | 0.042 | 0.042 | 3.208 |
| native | lada | cadence-spaced | 0.017 (0.017–0.023) | 0.000 | 0.042 | 0.042 | 5.541 |
| native | iskra | consecutive | 0.013 (0.013–0.013) | 0.000 | 0.042 | 0.042 | 1.625 |
| native | iskra | cadence-spaced | 0.018 (0.018–0.021) | 0.000 | 0.042 | 0.042 | 0.167 |
| native | vektor | consecutive | 0.271 (0.269–0.362) | 0.000 | 2.292 | 2.417 | 24.250 |
| native | vektor | cadence-spaced | 2.329 (2.316–2.333) | 2.292 | 2.417 | 2.584 | 30.250 |

Both batch and sampled allocation deltas were **0 bytes for every policy window in every run**. All corresponding floor windows also measured 0 bytes. Native tracker and cached-call medians of zero reflect timer resolution; they do not mean the calls have no cost. Tracker results are close to the instrumentation floor, so batch means are more informative than individual samples. Vektor's consecutive median mostly reflects cached calls; the spaced workload exposes ONNX decision cost. These controller measurements alone do not establish the complete 16.7 ms tick budget or input-to-display latency.

Raw timer/no-op floor, in the same units and with the same aggregation:

| Mode | ID | Workload | Batch mean (range), µs | Call median | p95 | p99 | Worst |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: |
| managed | lada | consecutive | 0.006 (0.005–0.006) | 0.000 | 0.042 | 0.042 | 0.125 |
| managed | lada | cadence-spaced | 0.006 (0.005–0.006) | 0.000 | 0.042 | 0.042 | 0.166 |
| managed | iskra | consecutive | 0.006 (0.005–0.006) | 0.000 | 0.042 | 0.042 | 0.167 |
| managed | iskra | cadence-spaced | 0.006 (0.005–0.006) | 0.000 | 0.042 | 0.042 | 3.375 |
| managed | vektor | consecutive | 0.006 (0.006–0.006) | 0.000 | 0.042 | 0.042 | 0.167 |
| managed | vektor | cadence-spaced | 0.008 (0.007–0.009) | 0.000 | 0.042 | 0.042 | 1.833 |
| native | lada | consecutive | 0.004 (0.004–0.005) | 0.000 | 0.042 | 0.042 | 0.084 |
| native | lada | cadence-spaced | 0.004 (0.004–0.005) | 0.000 | 0.042 | 0.042 | 0.166 |
| native | iskra | consecutive | 0.005 (0.004–0.005) | 0.000 | 0.042 | 0.042 | 0.125 |
| native | iskra | cadence-spaced | 0.004 (0.004–0.004) | 0.000 | 0.042 | 0.042 | 0.125 |
| native | vektor | consecutive | 0.006 (0.005–0.006) | 0.000 | 0.042 | 0.042 | 0.167 |
| native | vektor | cadence-spaced | 0.004 (0.004–0.005) | 0.000 | 0.042 | 0.042 | 0.084 |

Preparation is **milliseconds**, again median (minimum–maximum) across the three fresh processes:

| Mode | ID | Preparation, ms |
| --- | --- | ---: |
| managed | lada | 1.257 (1.206–1.278) |
| managed | iskra | 1.225 (1.211–1.227) |
| managed | vektor | 21.180 (20.887–21.370) |
| native | lada | 0.010 (0.003–0.011) |
| native | iskra | 0.002 (0.002–0.003) |
| native | vektor | 10.199 (9.966–552.288) |

The 552.288125 ms preparation occurred in the first native Vektor run and is retained. Its cause was not isolated; these runs do not establish a cold-loading or preparation-time bound. Preparation and inference are separate costs. There is no evidence here for changing steady gameplay policy code or moving native cleanup onto another thread.

Checksums were identical across all three managed and all three native runs for each profile/workload. They consume tick and axis values, providing reproducible output evidence and preventing unused-result elimination:

| ID | Workload | Batch checksum | Sampled checksum |
| --- | --- | ---: | ---: |
| lada | consecutive | 16942255231996394534 | 11884590601311886544 |
| lada | cadence-spaced | 508434303813938166 | 3125014974943681278 |
| iskra | consecutive | 7775834646257906140 | 18274545188274158670 |
| iskra | cadence-spaced | 13809445298664360970 | 4359997293748959990 |
| vektor | consecutive | 13585327793883015353 | 9868735501071717565 |
| vektor | cadence-spaced | 2045637098551288132 | 13377870449845397564 |

## Reproduction

Run from the repository root, with no concurrent builds, tests, or preview process during timing. These commands explicitly select Production with `--environment Production`, which also overrides an inherited `DOTNET_ENVIRONMENT`, and use an absolute content root. Existing normal configuration overrides can intentionally change the measured profiles. Samples accept 100–100,000; warmup accepts 2–100,000. Defaults are 20,000 each. Use the same counts when comparing deterministic checksums.

```sh
dotnet build src/LanPong/LanPong.csproj -c Release
dotnet publish src/LanPong/LanPong.csproj -c Release -r osx-arm64 -o .artifacts/bot-catalog-step6/publish
mkdir -p .artifacts/bot-catalog-step6/measurements
```

Managed, three serial fresh launches per profile:

```sh
for run in 1 2 3; do
  for bot in lada iskra vektor; do
    ASPNETCORE_ENVIRONMENT=Production dotnet "$PWD/src/LanPong/bin/Release/net10.0/LanPong.dll" \
      --bot-benchmark "$bot" --benchmark-samples 20000 --benchmark-warmup 20000 \
      --environment Production --contentRoot "$PWD/src/LanPong" \
      > ".artifacts/bot-catalog-step6/measurements/managed-$bot-$run.json"
  done
done
```

Native, three serial fresh launches per profile:

```sh
for run in 1 2 3; do
  for bot in lada iskra vektor; do
    ASPNETCORE_ENVIRONMENT=Production "$PWD/.artifacts/bot-catalog-step6/publish/LanPong" \
      --bot-benchmark "$bot" --benchmark-samples 20000 --benchmark-warmup 20000 \
      --environment Production --contentRoot "$PWD/.artifacts/bot-catalog-step6/publish" \
      > ".artifacts/bot-catalog-step6/measurements/native-$bot-$run.json"
  done
done
```

Captured raw JSON is retained locally in `.artifacts/bot-catalog-step6/measurements/{managed,native}-{lada,iskra,vektor}-{1,2,3}.json`; logs and the fresh native artifact are in the adjacent `logs` and `publish` directories. These artifacts are ignored by Git. Tables in this document preserve the reviewed results in the repository.

The 18 captures preceded the review correction that gives the diagnostic a built, unstarted host for complete configuration-resource disposal. That correction changes setup and cleanup outside all measured windows; the policy path, inputs, warmup, counters, and timing loops are unchanged. The accepted captures and their preparation outliers are retained rather than replaced. Follow-up validation uses a separate fresh publication at `.artifacts/bot-catalog-step6/ownership-publish`, preserving the binary and logs used for these measurements.

## Validation

- All 173 Release .NET tests passed, including nine focused diagnostic tests. These test parser/configuration behavior, actual tracker factories, deterministic action-sensitive checksums, varying monotonic inputs, successful/failed disposal, verified model proof, fallback/identity/exhaustion rejection, and illegal axes without fragile timing thresholds.
- Frontend behavior, input, v8 protocol, typecheck/build, and formatting checks passed.
- Full source HTTP/WebSocket/two-process integration passed, including the provider-only fourth tracker, tuned movement, rematch/leave, LAN coexistence, and missing/corrupt model fallback.
- Fresh macOS ARM64 Native AOT publication and contents verification passed. Publish warnings were the previously reviewed MessagePack IL3053 and IL2104 warnings; no new warnings were introduced.
- Published smoke passed for ONNX native-library inference, frozen-model identity, configured diagnostic selection/CLI overrides, ASP.NET environment/content-root providers from a different cwd, useful validation errors, fallback rejection, HTTP/static content, and protocol v8 catalog/status. The diagnostic also succeeds with its HTTP endpoint already occupied, proving it does not start a listener.
- Full integration against that fresh Native AOT binary passed, including the same configured fourth tracker, tuning, fallback, HTTP/WebSocket, gameplay, and LAN transition scenarios as the source run.
- After the configuration-ownership correction, all 173 .NET tests passed again, including rejected diagnostic invocation followed by two fresh successful invocations on the same temporary root. Managed process diagnostics and a separate fresh Native AOT publication's contents/model/configured-diagnostic/HTTP smoke passed, including the occupied endpoint. A native process with conflicting inherited `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` also confirmed that `--environment Production` excludes the conflicting environment-specific JSON. Only the existing MessagePack publish warnings remained. Follow-up logs use the `ownership-` prefix in the same log directory.

Validation commands:

```sh
dotnet test --solution LanPong.slnx -c Release --no-restore
pnpm test:frontend
pnpm build
pnpm format:check
python3 tests/integration/integration_test.py
python3 tests/integration/verify_publish_contents.py .artifacts/bot-catalog-step6/publish
LANPONG_TEST_BINARY="$PWD/.artifacts/bot-catalog-step6/publish/LanPong" python3 tests/integration/published_smoke_test.py
LANPONG_TEST_BINARY="$PWD/.artifacts/bot-catalog-step6/publish/LanPong" python3 tests/integration/integration_test.py
```

Ownership follow-up publication and native checks:

```sh
dotnet publish src/LanPong/LanPong.csproj -c Release -r osx-arm64 -o .artifacts/bot-catalog-step6/ownership-publish
python3 tests/integration/verify_publish_contents.py .artifacts/bot-catalog-step6/ownership-publish
LANPONG_TEST_BINARY="$PWD/.artifacts/bot-catalog-step6/ownership-publish/LanPong" python3 tests/integration/published_smoke_test.py
```

Optional strict mDNS testing was not enabled.
