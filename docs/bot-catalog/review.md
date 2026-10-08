# Coordinator review record

## Runtime risks and required evidence

- The local strategy runs under the simulation state lock. A strategy exception must not fault the shared clock and break later LAN play or shutdown. Failure without an explicit configured fallback needs a coherent stopped local session and safe user-visible reason.
- Controller construction/hash/model warmup and native disposal should occur outside the state lock. Admit prepared candidates under the transition semaphore and recheck shutdown; dispose candidates rejected after preparation.
- Unknown, disabled, or unusable selections must preserve an existing valid match, including its round, selected identity, and controller state.
- Model failure fallback must expose requested and effective IDs/names, persist through rematch, and clear on leave/LAN transition. Follow configured relationships; validate cycles at startup.
- Multiple bots sharing a strategy must retain their own parameters and mutable decision state. Reset must restore deterministic policy state and never restart costly catalog discovery/model creation every tick.
- Session resources need unambiguous ownership: leave, replacement, shutdown, and initialization failure must dispose exactly once; rematch should retain warm reusable resources.
- Bot metadata bounds must be independent of the 24-character human nickname limit and match TypeScript/MessagePack validation.
- Source-generated binding and JSON must survive a real Native AOT publish with nested configuration overrides. Compare new warning categories with the existing MessagePack AOT/trim baseline.
- Public catalog descriptors expose user-facing metadata and safe availability, not internal model paths/checksums or the full configuration object. Bot name validation is separate from human nickname validation.
- A configured fallback may itself use ONNX. Prepare the selected entry's required fallback resources outside the state lock; switching on a tick must not hash/load/warm models or do expensive native disposal under that lock.

## UX risks and required evidence

- Catalog rendering belongs to fetch/selection events, not per-frame snapshot rendering. Keyboard focus and selection must survive snapshots.
- Radio input styling must override the existing global full-width/tall input rule. Arrow navigation must not send paddle controls.
- Verify loading/error/retry/empty/unavailable-only states; long and HTML-looking configured metadata must render as text.
- Verify desktop and 320/390-pixel layout, one clear Play action, named scoreboard/session/rematch, persistent fallback, and reachable LAN actions.
- Protect nickname storage/validation, network discovery/manual joins, challenge acceptance, touch pointer cancellation, sound/reduced-motion settings, and leave/replay cleanup.

## Step reviews

### Step 1 — accepted

Commit: `d2c7b1b19bbfe273f1fca815435a07b3c19e9c1c` (`feat(bots): add validated configurable catalog`). Coordinator source review and independent backend review found no actionable blockers. Typed option graph, strict/generated binding, cross-entry validation, and copied immutable lookup meet the plan; active gameplay/API are unchanged.

Worker evidence: zero-warning Release build, 16 focused catalog tests, 122 total .NET tests, passing frontend/build and existing two-process integration, macOS ARM64 Native AOT publish and published smoke. Native startup accepted missing assets and rejected malformed configuration. Publish has existing MessagePack IL3053/IL2104 warnings only. Startup-only binding explicitly avoids reload callbacks and has a regression test.

Carry forward: catalog names permit 64 characters while human nicknames remain limited to 24; browser contracts must represent bot identity without accidentally applying the human nickname cap. Runtime ownership, availability, fallback, and path resolution are next.

### Step 2 — accepted

Initial controller review confirms configured tracker behavior and strict ONNX failure reporting, with replay parity checks against the trained default controller. Prepared fallback resources permit tick-time switching without loading models or disposing native resources under the state lock.

Coordinator refinement: preserve the prior leave-before-start contract. The first generic runtime draft intentionally allowed active bot replacement, but that behavior expands the scope and would admit two queued starts successively. The worker is adjusting it to one admission/one rejection and matching tests. This clarifies research target 7 before contract migration.

Commit: `a34d425eedc8d9f854d3dceb2a1b9780d0edbc3a` (`refactor(bots): resolve opponents through strategy registry`). Final source confirms the generic path preserves leave-before-start; a deterministic barrier test proves one admitted start and one rejection. Coordinator and independent backend reviews found no remaining blockers.

Worker evidence: 160 total .NET tests, including 11 configured-controller, 14 runtime, and 13 new session tests; zero-warning Release compilation; frontend/build, source integration, macOS ARM64 Native AOT publish/smoke and full native integration all pass. Only existing MessagePack IL3053/IL2104 publish warnings remain. Working tree was clean at the boundary.

Carry forward: temporary version-7 mode adapters and model-path diagnostic override exist solely to keep the old browser/integration contract coherent. Remove production compatibility branches in step 3 while preserving standalone training/model diagnostics. Catalog discovery reports uninitialized models as not checked; known failures remain until restart. Slow native disposal outside `_gate` can still delay the clock if a disposal blocks; consider measurements in step 6 before changing cleanup scheduling.

### Step 3 — accepted; checkpoint A complete

Commit: `2881f34d4aec8760899767f2d2497cc408f94a31` (`feat(api)!: select catalog bots by id`). Coordinator and independent backend/frontend reviews found no blockers. Public descriptors omit strategy settings/model paths/checksums; safe availability and `canPlay` account for explicit fallback chains without model initialization. Missing/null/unknown IDs have explicit validation, bot display names have separate 64-character fields, and HTTP/WS v8 identity matches the 30-field binary order. LAN UDP is unchanged.

Worker evidence: zero-warning Release compilation; 164 .NET tests; frontend behavior/input/protocol checks, format/build; source integration; Native AOT smoke and full published integration. Only existing MessagePack IL3053/IL2104 warnings remain. Production Simple/Hard selectors, adapters, and old model-path override are gone; standalone training/diagnostics remain. Catalog refresh now follows runtime/rematch failure and ignores stale overlapping responses, with tests proving ordinary ticks do not fetch the catalog.

The coordinator reconciled the first three commits and updated remaining scope in plan.md before dispatching step 4. A playable fallback must be explained before Play; the richer UI should choose eligible remembered/default entries. Step 5 will close actual gaps, and step 6 will benchmark the configured runtime rather than the retained legacy diagnostic.

### Step 4 — accepted

Commit: `3083f37d437e96df8a8832a6ccd5ef9f05cf79d3` (`feat(ui): add opponent catalog and selected bot profiles`). Grouped native radio cards, selected profiles and one Play action consume catalog data without ID/strategy branches. Known fallback is explained before selection; unchecked readiness is honest. Text rendering is safe, eligible defaults are selected, and catalog DOM changes only on catalog/selection events.

Independent review found and the worker fixed retry-success focus, missing radio difficulty/style descriptions, hidden terminal bot failure messages, narrow long-name clipping/overflow, and initial launch visibility. Real checks also found pre-action WebSocket races for launch/rematch; one bounded status reconciliation preserves stale-response guards and reveals only the exact accepted bot/round. No remaining source blocker was found.

Worker evidence: frontend behavior/input/protocol checks, format/build, Release build and 20 catalog/API tests pass. Browser checks cover desktop, 320px and 390px, loading/error/retry/empty/unavailable states, fallback, 64-character names, long metadata, launch/rematch/leave, and a clear console. The coordinator independently verified committed native arrow selection, named launch, arena top at 16px with focus on game-arena, and successful leave/re-enabled selection at 390px. Preview interference during concurrent checks was resolved by reviewing the idle committed preview. Tree was clean at the boundary.

Carry forward: step 5 needs real configured-process proof for an additional playable tuned tracker and corrupt-model fallback. Existing frontend fixtures already cover extra-ID submission and HTML-looking text, so duplicate mocks are unnecessary. The focused static performance review is recorded separately in performance-review.md; it does not replace step 6 measurements.

### Step 5 — accepted

Commit: `48aa7f933f92ca8903b6c9baa33d63aab17fa944` (`test(bots): cover configurable catalog and session transitions`). Only integration tests and coordinator Markdown changed. Coordinator and independent backend review accept the real provider-only fourth tracker scenario: exact safe metadata/default/order, long HTML-looking 64-character names through HTTP/MessagePack, baseline movement versus configured dead zone during live approach for at least 180 ticks, rematch/leave, and LAN gameplay with guest input on that same configured process.

Missing and damaged-byte/checksum failures are separate real process cases. They assert exact sanitized reasons, no exposed paths, lazy/latched availability, explicit requested/effective identity, advancing gameplay, persistent rematch notice, cleared leave identity, and retry behavior. Auxiliary process cleanup now kills after a graceful-termination timeout and always closes logs.

Worker evidence: 164 .NET tests; frontend behavior/input/protocol/typecheck/build/format; full source integration; fresh macOS ARM64 Native AOT publish, model/catalog/HTTP smoke and full native integration all pass. Coordinator inspected source/native PASS logs and publish warnings. Only prior MessagePack IL3053/IL2104 warnings remain. Optional strict mDNS loopback checks were not enabled; Linux/Windows execution is not claimed. Tree was clean at the boundary.

Carry forward: measure configured prepared sessions directly, including held-axis versus cadence-spaced decision cost; reject fallback so a failed model cannot masquerade as fast inference. Use built-in published diagnostics and scoped managed allocation counters; no BenchmarkDotNet dependency or speculative production change is justified by current research. Reconcile the plan after step 6 before final delivery.
