# Research and design decisions

Research date: 2026-10-08. This document describes LanPong design decisions, not Chess.com's private implementation.

## Repository baseline before implementation

- The application targets .NET 10/C# 14, runs Native AOT, uses source-generated JSON and MessagePack contracts, and already contains Microsoft.ML.OnnxRuntime 1.30.0.
- `Program.cs` registers concrete Simple/Hard singleton controllers and switches on a public opponent-mode enum.
- `PongPeer` chooses one of two controller fields, carries hard-specific fallback state, and owns serialized session transitions and deterministic simulation.
- The browser protocol is version 7. HTTP, MessagePack array fields/enums, TypeScript decoders, and integration fixtures must change together.
- The frontend is modular vanilla TypeScript/Vite. Two separate Simple/Hard forms encode the same backend distinction.
- The trained model has a frozen hash, observation/logit schema, nine-tick cadence, and tie behavior. Existing diagnostics and training parity depend on preserving the default model profile.

## Chess.com reference

[Official bot help](https://support.chess.com/en/articles/8614091-how-can-i-play-against-the-chess-com-bots) documents named personalities, strength groups, themed opponents, selecting an opponent before Play, optional settings, and visible availability. Its public help does not establish its catalog storage schema or server architecture.

LanPong will use an original catalog with a compact selectable card for each opponent, a selected opponent summary, explicit difficulty guidance and availability, and one Play action. Keep the arena as the main area and LAN actions reachable alongside the catalog. Use original names/glyphs and descriptive difficulty labels; no invented calibrated chess Elo, copied artwork, accounts, locks, coaching, chat, or crowns are needed.

Keyboard selection must not move the paddle. Native radio inputs/cards offer normal keyboard behavior and clear selection semantics. Focus should remain stable when status updates arrive. Mobile layout must allow bot selection and gameplay without horizontal scrolling. Catalog loading, failure/retry, empty catalogs, disabled entries, unavailable models, and visible configured fallback require explicit states.

## Evaluated libraries and framework capabilities

| Capability | Existing option/research | Decision |
| --- | --- | --- |
| Configuration binding | [Microsoft configuration source generator](https://learn.microsoft.com/en-us/dotnet/core/extensions/configuration-generator) | Enable generated binding for Native AOT; use typed appsettings options. |
| Validation | [Options pattern](https://learn.microsoft.com/en-us/dotnet/core/extensions/options), [generated validation](https://learn.microsoft.com/en-us/dotnet/core/extensions/options-validation-generator) | Use startup validation and explicit cross-entry checks. Generated property validation is optional; avoid reflection-based validation. No FluentValidation dependency is needed. |
| Strategy registration | [Built-in keyed DI](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/dependency-injection?view=aspnetcore-10.0#keyed-services) and explicit factory registry | Register strategy factories explicitly; either built-in keyed services or a small typed registry can serve the seam. Avoid assembly scanning, reflection activation, arbitrary type names, and plugin frameworks. |
| Read-mostly lookup | [System.Collections.Frozen](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen?view=net-10.0) | Freeze validated stable-ID lookup once; keep configuration and DI resolution outside gameplay ticks. |
| Model inference | [ONNX Runtime C# API](https://onnxruntime.ai/docs/api/csharp/api/Microsoft.ML.OnnxRuntime.html), [C# basics](https://onnxruntime.ai/docs/tutorials/csharp/basic_csharp.html) | Reuse installed ONNX runtime and existing warmed, preallocated CPU inference. Adding ML.NET or another inference engine would duplicate working functionality. |
| UI primitives | Native HTML controls, existing TypeScript/Vite; [radio controls](https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/input/radio) | Keep current stack; native selection controls need no React/component framework dependency. Build catalog DOM when catalog changes, not on each simulation snapshot. |
| Verification | Existing TUnit, frontend checks, Python HTTP/WS/two-process integration, published smoke | Extend meaningful behavior coverage; add a browser-test dependency only if a concrete gap cannot be covered by available browser tooling. |

The installed development SDK is 10.0.400 with .NET runtime 10.0.11. Preserve .NET 10/C# 14 and use supported framework features where they improve this design; a preview SDK migration is outside this task.

Additional alternatives reviewed: [FluentValidation](https://docs.fluentvalidation.net/en/latest/di.html) and [Scrutor](https://github.com/khellang/Scrutor) add capabilities beyond two explicit factories and a small options graph; assembly scanning complicates AOT auditing. [Lit](https://lit.dev/docs/) and [Web Awesome](https://webawesome.com/docs/) could support a broader component system but add integration work here. [Shoelace](https://shoelace.style/) reports ended development and redirects new work to Web Awesome. The `dotnet-ai:technology-selection` research skill also routes an existing trained-model inference requirement to ONNX Runtime, supporting reuse of the current package.

## Target architecture

1. A startup-bound `Bots` options section defines a default bot ID and a collection of entries. The section is an application-lifetime snapshot; configuration edits take effect after restart. Live reload is deferred because running controller lifetime and model disposal need explicit semantics.
2. Entries provide stable ID, display name, description/style, difficulty/category, display order, optional original glyph, enabled state, strategy ID, typed behavior settings, and an optional explicit fallback bot ID. Use a strict, documented ID format and ordinal ID lookup.
3. Register reusable `tracker` and `onnx` strategy factories. Multiple entries can use either strategy with different settings. New entries using an existing strategy need only configuration; new algorithms require a registered factory. UI/API do not branch on bot IDs or strategy IDs.
4. Tracker parameters include observation cadence, activation position, lookahead, and dead zone. ONNX settings include model path, expected SHA-256, and inference cadence. Keep observation/action tensor schema as the strategy contract, and preserve the calibrated model's defaults.
5. Validate duplicate/invalid IDs, unknown strategies, required metadata, numeric ranges and finite values, default ID, inappropriate/missing strategy settings, and fallback references/cycles. Configuration errors fail startup with actionable diagnostics; missing/unusable model assets are runtime availability failures rather than killing LAN play.
6. Resolve relative model paths consistently against the application content root. Appsettings and normal configuration providers are the production operator path; step 3 removed the legacy production model-path override. Standalone training/model diagnostics remain separate. Do not leak local paths/raw native errors into user-facing catalog reasons.
7. Catalog availability distinguishes disabled/unavailable bots and reports model initialization/inference failures truthfully. Model sessions remain lazy; catalog discovery must not load every native session. Successful selection prepares the controller before admitting a match. Preserve the existing session rule: leave a current bot/LAN/matchmaking session before starting another. Rejected selections leave the current match coherent; concurrent starts admit one session and reject the queued contender.
8. Fallback is an explicit configured relationship. Preserve requested and effective bot identity plus a persistent fallback notice across play/game over/rematch. No hidden universal Simple fallback. A bot without a fallback must stop cleanly and report failure rather than crash the clock. Exact policy and availability response must be verified in steps 2–3.
9. Keep mutable decision state isolated to the active local session or a well-defined cached per-entry controller owned by the runtime. Never share mutable policy state between entries accidentally. Establish disposal/reuse ownership explicitly and preserve lazy ONNX resource reuse across rematches where appropriate.
10. Browser discovery uses `GET /api/bots`; local start uses a stable `botId` in the request. Snapshot mode becomes generic local bot versus LAN, accompanied by requested/effective bot identity and fallback status. Version browser HTTP/MessagePack/TypeScript changes together; UDP protocol changes are unnecessary unless a reviewed dependency proves otherwise.

## Initial catalog content

Ship at least three original entries so configuration reuse is visible: two tracker profiles with different reaction guidance, and one ONNX profile using the existing model. The implementation worker can choose clear Russian names/descriptions compatible with the current UI. At least one tracker preserves the old calibrated Simple behavior and the ONNX profile preserves Hard v1 behavior for diagnostics/training comparisons. Difficulty labels guide selection; no numeric rating should imply an unperformed calibration.

## Performance guardrails

Do startup binding/validation and ID lookup once, resolve a controller at session start, and let the tick call its direct interface. Preserve preallocated inference buffers and deterministic engine semantics. Keep rich catalog metadata out of repeated game-frame processing when stable identity fields suffice. Measure the configured runtime path before/after any optimization using existing diagnostics, reporting managed allocation and latency limits accurately. Native AOT publication is required evidence for generated binding/serialization compatibility.

## Step 6 measurement research

Read-only review of the implemented runtime found that the retained `--hard-benchmark` constructs the legacy controller directly and bypasses catalog configuration. It cannot substantiate configured-runtime performance. The prepared session already avoids per-tick binding, DI lookup, model initialization, buffer allocation, and identity reconstruction; no speculative hot-path rewrite is justified.

[BenchmarkDotNet Native AOT tooling](https://benchmarkdotnet.org/articles/configs/toolchains.html#nativeaot) and its [.NET 10 support](https://benchmarkdotnet.org/changelog/v0.15.3.html) were evaluated using the `dotnet-diag:microbenchmarking` skill. It can run native child benchmarks from a managed host and is useful for comparative investigations. For this feature, extend the built-in diagnostics instead: measuring the actual published binary avoids another benchmark project/package and verifies the deployed configuration path. Add BenchmarkDotNet later if a proposed optimization needs a controlled comparison.

The diagnostic should load normal configuration, resolve the catalog/factories/runtime without starting networking, prepare a selected ID, and measure `PreparedBotSession.GetAxis`. Reject fallback or identity changes so an unavailable ONNX profile cannot silently yield tracker measurements. Use deterministic varying states and monotonic ticks, warming before measurement. Report both consecutive gameplay calls and cadence-spaced decision calls, preparation time, batched mean, per-call median/p95/p99/worst, instrumentation floor, checksum, and the calling-thread allocation delta. Setup, formatting, sample storage allocation, and disposal belong outside timed loops. Run Release and macOS ARM64 AOT in fresh processes for tracker and ONNX entries.

[GC.GetAllocatedBytesForCurrentThread](https://learn.microsoft.com/en-us/dotnet/api/system.gc.getallocatedbytesforcurrentthread?view=net-10.0) measures managed allocation only on the calling thread; the [Native AOT implementation](https://source.dot.net/System.Private.CoreLib/System/GC.NativeAot.cs.html) supports the same scope. Zero observed allocation does not prove native allocation freedom, other-thread allocation freedom, or absence of leaks. [BenchmarkDotNet diagnoser restrictions](https://benchmarkdotnet.org/articles/configs/diagnosers.html#restrictions) reinforce that distinction. Controller timings can be compared with the 16.7 ms game tick budget but do not establish complete game-clock or browser latency. Report run-to-run variation honestly and claim a speedup only with a measured comparison.
