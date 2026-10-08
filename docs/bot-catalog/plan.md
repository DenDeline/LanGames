# Configurable bot catalog and opponent experience

## Goal and working agreement

Replace hardcoded simple/hard opponent selection with an appsettings-driven bot catalog and redesign the opponent experience using publicly documented Chess.com bot-selection patterns as reference. Breaking contract changes are authorized. Preserve LAN play, deterministic gameplay, and Native AOT support.

The coordinator performs research, decomposition, coordination, and review. Its only repository writes are Markdown files in `docs/bot-catalog/`. A separate implementation chat executes exactly one assigned step at a time, runs appropriate checks, makes a conventional commit, reports evidence, and stops until the coordinator assigns the next step. No publishing or remote push is part of this goal.

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
| 1 | Typed, validated appsettings bot catalog, reusable behavior settings, stable IDs, and immutable lookup | Assigned |
| 2 | Registered bot strategies, truthful availability/fallback, and session lifecycle resolving a selected entry | Ready after step 1 review |
| 3 | Catalog-based browser API and versioned session contracts, replacing simple/hard public selection | Ready after step 2 review |
| Checkpoint A | Review steps 1–3 together; reconcile configuration, availability, migration, and UI needs | Required |
| 4 | Responsive, accessible opponent catalog and selected-bot details integrated with existing gameplay/LAN UI | Ready after checkpoint A |
| 5 | End-to-end regression coverage for config-only extra bots, errors, cleanup, fallback, and LAN coexistence | Ready after step 4 review |
| 6 | Evidence-based .NET 10/C# 14 runtime optimization and Native AOT verification | Ready after step 5 review |
| Checkpoint B | Review steps 4–6 together; reconcile performance, UX, and final validation needs | Required |
| 7 | Final published-app validation, configuration/operator documentation, and delivery evidence | Ready after checkpoint B |

## Step boundaries and review gates

Implementation chat: **Implement configurable bot catalog**, thread `01a11af7-357d-7593-a957-5a83a56b56b3`, project `lanpong`, same checkout. Only step 1 is currently assigned. The worker creates `codex/bot-catalog`, commits the bounded step, and stops for review. The coordinator is authorized by the human to send subsequent step assignments to this chat.

Research and architecture decisions are in [research.md](research.md). They are a specification for this feature, with routine implementation detail left to the worker.

1. **Catalog/configuration:** Implement typed options, generated binding, startup/cross-entry validation, immutable definitions/lookup, and at least three configured entries. Validate ID uniqueness, strategy registration, defaults, settings/ranges, fallback references/cycles, and disabled entries. Add focused configuration tests. Keep gameplay/API unchanged at this step so the foundation is independently reviewable. Commit `feat(bots): add validated configurable catalog` or equivalent.
2. **Runtime/lifecycle:** Implement registered tracker/ONNX factories, configurable policy parameters/model settings, generic selection/lifetime, and explicit fallback identity. Preserve model schema/default cadence and training tools. Test multiple entries sharing a strategy, reset/rematch, lazy model loading, failure/cleanup, and rejected selection preserving the current session. Temporary compatibility methods may keep the old browser API compiling until step 3. Commit `refactor(bots): resolve opponents through strategy registry` or equivalent.
3. **Contracts:** Add catalog discovery and bot-ID start; change generic local-bot snapshot identity across source-generated HTTP JSON, versioned browser MessagePack, TypeScript decoding, and directly affected fixtures/tests. Remove public hard/simple selection. Adapt existing UI wiring minimally if required for a coherent build, leaving catalog UX for step 4. Test invalid requests, HTTP/WS parity, selected/effective identity, and unchanged LAN UDP encoding. Commit `feat(api)!: select catalog bots by id` or equivalent.
4. **UX:** Replace the two bot forms with native selectable catalog cards, grouped difficulty guidance, selected profile and one Play action. Include loading/error/retry/empty/unavailable states, identity in score/session/overlays, persistent configured fallback notices, rematch and returning to selection, and keyboard/mobile support. Protect LAN actions, touch/input/sound settings. Validate real browser desktop and narrow layout. Commit `feat(ui): add opponent catalog and selected bot profiles` or equivalent.
5. **Integration:** Prove a config-only extra bot is discoverable, selectable, and correctly identified through HTTP/WS; exercise custom behavior, invalid/disabled IDs, missing/corrupt model and fallback, session transitions/rematch/cleanup, and existing two-process LAN. Add tests for genuine uncovered risks, then run existing appropriate suites. Commit `test(bots): cover configurable catalog and session transitions` or equivalent.
6. **Optimization/AOT:** Inspect the implemented hot path; preserve/reduce managed allocations with concrete changes only where evidence supports them. Verify no binding, registry lookup, DOM rebuilding, or rich metadata construction runs per tick. Benchmark the configured runtime path and publish Native AOT on macOS ARM64; run model/HTTP/catalog smoke and inspect warnings. If the design is already optimal, record evidence instead of artificial code churn. Commit `perf(bots): ...` when optimizing, or `test(bots): verify native catalog runtime` if validation is the outcome.
7. **Delivery:** Update operator/user docs and migration details (including historical docs references if necessary), record reproducible config examples and verification results, rerun only checks warranted by final changes, and inspect final clean history. Commit `docs(bots): document catalog configuration and migration` or equivalent.

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
