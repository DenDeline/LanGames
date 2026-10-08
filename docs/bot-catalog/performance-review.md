# Focused configured-runtime performance review

Reviewed 2026-10-08, after API step 3 and while UI step 4 was in progress. Read-only review used `dotnet-diag:analyzing-dotnet-performance`; no code, builds, or benchmarks were run by the reviewer.

Scope: nine complete C# files, 1,535 lines at review time: BotRuntime.cs (294), OnnxLocalOpponentController.cs (90), SimpleLocalOpponentController.cs (76), HardLocalOpponentController.cs (223), RightBotObservationV1.cs (99), PongPeer.cs (501), PongPeer.Clock.cs (239), BotSessionIdentity.cs (5), ILocalOpponentController.cs (8). Auxiliary checks confirmed GameState is a value type and GameEngine.Capture retains the existing event-history reference.

Reference coverage is complete for selected critical, memory/string, collections/LINQ, structural, and async recipes. Regex and serialization references were not selected because their signals were absent.

## Scan execution checklist

Counts are matching source lines across the scoped files, before manual hot-path filtering.

| Recipe | Hits | Context |
| --- | ---: | --- |
| Literal IndexOf without comparison | 0 | |
| Substring | 0 | |
| Literal StartsWith/EndsWith without comparison | 0 | |
| Literal string Contains without comparison | 0 | |
| Parameterless ToLower/ToUpper | 0 | |
| Three chained Replace calls | 0 | |
| params signatures | 0 | |
| LINQ All/Any with char predicates | 0 | |
| Static readonly Dictionary | 0 | |
| Static readonly FrozenDictionary inverse | 0 | Instance tables here |
| Explicit method-body new List | 1 | Preparation, BotRuntime.cs:100 |
| Explicit method-body new Dictionary | 1 | Construction, BotRuntime.cs:28 |
| StringComparer.CurrentCulture | 0 | |
| Select/Where/Cast/Take/Aggregate | 0 | |
| Unsealed class declarations | 0 | |
| Sealed class declarations including partials | 10 | Nine unique classes |
| Sealed record declarations | 2 | All 11 unique leaf reference types sealed |
| async void | 0 | |
| Result/Wait/GetAwaiter/GetResult candidates | 0 | No synchronous async blocking |
| stackalloc | 0 | |
| All Replace calls | 0 | |
| All IndexOf calls | 0 | |
| All StartsWith/EndsWith/Contains calls | 0 | |
| string.Format | 0 | |
| ToString calls | 0 | |
| += | 2 | Numeric arithmetic; zero string accumulation |
| Regex construction/compiled/generated signals | 0 | |
| ContainsKey | 0 | |
| Broader LINQ candidates | 4 | Two metadata SequenceEqual, two LAN DNS FirstOrDefault; no configured tick |
| Explicit array allocations | 9 | Six retained buffers, static feature names, convenience Encode, catalog response |
| HttpClient | 0 | |
| JSON serialization/options signals | 0 | |
| Task.Run | 2 | Peer initialization and bot preparation |
| ToCharArray/IndexOfAny | 0 | |
| Frozen dictionary fields | 1 | Factory registry |
| Mutable dictionary fields | 1 | Runtime availability must change |
| TryGetValue | 5 | No redundant ContainsKey check |
| ReadOnlySpan/Span declarations | 3 | Observation encoding and inference boundary |
| IDisposable/IAsyncDisposable declarations | 5 | Includes one interface |
| ValueTask occurrences | 1 | DisposeAsync; no repeated await |
| ToLowerInvariant/ToUpperInvariant | 2 | Model hashing at initialization |
| BlockingCollection/manual queue candidates | 0 | |
| Retirement/identity allocation and guard candidates | 7 | No recurring identity allocation after tracing |

Manual checks also covered repeated enumeration, temporary-buffer lifetime, boxing, multiple ValueTask consumption, cross-method/branched allocation, and sibling consistency. No actionable instance was found.

## Findings and positive evidence

No production change is warranted by this static scan.

- PreparedBotSession.GetAxis directly calls its prepared candidate on success; availability mutation/retirement occur only after exceptions (BotRuntime.cs:229).
- Configured ONNX decisions use retained observation/logit arrays, scalar validation, and existing inference resources (OnnxLocalOpponentController.cs:56).
- OnnxBotInference reuses arrays, OrtValues, run options, and bindings (HardLocalOpponentController.cs:134,195).
- Production controllers call the span-taking observation overload (RightBotObservationV1.cs:37); tracker decisions use scalar state/settings (SimpleLocalOpponentController.cs:33).
- The identity reference guard bypasses identity construction and message formatting until the effective definition changes (PongPeer.cs:153).
- Native preparation occurs before admission and outside the state lock (PongPeer.cs:178).

Preparation allocates candidate/visited collections, controllers, buffers, hashes, metadata, and native sessions. Failure transitions can allocate availability/retirement records and update identity; failed candidates are retired rather than retried every tick. These costs are separate from steady successful decisions. The legacy Hard wrapper's failure disposal is outside configured gameplay.

Native cleanup occurs after the state lock releases but synchronously on the clock continuation (PongPeer.Clock.cs:59). A slow disposal could delay the following tick. No disposal duration or delayed tick has been measured; this is a measurement limitation, not a demonstrated regression. GetAxis measurements will not cover cleanup latency, so background cleanup is not justified by current evidence.

Static review does not prove zero allocation inside ONNX Runtime, full-tick allocation freedom, native allocation freedom, or latency bounds. Step 6 must verify the scoped workload in managed Release and published Native AOT.

| Severity | Count | Top issue |
| --- | ---: | --- |
| Critical | 0 | None |
| Moderate | 0 | None |
| Informational actionable findings | 0 | None |

> These AI-generated scan results may miss issues or include false positives. Verify performance conclusions with scoped measurements and human review before applying production changes.
