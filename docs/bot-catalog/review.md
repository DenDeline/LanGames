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
