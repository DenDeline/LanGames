# CI preparation/shutdown test follow-up

## Report and scope

The human reports Linux x64 CI passing 214 of 215 tests, with `ShutdownDuringPreparation_RejectsAndDisposesCandidateWithoutHoldingStateLock` failing at `preparing.IsSet` after about 3.28 seconds. Baseline is `2e50bf571b1cdac517295a24c7aca4a7552896ee`, with a clean checkout. The assertion precedes `StopAsync`, so the provided failure does not demonstrate a shutdown-path failure. The ONNX device-discovery line is a warning; the reported failing test uses a synthetic tracker controller.

The coordinator performs research/source review and writes only Markdown under this feature directory. The existing **Implement configurable bot catalog** chat performs one assigned implementation step, validates it, creates one conventional commit and stops. The requested fix remains within the refactor; no push, PR or deployment is included.

## High-level decomposition

1. **Review and research:** trace admission/shutdown, distinguish preparation entry from shutdown behavior, and inspect sibling barrier tests. This is complete statically; reproduction remains the worker gate.
2. **One worker implementation step:** reproduce or diagnose the scheduling dependency, replace test-only blocking entry notifications with awaitable signals, preserve deliberate preparation/disposal blocking and meaningful state-lock/shutdown/disposal assertions, and validate under normal plus constrained scheduling. Scope is the failing test and the three adjacent tests sharing that handshake/probe pattern; change production only if a concrete runtime defect is demonstrated and reviewed.
3. **Coordinator acceptance and delivery:** review the final source and evidence, reconcile this record before the worker's single conventional commit, verify a clean stop, and report actual platform coverage.

No additional library is needed. Existing .NET 10 `TaskCompletionSource`/`Task.WaitAsync` and a dedicated-thread lock probe can express the test handshake. The current harness queues an outer `Task.Run`, queues a blocking `ManualResetEventSlim.Wait` observer on the same pool, and production queues `Prepare` through another `Task.Run`. Parallel blocked tests can consume the pool before the three-second signal deadline. [Microsoft's thread-pool starvation guidance](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation) describes blocking pool operations; [TaskCompletionSource constructors](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.taskcompletionsource.-ctor?view=net-10.0) support existing framework signaling. This is a source-supported hypothesis, not a measured Linux reproduction yet.

Independent runtime review finds preparation outside the peer state lock, shutdown publishing `_stopping` before awaiting the transition, rejection after preparation, and disposal before releasing the transition. Preserve those production paths. Keep shutdown incomplete while preparation is deliberately held, reject admission after shutdown begins, reset/dispose exactly once, and leave no bot identity or active session. Preserve responsive snapshots while preparation/disposal is held; pool scheduling must not masquerade as a locked peer.

## Worker review and validation gates

- Use an asynchronous entry notification with continuations scheduled asynchronously; invoke `StartBotAsync` directly because it already offloads preparation. Retain a bounded synchronous release barrier only where the factory/disposal callback must deliberately block. Always release and observe outstanding work in cleanup.
- Make the bounded synchronous snapshot probe independent of the shared pool. Keep worker-release bounds longer than entry/probe/assertion budgets; do not merely lengthen the old three-second wait, skip/retry failures, globally alter pool settings or serialize the entire suite.
- Validate the targeted barrier tests in bounded fresh processes under a low reported processor count, then the full Release solution under the normal CI command. Use existing Linux tooling if available; otherwise report constrained local proof separately from unverified Linux execution. Save exact commands, iteration results and any reproduction limitations under `.artifacts/bot-runtime-cleanup-ci-shutdown/`.
- Do not repeat native/frontend/integration jobs for a test-only change. Preserve production code, model/goldens, side contracts, user Lock, removed HasValidShape and canceled BenchmarkDotNet scope.
- Prepare the fix and evidence for coordinator precommit review, then make one conventional `test(bots): ...` commit including this reconciled Markdown record and stop clean.

## Reconciliation and acceptance

The one implementation step remains test-only. Independent review accepted the final four test bodies and cleanup; no runtime defect was demonstrated and no production change is required. All other test methods and all substantive assertions remain unchanged. Entry notification uses an asynchronous `TaskCompletionSource`, startup calls `StartBotAsync` directly, and the synchronous snapshot probe uses a default-scheduler `LongRunning` task. The three-second entry and one-second snapshot deadlines remain unchanged.

The first draft retained deliberately blocking `ManualResetEventSlim` release gates. Its normal run passed, but one of ten constrained processes failed at fallback preparation entry before the snapshot probe, with the other three tests passing. That failure is preserved in `fixed-lowcpu-iterations.json` and its raw log; it was not discarded as a passing retry. This evidence prompted an implementation correction within the same step.

The final release gate is an incomplete `TaskCompletionSource` task waited synchronously with a 15-second safety bound. It still holds the actual factory/reset/disposal callback until explicit release. In .NET 10, the timed `Task.Wait` path notifies the pool about blocking; direct event waiting does not provide that task-level notification. [The pinned .NET 10 Task implementation](https://raw.githubusercontent.com/dotnet/runtime/v10.0.11/src/libraries/System.Private.CoreLib/src/System/Threading/Tasks/Task.cs) and [Microsoft's blocking-compensation guidance](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/threading#thread-injection-in-response-to-blocking-work-items) support this choice. Compensation improves capacity without guaranteeing wall-clock deadlines; acceptance depends on the recorded runs. No pool settings, retry/skip attributes, suite serialization, package additions or benchmark project were introduced.

Review also corrected unbounded cleanup. Cleanup releases first, observes outstanding start/stop/probe work and peer disposal under one shared ten-second budget, and retains fault observation for unfinished tasks. Disposal entry itself runs on separate tracked work because it enters the peer lock synchronously. Cleanup failures attach to the primary failure instead of replacing it; a cleanup-only failure still fails the test.

### Validation reviewed

All execution below is **macOS ARM64**, .NET SDK 10.0.400/runtime 10.0.11. The reported original Linux failure was not locally reproduced, and no available Linux execution environment was found. Processor-count overrides are constrained scheduling evidence, not Linux or OS CPU-affinity coverage.

| Phase | Fresh processes | Result |
| --- | ---: | --- |
| Original tests, processor counts 1 and 2 | 3 each | All four passed each time; original Linux failure not reproduced |
| Intermediate asynchronous-entry draft, processor counts 1 and 2 | 5 each | One fallback entry timeout at count 1; retained and diagnosed |
| Final four-test group, normal processor count | 1 | 4 passed, zero skipped |
| Final four-test group, `DOTNET_PROCESSOR_COUNT=1` | 5 | 4 passed per process, zero skipped |
| Final four-test group, `DOTNET_PROCESSOR_COUNT=2` | 5 | 4 passed per process, zero skipped |
| Final full Release solution, unchanged CI command | 1 | 215 passed, zero failed/skipped, exit 0 |

Filtered runs preserve four-way competition with `--maximum-parallel-tests 4` and `--minimum-expected-tests 4`. Final normal and constrained records match source SHA-256 `307bd99510fe49c3a5acddcc0e88b141fafcba3d61f750ab2940e2e2ae07974e` and test DLL SHA-256 `069077a915e5ce0daefbaa0843f952cd95f69d16ed360c9cf026b567463d91ad`. The full command was exactly `dotnet test --solution LanPong.slnx --configuration Release`, without a processor override; its output confirms 215 tests. `git diff --check` passed. Production, frontend, dependencies, models, goldens and existing side/contract changes remain untouched, so no native/frontend/integration repetition was required.

Evidence under `.artifacts/bot-runtime-cleanup-ci-shutdown/` includes baseline provenance, original and intermediate iteration records, the retained failing log, cooperative-release diagnosis, final normal/constrained iteration records, exact full-CI command provenance and log, and the assertion/scope proof. Initial sandbox attempts failed during MTP named-pipe startup before any test ran; they are retained and excluded from test reproduction counts.

## Status

Coordinator source and validation acceptance are complete. Reconciliation occurred before the single conventional commit. The worker is authorized to commit the reviewed test file and this record, verify the checkout is clean, and stop. No further implementation step is authorized by this follow-up plan.
