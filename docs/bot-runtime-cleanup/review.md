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

## Step 3 — developer tool boundary

Reviewed commit: `053868d6afdc1a24e1a518f42dab56a17d61129b` (`refactor(tools): isolate bot diagnostics from production`).

`tools/LanPong.BotDiagnostics` owns configured probes/report records/generated report JSON, tiny ONNX probe and its model. Production no longer compiles or dispatches them. The tool references the app one way through internal friend access, explicitly applies the native telemetry guard and owns a built-but-unstarted WebApplication using normal providers. Root normalized the two moved diagnostic bodies and found them unchanged. Fixture tests/generator/solution references migrate coherently; the normal production telemetry guard and real model remain.

Review corrected two issues before acceptance: arbitrary publish-name/extension bans would reject legitimate custom/static/framework assets, and a relative content-root help example resolved against the wrong base. The final denylist uses known developer stems/result directories, and its temporary fixtures demonstrate both legitimate acceptance and developer-payload rejection. The corrected absolute-root help command produced a healthy real-model report. A deliberately stalled shared Node decoder terminated at the configured five-second timeout.

Root inspected the recorded Release/osx-arm64 evaluation: 43 Compile inputs, no production ProjectReference, direct package references only MessagePack/ONNX Runtime, real model only, and resolved managed/native targets without developer dependencies. New reusable graph and managed tool smoke checks run separately in CI. Production release smoke uses native-library/model identity and ordinary API/WebSocket Playing/cadence/paddle evidence. This is source/reference/payload plus actual production inference proof, rather than binary string-search inference.

Evidence in `.artifacts/bot-runtime-cleanup-step3/`: 173 tests; managed probe configuration/default/explicit/relative-model/provider precedence and failure/no-listener checks; syntax/generator/allow-deny/stalled-decoder checks; fresh AOT publish and payload check; real-model production smoke; full native integration including fallback and LAN. Existing MessagePack warnings remain. Native execution is macOS ARM64; Linux/Windows workflow configuration is not local execution evidence. Independent review accepts the final source and artifacts. Worker stopped cleanly at checkpoint A. Step 3 is accepted, and the coordinator reconciled the remaining plan before UI work.

## Step 4 — baseline captured before edits

Root inspected `.artifacts/bot-runtime-cleanup-step4/baseline.json` and the desktop screenshot. With default Lada, idle role, loaded fonts, zoom 1 and device pixel ratio 1, the connect panel measures 1334.31px at 1366×900, 1308.31px at 390×844 and 1358.19px at 320×800. Horizontal scroll width equals viewport width in each baseline. Final measurements must use the same conditions; the implementation and its acceptance remain pending.

## Step 4 — compact native modes and bot picker

Implementation and validation are accepted before the worker's conventional step commit; its hash will be recorded after the clean stop. The native three-mode select shows only the relevant idle setup. A selected-profile summary and one Play action replace the permanent card grid; grouped native radios and full profile are available in a bounded modal. No UI dependency is added. Sharing, challenge, match, fallback, rematch and leave remain outside the setup panels.

Review corrected fresh-tab LAN presentation, preserved quick-game origin through a successful HTTP reply superseded by a pre-action idle WebSocket frame, and retained the newer session when a quick request fails. These presentation fixes keep the existing snapshot staleness and exact bot/round guards. Busy/active/searching state locks setup. Picker focus is synchronous, invalidated by active arrival/close, scrolls an eligible selection into view and cannot override accepted arena focus. A small Close/Done endpoint guard closes Chrome's native Tab boundary gap; interior radio navigation stays native. All open-dialog descendants are excluded from paddle input.

Final `.artifacts/bot-runtime-cleanup-step4/browser-check.json` passes all 13 Chrome scenarios, including large catalogs, long safe text/reasons, all-unavailable/error/retry-after-close, active arrival, fresh LAN host/guest/search, quick guest context, action races, real Play/leave and layout/focus. At identical default baseline conditions, panel height is 392.42px on 1366×900 (70.59% reduction), 390.42px on 390×844 (70.16%) and 390.42px on 320×800 (71.25%). The 1024px layout also passes. No horizontal overflow; extra entries do not grow the closed panel. All 173 .NET tests, frontend behavior/input/v8 protocol/typecheck/format and full source integration pass.

Root independently reviewed the idle preview through the in-app browser: desktop layout, native radio selection, forward/reverse endpoint Tab wrap, Escape/opener focus, manual LAN mode, retained Vektor selection, and the 320px dialog (296×760 at x=12/y=20 with scroll width 320). Real Vektor start showed matching requested/effective identity, locked setup and arena focus; leave restored setup, and the 390px layout was inspected. The temporary review tab was closed and viewport reset. Chrome and in-app browser evidence does not claim screen-reader speech or other browser engines. Fresh LAN context fixtures supplement the full real source network integration.
