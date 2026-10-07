# Hard policy v1 (bot plan step 6)

`src/LanPong/Models/hard-v1.onnx` is the selected offline-trained policy. It is
a 10 → 64 → 64 → 3 ReLU MLP, with training-set mean and scale in the graph. Its
single float32 input is `observation[1,10]`; its float32 output is
`logits[1,3]`. The feature order, action mapping (up/stay/down), nine-tick
decision cadence, and tie rule follow [schema 1](BOT_TRAINING_DATA.md). The
artifact uses ONNX opset 18, has static dimensions, occupies 30,393 bytes, and
has SHA-256
`5d5d3cf0910d967cf2d6dc60e8fe0b63f772060178bf6673f6ddc5cdba98ab5a`.
The same hashes, confusion matrices, parity results, and gameplay counts are
also preserved in [hard-v1.json](training/results/hard-v1.json).

## Environment and data

Training ran on macOS ARM64 with Python 3.12.14, PyTorch 2.14.1, NumPy 2.2.6,
ONNX 1.23.2, ONNX Runtime 1.30.0, and ONNX Script 0.7.2. Exact resolved Python
packages are in [requirements-lock.txt](training/requirements-lock.txt). The
[training guide](training/README.md) gives the complete commands. CPU training
uses a fixed seed, one PyTorch thread, and deterministic algorithms. A repeat
of the selected fit produced the same ONNX SHA-256 byte for byte.

The base dataset has 48/12/12 whole train/validation/test matches from master
seed `20261010`. The student-rollout dataset has another 48/12/12 complete
matches from master seed `20261013`; a base ONNX policy with SHA-256
`36ead9cdca238c755ed7810b2b1414ba0cf798f9e83e31e3bee8d7a4ae137807`
controlled the right paddle, and the exact-engine teacher labeled its visited
states. The rollout contains 24,266 train, 5,577 validation, and 5,276 test
rows. Its manifest SHA-256 is
`1a526ec05460daed7f683dee74b9765d62a12a1c085b82663544635b19b201be`.
The original dataset's manifest SHA-256 is
`8d30dc327839773a0c8a3cfc378b852304136bb5b85216bec36bb159726444f4`.
The generator records every match seed in its manifests. Training rejects
cross-split and duplicate source seeds, and the `fit` command does not load
test labels.

## Training and model choice

The first fit used seed `20261014`, unweighted cross entropy, AdamW, batch
size 512, learning rate 0.001, weight decay 0.0001, and selected epoch 57 by
validation macro-F1 (then macro recall, then cross entropy). Its checkpoint
SHA-256 is
`d8445296cb2bc5c94ce55730eb86b5e093e573643d7765bb0d26b584ace1091c`.
The relabeling fit started from that checkpoint with seed `20261015`, combined
both train splits (49,111 rows), validated on both validation splits (10,681
rows), used the same optimizer settings, and selected epoch 55. Its checkpoint
SHA-256 is
`104fdd2031ff23490e187d6b0a4c68117cd266f1c565c9baa3ecaca49d20f656`.

The base and relabeled ONNX models were compared with the actual .NET ONNX
policy on two development gameplay seeds, `20261020` and `20261022`. Each seed
has 100 paired left-opponent scenarios, with the same profile and seed used
for the ONNX policy and Simple. The relabeled model won 198/200 matches across
the two seeds versus 195/200 for the base model, so the relabeled model was
frozen before inspecting test labels or the final gameplay seed. Simple won
116/200 of its paired matches. The direct student-versus-Simple control and
the frozen-seed result appear below.

| Development seed | Base wins | Relabeled wins | Simple wins | Base/relabeled score margin |
| --- | ---: | ---: | ---: | ---: |
| `20261020` | 96/100 | 100/100 | 58/100 | +635 / +643 |
| `20261022` | 99/100 | 98/100 | 58/100 | +658 / +619 |

The selected model's validation confusion matrices are true classes in rows
and predicted classes in columns, in up/stay/down order:

| Validation source | Rows | Confusion matrix | Per-class recall | Macro-F1 |
| --- | ---: | --- | --- | ---: |
| Teacher-play | 5,104 | `[[485,36,7],[82,3921,46],[8,44,475]]` | 0.919 / 0.968 / 0.901 | 0.918 |
| Student-rollout | 5,577 | `[[644,160,8],[56,3919,63],[3,109,615]]` | 0.793 / 0.971 / 0.846 | 0.891 |

`stay` accounts for about 79% of the original training labels. For a
rally-critical slice where the ball approaches the right paddle within 0.2
seconds, the selected model's up/down recall is 0.702/0.592 on teacher-play
validation and 0.679/0.632 on student-rollout validation. The base model's
corresponding teacher-play near-contact recall was 0.456/0.367. The slice is
defined from observation fields, not future state.

PyTorch and ONNX Runtime selected the same action on all 10,681 combined
validation rows; the maximum absolute logit difference was `2.29e-5`. The
export path is `torch.onnx.export(..., dynamo=True)` with fixed input shape.

## Post-selection test and gameplay check

After freezing the relabeled artifact, the explicit test command evaluated
both unseen whole-match test splits. PyTorch and ONNX Runtime agreed on every
action across all 11,289 test observations (maximum absolute logit difference
`2.29e-5`).

| Test source | Rows | Confusion matrix | Per-class recall | Macro-F1 |
| --- | ---: | --- | --- | ---: |
| Teacher-play | 6,013 | `[[592,43,8],[71,4587,69],[3,67,573]]` | 0.921 / 0.970 / 0.891 | 0.921 |
| Student-rollout | 5,276 | `[[617,165,9],[44,3757,68],[9,80,527]]` | 0.780 / 0.971 / 0.856 | 0.888 |

With the model frozen, a fresh paired gameplay check used master seed
`20261021`: the ONNX student won 100/100 matches against the five varied left
opponents, while Simple won 58/100 on the same scenarios. The student had the
better score margin in 87 scenarios, the worse in one, and tied in 12; its
aggregate score margin was +641 versus Simple's +116. All matches completed.
The descriptive Wilson 95% interval for the student's 100-match win rate is
`[0.963, 1]`.

Two direct ONNX-student-versus-Simple controls on the same frozen master seed
each completed 200/200 scheduled games: 100/100 student wins on the right and
100/100 on the left, with zero capped games. Both start with the same legal,
seeded, symmetric 120-tick opening and then swap the policy sides for each of
100 seeds. In `policies` mode, each bot owns every later countdown action, as
it would in local play; this produced 40 distinct playing trajectories per
side. In `seeded-targets` mode, both paddles instead follow the same legal
seeded countdown target after each point, a perturbation stress test that
produced 100 distinct playing trajectories per side. The scheduled-game win
rate is 100% in both protocols; the descriptive Wilson 95% interval is
`[0.981, 1]`. Each side-swapped pair shares a seed, so the 200 games are not
200 independent scenarios. Step 7 still needs evaluation with the actual
integrated Hard controller.

To repeat these checks against the committed model from the repository root:

```bash
dotnet run --project tools/LanPong.TrainingData -c Release -- evaluate-model \
  --output .artifacts/hard-v1-paired.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --seed 20261021 --matches 100
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate-model \
  --output .artifacts/hard-v1-direct-policies.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --seed 20261021 --matches 100 --countdown-mode policies
dotnet run --project tools/LanPong.TrainingData -c Release -- direct-evaluate-model \
  --output .artifacts/hard-v1-direct-stress.json \
  --student-model src/LanPong/Models/hard-v1.onnx \
  --seed 20261021 --matches 100 --countdown-mode seeded-targets
```

The separate Step 7 200-match acceptance seed is reserved as `20261107` and
has not been inspected here.
