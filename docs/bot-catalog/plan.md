# Configurable bot catalog and opponent experience

## Goal and working agreement

Replace hardcoded simple/hard opponent selection with an appsettings-driven bot catalog and redesign the opponent experience using publicly documented Chess.com bot-selection patterns as reference. Breaking contract changes are authorized. Preserve LAN play, deterministic gameplay, and Native AOT support.

The coordinator performs research, decomposition, coordination, and review. Its only repository writes are Markdown files in `docs/bot-catalog/`. A separate implementation chat executes exactly one assigned step at a time, runs appropriate checks, makes a conventional commit, reports evidence, and stops until the coordinator assigns the next step. No remote deployment, release publication, or remote push is part of this goal; local Native AOT publishing is a validation step.

## Coordinator workflow

1. Review existing bot runtime, browser contracts, UI, tests, and build constraints.
2. Research Chess.com's public bot UX and existing .NET/frontend libraries before implementation; distinguish observed UX from undocumented backend architecture.
3. Refine the steps and acceptance criteria below and create one implementation chat.
4. Review each step's diff, tests, and conventional commit before assigning the next step. Delegate independent read-only reviews where useful.
5. Reconcile this plan after steps 3 and 6, incorporating findings before further implementation.
6. Validate the final feature, record remaining limitations, and complete the Codex goal only when required work is finished.

## Initial implementation decomposition

| Step | Outcome | Status |
| --- | --- | --- |
| 1 | Typed, validated appsettings bot catalog, reusable behavior settings, stable IDs, and immutable lookup | Reviewed: `d2c7b1b` |
| 2 | Registered bot strategies, truthful availability/fallback, and session lifecycle resolving a selected entry | Reviewed: `a34d425` |
| 3 | Catalog-based browser API and versioned session contracts, replacing simple/hard public selection | Reviewed: `2881f34` |
| Checkpoint A | Review steps 1–3 together; reconcile configuration, availability, migration, and UI needs | Complete |
| 4 | Responsive native radio-card catalog, selected profile, honest availability, and one Play action | Reviewed: `3083f37` |
| 5 | Close actual gaps: config-only fourth bot, UI states, configured behavior and browser/LAN integration | Assigned |
| 6 | Benchmark actual configured runtime; optimize evidenced costs and verify Native AOT | Ready after step 5 review |
| Checkpoint B | Review steps 4–6 together; reconcile performance, UX, and final validation needs | Required |
| 7 | Final published-app validation, configuration/operator documentation, and delivery evidence | Ready after checkpoint B |

## Step boundaries and review gates

Implementation chat: **Implement configurable bot catalog**, thread `01a11af7-357d-7593-a957-5a83a56b56b3`, project `lanpong`, same checkout. Branch: `codex/bot-catalog`. Steps 1–4 and checkpoint A are reviewed; only step 5 is currently assigned. The worker commits each bounded step and stops for review. The coordinator is authorized by the human to send subsequent step assignments to this chat.

Research and architecture decisions are in [research.md](research.md). They are a specification for this feature, with routine implementation detail left to the worker.

1. **Catalog/configuration:** Implement typed options, generated binding, startup/cross-entry validation, immutable definitions/lookup, and at least three configured entries. Validate ID uniqueness, strategy registration, defaults, settings/ranges, fallback references/cycles, and disabled entries. Add focused configuration tests. Keep gameplay/API unchanged at this step so the foundation is independently reviewable. Commit `feat(bots): add validated configurable catalog` or equivalent.
2. **Runtime/lifecycle:** Implement registered tracker/ONNX factories, configurable policy parameters/model settings, generic selection/lifetime, and explicit fallback identity. Preserve model schema/default cadence and training tools. Test multiple entries sharing a strategy, reset/rematch, lazy model loading, failure/cleanup, and rejected selection preserving the current session. Temporary compatibility methods may keep the old browser API compiling until step 3. Commit `refactor(bots): resolve opponents through strategy registry` or equivalent.
3. **Contracts:** Add catalog discovery and bot-ID start; change generic local-bot snapshot identity across source-generated HTTP JSON, versioned browser MessagePack, TypeScript decoding, and directly affected fixtures/tests. Remove public hard/simple selection. Adapt existing UI wiring minimally if required for a coherent build, leaving catalog UX for step 4. Test invalid requests, HTTP/WS parity, selected/effective identity, and unchanged LAN UDP encoding. Commit `feat(api)!: select catalog bots by id` or equivalent.
4. **UX:** Replace the two bot forms with native selectable catalog cards, grouped difficulty guidance, selected profile and one Play action. Include loading/error/retry/empty/unavailable states, identity in score/session/overlays, persistent configured fallback notices, rematch and returning to selection, and keyboard/mobile support. Protect LAN actions, touch/input/sound settings. Validate real browser desktop and narrow layout. Commit `feat(ui): add opponent catalog and selected bot profiles` or equivalent.
5. **Integration:** Extend the real configuration-provider/process fixture with a playable extra tracker, a long HTML-looking name, distinctive tuning, and exact metadata/default/order assertions. Prove selection and HTTP/MessagePack identity, observable tuning, rematch/leave, and a short LAN round using that configured process. Reuse the missing-model process assertions for a real corrupt/wrong-checksum model and explicit fallback. Existing unit and frontend coverage already proves lifecycle/concurrency, invalid IDs, text rendering, and fourth-ID form submission; avoid duplicating it. Run appropriate existing suites. Commit `test(bots): cover configurable catalog and session transitions` or equivalent.
6. **Optimization/AOT:** Inspect the implemented hot path; preserve/reduce managed allocations with concrete changes only where evidence supports them. Verify no binding, registry lookup, DOM rebuilding, or rich metadata construction runs per tick. Benchmark the configured runtime path and publish Native AOT on macOS ARM64; run model/HTTP/catalog smoke and inspect warnings. If the design is already optimal, record evidence instead of artificial code churn. Commit `perf(bots): ...` when optimizing, or `test(bots): verify native catalog runtime` if validation is the outcome.
7. **Delivery:** Update operator/user docs and migration details (including historical docs references if necessary), record reproducible config examples and verification results, rerun only checks warranted by final changes, and inspect final clean history. Commit `docs(bots): document catalog configuration and migration` or equivalent.

## Checkpoint A reconciliation — after step 3

Reviewed commits: `d2c7b1b`, `a34d425`, `2881f34`. Foundation, runtime, and browser contracts fit the original goal. Configuration-driven entries share registered strategies, generated binding survives Native AOT, session ownership and explicit fallback are tested, and public discovery/start/snapshots are generic. Browser protocol is now v8 with 30 fields; LAN UDP is unchanged. Temporary adapters and production model-path override are removed. Independent backend/frontend review found no blockers.

Refinements before further work:

- Step 4 should replace the minimal dropdown with native radio cards and a selected profile. Prefer the remembered/default entry only while `canPlay` is true; otherwise choose the first eligible entry, or show the unavailable-only/empty state with LAN actions reachable.
- A known-unavailable entry with a playable configured fallback remains selectable but must explain that substitution before Play. An unchecked model is eligible for an attempt, not reported as already verified.
- Keep catalog rendering independent of snapshots and preserve the completed overlap guard and refresh-on-failure/rematch behavior. Bot identities from snapshots remain usable if catalog loading fails.
- Use player-facing style descriptions rather than ONNX/runtime terminology in the visible product copy. Native strategy/model details belong in operator documentation.
- Step 5 should add only genuine coverage gaps: a fourth bot added through configuration alone, custom tuning/long or HTML-looking metadata, catalog failure/retry/unavailable states, and browser selection/input behavior. Reuse the existing 164-test lifecycle, fallback, concurrency, contract, and LAN coverage instead of duplicating it.
- Step 6 must measure the real configured `BotRuntime`/prepared session path, not only the retained legacy Hard diagnostic. Preserve the warmed preallocated model path and cached identity. Optimize only observed costs; report managed allocations and latency separately from native memory.
- Step 7 must document required `botId`, v8 identity/migration, content-root model paths, array/provider overrides, explicit fallback chains, and availability/configuration changes taking effect after restart.

No extra production library, preview .NET migration, UDP contract change, hot reload, or active-session replacement is justified by this checkpoint.

## Acceptance criteria to refine after research

- Adding another bot using an already registered strategy requires appsettings changes only, with no bot-specific backend or UI branch.
- Catalog entries have stable IDs, user-facing metadata, behavior parameters, display ordering/grouping, and truthful runtime availability.
- Invalid configuration is diagnosed predictably; unavailable model bots do not silently impersonate another bot.
- Browser clients discover and select bots by ID. Unknown/unavailable selections receive an explicit response and leave the session coherent.
- UI provides browsable opponents, clear difficulty guidance, a selected opponent summary, and a clear play action, with keyboard/mobile support.
- Native AOT, deterministic gameplay, existing model behavior, and LAN play continue to work.
- Research records evaluated libraries and explains reuse/addition decisions. Optimizations have a reason and validation evidence.

## Research and review log

- 2026-10-08: Initial decomposition recorded before implementation. Repository is clean on `main`; project already targets .NET 10 and C# 14 and enables Native AOT. Research and baseline review are next.
- 2026-10-08: Parallel backend, UX, and library research supports built-in options/generated binding, a registered strategy factory seam, immutable lookup, and native HTML selection. Public Chess.com UX informs catalog/selection flow; no undocumented backend claims are used. The plan is ready to dispatch step 1.
- 2026-10-08: Step 1 dispatched to the separate implementation chat. Research also evaluated FluentValidation, Scrutor, Lit, Web Awesome, and Shoelace; built-in framework options and native HTML controls best fit the focused refactor.
- 2026-10-08: Step 1 reviewed at `d2c7b1b`. Sixteen catalog tests and 122 total .NET tests pass; frontend/build, existing integration, Native AOT publish/smoke pass. Only prior MessagePack IL3053/IL2104 publish warnings remain. The worker removed configuration reload callbacks so invalid live edits cannot affect startup-only options. Step 2 is next; no plan reconciliation checkpoint is due yet.
- 2026-10-08: Step 2 reviewed at `a34d425`. All 160 .NET tests, frontend/build, source integration, Native AOT smoke and full published integration pass. Controllers use configured tuning and preprepared fallback chains; ownership/disposal and one-admission concurrent starts are covered. Model availability is honest (`not checked` before lazy initialization) and known failures remain latched until restart. Step 3 must remove temporary browser adapters and use safe public descriptors; checkpoint A follows that step.
- 2026-10-08: Step 3 reviewed at `2881f34`, with 164 .NET tests plus frontend/format/build, source integration, Native AOT smoke and full published integration passing. Safe catalog discovery, required IDs, separate bot-name bounds, and v8 requested/effective identity are covered. Checkpoint A reconciled the remaining steps as above; only step 4 is dispatched next.
- 2026-10-08: Step 4 reviewed at `3083f37`. Frontend/format/build, Release build and 20 catalog/API tests pass. Desktop/320/390 browser evidence includes retry, fallback, long metadata and long-name game-over layouts. Coordinator independently confirmed native arrow selection, successful launch visibility/focus and leave at 390px. Source review accepts bounded start/rematch status reconciliation with exact bot/round guards. Step 5 is assigned only to real process-level coverage gaps.
