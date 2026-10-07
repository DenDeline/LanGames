# Bot implementation plan

Status: steps 1–4 complete and reviewed; step 5 pending. The coordinator owns this plan and reviews code; implementation belongs to a separate Codex worker task. The worker takes one step at a time and ends each step with a conventional commit. The coordinator reconciles this plan after steps 1–3 and again after steps 4–6.

## Goal and acceptance

- A player can start a local match against a computer controlled second paddle, play through scoring and rematches, and return to the existing LAN flow.
- **Simple** uses a deterministic .NET controller based on game state and geometry. It is a useful pilot with a deliberately limited reaction rate and reachable paddle movement.
- **Hard** uses a model trained in Python and exported to ONNX. .NET performs inference inside the game process, with bounded work per tick and a defined fallback if the model cannot load or return a valid action.
- The training procedure, dataset generation, model schema, seeds, evaluation results, and exact reproduction commands are documented. Hard must win at least 65% of 200 held-out, paired seeded matches against Simple before it is presented as a harder mode; use side swaps or equivalent controls against side bias and report the uncertainty interval.
- Existing two-computer LAN play, browser protocol, tests, and supported publish targets continue to work. Any intentional protocol/contract changes are versioned and tested.

## Repository review and library research (completed 2026-10-07)

- `GameEngine.Advance(dt, leftAxis, rightAxis)` is the authoritative deterministic physics; it accepts three legal axes per paddle and runs at 60 Hz. Its checkpoint API supports exact headless rollouts. The browser sends one axis through WebSocket to `PongPeer`; the host currently gets the second axis from guest UDP input and rollback. `PongPeer.ClockAsync` stops simulation when there is no UDP socket, so local play needs its own clock branch. Keep bot policy outside physics and leave the network rollback path intact.
- A local bot match can retain browser `Host`/`Connected` semantics for left-paddle prediction and rendering, while a separate versioned opponent-mode field distinguishes bot play from LAN play. The HTTP snapshot, 23-field MessagePack WebSocket snapshot, TypeScript parser, and protocol tests must change together if this field is added.
- The repo already targets .NET 10/C# 14 with Native AOT publishing. Use current [ONNX Runtime CPU NuGet 1.30.0](https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.30.0) for in-process .NET inference and [PyTorch 2.14.1](https://pypi.org/project/torch/2.14.1/) for offline training. The [ONNX Runtime C# guide](https://onnxruntime.ai/docs/get-started/with-csharp.html) recommends reusable `OrtValue` buffers for fixed-size numeric models. The [PyTorch exporter](https://docs.pytorch.org/docs/2.14/onnx.html) supports `torch.onnx.export(..., dynamo=True)`; validate ONNX outputs against PyTorch. CPU inference is sufficient for a small state-vector model.
- The selected learning path is supervised imitation of a stronger teacher that sees the same legitimate game state, predicts contact after wall bounces, and evaluates legal paddle aims using exact `GameEngine` rollouts. Generate seeded reachable trajectories against varied opponents, train a small three-logit MLP in Python, then collect student-play states and relabel them with the teacher to address [behavior-cloning distribution shift](https://proceedings.mlr.press/v15/ross11a/ross11a.pdf). Split by entire match/seed; select a checkpoint by held-out wins. [Gymnasium](https://gymnasium.farama.org/introduction/create_custom_env/) and [Stable-Baselines3 PPO](https://stable-baselines3.readthedocs.io/en/master/modules/ppo.html) are available if imitation fails its gameplay gate, but add an environment bridge only if evidence justifies it.
- ONNX Runtime's NuGet compatibility alone does not prove Native AOT behavior. The [Native AOT limits](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) and ONNX Runtime's native binary packaging require an early publish-and-run spike. For the tiny MLP, benchmark single-thread, sequential CPU inference and spinning disabled per [ONNX Runtime thread guidance](https://onnxruntime.ai/docs/performance/tune-performance/threading.html); create and warm one session, then measure worst-case inference against the 16.7 ms tick budget.

## Sequential worker steps

1. **Local opponent session.** Add a single-process match path that gives the human the first paddle and reserves the second paddle for a bot controller. Reuse the authoritative fixed-tick game engine and its existing lifecycle. Add focused lifecycle/protocol tests. Commit `feat(bot): add local opponent session`.
2. **Simple .NET bot.** Implement a deterministic controller that observes only legitimate game state, chooses a legal second-player input, and respects tick timing and paddle movement. Cover serves, rallies, misses, rematches, and seeded performance. Commit `feat(bot): add simple paddle controller`.
3. **Playable Simple mode.** Add an explicit browser action and clear match status to start/play/leave a Simple bot game. Exercise the full browser-to-engine path and guard the existing LAN path. Commit `feat(ui): add play versus bot flow`.

**Reconcile after step 3:** review commits, tests, user experience, and the simulation API available for training. Adjust steps 4–6 before assigning step 4.

### Reconciliation after steps 1–3 (2026-10-07)

| Step | Commit | Review result |
| --- | --- | --- |
| 1. Local session | `c8fa0c0` | The local fixed-tick path advances both paddles without UDP, supports rematch/leave, and leaves LAN rollback separate. Browser snapshots kept the existing layout at this step. |
| 2. Simple controller | `0eb96a7` | A deterministic 150 ms sampled tracker with short wall-aware lookahead and a deadband controls only the right paddle. Tests cover legal axes, wall paths, unreachable balls, countdown, and repeatable matches. |
| 3. Browser flow | `2af5d18` | A visible bot action and mode-specific status work in the browser. HTTP and WebSocket snapshots carry explicit `opponentMode`; WebSocket changed to version 6. The page hides network-only details in bot play. |

The worker reported 76 passing C# tests, passing frontend checks, the full two-process integration suite, and a browser start/leave smoke check after step 3. The coordinator reviewed the committed diff and corrected a README/UI label mismatch through the worker. The worktree is clean. Simple is playable now; Hard remains to be built.

The next work remains in the planned order. Step 4 must prove ONNX Runtime inference in a **published and executed** Native AOT build on this macOS ARM64 host before any training investment. Existing release targets are macOS ARM64, Linux x64, and Windows x64; step 7 must add or run an equivalent smoke on each target. The host's default `python3` is 3.9.6, while Python 3.10 is available at `/opt/homebrew/bin/python3.10`; PyTorch 2.14.1 requires Python 3.10 or newer. Step 6 must use and document a compatible environment. Step 5 must version the feature order and use only state legitimately available to the bot, with training labels generated by the exact production engine.

4. **Native AOT inference spike.** Add the pinned ONNX Runtime CPU dependency and a tiny known model smoke path. Prove normal build, Native AOT publish **and launch**, model loading, one inference, and native library inclusion on macOS ARM64. Record package size and any AOT/trimming warnings. Define equivalent smoke verification for Linux x64 and Windows x64 release jobs. This step is a real runtime integration gate before training work. Commit `build(bot): verify onnx native aot inference`.

Step 4 review: `9832d93` passed regular and published macOS ARM64 inference (`2 → 3`), published HTTP startup, published full integration, 76 C# tests, and frontend checks. The macOS output contains an 18.6 MB executable, a 43.9 MB ONNX Runtime library, the 115-byte smoke model, and the frontend; its release-style archive is 37.1 MB. The publish reported MessagePack IL3053/IL2104 warnings and no ONNX Runtime warnings. The release smoke test now checks inference and native-library packaging on macOS, Linux, and Windows; the latter two still need an actual target-run result. The macOS publication includes unused Windows DLLs, so review package filtering in step 7 without weakening the smoke gate.
5. **Exact-engine training and evaluation environment.** Build reproducible headless trajectory generation from the production engine. Define and version one float32 observation vector and three-action mapping; generate teacher labels via contact prediction and checkpoint rollouts. Include seeded opponent variety, whole-match splits, student-rollout relabeling support, and machine-readable match metrics. Prove the teacher beats Simple before using it for training. Commit `feat(bot): add training data and evaluation`.
6. **Python training and ONNX artifact.** Train a compact PyTorch MLP from the generated data in a documented Python 3.10+ environment, add student-rollout relabeling, export fixed-shape ONNX with the current exporter, check PyTorch/ONNX parity, and commit the model, training scripts, pinned dependencies, seeds, hash, and results. Commit `feat(bot): train and export hard policy`.

**Reconcile after step 6:** confirm the AOT spike, teacher strength, data provenance, model quality, and remaining integration work. Improve the teacher/data/model or add PPO only if evidence shows the selected path cannot meet the gameplay gate.

7. **Hard bot integration and playable mode.** Load and warm one ONNX Runtime session, use bounded low-allocation inference on the agreed cadence, reject invalid outputs, and fall back to the Simple controller on runtime failure. Add Hard selection to the browser and a clear fallback status. Run paired held-out matches with the actual .NET ONNX policy and meet the win-rate gate; verify tests and release publish/run on supported targets. Commit `feat(bot): add onnx hard opponent`.

**Final review after step 7:** compare gameplay outcomes, inference latency, package/runtime distribution, UX, and all regressions with acceptance criteria. Assign a focused follow-up step for any observed gap before closing the goal.

## Review gates for every step

- One worker step is active at a time; the next starts after the previous commit and coordinator review.
- Each step includes only tests that verify behavior or a concrete risk, and reports the exact commands/results.
- The coordinator may edit this Markdown plan or other Markdown research notes, but does not edit implementation files.
