# Current bot tooling, match contracts and migration

This guide describes the configured runtime and version 9 contracts accepted in
the bot runtime cleanup. Start with the [README](../../README.md) for playing and
the [catalog configuration guide](../bot-catalog/configuration.md) for adding or
changing bots. The [cleanup plan](plan.md) and [review](review.md) record the
implementation and acceptance evidence. Historical measurements and training
results retain their original commands, versions and scope; they are linked below.

## Runtime and developer tools

The application runs configured catalog bots through `PreparedBotSession`.
`Bots/Catalog` owns catalog contracts, `Bots/Configuration` owns validation and
registration, `Bots/Strategies` owns policies and factories, `Bots/Inference`
owns ONNX execution, and `Bots/Runtime` owns preparation, identity and fallback
lifetime. These directories are under [src/LanPong](../../src/LanPong).
`BotRuntime` prepares candidates before admission and reports the requested and
effective bot separately. A configured fallback remains explicit in the
snapshot and UI. The ONNX session uses reusable bindings and buffers.

The production application has no diagnostic command dispatch, benchmark report
types or tiny smoke model. Its direct package references are MessagePack and
ONNX Runtime; tooling, tests and training projects reference the app in the
other direction. SDK-added Native AOT compiler/linker references are distinct
from those direct application references. The native publication includes the
configured model, native ONNX library and built frontend.

The managed [LanPong.BotDiagnostics](../../tools/LanPong.BotDiagnostics)
project owns the retained developer probes. It prepares the same configured
runtime without starting an HTTP listener or game peer. Run these commands
from the repository root:

```bash
dotnet run --project tools/LanPong.BotDiagnostics -c Release -- --onnx-smoke
dotnet run --project tools/LanPong.BotDiagnostics -c Release -- \
  --bot-benchmark vektor --environment Production --contentRoot "$PWD/src/LanPong"
dotnet run --project tools/LanPong.BotDiagnostics -c Release -- \
  --bot-benchmark lada --benchmark-samples 100 --benchmark-warmup 2 \
  --environment Production --contentRoot "$PWD/src/LanPong"
```

`--onnx-smoke` must be the sole argument. It runs the tool's tiny packaged
`2 -> 3` model and does not check the published game's model. For configured
probes, an omitted bot ID selects the catalog default. Samples accept
`100..100000`, warmup accepts `2..100000`, and both default to `20000`.
Duplicate probe/count flags and invalid counts are rejected.

Configured probes accept the normal ASP.NET configuration arguments and
providers. The default content root is the working directory, subject to normal
provider precedence; an explicitly supplied relative root resolves against the
tool's binary directory. Use an absolute root containing the intended
`appsettings.json` and model paths, as above. For a custom catalog, replace that
root with its absolute directory. The tool also copies the default settings and
model into its output, so an explicit absolute tool output directory is another
valid root.

The retained `--bot-benchmark` command is a deterministic prepared-policy probe.
It verifies healthy requested/effective identity and the configured model digest,
and fails if preparation or inference activates fallback. It reports consecutive
and cadence-spaced workloads, timestamp/no-op floors, and calling-thread managed
allocation deltas. These measurements exclude native memory and other threads;
they do not measure the complete game tick or prove an absence of leaks.
BenchmarkDotNet was not introduced, and no new performance comparison was made
as part of this cleanup.

The separate tool smoke and production boundary checks are:

```bash
dotnet build tools/LanPong.BotDiagnostics/LanPong.BotDiagnostics.csproj -c Release
LANPONG_DIAGNOSTICS_DLL="$PWD/tools/LanPong.BotDiagnostics/bin/Release/net10.0/LanPong.BotDiagnostics.dll" \
  python3 tests/integration/bot_diagnostics_smoke_test.py
python3 tests/integration/verify_production_boundary.py
```

## Initial sides and confirmed rematches

In bot mode, the native **Начальная сторона** select offers **Слева** (the
default), **Справа** and **Случайно**. This is a preference for the next admission.
The open tab retains it through rematches, leaving and mode changes; the app
does not persist it across reload. Actual ownership comes from `localSide` in
the accepted snapshot. Random resolves once after successful bot preparation.
The bot takes the opposite physical paddle; horizontal policy projection handles
either bot side while world coordinates and scores stay physical.

LAN host/guest roles express authority and connection responsibilities. The
initial physical host side is chosen randomly when a challenge is accepted;
the guest owns the opposite side. Welcome retries serialize the current
authoritative side and round, including a rematch's swapped assignment, without
a new random draw. LAN modes hide the bot side preference and explain this
initial random assignment. Setup controls lock while an admission, search,
challenge or session is active. The compact selected-bot summary opens the picker on **Изменить**;
the mode select uses **Против бота**, **Быстрая игра по сети** and
**Присоединиться по сети**. A picker selection applies immediately. **Готово**,
**Закрыть** and Escape return to the compact panel without starting a match;
the main **Играть с «имя»** (or **Попробовать «имя»**) action starts the selected bot.

A rematch requires the current match's confirmed finished round. A successful
authoritative transition keeps the match and requested bot selection, increments
`roundId`, swaps physical sides once and clears scores, controls and round
history. The prepared bot session is reused; a controller reset failure can
promote an explicit configured fallback, and effective identity remains truthful
in the snapshot and UI. The initial preference does not choose rematch sides.
Finished scores and names remain in the completed round's orientation until
that transition arrives.

A guest can predict `phase: "gameover"` before the host confirms the result.
During that interval `canRematch` is false and the UI waits for confirmation.
After a guest requests a confirmed rematch, HTTP 200 can still describe the
completed round while the request awaits host acknowledgement. Keep its scores
and names until an authoritative next-round snapshot arrives. An accepted
same-round nonterminal correction cancels the pending request. An authoritative
same-round `canRematch: false` also cancels the browser's pending rematch intent
and planned arena focus even if prediction replay still displays GameOver. The
next-side hint and rematch action follow `canRematch`; a predicted result alone
is insufficient.

## HTTP v9 examples

HTTP snapshots and `GET /api/bots` carry `version: 9`. Start a bot match with a
required canonical side string:

```bash
curl --fail-with-body -sS http://127.0.0.1:5080/api/local-opponent \
  -H 'Content-Type: application/json' \
  -d '{"nickname":"Игрок","botId":"vektor","side":"left"}'
```

Use `"right"` or `"random"` for the other preferences. A missing side, numeric
enum, unknown/composite string, invalid bot, failed preparation without a
playable fallback, or an already active admission/session is rejected with HTTP
400. Endpoint domain errors use `{ "error": "..." }`; invalid request binding
also returns HTTP 400. Successful bot admission identifies its actual side,
requested/effective bot, match and round.

To request a rematch, fetch a current local snapshot and send its exact match
and finished round. This shell example requires `curl` and `jq` and stops if
the result is not yet eligible:

```bash
status=$(curl --fail-with-body -sS http://127.0.0.1:5080/api/status)
restart_body=$(printf '%s' "$status" | jq -ce \
  'if .canRematch then {matchId, expectedRoundId: .roundId} else error("Round is not confirmed finished") end') &&
curl --fail-with-body -sS http://127.0.0.1:5080/api/restart \
  -H 'Content-Type: application/json' -d "$restart_body"
```

`matchId` is a nonempty canonical lowercase 32-hex GUID string; `expectedRoundId`
is the exact positive current round. Missing, malformed, stale or mismatched
identity, or a live/unconfirmed round, is rejected with HTTP 400. Treat even a
successful guest response as a snapshot to reconcile, rather than proof that
the next round has started. `POST /api/leave` ends the current admission/session.

## Browser and LAN contract migration

Upgrade all app instances and browser clients together, and reload old tabs.
Version 9 does not accept earlier browser controls or UDP versions. Clients
must send `side` for bot admission and the current `matchId`/`expectedRoundId`
for restart. A host role no longer implies a left paddle.

Each `/ws` binary snapshot is one MessagePack array with **35 fields**. Fields
0 through 29 retain their existing order:

```text
0 version, 1 role, 2 connection, 3 message, 4 udpPort,
5 localAddresses, 6 peerAddress, 7 leftY, 8 rightY,
9 ballX, 10 ballY, 11 ballVx, 12 ballVy, 13 leftScore, 14 rightScore,
15 phase, 16 countdown, 17 tick, 18 roundId, 19 pingMs, 20 recentEvents,
21 localNickname, 22 peerNickname, 23 opponentMode,
24 requestedBotId, 25 requestedBotName, 26 effectiveBotId, 27 effectiveBotName,
28 opponentFallbackActive, 29 botFallbackReason
```

The v9 tail is:

| Ordinal | Field | Meaning |
| --- | --- | --- |
| 30 | `localSide` | Physical paddle: MessagePack `1` left, `2` right, or nil; HTTP `"left"`, `"right"`, or null |
| 31 | `matchId` | Current admitted match's canonical GUID string, or null before assignment |
| 32 | `sourceId` | Lifetime identity of this local `PongPeer` |
| 33 | `snapshotSequence` | Increasing capture sequence within that source |
| 34 | `canRematch` | Current connected round is confirmed finished and eligible |

`localSide` and `matchId` remain null before assignment, including an awaiting
LAN guest. Compare capture sequences only within one source. HTTP and WebSocket
captures share the same incrementing sequence under the peer's gate; capture
order is independent of physics ticks and rounds. Tick rollback/rebasing is
legitimate, so tick alone cannot order snapshots. Host and guest have different
local source IDs even when they share a match. A fresh source needs a new
sequence baseline; retired-source responses must not restore stale state.
The browser fences a superseded successful bot-start HTTP reply against the
accepted snapshot. If that reply carries a different source, a bounded fresh
status request reconciles it before the reply can restore a stale match. New
HTTP/WebSocket source baselines can otherwise be accepted by the session.

Browser controls are one MessagePack value per frame:

```text
[9, matchId, roundId, axis]
```

The match ID is canonical and nonempty, the round is positive, and `axis` is
`-1`, `0` or `1`. The server applies controls only to the current match/round and
physical owner. Clear held input on transition and require a fresh press; old
controls cannot move a new round's paddle. See
[BrowserWebSocketProtocol.cs](../../src/LanPong/BrowserWebSocketProtocol.cs) and
[the browser decoder](../../frontend/src/wsProtocol.ts) for exact
serialization and validation.

UDP v9 Welcome includes the host's physical side and current round; State carries
host side; Restart carries the expected completed round. Both rollback and
prediction timelines apply that assignment. UDP decoding uses the generated
MessagePack serializer once, requires `reader.End`, and retains the 1200-byte
cap, protocol version and decoded-value validation. The previous `HasValidShape`
pre-pass was removed. See [WirePacket.cs](../../src/LanPong/WirePacket.cs).

## Published application checks and evidence

Use a fresh empty publish directory. From the repository root, the macOS ARM64
commands below publish the app and run the real-game smoke and full integration
suite against that exact executable:

```bash
dotnet publish src/LanPong/LanPong.csproj -c Release -r osx-arm64 \
  -o .artifacts/publish/osx-arm64
python3 tests/integration/verify_publish_contents.py .artifacts/publish/osx-arm64
LANPONG_TEST_BINARY="$PWD/.artifacts/publish/osx-arm64/LanPong" \
  python3 tests/integration/published_smoke_test.py
LANPONG_TEST_BINARY="$PWD/.artifacts/publish/osx-arm64/LanPong" \
  python3 tests/integration/integration_test.py
python3 tests/integration/verify_production_boundary.py --runtime osx-arm64
```

Use the target's corresponding runtime and executable name for another platform.
The published app runs without the .NET SDK; these developer test scripts need
their Python/Node harness dependencies. Without `LANPONG_TEST_BINARY`, the full
integration script builds and starts the source application.

The published smoke uses real Vektor through public HTTP and WebSocket APIs.
It verifies the frozen model hash, healthy requested/effective identity, entry
into Playing, advance through at least two nine-tick inference opportunities,
bot paddle motion, capture identity and the native ONNX library/static frontend.
It does not execute the tiny tool model. The frozen model SHA-256 remains
`5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a`.

Cleanup acceptance passed the source suite, a fresh macOS ARM64 Native AOT
publish and full native suite, 215 .NET tests, frontend checks, managed tool
smoke and production/publish boundary checks. Side lifecycle coverage includes
Left/Right/Random bot admission, genuine finished bot and LAN rematches,
host/guest ownership on both physical sides, fenced stale controls and recovery
after lost initial/retried Welcome packets. Browser acceptance covers the native
selectors, focus and input transitions, compact layout and touch controls.
Detailed evidence and limitations are recorded in the [review](review.md).

The full source run preceded the final Random observer refinement; a focused
source case then verified that refinement under delayed snapshot decoding. The
complete native run used the final harness. Unchanged policy/training and browser
evidence were carried from their accepted steps.

Linux x64 and Windows x64 release smoke are configured in CI; their executions
were not performed on the local macOS host. Strict mDNS acceptance was not run.
The existing MessagePack IL2104/IL3053 publication warnings remain. This work
does not establish a speedup, left/right win-rate symmetry, native allocation
behavior, leak freedom, screen-reader acceptance or other browser-engine results.

## Training and historical records

The training tool's `--backend production` now prepares a strict configured
`OnnxLocalOpponentController` through
[EvaluatedModelPolicy](../../tools/LanPong.TrainingData/EvaluatedModelPolicy.cs),
with the canonical frozen hash and nine-tick cadence. It requires an explicit
`--student-model`; load or inference failure aborts evaluation. It does not
resolve catalog fallback chains or silently substitute Simple. The default
backend remains the offline student. No model retraining was performed during
the cleanup; the existing 600-action real-ONNX golden and production/offline
policy parity were preserved.

The [historical Hard runtime record](../bots/BOT_HARD_RUNTIME.md) retains its
version 7 acceptance, frozen Hard v1 model, seeds, trajectory results, hashes
and removed CLI commands.
The [catalog measurements](../bot-catalog/performance.md) and
[performance review](../bot-catalog/performance-review.md) preserve all original
18 captures, tables, timing outlier, command blocks and source-line evidence.
They are historical measurements from the earlier source layout. Use this
guide and the [configuration guide](../bot-catalog/configuration.md) for current
commands and contracts.
