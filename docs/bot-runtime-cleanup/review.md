# Coordinator review record

## Step 1 — canonical runtime and legacy removal

Reviewed commit: `325021cb0f37042dc541f219a5383b1a6b902146` (`refactor(bots): remove legacy opponent controllers`).

The supported tracker is now `TrackerBotPolicy`; the strict configured ONNX policy uses independently named `IOnnxInferenceSession`, `OrtOnnxInferenceSession` and frozen model constants. Live source has no legacy Hard/Simple controller or Hard diagnostic callers. The ONNX adapter preserves the warmed reusable buffers and model contract. The legitimate `hard-v1.onnx` artifact and digest remain unchanged. The pre-existing user `System.Threading.Lock` edit is included explicitly.

Training production evaluation now prepares the supported strict ONNX policy and propagates load/inference failures. It no longer substitutes a baseline. Historical report labels/schema remain for artifact comparison, with successful production fallback fields false/zero/null. Tests verify rejection before report creation and preservation of an existing report, disposal and failure handling, canonical golden actions/checkpoints across reset, and frozen model schema/hash. CLI evidence compares production/offline evaluation and four historical direct-duel trajectories.

The published test now requires real Vektor Playing state, two advanced cadence intervals, paddle movement and matching healthy HTTP/WebSocket identity. Review found that importing shared WebSocket helpers initially executed the full integration runner. Moving the unchanged runner under a `main` entry point fixes those import side effects. Fresh bounded smoke and full source/native integration pass after that correction; release jobs already provide the Node/MessagePack fixture dependency.

Validation inspected from `.artifacts/bot-runtime-cleanup-step1/logs/`:

- 173 .NET tests passed with zero failures/skips.
- Frontend behavior/input/protocol checks, typecheck/build and formatting passed.
- Full source and published native integration passed, including configured fourth bot, tuned movement, missing/corrupt-model explicit fallback, identity/lifecycle, UDP/LAN/quick-game flows and shutdown.
- Training CLI parity, historical trajectories and missing/wrong-digest no-report failures passed.
- Fresh macOS ARM64 Native AOT publish, publish contents and bounded real-model smoke passed. Existing MessagePack IL3053/IL2104 warnings remain; no other platform execution is claimed.

Working tree was clean at the worker's stop. Remaining configured diagnostics and tiny ONNX smoke are intentionally pending step 3. Step 1 is accepted; no other implementation step is authorized by this review record.

Independent harness review also accepts the import boundary and unchanged runner. The shared Node decoder has no subprocess timeout; address that bounded validation robustness alongside step 3's published-smoke changes. A release matrix is configured for other platforms, but local evidence is macOS ARM64 only.

## Step 2 — folders and namespaces

Reviewed commit: `d84f8c409fe40a785eee810b0693f04c9d2e4fd1` (`refactor(bots): organize configured runtime by responsibility`).

Seventeen production files move under `Bots/Configuration`, `Bots/Catalog`, `Bots/Strategies`, `Bots/Inference` and `Bots/Runtime`, with matching namespaces and explicit imports in app, training, tests and the temporarily retained diagnostics. Root's read-only comparison confirms every moved file body is identical after excluding using/namespace lines. Independent review finds coherent responsibilities and preserved assembly/internal access, telemetry initialization, frozen policy behavior and user Lock. Network/physics/input code retains its placement and behavior.

Inspected `.artifacts/bot-runtime-cleanup-step2/logs/` and saved generated sources: all 173 tests pass; training builds cleanly and production/offline evaluation matches four historical trajectories exactly; generated binding, JSON, request delegates and native imports use the new types; fresh AOT publication and bounded healthy Vektor gameplay/HTTP/WebSocket smoke pass. Only the existing MessagePack AOT/trim warnings remain. Execution evidence is macOS ARM64. The worker stopped with a clean tree. Step 2 is accepted; remaining diagnostics are still pending step 3.
